namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Append-only human review lifecycle status for a finding revision (design doc §4).
/// </summary>
public enum ReviewStatus
{
    Candidate,
    HumanReview,
    Published,
    Rejected,
    NeedsEvidence,
    Superseded
}

/// <summary>
/// A stable, cross-revision finding identity. A finding groups an append-only chain of
/// <see cref="FindingRevision"/> records; the finding itself is never mutated, only revised.
/// </summary>
public sealed class Finding
{
    /// <summary>Stable run-scoped identifier, e.g. "F-0001". Never reused as a correlation key.</summary>
    public required string FindingId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Optional cross-run correlation key (fingerprint) distinct from the identifier, used to
    /// relate the same real-world fact across separate runs/revisions.
    /// </summary>
    public string? CorrelationKey { get; init; }

    /// <summary>UTC timestamp the finding identity was first created.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// A single append-only revision of a finding's content and review status. Corrections append a
/// new revision with an incremented <see cref="RevisionNumber"/> and a
/// <see cref="SupersedesRevisionId"/> link; prior revisions are never overwritten or deleted.
/// </summary>
public sealed class FindingRevision
{
    /// <summary>Stable run-scoped identifier, e.g. "F-0001-R1".</summary>
    public required string FindingRevisionId { get; init; }

    /// <summary>Owning finding identity.</summary>
    public required string FindingId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>1-based revision number within the finding's chain.</summary>
    public required int RevisionNumber { get; init; }

    /// <summary>Current-state statement describing the candidate/published fact.</summary>
    public required string Statement { get; init; }

    /// <summary>Cited evidence IDs supporting this statement.</summary>
    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();

    /// <summary>Confidence in this statement, 0.0-1.0, based on deterministic evidence strength.</summary>
    public required double Confidence { get; init; }

    /// <summary>Current append-only review lifecycle status.</summary>
    public required ReviewStatus Status { get; init; }

    /// <summary>Provenance record ID explaining how this revision was produced.</summary>
    public required string ProvenanceId { get; init; }

    /// <summary>If this revision corrects/supersedes a prior published revision, its ID.</summary>
    public string? SupersedesRevisionId { get; init; }

    /// <summary>UTC timestamp this revision was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
