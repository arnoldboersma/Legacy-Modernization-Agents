namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Deterministic category of a risk register entry (design doc §6 section 9, issue #5). Every
/// entry is derived from an already-persisted, evidence-backed record — never invented.
/// </summary>
public enum RiskCategory
{
    /// <summary>An expected section of the semantic model has no supporting records at all
    /// (e.g. no use-case/business-rule extractor has run for this subject/phase).</summary>
    EvidenceGap,

    /// <summary>Two or more governed records make incompatible statements about the same subject.</summary>
    Conflict,

    /// <summary>A candidate record exists but remains below the confidence/evidence threshold for
    /// publication (e.g. a <see cref="ContextCandidate"/> at <see cref="ReviewStatus.NeedsEvidence"/>
    /// or an <see cref="Integration"/>/<see cref="RoleAssignment"/> flagged <c>RequiresReview</c>).</summary>
    UnconfirmedFinding,

    /// <summary>A documented limitation of a deterministic analyzer/heuristic itself (design doc
    /// §14), not a limitation of any single record.</summary>
    AnalyzerLimitation,

    /// <summary>Technical debt or structural concern surfaced by the analysis (e.g. a
    /// generic/cross-cutting namespace cluster, a low-cohesion catch-all context).</summary>
    TechnicalDebt
}

/// <summary>Severity of a risk register entry, for reviewer triage.</summary>
public enum RiskSeverity
{
    Low,
    Medium,
    High
}

/// <summary>
/// Append-only lifecycle status for a risk register entry. Distinct from <see cref="ReviewStatus"/>
/// because a risk is an operational tracking record, not a candidate fact to publish/reject — a
/// human reviewer acknowledges or resolves it, they do not "publish" a risk.
/// </summary>
public enum RiskStatus
{
    Open,
    Acknowledged,
    Resolved,
    Superseded
}

/// <summary>
/// An append-only, evidence-linked risk register entry (design doc §6 section 9, issue #5): a
/// technical debt item, conflict, uncertainty, evidence gap, or documented analyzer limitation
/// that Specification Factory must see before writing a specification. Never contains
/// future-state/roadmap/modernization-recommendation language — only the current-state gap or
/// uncertainty and an escalation question for a human to resolve (design doc §9 constraint).
/// Corrections are new rows linked via <see cref="SupersedesRiskId"/>; existing rows are never
/// mutated, consistent with every other Discovery Factory governed record family.
/// </summary>
public sealed class RiskRegisterEntry
{
    /// <summary>Stable run-scoped identifier, e.g. "R-000000001".</summary>
    public required string RiskId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Short human-readable title, e.g. "Unconfirmed context candidate: Shared".</summary>
    public required string Title { get; init; }

    /// <summary>Deterministic category of this risk.</summary>
    public required RiskCategory Category { get; init; }

    /// <summary>Reviewer-triage severity.</summary>
    public required RiskSeverity Severity { get; init; }

    /// <summary>Current append-only lifecycle status. Starts at <see cref="RiskStatus.Open"/>.</summary>
    public required RiskStatus Status { get; init; }

    /// <summary>Current-state description of the gap/conflict/uncertainty/limitation. Never
    /// future-state advice, roadmap language, or a modernization recommendation.</summary>
    public required string Description { get; init; }

    /// <summary>The question a human reviewer/architect must answer to close this risk.</summary>
    public required string EscalationQuestion { get; init; }

    /// <summary>
    /// Name of the deterministic derivation rule that produced this entry, e.g.
    /// "UnconfirmedContextCandidate", "IntegrationRequiresReview", "RoleRequiresReview",
    /// "SyntaxTreeOnlyAnalysisLimitation", "GenericNamespaceClusteringLimitation",
    /// "UseCaseBusinessRuleExtractionGap". Combined with <see cref="SourceRecordId"/> this forms
    /// the natural key used to keep re-derivation idempotent.
    /// </summary>
    public required string DerivationRule { get; init; }

    /// <summary>
    /// Stable identifier of the single governed record this entry was derived from (a
    /// <see cref="ContextCandidate.ContextCandidateId"/>, <see cref="Integration.IntegrationId"/>,
    /// <see cref="RoleAssignment.RoleAssignmentId"/>, or the owning <see cref="RunId"/> itself for
    /// run-scoped/fixed entries) — used with <see cref="DerivationRule"/> as a natural key so
    /// re-running derivation for the same run never creates a duplicate entry for the same source.
    /// </summary>
    public required string SourceRecordId { get; init; }

    /// <summary>Cited evidence IDs supporting this risk (the source record's own evidence).</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }

    /// <summary>Provenance record ID explaining how this entry was produced.</summary>
    public required string ProvenanceId { get; init; }

    /// <summary>If this entry corrects/supersedes a prior risk entry, its ID.</summary>
    public string? SupersedesRiskId { get; init; }

    /// <summary>UTC timestamp this entry was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
