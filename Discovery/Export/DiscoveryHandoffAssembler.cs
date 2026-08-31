using System.Globalization;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;

namespace CobolToQuarkusMigration.Discovery.Export;

/// <summary>
/// Read-only 10-section Specification Factory handoff document (design doc §6, issue #5).
/// Every section is assembled from already-persisted governed records for a single run — no new
/// analysis is performed here, and no future-state/roadmap/modernization-recommendation content is
/// produced (design doc §9). Sections 3 and 4 (use cases; business rules/policies) render as
/// explicitly empty/sparse when no dedicated extractor has produced use-case- or business-rule-
/// shaped <see cref="Finding"/> records, per the issue #5 task framing — never fabricated.
/// </summary>
public sealed record HandoffDocument(
    string ContractVersion,
    string RunId,
    string Subject,
    DateTimeOffset AssembledAtUtc,
    HandoffSection1_RunScope Section1_RunScope,
    HandoffSection2_DomainLandscape Section2_DomainLandscape,
    HandoffSection3_UseCases Section3_UseCases,
    HandoffSection4_BusinessRules Section4_BusinessRules,
    HandoffSection5_Architecture Section5_Architecture,
    HandoffSection6_Integrations Section6_Integrations,
    HandoffSection7_DataModel Section7_DataModel,
    HandoffSection8_NonFunctional Section8_NonFunctional,
    HandoffSection9_Risks Section9_Risks,
    HandoffSection10_Navigation Section10_Navigation);

public sealed record HandoffSection1_RunScope(
    string RunId,
    string Subject,
    string SourceLocator,
    string SourceRevision,
    IReadOnlyList<string> Inclusions,
    IReadOnlyList<string> Exclusions,
    string EvidenceBoundary,
    string RunStatus,
    string? SupersedesRunId,
    DateTimeOffset CreatedAtUtc);

public sealed record HandoffContextEntry(
    string ContextCandidateId,
    string Name,
    string Kind,
    string Status,
    double Confidence,
    string SeedingRule,
    IReadOnlyList<string> EvidenceIds);

public sealed record HandoffContextDependency(
    string FromContextCandidateId,
    string ToContextCandidateId,
    double Confidence,
    IReadOnlyList<string> EvidenceIds);

public sealed record HandoffSection2_DomainLandscape(
    IReadOnlyList<HandoffContextEntry> Contexts,
    IReadOnlyList<HandoffContextDependency> CrossContextDependencies);

public sealed record HandoffSection3_UseCases(
    bool IsSparse,
    string GapExplanation,
    string? LinkedRiskId,
    IReadOnlyList<string> UseCaseFindingIds);

public sealed record HandoffSection4_BusinessRules(
    bool IsSparse,
    string GapExplanation,
    string? LinkedRiskId,
    IReadOnlyList<string> BusinessRuleFindingIds);

public sealed record HandoffArchitectureNode(
    string NodeId,
    string Kind,
    string SymbolLocator,
    string DisplayName,
    string? OwningContextCandidateId,
    IReadOnlyList<string> EvidenceIds);

public sealed record HandoffGraphEdgeSummary(string FromNodeId, string ToNodeId, string Kind, double Confidence);

public sealed record HandoffRoleTrace(string SymbolLocator, IReadOnlyList<string> Roles, bool RequiresReview);

public sealed record HandoffSection5_Architecture(
    IReadOnlyList<HandoffArchitectureNode> Nodes,
    int EdgeCount,
    IReadOnlyList<HandoffGraphEdgeSummary> Edges,
    IReadOnlyList<HandoffRoleTrace> RoleTraceLocations);

public sealed record HandoffIntegrationEntry(
    string IntegrationId,
    string Category,
    string Classification,
    string Direction,
    string TriggerOrCaller,
    string ProtocolOrMechanism,
    string LogicalTarget,
    string? ConfigurationKeySemantics,
    string? AuthenticationSemantics,
    string? ReliabilityBehavior,
    string? OwningContextCandidateId,
    double Confidence,
    IReadOnlyList<string> BlindSpots,
    bool RequiresReview,
    IReadOnlyList<string> EvidenceIds);

public sealed record HandoffSection6_Integrations(IReadOnlyList<HandoffIntegrationEntry> Integrations);

public sealed record HandoffDataEntity(
    string NodeId,
    string SymbolLocator,
    string DisplayName,
    IReadOnlyList<string> EvidenceIds);

public sealed record HandoffSection7_DataModel(
    IReadOnlyList<HandoffDataEntity> Entities,
    IReadOnlyList<HandoffIntegrationEntry> DataStoreIntegrations);

public sealed record HandoffSection8_NonFunctional(
    IReadOnlyList<HandoffIntegrationEntry> PlatformIdentityIntegrations,
    IReadOnlyList<HandoffIntegrationEntry> ObservabilityIntegrations,
    IReadOnlyList<HandoffIntegrationEntry> DeliveryIntegrations);

public sealed record HandoffRiskEntry(
    string RiskId,
    string Title,
    string Category,
    string Severity,
    string Status,
    string Description,
    string EscalationQuestion,
    string DerivationRule,
    string SourceRecordId,
    IReadOnlyList<string> EvidenceIds);

public sealed record HandoffSection9_Risks(IReadOnlyList<HandoffRiskEntry> Risks);

public sealed record HandoffSection10_Navigation(
    int TotalArtifacts,
    int TotalEvidence,
    int TotalFindings,
    int TotalContextCandidates,
    int TotalIntegrations,
    int TotalRisks,
    IReadOnlyList<string> McpQueryOperationsReference);

/// <summary>
/// Assembles a <see cref="HandoffDocument"/> from already-persisted governed records for a run.
/// Read-only: never appends, mutates, or derives new records. The risk register itself must
/// already have been built via <see cref="DiscoveryService.BuildRiskRegisterAsync"/> before
/// calling this assembler; it does not build it implicitly.
/// </summary>
public sealed class DiscoveryHandoffAssembler
{
    /// <summary>
    /// Read-only MCP/API query operations described in design doc §8. Navigation reference only —
    /// this phase does not implement these endpoints (design doc §12).
    /// </summary>
    private static readonly string[] McpQueryOperationsReference =
    {
        "Find record — e.g. \"What is the use case for a forecast update?\"",
        "Trace flow — e.g. \"Which policies and data changes follow this command?\"",
        "Trace impact — e.g. \"What contexts and integrations are affected by this entity?\"",
        "List boundary — e.g. \"Which unconfirmed modules are related to Forecast Management?\"",
        "Retrieve evidence — e.g. \"Why is this rule considered published?\"",
        "List uncertainty — e.g. \"Which risks and gaps block a specification for this flow?\"",
    };

    private readonly IDiscoveryRepository _repository;

    public DiscoveryHandoffAssembler(IDiscoveryRepository repository)
    {
        _repository = repository;
    }

    public async Task<HandoffDocument> AssembleAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await _repository.GetRunAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException($"Run not found: {runId}");

        var artifacts = await _repository.GetArtifactsAsync(runId, cancellationToken);
        var evidence = await _repository.GetEvidenceAsync(runId, cancellationToken);
        var findings = await _repository.GetFindingsAsync(runId, cancellationToken);
        var contexts = await _repository.GetContextCandidatesAsync(runId, cancellationToken);
        var contextDependencyEdges = await _repository.GetContextDependencyEdgesAsync(runId, cancellationToken);
        var graphNodes = await _repository.GetGraphNodesAsync(runId, cancellationToken);
        var graphEdges = await _repository.GetGraphEdgesAsync(runId, cancellationToken);
        var contextMemberships = await _repository.GetContextMembershipsAsync(runId, cancellationToken);
        var roles = await _repository.GetRoleAssignmentsAsync(runId, cancellationToken);
        var integrations = await _repository.GetIntegrationsAsync(runId, cancellationToken);
        var risks = await _repository.GetRiskRegisterEntriesAsync(runId, cancellationToken);

        var contextByNodeId = contextMemberships
            .GroupBy(m => m.NodeId)
            .ToDictionary(g => g.Key, g => g.First().ContextCandidateId);

        var section1 = new HandoffSection1_RunScope(
            run.RunId,
            run.Subject,
            run.SourceLocator,
            run.SourceRevision,
            run.Inclusions,
            run.Exclusions,
            run.EvidenceBoundary,
            run.Status.ToString(),
            run.SupersedesRunId,
            run.CreatedAtUtc);

        var section2 = new HandoffSection2_DomainLandscape(
            contexts.Select(c => new HandoffContextEntry(
                c.ContextCandidateId, c.Name, c.Kind.ToString(), c.Status.ToString(), c.Confidence, c.SeedingRule, c.EvidenceIds)).ToList(),
            contextDependencyEdges.Select(e => new HandoffContextDependency(e.FromContextCandidateId, e.ToContextCandidateId, e.Confidence, e.EvidenceIds)).ToList());

        var useCaseFindingIds = findings
            .Where(f => f.CorrelationKey?.Contains("UseCase", StringComparison.OrdinalIgnoreCase) == true)
            .Select(f => f.FindingId).ToList();
        var useCaseGapRisk = risks.FirstOrDefault(r => r.DerivationRule == "UseCaseBusinessRuleExtractionGap");
        var section3 = new HandoffSection3_UseCases(
            IsSparse: useCaseFindingIds.Count == 0,
            GapExplanation: useCaseFindingIds.Count == 0
                ? "No use-case/functional-flow extractor has run for this subject in Phases 1-6; this section has no supporting records. See the linked risk register entry."
                : "Populated from existing use-case-shaped finding records.",
            LinkedRiskId: useCaseFindingIds.Count == 0 ? useCaseGapRisk?.RiskId : null,
            UseCaseFindingIds: useCaseFindingIds);

        var businessRuleFindingIds = findings
            .Where(f => f.CorrelationKey?.Contains("BusinessRule", StringComparison.OrdinalIgnoreCase) == true)
            .Select(f => f.FindingId).ToList();
        var section4 = new HandoffSection4_BusinessRules(
            IsSparse: businessRuleFindingIds.Count == 0,
            GapExplanation: businessRuleFindingIds.Count == 0
                ? "No business-rule/policy/state-transition extractor has run for this subject in Phases 1-6; this section has no supporting records. See the linked risk register entry."
                : "Populated from existing business-rule-shaped finding records.",
            LinkedRiskId: businessRuleFindingIds.Count == 0 ? useCaseGapRisk?.RiskId : null,
            BusinessRuleFindingIds: businessRuleFindingIds);

        var section5 = new HandoffSection5_Architecture(
            graphNodes.Select(n => new HandoffArchitectureNode(
                n.NodeId, n.Kind.ToString(), n.SymbolLocator, n.DisplayName,
                contextByNodeId.TryGetValue(n.NodeId, out var ctxId) ? ctxId : null,
                n.EvidenceIds)).ToList(),
            graphEdges.Count,
            graphEdges.Select(e => new HandoffGraphEdgeSummary(e.FromNodeId, e.ToNodeId, e.Kind.ToString(), e.Confidence)).ToList(),
            roles.Select(r => new HandoffRoleTrace(r.SymbolLocator, r.Roles.Select(x => x.ToString()).ToList(), r.RequiresReview)).ToList());

        var integrationEntries = integrations.Select(ToHandoffIntegrationEntry).ToList();
        var section6 = new HandoffSection6_Integrations(integrationEntries);

        var dataEntities = graphNodes.Where(n => n.Kind == GraphNodeKind.DbEntity || n.Kind == GraphNodeKind.DbContext)
            .Select(n => new HandoffDataEntity(n.NodeId, n.SymbolLocator, n.DisplayName, n.EvidenceIds)).ToList();
        var dataStoreIntegrations = integrationEntries.Where(i => i.Category == IntegrationCategory.DatabaseOrSharedStore.ToString()).ToList();
        var section7 = new HandoffSection7_DataModel(dataEntities, dataStoreIntegrations);

        var section8 = new HandoffSection8_NonFunctional(
            integrationEntries.Where(i => i.Classification == IntegrationClassification.PlatformIdentity.ToString()).ToList(),
            integrationEntries.Where(i => i.Classification == IntegrationClassification.Observability.ToString()).ToList(),
            integrationEntries.Where(i => i.Classification == IntegrationClassification.Delivery.ToString()).ToList());

        var section9 = new HandoffSection9_Risks(
            risks.Where(r => r.Status != RiskStatus.Superseded).Select(r => new HandoffRiskEntry(
                r.RiskId, r.Title, r.Category.ToString(), r.Severity.ToString(), r.Status.ToString(),
                r.Description, r.EscalationQuestion, r.DerivationRule, r.SourceRecordId, r.EvidenceIds)).ToList());

        var section10 = new HandoffSection10_Navigation(
            artifacts.Count, evidence.Count, findings.Count, contexts.Count, integrations.Count,
            risks.Count(r => r.Status != RiskStatus.Superseded),
            McpQueryOperationsReference);

        return new HandoffDocument(
            DiscoveryExporter.ContractVersion,
            run.RunId,
            run.Subject,
            DateTimeOffset.UtcNow,
            section1, section2, section3, section4, section5, section6, section7, section8, section9, section10);
    }

    private static HandoffIntegrationEntry ToHandoffIntegrationEntry(Integration i) => new(
        i.IntegrationId, i.Category.ToString(), i.Classification.ToString(), i.Direction.ToString(),
        i.TriggerOrCaller, i.ProtocolOrMechanism, i.LogicalTarget, i.ConfigurationKeySemantics,
        i.AuthenticationSemantics, i.ReliabilityBehavior, i.OwningContextCandidateId, i.Confidence,
        i.BlindSpots, i.RequiresReview, i.EvidenceIds);
}
