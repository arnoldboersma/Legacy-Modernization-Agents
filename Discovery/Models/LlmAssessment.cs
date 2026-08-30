namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Non-authoritative LLM candidate assessment: a review-priority score and structured
/// explanation. Never establishes or publishes a factual claim — only prioritizes/explains a
/// candidate finding for human review (design doc §5.4).
/// </summary>
public sealed class LlmAssessment
{
    /// <summary>Stable run-scoped identifier, e.g. "LLM-0001".</summary>
    public required string LlmAssessmentId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Finding revision this assessment applies to.</summary>
    public required string FindingRevisionId { get; init; }

    /// <summary>Non-authoritative review priority, e.g. 0.0-1.0 or a coarse bucket score.</summary>
    public required double ReviewPriority { get; init; }

    /// <summary>Structured explanation of why the candidate needs review attention.</summary>
    public required string WhyExplanation { get; init; }

    /// <summary>Cited deterministic evidence IDs the explanation relies on.</summary>
    public IReadOnlyList<string> CitedEvidenceIds { get; init; } = Array.Empty<string>();

    /// <summary>Known conflicts or unknowns surfaced by the assessment.</summary>
    public string? ConflictsOrUnknowns { get; init; }

    /// <summary>Model identity used to produce this assessment.</summary>
    public required string ModelId { get; init; }

    /// <summary>Provenance record ID for this assessment.</summary>
    public required string ProvenanceId { get; init; }

    /// <summary>UTC timestamp the assessment was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
