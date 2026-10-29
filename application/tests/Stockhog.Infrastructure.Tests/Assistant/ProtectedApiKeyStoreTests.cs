using System.Text;
using Stockhog.Infrastructure.Anthropic;
using Stockhog.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.Assistant;

public class ProtectedApiKeyStoreTests
{
    [Fact]
    public void Key_is_stored_encrypted_and_read_back_by_a_new_store()
    {
        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, "key.bin");
        const string key = "sk-ant-api03-secret-value-1234567890";

        new ProtectedApiKeyStore(Options.Create(new AnthropicOptions()), path).SetApiKey(key);

        Assert.DoesNotContain(key, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        var reopened = new ProtectedApiKeyStore(Options.Create(new AnthropicOptions()), path);
        Assert.True(reopened.HasApiKey);
        Assert.Equal(key, reopened.GetApiKey());
    }

    [Fact]
    public void Removing_the_key_falls_back_to_configuration()
    {
        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, "key.bin");
        var store = new ProtectedApiKeyStore(Options.Create(new AnthropicOptions { ApiKey = "from-config-key-0123456789" }), path);
        store.SetApiKey("stored-key-0123456789abcdef");

        store.SetApiKey(null);

        Assert.False(File.Exists(path));
        Assert.Equal("from-config-key-0123456789", store.GetApiKey());
    }

    [Fact]
    public void A_file_that_cannot_be_decrypted_counts_as_no_key()
    {
        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, "key.bin");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        var store = new ProtectedApiKeyStore(Options.Create(new AnthropicOptions()), path);

        // The environment may provide ANTHROPIC_API_KEY; the corrupt file itself must not.
        Assert.Equal(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } env ? env : null, store.GetApiKey());
    }
}
