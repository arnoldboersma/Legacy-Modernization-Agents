namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// A reviewer decision that transitions a finding revision's status. Requires a Reviewer identity
/// and rationale (design doc §4.1). Append-only: decisions are never edited or deleted, only
/// followed by further decisions or a superseding revision.
/// </summary>
public sealed class ReviewDecision
{
    /// <summary>Stable run-scoped identifier, e.g. "REV-0001".</summary>
    public required string ReviewDecisionId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Finding revision this decision applies to.</summary>
    public required string FindingRevisionId { get; init; }

    /// <summary>
    /// Acting reviewer identity. The pilot has no authentication/role management; this is a
    /// simple local identity string (defaults to "Reviewer") designed so an authenticated host
    /// identity can replace it later without a schema change.
    /// </summary>
    public required string ReviewerIdentity { get; init; }

    /// <summary>Resulting status: Published, Rejected, or NeedsEvidence.</summary>
    public required ReviewStatus Decision { get; init; }

    /// <summary>Required rationale for the decision.</summary>
    public required string Rationale { get; init; }

    /// <summary>UTC timestamp the decision was recorded.</summary>
    public required DateTimeOffset DecidedAtUtc { get; init; }
}
