using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anchor.Infrastructure.DeepSeek;

public sealed record DeepSeekSettings(
    bool Enabled,
    string ApiKey,
    string Model,
    string Endpoint);

public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext, byte[] entropy);
    byte[] Unprotect(byte[] ciphertext, byte[] entropy);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DeepSeek key encryption requires Windows DPAPI.");
        }

        return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DeepSeek key decryption requires Windows DPAPI.");
        }

        return ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
    }
}

public sealed class DeepSeekSettingsStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("Anchor.DeepSeek.Settings.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;

    public DeepSeekSettingsStore(string path, ISecretProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _protector = protector ?? new DpapiSecretProtector();
    }

    public async Task SaveAsync(
        DeepSeekSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateEndpoint(settings.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Model);
        var encrypted = _protector.Protect(
            Encoding.UTF8.GetBytes(settings.ApiKey ?? string.Empty),
            Entropy);
        var envelope = new SettingsEnvelope(
            settings.Enabled,
            Convert.ToBase64String(encrypted),
            settings.Model.Trim(),
            settings.Endpoint.Trim());

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(envelope, JsonOptions),
            cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }

    public async Task<DeepSeekSettings?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var envelope = JsonSerializer.Deserialize<SettingsEnvelope>(
            await File.ReadAllTextAsync(_path, cancellationToken),
            JsonOptions) ?? throw new InvalidDataException("DeepSeek settings are invalid.");
        ValidateEndpoint(envelope.Endpoint);
        var plaintext = _protector.Unprotect(
            Convert.FromBase64String(envelope.EncryptedApiKey),
            Entropy);
        return new DeepSeekSettings(
            envelope.Enabled,
            Encoding.UTF8.GetString(plaintext),
            envelope.Model,
            envelope.Endpoint);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }

    private static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("DeepSeek endpoint must be an absolute HTTPS URL.", nameof(endpoint));
        }
    }

    private sealed record SettingsEnvelope(
        bool Enabled,
        string EncryptedApiKey,
        string Model,
        string Endpoint);
}
