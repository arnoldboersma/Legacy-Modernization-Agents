namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Supported evidence types. Source code, Configuration, and Schema support static claims only;
/// runtime claims require linked Logs evidence (design doc §5.2).
/// </summary>
public enum EvidenceType
{
    SourceCode,
    Configuration,
    Logs,
    Schema
}

/// <summary>
/// Neutral, redacted evidence record supporting a finding. Never stores resolved configuration
/// values, secrets, or PII — only locator, redacted excerpt (if permitted), and redaction status.
/// </summary>
public sealed class Evidence
{
    /// <summary>Stable run-scoped identifier, e.g. "EVD-0001".</summary>
    public required string EvidenceId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Evidence type. Determines what kind of claim it may support.</summary>
    public required EvidenceType Type { get; init; }

    /// <summary>Artifact this evidence was located in, if applicable.</summary>
    public string? ArtifactId { get; init; }

    /// <summary>Human-readable source locator, e.g. "source/CUSTOMER-INQUIRY.cbl:L42-L58".</summary>
    public required string Locator { get; init; }

    /// <summary>Redacted excerpt permitted for retention/display. Never raw secrets/PII/payloads.</summary>
    public string? RedactedExcerpt { get; init; }

    /// <summary>True if redaction was applied to the excerpt before persistence.</summary>
    public bool WasRedacted { get; init; }

    /// <summary>Free-text summary of what was redacted, for reviewer transparency.</summary>
    public string? RedactionSummary { get; init; }

    /// <summary>UTC timestamp the evidence record was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
