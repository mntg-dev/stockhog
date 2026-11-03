using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stockhog.Application.Brokers;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca;

/// <summary>
/// The keys entered on the Settings page, in a file encrypted with Windows DPAPI for the current user: only this
/// Windows account on this machine can read it.
/// </summary>
internal sealed class ProtectedAlpacaKeyFile(string? path = null)
{
    // Ties the encrypted file to this app and its purpose, not just to the Windows user.
    private static readonly byte[] Entropy = "Stockhog.Alpaca.v1"u8.ToArray();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stockhog", "alpaca-keys.bin");

    private readonly string _path = path ?? DefaultPath;
    private readonly Lock _sync = new();

    public BrokerKeys? Read()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
                return null;

            try
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<StoredKeys>(json) is { ApiKey: { Length: > 0 } key, ApiSecret: { Length: > 0 } secret } stored
                    ? new BrokerKeys(key, secret, stored.PaperApiKey, stored.PaperApiSecret)
                    : null;
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                // Written by another Windows user or machine, or damaged: treat as not set.
                return null;
            }
        }
    }

    public void Write(BrokerKeys? keys)
    {
        lock (_sync)
        {
            if (keys is null)
            {
                File.Delete(_path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(new StoredKeys(keys.ApiKey, keys.ApiSecret, keys.PaperApiKey, keys.PaperApiSecret));
            File.WriteAllBytes(_path, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
        }
    }

    private sealed record StoredKeys(string ApiKey, string ApiSecret, string? PaperApiKey, string? PaperApiSecret);
}

/// <summary>
/// Puts the stored keys into <see cref="AlpacaOptions"/> when the options are first read, so every client uses them;
/// stored keys win over configuration. Live keys are never touched.
/// </summary>
internal sealed class StoredAlpacaKeys(ProtectedAlpacaKeyFile file) : IPostConfigureOptions<AlpacaOptions>
{
    public BrokerKeySource Source { get; private set; }

    public void PostConfigure(string? name, AlpacaOptions options)
    {
        if (file.Read() is { } keys)
        {
            options.ApiKey = keys.ApiKey;
            options.ApiSecret = keys.ApiSecret;
            options.TradingApiKey = keys.PaperApiKey;
            options.TradingApiSecret = keys.PaperApiSecret;
            Source = BrokerKeySource.Settings;
        }
        else
        {
            Source = options.HasCredentials ? BrokerKeySource.Configuration : BrokerKeySource.None;
        }
    }
}

internal sealed class AlpacaKeyStore(ProtectedAlpacaKeyFile file, StoredAlpacaKeys startup, IOptions<AlpacaOptions> options) : IBrokerKeyStore
{
    public BrokerKeySource SourceInUse
    {
        get
        {
            _ = options.Value;   // the source is known once the options have been built
            return startup.Source;
        }
    }

    public string? KeyInUse => options.Value.ApiKey;

    public BrokerKeys? Stored => file.Read();

    public void Save(BrokerKeys? keys) => file.Write(keys);
}

/// <summary>
/// Reads one latest bar from the data API and the account from the paper trading API. Never calls the live API, so
/// testing a key can not touch real money even if a live key is pasted by mistake.
/// </summary>
internal sealed class AlpacaKeyTester(HttpClient http, IOptions<AlpacaOptions> options) : IBrokerKeyTester
{
    public async Task<BrokerKeyCheck> TestAsync(BrokerKeys keys, CancellationToken cancellationToken = default)
    {
        var dataUrl = $"{options.Value.DataBaseUrl.TrimEnd('/')}/v2/stocks/bars/latest?symbols=SPY&feed={Uri.EscapeDataString(options.Value.Feed)}";
        var (dataError, _) = await GetAsync<JsonElement>(dataUrl, keys.ApiKey, keys.ApiSecret, cancellationToken);

        var accountUrl = $"https://{AlpacaOptions.PaperTradingHost}/v2/account";
        var (tradingError, account) = await GetAsync<AccountDto>(accountUrl, keys.PaperApiKey ?? keys.ApiKey, keys.PaperApiSecret ?? keys.ApiSecret, cancellationToken);

        return new BrokerKeyCheck(dataError, tradingError, account?.AccountNumber);
    }

    private async Task<(string? Error, T? Body)> GetAsync<T>(string url, string key, string secret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("APCA-API-KEY-ID", key);
        request.Headers.Add("APCA-API-SECRET-KEY", secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ("the key or secret was not accepted.", default);
            if (!response.IsSuccessStatusCode)
                return ($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim(), default);
            return (null, await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ("Alpaca could not be reached: " + ex.Message, default);
        }
        catch (JsonException)
        {
            return ("the answer could not be read.", default);
        }
    }

    private sealed record AccountDto([property: JsonPropertyName("account_number")] string? AccountNumber);
}
