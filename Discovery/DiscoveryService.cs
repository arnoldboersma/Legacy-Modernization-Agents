using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
using CobolToQuarkusMigration.Discovery.Redaction;
using CobolToQuarkusMigration.Discovery.Roles;
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
    /// Runs the deterministic <see cref="ArtifactRoleClassifier"/> over one project's source
    /// files, appending an evidence-backed <see cref="RoleAssignment"/> (and its citing evidence
    /// records) per classified symbol. Design doc §5, §5.1, issue #8: multiple role tags may
    /// co-occur, and mixed/unknown results are marked <see cref="RoleAssignment.RequiresReview"/>
    /// rather than collapsed to a single guessed role.
    /// </summary>
    public async Task<IReadOnlyList<RoleAssignment>> ClassifyArtifactRolesAsync(
        string runId,
        IReadOnlyList<(SourceArtifact Artifact, string SourceText)> artifacts,
        string producerVersion,
        CancellationToken cancellationToken = default)
    {
        var files = artifacts.Select(a => new ClassifierSourceFile(a.Artifact.Path, a.SourceText)).ToList();
        var classifications = ArtifactRoleClassifier.ClassifyProject(files);

        var artifactsByPath = artifacts.ToDictionary(a => a.Artifact.Path.Replace('\\', '/'), a => a.Artifact);
        var results = new List<RoleAssignment>();

        foreach (var classification in classifications)
        {
            // The classifier's symbol locator is namespace-qualified for types, or the raw path
            // for whole-file classifications (top-level statements, build-tooling files); resolve
            // back to the originating artifact by matching path prefix.
            var artifact = artifactsByPath.Values.FirstOrDefault(a =>
                classification.SymbolLocator.Equals(a.Path.Replace('\\', '/'), StringComparison.Ordinal) ||
                classification.Citations.Any(c => c.Locator.StartsWith(a.Path.Replace('\\', '/') + ":", StringComparison.Ordinal)));
            if (artifact is null)
            {
                continue;
            }

            var evidenceIds = new List<string>();
            foreach (var citation in classification.Citations)
            {
                var evidence = await AppendEvidenceAsync(
                    runId,
                    EvidenceType.SourceCode,
                    citation.Locator,
                    artifact.ArtifactId,
                    rawExcerpt: $"[{citation.RuleName}] {citation.Excerpt}",
                    cancellationToken);
                evidenceIds.Add(evidence.EvidenceId);
            }

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

            var assignment = new RoleAssignment
            {
                RoleAssignmentId = NewId("ROLE"),
                RunId = runId,
                ArtifactId = artifact.ArtifactId,
                SymbolLocator = classification.SymbolLocator,
                Roles = classification.Roles,
                Confidence = classification.Confidence,
                EvidenceIds = evidenceIds,
                ClassificationRule = classification.ClassificationRule,
                RequiresReview = classification.RequiresReview,
                ProvenanceId = provenance.ProvenanceId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            await _repository.AppendRoleAssignmentAsync(assignment, cancellationToken);
            results.Add(assignment);
        }

        return results;
    }

    public Task<IReadOnlyList<RoleAssignment>> GetRoleAssignmentsAsync(string runId, CancellationToken cancellationToken = default)
        => _repository.GetRoleAssignmentsAsync(runId, cancellationToken);

    // ---------------------------------------------------------------------------------------
    // Discovery Factory phase 5 (issue #3): dependency graph construction and candidate context
    // seeding. Never invents a fact: every node/edge/candidate below is produced by a
    // deterministic extractor (Discovery/Graph) and cites its own evidence.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Persists a set of already-extracted graph node/edge candidates (produced by the caller,
    /// typically via <see cref="Graph.DependencyGraphBuilder"/>, from artifact source text) with
    /// cited evidence and provenance. Node de-duplication is by symbol locator within the run: a
    /// symbol already appended as a node in this run is not re-appended.
    /// </summary>
    public async Task<(IReadOnlyList<DependencyGraphNode> Nodes, IReadOnlyList<DependencyGraphEdge> Edges)> AppendDependencyGraphAsync(
        string runId,
        IReadOnlyList<(Graph.GraphNodeCandidate Candidate, string? ArtifactId)> nodeCandidates,
        IReadOnlyList<Graph.GraphEdgeCandidate> edgeCandidates,
        string producerVersion,
        CancellationToken cancellationToken = default)
    {
        var existingNodes = await _repository.GetGraphNodesAsync(runId, cancellationToken);
        var nodesBySymbol = existingNodes.ToDictionary(n => n.SymbolLocator, StringComparer.Ordinal);
        var appendedNodes = new List<DependencyGraphNode>();

        foreach (var (candidate, artifactId) in nodeCandidates)
        {
            if (nodesBySymbol.ContainsKey(candidate.SymbolLocator))
            {
                continue;
            }

            var evidenceIds = new List<string>();
            foreach (var citation in candidate.Citations)
            {
                var evidence = await AppendEvidenceAsync(runId, EvidenceType.SourceCode, citation.Locator, artifactId,
                    rawExcerpt: $"[{citation.RuleName}] {citation.Excerpt}", cancellationToken);
                evidenceIds.Add(evidence.EvidenceId);
            }

            var node = new DependencyGraphNode
            {
                NodeId = NewId("GNODE"),
                RunId = runId,
                Kind = candidate.Kind,
                SymbolLocator = candidate.SymbolLocator,
                DisplayName = candidate.DisplayName,
                ArtifactId = artifactId,
                EvidenceIds = evidenceIds,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            await _repository.AppendGraphNodeAsync(node, cancellationToken);
            nodesBySymbol[node.SymbolLocator] = node;
            appendedNodes.Add(node);
        }

        var appendedEdges = new List<DependencyGraphEdge>();
        foreach (var candidate in edgeCandidates)
        {
            if (!nodesBySymbol.TryGetValue(candidate.FromSymbolLocator, out var fromNode) ||
                !nodesBySymbol.TryGetValue(candidate.ToSymbolLocator, out var toNode))
            {
                // Edge references a symbol with no corresponding node in this run (e.g. an
                // external/unresolved type): skip rather than fabricate a placeholder node.
                continue;
            }

            var evidenceIds = new List<string>();
            foreach (var citation in candidate.Citations)
            {
                var evidence = await AppendEvidenceAsync(runId, EvidenceType.SourceCode, citation.Locator, fromNode.ArtifactId,
                    rawExcerpt: $"[{citation.RuleName}] {citation.Excerpt}", cancellationToken);
                evidenceIds.Add(evidence.EvidenceId);
            }

            var edge = new DependencyGraphEdge
            {
                EdgeId = NewId("GEDGE"),
                RunId = runId,
                FromNodeId = fromNode.NodeId,
                ToNodeId = toNode.NodeId,
                Kind = candidate.Kind,
                EvidenceIds = evidenceIds,
                Confidence = candidate.Confidence,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            await _repository.AppendGraphEdgeAsync(edge, cancellationToken);
            appendedEdges.Add(edge);
        }

        var provenance = new Provenance
        {
            ProvenanceId = NewId("PROV"),
            RunId = runId,
            ProducerKind = "DeterministicExtractor",
            ProducerVersion = producerVersion,
            InputRecordIds = appendedNodes.Select(n => n.NodeId).Concat(appendedEdges.Select(e => e.EdgeId)).ToList(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendProvenanceAsync(provenance, cancellationToken);

        return (appendedNodes, appendedEdges);
    }

    public Task<IReadOnlyList<DependencyGraphNode>> GetGraphNodesAsync(string runId, CancellationToken cancellationToken = default)
        => _repository.GetGraphNodesAsync(runId, cancellationToken);

    public Task<IReadOnlyList<DependencyGraphEdge>> GetGraphEdgesAsync(string runId, CancellationToken cancellationToken = default)
        => _repository.GetGraphEdgesAsync(runId, cancellationToken);

    /// <summary>
    /// Runs <see cref="Graph.ContextSeeder"/> over the run's persisted graph and Phase 4 role
    /// assignments, appending candidate contexts, memberships, and cross-context dependency
    /// edges. Every candidate starts life as <see cref="ReviewStatus.Candidate"/> (Plausible) or
    /// <see cref="ReviewStatus.NeedsEvidence"/> (Unconfirmed); this method never assigns
    /// <see cref="ReviewStatus.Published"/> — only a human reviewer decision (design doc §5.4)
    /// can confirm a candidate, mirroring the finding-review lifecycle.
    /// </summary>
    public async Task<IReadOnlyList<ContextCandidate>> SeedContextCandidatesAsync(
        string runId,
        string producerVersion,
        CancellationToken cancellationToken = default)
    {
        var nodes = await _repository.GetGraphNodesAsync(runId, cancellationToken);
        var edges = await _repository.GetGraphEdgesAsync(runId, cancellationToken);
        var roleAssignments = await _repository.GetRoleAssignmentsAsync(runId, cancellationToken);

        var seedResult = Graph.ContextSeeder.SeedCandidates(nodes, edges, roleAssignments);

        var nodesById = nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        var edgesById = edges.ToDictionary(e => e.EdgeId, StringComparer.Ordinal);
        var candidatesByName = new Dictionary<string, ContextCandidate>(StringComparer.Ordinal);
        var results = new List<ContextCandidate>();

        foreach (var seed in seedResult.Candidates)
        {
            var evidenceIds = new List<string>();
            foreach (var edgeId in seed.EvidenceGraphEdgeIds)
            {
                if (edgesById.TryGetValue(edgeId, out var edge))
                {
                    evidenceIds.AddRange(edge.EvidenceIds);
                }
            }

            // Every candidate must carry stable citations even when its member nodes have no
            // touching edges (e.g. a singleton, edge-free cluster): fall back to the member
            // nodes' own evidence (their originating TypeDeclaration/etc. citations).
            foreach (var nodeId in seed.MemberNodeIds)
            {
                if (nodesById.TryGetValue(nodeId, out var node))
                {
                    evidenceIds.AddRange(node.EvidenceIds);
                }
            }

            evidenceIds = evidenceIds.Distinct().ToList();

            var provenance = new Provenance
            {
                ProvenanceId = NewId("PROV"),
                RunId = runId,
                ProducerKind = "DeterministicExtractor",
                ProducerVersion = producerVersion,
                InputRecordIds = seed.EvidenceGraphEdgeIds,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            await _repository.AppendProvenanceAsync(provenance, cancellationToken);

            // Low-confidence, low-signal candidates are recorded as NeedsEvidence (Unconfirmed)
            // rather than Candidate (Plausible) — mixed/ambiguous boundaries stay visibly
            // uncertain instead of being forced into a confident-looking grouping.
            var status = seed.Confidence >= 0.55 ? ReviewStatus.Candidate : ReviewStatus.NeedsEvidence;

            var candidate = new ContextCandidate
            {
                ContextCandidateId = NewId("CTX"),
                RunId = runId,
                Name = seed.Name,
                Kind = seed.Kind,
                Status = status,
                Confidence = seed.Confidence,
                EvidenceIds = evidenceIds,
                SeedingRule = seed.SeedingRule,
                ProvenanceId = provenance.ProvenanceId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            await _repository.AppendContextCandidateAsync(candidate, cancellationToken);
            candidatesByName[seed.Name] = candidate;
            results.Add(candidate);

            var ownerSet = seed.OwnerNodeIds.ToHashSet(StringComparer.Ordinal);
            foreach (var nodeId in seed.MemberNodeIds)
            {
                var membership = new ContextMembership
                {
                    ContextMembershipId = NewId("CMEM"),
                    RunId = runId,
                    ContextCandidateId = candidate.ContextCandidateId,
                    NodeId = nodeId,
                    Role = ownerSet.Contains(nodeId) ? ContextMembershipRole.Owner : ContextMembershipRole.Member,
                    EvidenceIds = evidenceIds,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };
                await _repository.AppendContextMembershipAsync(membership, cancellationToken);
            }
        }

        foreach (var dependency in seedResult.Dependencies)
        {
            if (!candidatesByName.TryGetValue(dependency.FromCandidateName, out var fromCandidate) ||
                !candidatesByName.TryGetValue(dependency.ToCandidateName, out var toCandidate))
            {
                continue;
            }

            var dependencyEvidenceIds = dependency.EvidenceGraphEdgeIds
                .SelectMany(id => edgesById.TryGetValue(id, out var e) ? e.EvidenceIds : Array.Empty<string>())
                .Distinct()
                .ToList();

            var edge = new ContextDependencyEdge
            {
                ContextDependencyEdgeId = NewId("CDEP"),
                RunId = runId,
                FromContextCandidateId = fromCandidate.ContextCandidateId,
                ToContextCandidateId = toCandidate.ContextCandidateId,
                EvidenceIds = dependencyEvidenceIds,
                Confidence = dependency.Confidence,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            await _repository.AppendContextDependencyEdgeAsync(edge, cancellationToken);
        }

        return results;
    }

    public Task<IReadOnlyList<ContextCandidate>> GetContextCandidatesAsync(string runId, CancellationToken cancellationToken = default)
        => _repository.GetContextCandidatesAsync(runId, cancellationToken);

    public Task<IReadOnlyList<ContextMembership>> GetContextMembershipsForContextAsync(string contextCandidateId, CancellationToken cancellationToken = default)
        => _repository.GetContextMembershipsForContextAsync(contextCandidateId, cancellationToken);

    public Task<IReadOnlyList<ContextDependencyEdge>> GetContextDependencyEdgesAsync(string runId, CancellationToken cancellationToken = default)
        => _repository.GetContextDependencyEdgesAsync(runId, cancellationToken);

    /// <summary>
    /// Returns only the role assignments eligible to seed LLM business-use-case prompts by
    /// default (design doc §5.4): assignments whose roles are exclusively <see cref="ArtifactRoleTag.Business"/>
    /// and/or <see cref="ArtifactRoleTag.Shared"/>, with <see cref="RoleAssignment.RequiresReview"/>
    /// false. Mixed, unknown, and purely-technical (DI/middleware/persistence/etc.) assignments
    /// are excluded here but remain fully visible via <see cref="GetRoleAssignmentsAsync"/> for
    /// reviewer inspection — they are never silently dropped from the governed record set.
    /// </summary>
    public async Task<IReadOnlyList<RoleAssignment>> GetBusinessEligibleArtifactsAsync(string runId, CancellationToken cancellationToken = default)
    {
        var assignments = await _repository.GetRoleAssignmentsAsync(runId, cancellationToken);
        return assignments
            .Where(a => !a.RequiresReview && a.Roles.All(r => r is ArtifactRoleTag.Business or ArtifactRoleTag.Shared))
            .ToList();
    }

    /// <summary>
    /// Appends a same-content revision row that only changes status, so status transitions are
    /// themselves append-only records rather than in-place updates on <c>finding_revisions</c>.
    /// </summary>
    // ---------------------------------------------------------------------------------------
    // Discovery Factory phase 6 (issue #6): integration inventory and topology links. Reuses
    // Phase 5 graph nodes/context memberships as an input signal for owning-context resolution
    // rather than re-deriving context boundaries.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Persists a set of already-extracted integration candidates (produced by the caller via
    /// <see cref="Integrations.IntegrationClassifier"/>) with cited evidence and provenance.
    /// When <paramref name="artifactId"/> resolves to a graph <see cref="DependencyGraphNode"/>
    /// declared in the same artifact that already has a context membership, the integration is
    /// linked to that owning <see cref="ContextCandidate"/> (best-effort; unresolved integrations
    /// remain unlinked rather than being force-fit into a fallback context).
    /// </summary>
    public async Task<IReadOnlyList<Integration>> AppendIntegrationsAsync(
        string runId,
        IReadOnlyList<(Integrations.IntegrationCandidate Candidate, string? ArtifactId)> candidates,
        string producerVersion,
        CancellationToken cancellationToken = default)
    {
        var graphNodes = await _repository.GetGraphNodesAsync(runId, cancellationToken);
        var nodesByArtifact = graphNodes.Where(n => n.ArtifactId is not null)
            .GroupBy(n => n.ArtifactId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var memberships = await _repository.GetContextMembershipsAsync(runId, cancellationToken);
        var contextByNodeId = memberships
            .GroupBy(m => m.NodeId)
            .ToDictionary(g => g.Key, g => g.First().ContextCandidateId);

        var results = new List<Integration>();

        foreach (var (candidate, artifactId) in candidates)
        {
            var evidenceIds = new List<string>();
            foreach (var citation in candidate.Citations)
            {
                var evidence = await AppendEvidenceAsync(runId, EvidenceType.SourceCode, citation.Locator, artifactId,
                    rawExcerpt: $"[{citation.RuleName}] {citation.Excerpt}", cancellationToken);
                evidenceIds.Add(evidence.EvidenceId);
            }

            string? owningContextCandidateId = null;
            if (artifactId is not null && nodesByArtifact.TryGetValue(artifactId, out var nodesForArtifact))
            {
                foreach (var node in nodesForArtifact)
                {
                    if (contextByNodeId.TryGetValue(node.NodeId, out var contextId))
                    {
                        owningContextCandidateId = contextId;
                        break;
                    }
                }
            }

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

            var integration = new Integration
            {
                IntegrationId = NewId("INTG"),
                RunId = runId,
                Category = candidate.Category,
                Classification = candidate.Classification,
                Direction = candidate.Direction,
                TriggerOrCaller = candidate.TriggerOrCaller,
                ProtocolOrMechanism = candidate.ProtocolOrMechanism,
                LogicalTarget = candidate.LogicalTarget,
                ConfigurationKeySemantics = candidate.ConfigurationKeySemantics,
                RedactedContractShape = candidate.RedactedContractShape,
                AuthenticationSemantics = candidate.AuthenticationSemantics,
                ReliabilityBehavior = candidate.ReliabilityBehavior,
                OwningContextCandidateId = owningContextCandidateId,
                EvidenceIds = evidenceIds,
                Confidence = candidate.Confidence,
                ClassificationRule = candidate.ClassificationRule,
                BlindSpots = candidate.BlindSpots,
                RequiresReview = candidate.RequiresReview,
                ProvenanceId = provenance.ProvenanceId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            await _repository.AppendIntegrationAsync(integration, cancellationToken);
            results.Add(integration);

            if (owningContextCandidateId is not null)
            {
                await _repository.AppendIntegrationLinkAsync(new IntegrationLink
                {
                    IntegrationLinkId = NewId("ILNK"),
                    RunId = runId,
                    IntegrationId = integration.IntegrationId,
                    LinkedRecordKind = IntegrationLinkedRecordKind.ContextCandidate,
                    LinkedRecordId = owningContextCandidateId,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                }, cancellationToken);
            }
        }

        return results;
    }

    public Task<IReadOnlyList<Integration>> GetIntegrationsAsync(string runId, CancellationToken cancellationToken = default)
        => _repository.GetIntegrationsAsync(runId, cancellationToken);

    public Task<IReadOnlyList<IntegrationLink>> GetIntegrationLinksAsync(string integrationId, CancellationToken cancellationToken = default)
        => _repository.GetIntegrationLinksAsync(integrationId, cancellationToken);

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
