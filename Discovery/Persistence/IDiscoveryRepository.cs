using CobolToQuarkusMigration.Discovery.Models;

namespace CobolToQuarkusMigration.Discovery.Persistence;

/// <summary>
/// Neutral, append-only persistence contract for the Discovery Factory governed record model.
/// Implementations must never overwrite or delete governed history rows (runs, artifacts,
/// evidence, findings, finding revisions, review decisions, provenance, LLM assessments); only
/// inserts are permitted for those record families.
/// </summary>
public interface IDiscoveryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task AppendRunAsync(DiscoveryRun run, CancellationToken cancellationToken = default);
    Task<DiscoveryRun?> GetRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscoveryRun>> GetAllRunsAsync(CancellationToken cancellationToken = default);

    Task AppendArtifactAsync(SourceArtifact artifact, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SourceArtifact>> GetArtifactsAsync(string runId, CancellationToken cancellationToken = default);

    Task AppendEvidenceAsync(Evidence evidence, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Evidence>> GetEvidenceAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Evidence>> GetEvidenceByIdsAsync(IEnumerable<string> evidenceIds, CancellationToken cancellationToken = default);

    Task AppendProvenanceAsync(Provenance provenance, CancellationToken cancellationToken = default);

    Task AppendFindingAsync(Finding finding, CancellationToken cancellationToken = default);
    Task<Finding?> GetFindingAsync(string findingId, CancellationToken cancellationToken = default);

    Task AppendFindingRevisionAsync(FindingRevision revision, CancellationToken cancellationToken = default);
    Task<FindingRevision?> GetFindingRevisionAsync(string findingRevisionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FindingRevision>> GetFindingRevisionsAsync(string findingId, CancellationToken cancellationToken = default);
    Task<FindingRevision?> GetLatestFindingRevisionAsync(string findingId, CancellationToken cancellationToken = default);

    Task AppendLlmAssessmentAsync(LlmAssessment assessment, CancellationToken cancellationToken = default);
    Task<LlmAssessment?> GetLlmAssessmentForRevisionAsync(string findingRevisionId, CancellationToken cancellationToken = default);

    Task AppendReviewDecisionAsync(ReviewDecision decision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReviewDecision>> GetReviewDecisionsAsync(string findingRevisionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all finding revisions across a run whose status is Candidate, HumanReview, or
    /// NeedsEvidence — i.e. the reviewer queue contents for that run.
    /// </summary>
    Task<IReadOnlyList<FindingRevision>> GetReviewQueueAsync(string runId, CancellationToken cancellationToken = default);

    Task AppendRoleAssignmentAsync(RoleAssignment assignment, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RoleAssignment>> GetRoleAssignmentsAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RoleAssignment>> GetRoleAssignmentsForArtifactAsync(string artifactId, CancellationToken cancellationToken = default);
}
