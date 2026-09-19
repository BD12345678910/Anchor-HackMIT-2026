using Anchor.Infrastructure.DeepSeek;

namespace Anchor.Infrastructure.Tests;

public sealed class DeepSeekSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"anchor-deepseek-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Settings_round_trip_without_plaintext_key_on_disk()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new DeepSeekSettingsStore(path, new ReversibleTestProtector());
        var settings = new DeepSeekSettings(
            Enabled: true,
            ApiKey: "secret-deepseek-key",
            Model: "deepseek-flash",
            Endpoint: "https://api.deepseek.com/chat/completions");

        await store.SaveAsync(settings);
        var disk = await File.ReadAllTextAsync(path);
        var loaded = await store.LoadAsync();

        Assert.DoesNotContain("secret-deepseek-key", disk, StringComparison.Ordinal);
        Assert.Equal(settings, loaded);
    }

    [Fact]
    public async Task Delete_removes_persisted_settings()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new DeepSeekSettingsStore(path, new ReversibleTestProtector());
        await store.SaveAsync(new DeepSeekSettings(
            true,
            "secret",
            "deepseek-flash",
            "https://api.deepseek.com/chat/completions"));

        await store.DeleteAsync();

        Assert.False(File.Exists(path));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Save_rejects_non_https_endpoint()
    {
        var store = new DeepSeekSettingsStore(
            Path.Combine(_directory, "settings.json"),
            new ReversibleTestProtector());

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new DeepSeekSettings(
            true,
            "secret",
            "deepseek-flash",
            "http://api.deepseek.com/chat/completions")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ReversibleTestProtector : ISecretProtector
    {
        private const byte Mask = 0xA7;

        public byte[] Protect(byte[] plaintext, byte[] entropy) => Transform(plaintext);
        public byte[] Unprotect(byte[] ciphertext, byte[] entropy) => Transform(ciphertext);

        private static byte[] Transform(byte[] input) =>
            input.Select(value => (byte)(value ^ Mask)).ToArray();
    }
}
