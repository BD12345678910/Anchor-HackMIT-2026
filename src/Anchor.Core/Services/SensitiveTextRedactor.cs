using System.Text.RegularExpressions;

namespace Anchor.Core.Services;

public static partial class SensitiveTextRedactor
{
    private static readonly HashSet<string> BlockedFeatureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "raw_key",
        "key_value",
        "clipboard",
        "password",
        "secret",
        "access_token",
        "refresh_token"
    };

    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var bounded = value[..Math.Min(value.Length, 4_096)];
        bounded = EmailRegex().Replace(bounded, "[redacted-email]");
        bounded = SecretRegex().Replace(bounded, "[redacted-secret]");
        bounded = BearerRegex().Replace(bounded, "Bearer [redacted-secret]");
        return bounded;
    }

    public static IReadOnlyDictionary<string, string> RedactFeatures(
        IReadOnlyDictionary<string, string>? features)
    {
        if (features is null || features.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in features.Take(64))
        {
            if (string.IsNullOrWhiteSpace(key) || BlockedFeatureNames.Contains(key))
            {
                continue;
            }

            var boundedKey = key.Trim()[..Math.Min(key.Trim().Length, 64)];
            result[boundedKey] = Redact(value) ?? string.Empty;
        }

        return result;
    }

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:sk|pk|api|token)[-_][A-Za-z0-9_-]{16,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/-]{16,}=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();
}
