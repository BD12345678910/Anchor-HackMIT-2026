namespace Anchor.Infrastructure.Windows;

public static class SecureWindowClassifier
{
    private static readonly HashSet<string> SecureProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CredentialUIBroker",
        "Consent",
        "LogonUI",
        "LockApp",
        "SecurityHealthSystray"
    };

    private static readonly string[] SensitiveTitleTerms =
    [
        "password",
        "passcode",
        "windows security",
        "credential",
        "permission request",
        "secure desktop"
    ];

    public static bool IsSecure(string? processName, string? windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(processName) && SecureProcesses.Contains(processName))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(windowTitle)
            && SensitiveTitleTerms.Any(term => windowTitle.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
