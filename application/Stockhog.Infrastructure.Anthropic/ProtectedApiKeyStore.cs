using System.Security.Cryptography;
using System.Text;
using Stockhog.Application.Assistant;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Anthropic;

/// <summary>
/// Keeps the API key in a file encrypted with Windows DPAPI for the current user: only this Windows account on this
/// machine can decrypt it. Falls back to configuration (<c>Anthropic:ApiKey</c>) and the <c>ANTHROPIC_API_KEY</c>
/// environment variable when nothing is stored.
/// </summary>
internal sealed class ProtectedApiKeyStore(IOptions<AnthropicOptions> options, string? path = null) : IAssistantCredentials
{
    // Extra entropy ties the encrypted file to this app, not just to the Windows user. It keeps the app's former name:
    // changing it would make keys saved before the rename unreadable.
    private static readonly byte[] Entropy = "EmTrading.Assistant.v1"u8.ToArray();

    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stockhog", "anthropic-key.bin");

    private readonly Lock _sync = new();
    private string? _cached;
    private bool _loaded;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());

    public string? GetApiKey()
    {
        lock (_sync)
        {
            if (!_loaded)
            {
                _cached = Read();
                _loaded = true;
            }

            return _cached
                   ?? NullIfBlank(options.Value.ApiKey)
                   ?? NullIfBlank(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        }
    }

    public void SetApiKey(string? apiKey)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                File.Delete(_path);
                _cached = null;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_path, encrypted);
                _cached = apiKey;
            }

            _loaded = true;
        }
    }

    private string? Read()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Written by another Windows user or machine: treat as not set.
            return null;
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
