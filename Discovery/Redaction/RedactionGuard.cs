using System.Text.RegularExpressions;

namespace CobolToQuarkusMigration.Discovery.Redaction;

/// <summary>
/// Result of applying <see cref="RedactionGuard"/> to a text excerpt.
/// </summary>
public sealed record RedactionResult(string SanitizedText, bool WasRedacted, string? Summary);

/// <summary>
/// Scrubs secrets, credentials, PII, and resolved configuration values from text before it is
/// persisted or sent to an LLM (design doc §2, §5.2). This is a defense-in-depth guard for the
/// pilot slice; it does not claim to catch every possible sensitive pattern.
/// </summary>
public static class RedactionGuard
{
    private const string Mask = "[REDACTED]";

    // Ordered rules: (label, pattern). Patterns are intentionally conservative/broad so that
    // false positives (over-redaction) are preferred over leaking sensitive values.
    private static readonly (string Label, Regex Pattern)[] Rules =
    {
        ("connection-string",
            new Regex(@"(?i)\b(Password|Pwd|User\s*ID|Secret|Api[-_]?Key|Access[-_]?Key)\s*=\s*[^;""'\r\n]+",
                RegexOptions.Compiled)),
        ("bearer-token",
            new Regex(@"(?i)\bBearer\s+[A-Za-z0-9\-\._~\+/]+=*", RegexOptions.Compiled)),
        ("api-key-assignment",
            new Regex(@"(?i)\b(api[-_]?key|apikey|secret|token|password|passwd)\s*[:=]\s*[""']?[^\s""',;]{4,}",
                RegexOptions.Compiled)),
        ("aws-access-key",
            new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("private-key-block",
            new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
                RegexOptions.Compiled)),
        ("email",
            new Regex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled)),
        ("ssn",
            new Regex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled)),
        ("credit-card",
            new Regex(@"\b(?:\d[ -]*?){13,16}\b", RegexOptions.Compiled)),
    };

    /// <summary>
    /// Applies all redaction rules to <paramref name="text"/> and returns the sanitized result
    /// with metadata describing whether anything was redacted.
    /// </summary>
    public static RedactionResult Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new RedactionResult(text ?? string.Empty, false, null);
        }

        var sanitized = text;
        var hitLabels = new List<string>();

        foreach (var (label, pattern) in Rules)
        {
            var matchCount = pattern.Matches(sanitized).Count;
            if (matchCount > 0)
            {
                sanitized = pattern.Replace(sanitized, Mask);
                hitLabels.Add($"{label}x{matchCount}");
            }
        }

        if (hitLabels.Count == 0)
        {
            return new RedactionResult(sanitized, false, null);
        }

        return new RedactionResult(sanitized, true, string.Join(", ", hitLabels));
    }

    /// <summary>
    /// Configuration evidence must never retain a resolved value — only the key name and its
    /// semantic role (design doc §5.2). This helper enforces that contract explicitly.
    /// </summary>
    public static string DescribeConfigurationKey(string keyName, string semanticRole)
        => $"{keyName} (role: {semanticRole}; value not retained)";
}
