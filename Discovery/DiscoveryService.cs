using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
using CobolToQuarkusMigration.Discovery.Redaction;
using Microsoft.Extensions.Logging;

namespace CobolToQuarkusMigration.Discovery;

/// <summary>
/// Orchestrates the Discovery Factory append-only lifecycle: run declaration, deterministic
/// artifact/evidence/candidate-finding capture (with redaction applied first), and the human
/// review transitions candidate -> human review -> published | rejected | needs evidence.
/// Corrections to a published finding always create a new superseding revision; no governed
/// record is ever mutated or deleted.
/// </summary>
public sealed class DiscoveryService
{
    /// <summary>Default acting reviewer identity for the pilot (no authentication/roles yet).</summary>
    public const string DefaultReviewerIdentity = "Reviewer";

    private readonly IDiscoveryRepository _repository;
    private readonly ILogger<DiscoveryService> _logger;

    public DiscoveryService(IDiscoveryRepository repository, ILogger<DiscoveryService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<DiscoveryRun> StartRunAsync(
        string subject,
        string sourceLocator,
        string sourceRevision,
        IReadOnlyList<string> inclusions,
        IReadOnlyList<string> exclusions,
        string evidenceBoundary,
        string? intent = null,
        CancellationToken cancellationToken = default)
    {
        await _repository.InitializeAsync(cancellationToken);

        var runId = $"RUN-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var run = new DiscoveryRun
        {
            RunId = runId,
            Subject = subject,
            SourceLocator = sourceLocator,
            SourceRevision = sourceRevision,
            Inclusions = inclusions,
            Exclusions = exclusions,
            EvidenceBoundary = evidenceBoundary,
            Intent = intent,
            Status = DiscoveryRunStatus.Declared,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await _repository.AppendRunAsync(run, cancellationToken);
        _logger.LogInformation("[Discovery] started run {RunId} for subject '{Subject}' at revision {Revision}", runId, subject, sourceRevision);
        return run;
    }

    public async Task<SourceArtifact> AppendArtifactAsync(
        string runId,
        string path,
        string language,
        string contentHash,
        long? sizeBytes = null,
        CancellationToken cancellationToken = default)
    {
        var artifact = new SourceArtifact
        {
            ArtifactId = NewId("ART"),
            RunId = runId,
            Path = path,
            Language = language,
            ContentHash = contentHash,
            SizeBytes = sizeBytes,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendArtifactAsync(artifact, cancellationToken);
        return artifact;
    }

    /// <summary>
    /// Appends an evidence record. Redaction is always applied to <paramref name="rawExcerpt"/>
    /// before persistence — the raw excerpt is never itself stored.
    /// </summary>
    public async Task<Evidence> AppendEvidenceAsync(
        string runId,
        EvidenceType type,
        string locator,
        string? artifactId = null,
        string? rawExcerpt = null,
        CancellationToken cancellationToken = default)
    {
        var redaction = RedactionGuard.Apply(rawExcerpt);

        var evidence = new Evidence
        {
            EvidenceId = NewId("EVD"),
            RunId = runId,
            Type = type,
            ArtifactId = artifactId,
            Locator = locator,
            RedactedExcerpt = redaction.SanitizedText.Length == 0 ? null : redaction.SanitizedText,
            WasRedacted = redaction.WasRedacted,
            RedactionSummary = redaction.Summary,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await _repository.AppendEvidenceAsync(evidence, cancellationToken);
        return evidence;
    }

    /// <summary>
    /// Appends a new candidate finding with its first revision. The finding statement is a
    /// deterministic fact; LLM output must never be passed here as the statement — only as an
    /// optional linked <see cref="LlmAssessment"/> via <see cref="AttachLlmAssessmentAsync"/>.
    /// </summary>
    public async Task<(Finding Finding, FindingRevision Revision)> AppendCandidateFindingAsync(
        string runId,
        string statement,
        IReadOnlyList<string> evidenceIds,
        double confidence,
        string producerVersion,
        string? correlationKey = null,
        CancellationToken cancellationToken = default)
    {
        var findingId = NewId("F");
        var finding = new Finding
        {
            FindingId = findingId,
            RunId = runId,
            CorrelationKey = correlationKey,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendFindingAsync(finding, cancellationToken);

        var provenance = new Provenance
        {
            ProvenanceId = NewId("PROV"),
            RunId = runId,
            ProducerKind = "DeterministicExtractor",
            ProducerVersion = producerVersion,
            InputRecordIds = evidenceIds,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendProvenanceAsync(provenance, cancellationToken);

        var revision = new FindingRevision
        {
            FindingRevisionId = $"{findingId}-R1",
            FindingId = findingId,
            RunId = runId,
            RevisionNumber = 1,
            Statement = statement,
            EvidenceIds = evidenceIds,
            Confidence = confidence,
            Status = ReviewStatus.Candidate,
            ProvenanceId = provenance.ProvenanceId,
            SupersedesRevisionId = null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendFindingRevisionAsync(revision, cancellationToken);

        return (finding, revision);
    }

    /// <summary>
    /// Attaches a non-authoritative LLM review-priority assessment to a candidate revision and
    /// transitions it to HumanReview. The assessment can never publish or reject on its own.
    /// </summary>
    public async Task<LlmAssessment> AttachLlmAssessmentAsync(
        string findingRevisionId,
        double reviewPriority,
        string whyExplanation,
        IReadOnlyList<string> citedEvidenceIds,
        string modelId,
        string? conflictsOrUnknowns = null,
        CancellationToken cancellationToken = default)
    {
        var revision = await _repository.GetFindingRevisionAsync(findingRevisionId, cancellationToken)
            ?? throw new InvalidOperationException($"Finding revision not found: {findingRevisionId}");

        var provenance = new Provenance
        {
            ProvenanceId = NewId("PROV"),
            RunId = revision.RunId,
            ProducerKind = "LlmSynthesis",
            ProducerVersion = modelId,
            InputRecordIds = citedEvidenceIds,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendProvenanceAsync(provenance, cancellationToken);

        var assessment = new LlmAssessment
        {
            LlmAssessmentId = NewId("LLM"),
            RunId = revision.RunId,
            FindingRevisionId = findingRevisionId,
            ReviewPriority = reviewPriority,
            WhyExplanation = whyExplanation,
            CitedEvidenceIds = citedEvidenceIds,
            ConflictsOrUnknowns = conflictsOrUnknowns,
            ModelId = modelId,
            ProvenanceId = provenance.ProvenanceId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendLlmAssessmentAsync(assessment, cancellationToken);

        if (revision.Status == ReviewStatus.Candidate)
        {
            await AppendStatusOnlyRevisionAsync(revision, ReviewStatus.HumanReview, cancellationToken);
        }

        return assessment;
    }

    /// <summary>
    /// Ensures a candidate is queued for review even without an LLM assessment attached.
    /// </summary>
    public async Task SubmitForReviewAsync(string findingRevisionId, CancellationToken cancellationToken = default)
    {
        var revision = await _repository.GetFindingRevisionAsync(findingRevisionId, cancellationToken)
            ?? throw new InvalidOperationException($"Finding revision not found: {findingRevisionId}");
        if (revision.Status == ReviewStatus.Candidate)
        {
            await AppendStatusOnlyRevisionAsync(revision, ReviewStatus.HumanReview, cancellationToken);
        }
    }

    public Task<ReviewDecision> PublishAsync(string findingRevisionId, string rationale, string? reviewerIdentity = null, CancellationToken cancellationToken = default)
        => RecordDecisionAsync(findingRevisionId, ReviewStatus.Published, rationale, reviewerIdentity, cancellationToken);

    public Task<ReviewDecision> RejectAsync(string findingRevisionId, string rationale, string? reviewerIdentity = null, CancellationToken cancellationToken = default)
        => RecordDecisionAsync(findingRevisionId, ReviewStatus.Rejected, rationale, reviewerIdentity, cancellationToken);

    public Task<ReviewDecision> RequestEvidenceAsync(string findingRevisionId, string rationale, string? reviewerIdentity = null, CancellationToken cancellationToken = default)
        => RecordDecisionAsync(findingRevisionId, ReviewStatus.NeedsEvidence, rationale, reviewerIdentity, cancellationToken);

    private async Task<ReviewDecision> RecordDecisionAsync(
        string findingRevisionId,
        ReviewStatus decisionStatus,
        string rationale,
        string? reviewerIdentity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rationale))
        {
            throw new ArgumentException("A rationale is required for every review decision.", nameof(rationale));
        }

        var revision = await _repository.GetFindingRevisionAsync(findingRevisionId, cancellationToken)
            ?? throw new InvalidOperationException($"Finding revision not found: {findingRevisionId}");

        if (revision.Status is ReviewStatus.Published or ReviewStatus.Rejected or ReviewStatus.Superseded)
        {
            throw new InvalidOperationException(
                $"Finding revision {findingRevisionId} is already in a terminal status ({revision.Status}); " +
                "a correction must be submitted as a new superseding revision instead.");
        }

        var decision = new ReviewDecision
        {
            ReviewDecisionId = NewId("REV"),
            RunId = revision.RunId,
            FindingRevisionId = findingRevisionId,
            ReviewerIdentity = string.IsNullOrWhiteSpace(reviewerIdentity) ? DefaultReviewerIdentity : reviewerIdentity,
            Decision = decisionStatus,
            Rationale = rationale,
            DecidedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendReviewDecisionAsync(decision, cancellationToken);

        // The decision is itself append-only history; reflect the resulting status as a new
        // revision row so GetLatestFindingRevisionAsync always returns the current status without
        // ever mutating the row the decision was made against.
        await AppendStatusOnlyRevisionAsync(revision, decisionStatus, cancellationToken);

        _logger.LogInformation(
            "[Discovery] reviewer '{Reviewer}' recorded decision {Decision} for {FindingRevisionId}: {Rationale}",
            decision.ReviewerIdentity, decisionStatus, findingRevisionId, rationale);

        return decision;
    }

    /// <summary>
    /// Creates a superseding revision for a finding whose published statement needs correction.
    /// The prior revision is marked Superseded (via an appended status-only revision is not used
    /// here — instead the new revision links back via <see cref="FindingRevision.SupersedesRevisionId"/>
    /// and starts life as a new Candidate for re-review). History is never overwritten.
    /// </summary>
    public async Task<FindingRevision> CreateCorrectionAsync(
        string findingId,
        string newStatement,
        IReadOnlyList<string> evidenceIds,
        double confidence,
        string producerVersion,
        CancellationToken cancellationToken = default)
    {
        var latest = await _repository.GetLatestFindingRevisionAsync(findingId, cancellationToken)
            ?? throw new InvalidOperationException($"Finding not found or has no revisions: {findingId}");

        var provenance = new Provenance
        {
            ProvenanceId = NewId("PROV"),
            RunId = latest.RunId,
            ProducerKind = "DeterministicExtractor",
            ProducerVersion = producerVersion,
            InputRecordIds = evidenceIds,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendProvenanceAsync(provenance, cancellationToken);

        var supersededMarkerRevisionId = latest.FindingRevisionId;

        // First append a status-only marker recording that the prior (typically Published)
        // revision is now superseded — this is itself a new append-only row, never an edit of
        // the original. The correction is then appended after it as the truly latest revision.
        if (latest.Status == ReviewStatus.Published)
        {
            await AppendStatusOnlyRevisionAsync(latest, ReviewStatus.Superseded, cancellationToken);
        }

        var priorLatest = await _repository.GetLatestFindingRevisionAsync(findingId, cancellationToken) ?? latest;
        var nextRevisionNumber = priorLatest.RevisionNumber + 1;
        var correction = new FindingRevision
        {
            FindingRevisionId = $"{findingId}-R{nextRevisionNumber}",
            FindingId = findingId,
            RunId = latest.RunId,
            RevisionNumber = nextRevisionNumber,
            Statement = newStatement,
            EvidenceIds = evidenceIds,
            Confidence = confidence,
            Status = ReviewStatus.Candidate,
            ProvenanceId = provenance.ProvenanceId,
            SupersedesRevisionId = supersededMarkerRevisionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendFindingRevisionAsync(correction, cancellationToken);

        _logger.LogInformation("[Discovery] correction {NewRevisionId} supersedes {PriorRevisionId} for finding {FindingId}", correction.FindingRevisionId, supersededMarkerRevisionId, findingId);
        return correction;
    }

    public Task<IReadOnlyList<FindingRevision>> GetReviewQueueAsync(string runId, CancellationToken cancellationToken = default)
        => _repository.GetReviewQueueAsync(runId, cancellationToken);

    public Task<LlmAssessment?> GetLlmAssessmentAsync(string findingRevisionId, CancellationToken cancellationToken = default)
        => _repository.GetLlmAssessmentForRevisionAsync(findingRevisionId, cancellationToken);

    public Task<IReadOnlyList<Evidence>> GetEvidenceByIdsAsync(IEnumerable<string> evidenceIds, CancellationToken cancellationToken = default)
        => _repository.GetEvidenceByIdsAsync(evidenceIds, cancellationToken);

    public Task<IReadOnlyList<ReviewDecision>> GetReviewDecisionsAsync(string findingRevisionId, CancellationToken cancellationToken = default)
        => _repository.GetReviewDecisionsAsync(findingRevisionId, cancellationToken);

    /// <summary>
    /// Appends a same-content revision row that only changes status, so status transitions are
    /// themselves append-only records rather than in-place updates on <c>finding_revisions</c>.
    /// </summary>
    private async Task AppendStatusOnlyRevisionAsync(
        FindingRevision current,
        ReviewStatus newStatus,
        CancellationToken cancellationToken,
        string? linkTo = null)
    {
        var latest = await _repository.GetLatestFindingRevisionAsync(current.FindingId, cancellationToken) ?? current;
        var next = new FindingRevision
        {
            FindingRevisionId = $"{current.FindingId}-R{latest.RevisionNumber + 1}",
            FindingId = current.FindingId,
            RunId = current.RunId,
            RevisionNumber = latest.RevisionNumber + 1,
            Statement = latest.Statement,
            EvidenceIds = latest.EvidenceIds,
            Confidence = latest.Confidence,
            Status = newStatus,
            ProvenanceId = latest.ProvenanceId,
            SupersedesRevisionId = linkTo ?? latest.FindingRevisionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendFindingRevisionAsync(next, cancellationToken);
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)].ToUpperInvariant();
}
