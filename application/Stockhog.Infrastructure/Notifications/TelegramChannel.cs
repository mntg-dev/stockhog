using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stockhog.Application.Notifications;
using Stockhog.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Stockhog.Infrastructure.Notifications;

/// <summary>
/// Sends messages through the Telegram Bot API. The bot token is part of the request URL, so neither the URL nor the
/// HTTP error texts are ever logged or returned; only Telegram's own description of a refusal is. The target is kept
/// in a file encrypted with DPAPI for the current Windows user.
/// </summary>
internal sealed class TelegramChannel(IHttpClientFactory httpClients, ILogger<TelegramChannel> logger, string? path = null) : IChatChannel
{
    public const string HttpClientName = "telegram";

    private static readonly byte[] Entropy = "Stockhog.Telegram.v1"u8.ToArray();

    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyDataMigration.FolderName, "telegram.bin");

    private readonly Lock _sync = new();
    private ChatTarget? _target;
    private bool _loaded;

    public bool IsConfigured => Target is not null;

    public ChatTarget? Target
    {
        get
        {
            lock (_sync)
            {
                if (!_loaded)
                {
                    _target = Read();
                    _loaded = true;
                }

                return _target;
            }
        }
    }

    public void Save(ChatTarget? target)
    {
        lock (_sync)
        {
            if (target is null)
            {
                File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.SerializeToUtf8Bytes(new Stored(target.BotToken, target.ChatId));
                File.WriteAllBytes(_path, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
            }

            _target = target;
            _loaded = true;
        }
    }

    public async Task<string?> SendAsync(ChatTarget target, string text, CancellationToken cancellationToken = default)
    {
        var http = httpClients.CreateClient(HttpClientName);
        var url = $"https://api.telegram.org/bot{Uri.EscapeDataString(target.BotToken)}/sendMessage";
        try
        {
            using var response = await http.PostAsJsonAsync(url, new SendMessage(target.ChatId, text), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return null;

            var answer = await ReadAnswerAsync(response, cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Telegram refused a message: {Status} {Description}", (int)response.StatusCode, answer);
            return answer ?? $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The exception may carry the URL with the token: only its type is logged.
            logger.LogWarning("Telegram could not be reached ({Error})", ex.GetType().Name);
            return "Telegram could not be reached.";
        }
    }

    private static async Task<string?> ReadAnswerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<Answer>(cancellationToken).ConfigureAwait(false))?.Description;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ChatTarget? Read()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser));
            return stored is { BotToken.Length: > 0, ChatId.Length: > 0 } ? new ChatTarget(stored.BotToken, stored.ChatId) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private sealed record Stored(string BotToken, string ChatId);

    private sealed record SendMessage([property: JsonPropertyName("chat_id")] string ChatId, [property: JsonPropertyName("text")] string Text);

    private sealed record Answer([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("description")] string? Description);
}
