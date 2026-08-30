namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Neutral, language-agnostic metadata for a source artifact discovered within a run's declared
/// scope. Deliberately excludes raw file content/payloads; retains only locator, hash, and
/// classification metadata needed to cite evidence.
/// </summary>
public sealed class SourceArtifact
{
    /// <summary>Stable run-scoped identifier, e.g. "ART-0001".</summary>
    public required string ArtifactId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Source-relative path/locator of the artifact.</summary>
    public required string Path { get; init; }

    /// <summary>Neutral language classification, e.g. "COBOL", "CSharp", "Copybook", "Config".</summary>
    public required string Language { get; init; }

    /// <summary>SHA-256 content hash of the artifact at the declared source revision.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Size in bytes at the declared source revision, if known.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>UTC timestamp the artifact record was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
