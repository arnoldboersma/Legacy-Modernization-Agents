using CobolToQuarkusMigration.Discovery.Models;

namespace CobolToQuarkusMigration.Discovery.Risks;

/// <summary>
/// A deterministic, not-yet-persisted risk candidate. <see cref="DiscoveryService.BuildRiskRegisterAsync"/>
/// appends evidence/provenance and persists these as governed <see cref="RiskRegisterEntry"/> rows.
/// </summary>
public sealed record RiskCandidate(
    string Title,
    RiskCategory Category,
    RiskSeverity Severity,
    string Description,
    string EscalationQuestion,
    string DerivationRule,
    string SourceRecordId,
    IReadOnlyList<string> EvidenceIds);

/// <summary>
/// Derives the risk register (design doc §6 section 9, issue #5) purely from already-persisted
/// Discovery Factory records for a run — never from new analysis. Every entry traces to a real
/// governed record (a <see cref="ContextCandidate"/>, <see cref="Integration"/>,
/// <see cref="RoleAssignment"/>, or the run itself) or to a documented, fixed analyzer limitation
/// already recorded in design doc §14. No future-state/roadmap/modernization-recommendation
/// language is produced — only current-state gaps, conflicts, and escalation questions (design
/// doc §9 constraint, issue #5 acceptance criteria).
/// </summary>
public static class RiskRegisterBuilder
{
    /// <summary>
    /// Namespace/module segment names already documented in design doc §14 as a known,
    /// deferred generic-name-clustering limitation (e.g. the PlanBoard "Shared" cluster finding).
    /// This is a citation of that documented limitation, not a new clustering heuristic — it does
    /// not change context seeding, it only flags for the risk register that a candidate whose
    /// name matches one of these already-known-ambiguous terms deserves reviewer attention.
    /// </summary>
    private static readonly string[] GenericNamespaceSegments =
    {
        "Shared", "Common", "Core", "Utilities", "Infrastructure"
    };

    public static IReadOnlyList<RiskCandidate> Build(
        IReadOnlyList<ContextCandidate> contextCandidates,
        IReadOnlyList<Integration> integrations,
        IReadOnlyList<RoleAssignment> roleAssignments,
        IReadOnlyList<Finding> findings,
        string runId)
    {
        var risks = new List<RiskCandidate>();

        foreach (var candidate in contextCandidates.Where(c => c.Status == ReviewStatus.NeedsEvidence))
        {
            risks.Add(new RiskCandidate(
                Title: $"Unconfirmed context candidate: {candidate.Name}",
                Category: RiskCategory.UnconfirmedFinding,
                Severity: RiskSeverity.Medium,
                Description:
                    $"Candidate logical context '{candidate.Name}' ({candidate.Kind}) remains at " +
                    $"NeedsEvidence/Unconfirmed status (confidence {candidate.Confidence:0.00}, " +
                    $"seeding rule: {candidate.SeedingRule}). It has not been human-published and " +
                    "must not be treated as a confirmed domain boundary.",
                EscalationQuestion:
                    $"Does '{candidate.Name}' represent a genuine, cohesive logical context, or is " +
                    "it a low-cohesion/catch-all grouping that should be split or merged on review?",
                DerivationRule: "UnconfirmedContextCandidate",
                SourceRecordId: candidate.ContextCandidateId,
                EvidenceIds: candidate.EvidenceIds));

            if (GenericNamespaceSegments.Any(segment =>
                    candidate.Name.Equals(segment, StringComparison.OrdinalIgnoreCase) ||
                    candidate.Name.EndsWith("." + segment, StringComparison.OrdinalIgnoreCase)))
            {
                risks.Add(new RiskCandidate(
                    Title: $"Generic/cross-cutting namespace segment clustered as a context: {candidate.Name}",
                    Category: RiskCategory.AnalyzerLimitation,
                    Severity: RiskSeverity.Medium,
                    Description:
                        $"'{candidate.Name}' matches a generic/cross-cutting namespace segment name " +
                        "(e.g. Shared, Common, Core, Utilities, Infrastructure). Per design doc §14, " +
                        "the context seeder has no signal distinguishing a generic technical bucket " +
                        "from a genuine business capability of the same name, regardless of how " +
                        "strong its coupling/co-location score is. This is a documented, deferred " +
                        "limitation, not a per-candidate defect.",
                    EscalationQuestion:
                        $"Is '{candidate.Name}' a real bounded context, or a generic/cross-cutting " +
                        "bucket that should be excluded from the business-context handoff?",
                    DerivationRule: "GenericNamespaceClusteringLimitation",
                    SourceRecordId: candidate.ContextCandidateId,
                    EvidenceIds: candidate.EvidenceIds));
            }
        }

        foreach (var integration in integrations.Where(i => i.RequiresReview))
        {
            risks.Add(new RiskCandidate(
                Title: $"Integration requires review: {integration.Category} ({integration.LogicalTarget})",
                Category: RiskCategory.UnconfirmedFinding,
                Severity: integration.BlindSpots.Count > 0 ? RiskSeverity.Medium : RiskSeverity.Low,
                Description:
                    $"Integration '{integration.IntegrationId}' ({integration.Category}, " +
                    $"{integration.Classification}, rule: {integration.ClassificationRule}) is flagged " +
                    $"RequiresReview. Blind spots: {(integration.BlindSpots.Count == 0 ? "none recorded" : string.Join("; ", integration.BlindSpots))}.",
                EscalationQuestion:
                    "Does this integration's classification/direction/target hold under review, or " +
                    "does the flagged blind spot change its business-vs-platform classification?",
                DerivationRule: "IntegrationRequiresReview",
                SourceRecordId: integration.IntegrationId,
                EvidenceIds: integration.EvidenceIds));
        }

        foreach (var role in roleAssignments.Where(r => r.RequiresReview))
        {
            risks.Add(new RiskCandidate(
                Title: $"Role assignment requires review: {role.SymbolLocator}",
                Category: RiskCategory.UnconfirmedFinding,
                Severity: RiskSeverity.Low,
                Description:
                    $"Role assignment '{role.RoleAssignmentId}' for '{role.SymbolLocator}' " +
                    $"(roles: {string.Join(", ", role.Roles)}, rule: {role.ClassificationRule}) is " +
                    "flagged RequiresReview — either Unknown, or a mix of Business with a " +
                    "non-business role, or more than one distinct non-business role.",
                EscalationQuestion:
                    $"What is the correct role classification for '{role.SymbolLocator}', and does " +
                    "it belong in business-use-case analysis?",
                DerivationRule: "RoleRequiresReview",
                SourceRecordId: role.RoleAssignmentId,
                EvidenceIds: new[] { role.RoleAssignmentId }));
        }

        // Fixed, run-scoped analyzer-limitation entries, each citing a design doc §14 finding that
        // applies to every run using the current deterministic extractors. Idempotent by
        // construction: DerivationRule + the run's own RunId is the natural key.
        risks.Add(new RiskCandidate(
            Title: "Syntax-tree-only analysis: no cross-file symbol resolution",
            Category: RiskCategory.AnalyzerLimitation,
            Severity: RiskSeverity.Medium,
            Description:
                "Role classification, dependency graph, and context seeding (design doc §14) parse " +
                "each .cs file with the Roslyn CSharpSyntaxTree API only — no CSharpCompilation/" +
                "SemanticModel is built. Rules match syntactic shape (base type names, attribute " +
                "names, invocation names, using directives, file path) rather than resolved symbols, " +
                "so two distinct types sharing an identifier in different namespaces can be " +
                "conflated (the same risk class as the generic-namespace-clustering limitation). " +
                "Full semantic/compilation-based analysis remains deferred per §12.",
            EscalationQuestion:
                "For subjects where the syntax-tree-only proxy has produced ambiguous results, is " +
                "full semantic (CSharpCompilation) analysis warranted before publication?",
            DerivationRule: "SyntaxTreeOnlyAnalysisLimitation",
            SourceRecordId: runId,
            EvidenceIds: Array.Empty<string>()));

        if (integrations.Any(i => i.ClassificationRule.Contains("BackgroundServiceBaseTypeWithPathBasedDeliverySplit", StringComparison.Ordinal)))
        {
            risks.Add(new RiskCandidate(
                Title: "Background-worker delivery-vs-runtime split is a path-name heuristic",
                Category: RiskCategory.AnalyzerLimitation,
                Severity: RiskSeverity.Low,
                Description:
                    "Design doc §14: a BackgroundService/IHostedService type is classified Delivery " +
                    "instead of RuntimeApplication only when its file path contains a deploy/" +
                    "migration-shaped project segment (e.g. *.DatabaseMigration). A differently-named " +
                    "migration-runner project would not be caught, and a business worker whose path " +
                    "happens to contain 'Migration' for unrelated reasons could be misclassified.",
                EscalationQuestion:
                    "Do any background workers in this run's Delivery classification actually " +
                    "represent business runtime behavior, or vice versa?",
                DerivationRule: "BackgroundWorkerPathHeuristicLimitation",
                SourceRecordId: runId,
                EvidenceIds: Array.Empty<string>()));
        }

        // Sections 3/4 (use cases; business rules, policies, calculations, state transitions) have
        // no dedicated extractor in Phases 1-6. This entry is always emitted so the handoff is
        // explicit about the gap rather than silently rendering empty sections (issue #5 task
        // framing). It is only satisfied if a Finding's statement already carries use-case- or
        // rule-shaped content, which no Phase 1-6 producer creates today.
        var hasUseCaseOrRuleFindings = findings.Any(f =>
            f.CorrelationKey?.Contains("UseCase", StringComparison.OrdinalIgnoreCase) == true ||
            f.CorrelationKey?.Contains("BusinessRule", StringComparison.OrdinalIgnoreCase) == true);
        if (!hasUseCaseOrRuleFindings)
        {
            risks.Add(new RiskCandidate(
                Title: "No use-case or business-rule extraction has run for this subject",
                Category: RiskCategory.EvidenceGap,
                Severity: RiskSeverity.High,
                Description:
                    "Phases 1-6 deterministically extract runs/artifacts/evidence, roles, the " +
                    "dependency graph, candidate contexts, and integrations, but no phase extracts " +
                    "use cases/functional flows (design doc §6 section 3) or business " +
                    "rules/policies/calculations/state transitions (§6 section 4). Those handoff " +
                    "sections are therefore necessarily empty/sparse for this run rather than " +
                    "populated from real evidence; they must not be filled in from reviewer " +
                    "knowledge or invented analysis.",
                EscalationQuestion:
                    "Should a dedicated use-case/business-rule extraction phase be scoped as future " +
                    "work, and if so, against which evidence sources (source, logs, tests)?",
                DerivationRule: "UseCaseBusinessRuleExtractionGap",
                SourceRecordId: runId,
                EvidenceIds: Array.Empty<string>()));
        }

        return risks;
    }
}
