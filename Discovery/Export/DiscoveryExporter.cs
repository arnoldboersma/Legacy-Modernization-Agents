using System.Text;
using System.Text.Json;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
namespace CobolToQuarkusMigration.Discovery.Export;

/// <summary>
/// Derives versioned Markdown and JSON handoff exports for a single reviewed finding revision.
/// Exports are a derived view of the governed record model, never the source of truth (design
/// doc §2, §6). Only finding revisions with a terminal reviewer decision (Published, Rejected,
/// or NeedsEvidence) may be exported.
/// </summary>
public sealed class DiscoveryExporter
{
    /// <summary>Semantic contract version for the export shape. Consumers reject unsupported majors.</summary>
    public const string ContractVersion = "1.1.0";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IDiscoveryRepository _repository;

    public DiscoveryExporter(IDiscoveryRepository repository)
    {
        _repository = repository;
    }

    public async Task<(string MarkdownPath, string JsonPath)> ExportFindingAsync(
        string findingRevisionId,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var revision = await _repository.GetFindingRevisionAsync(findingRevisionId, cancellationToken)
            ?? throw new InvalidOperationException($"Finding revision not found: {findingRevisionId}");

        if (revision.Status is not (ReviewStatus.Published or ReviewStatus.Rejected or ReviewStatus.NeedsEvidence or ReviewStatus.Superseded))
        {
            throw new InvalidOperationException(
                $"Finding revision {findingRevisionId} has status {revision.Status}; only reviewed findings (Published/Rejected/NeedsEvidence/Superseded) can be exported.");
        }

        var evidence = await _repository.GetEvidenceByIdsAsync(revision.EvidenceIds, cancellationToken);
        var llmAssessment = await _repository.GetLlmAssessmentForRevisionAsync(findingRevisionId, cancellationToken);

        // Role assignments are keyed by artifact, not by finding/evidence, so we resolve them via
        // the distinct artifact IDs referenced by this revision's cited evidence (design doc §5.4:
        // exports must surface role/RequiresReview context so consumers can tell business-eligible
        // findings from technical/mixed/unknown ones without re-deriving it).
        var artifactIds = evidence.Select(e => e.ArtifactId).Where(id => id is not null).Distinct().ToList();
        var roleAssignments = new List<RoleAssignment>();
        foreach (var artifactId in artifactIds)
        {
            roleAssignments.AddRange(await _repository.GetRoleAssignmentsForArtifactAsync(artifactId!, cancellationToken));
        }

        // Review decisions are recorded against the revision they were made against, and each
        // decision produces a new status-only revision (append-only design — see
        // DiscoveryService.RecordDecisionAsync). To render a complete review history for this
        // finding we aggregate decisions across every revision in the finding's chain, not just
        // the exported revision itself.
        var allRevisions = await _repository.GetFindingRevisionsAsync(revision.FindingId, cancellationToken);
        var decisions = new List<ReviewDecision>();
        foreach (var r in allRevisions)
        {
            decisions.AddRange(await _repository.GetReviewDecisionsAsync(r.FindingRevisionId, cancellationToken));
        }
        decisions.Sort((a, b) => a.DecidedAtUtc.CompareTo(b.DecidedAtUtc));

        Directory.CreateDirectory(outputDirectory);

        var baseName = $"{revision.FindingId}-v{revision.RevisionNumber}";
        var markdownPath = Path.Combine(outputDirectory, $"{baseName}.md");
        var jsonPath = Path.Combine(outputDirectory, $"{baseName}.json");

        var exportModel = new FindingExport(
            ContractVersion,
            revision.FindingId,
            revision.FindingRevisionId,
            revision.RevisionNumber,
            revision.RunId,
            revision.Statement,
            revision.Confidence,
            revision.Status.ToString(),
            revision.SupersedesRevisionId,
            revision.ProvenanceId,
            revision.CreatedAtUtc,
            evidence.Select(e => new EvidenceExport(e.EvidenceId, e.Type.ToString(), e.Locator, e.WasRedacted)).ToList(),
            decisions.Select(d => new ReviewDecisionExport(d.ReviewDecisionId, d.ReviewerIdentity, d.Decision.ToString(), d.Rationale, d.DecidedAtUtc)).ToList(),
            llmAssessment is null
                ? null
                : new LlmAssessmentExport(llmAssessment.ReviewPriority, llmAssessment.WhyExplanation, llmAssessment.ModelId),
            roleAssignments.Select(r => new RoleAssignmentExport(
                r.RoleAssignmentId,
                r.ArtifactId,
                r.SymbolLocator,
                r.Roles.Select(role => role.ToString()).ToList(),
                r.Confidence,
                r.ClassificationRule,
                r.RequiresReview)).ToList());

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(exportModel, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, RenderMarkdown(exportModel), cancellationToken);

        return (markdownPath, jsonPath);
    }

    /// <summary>
    /// Exports a run's full integration inventory (design doc §7, issue #6) to JSON, grouped by
    /// <see cref="IntegrationClassification"/> so runtime application, platform/identity,
    /// observability, and delivery dependencies are distinguishable by consumers without
    /// re-deriving the classification. Unlike <see cref="ExportFindingAsync"/>, integrations have
    /// no publish/reject review lifecycle (they are deterministic presence facts, like role
    /// assignments), so every integration recorded for the run is included.
    /// </summary>
    public async Task<string> ExportIntegrationInventoryAsync(
        string runId,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var run = await _repository.GetRunAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException($"Run not found: {runId}");
        var integrations = await _repository.GetIntegrationsAsync(runId, cancellationToken);

        Directory.CreateDirectory(outputDirectory);
        var jsonPath = Path.Combine(outputDirectory, $"{runId}-integrations.json");

        var byClassification = integrations
            .GroupBy(i => i.Classification)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key.ToString(),
                g => g.Select(i => new IntegrationExport(
                    i.IntegrationId,
                    i.Category.ToString(),
                    i.Classification.ToString(),
                    i.Direction.ToString(),
                    i.TriggerOrCaller,
                    i.ProtocolOrMechanism,
                    i.LogicalTarget,
                    i.ConfigurationKeySemantics,
                    i.RedactedContractShape,
                    i.AuthenticationSemantics,
                    i.ReliabilityBehavior,
                    i.OwningContextCandidateId,
                    i.EvidenceIds,
                    i.Confidence,
                    i.ClassificationRule,
                    i.BlindSpots,
                    i.RequiresReview,
                    i.CreatedAtUtc)).ToList());

        var exportModel = new IntegrationInventoryExport(
            ContractVersion,
            runId,
            run.Subject,
            DateTimeOffset.UtcNow,
            integrations.Count,
            byClassification);

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(exportModel, JsonOptions), cancellationToken);
        return jsonPath;
    }

    /// <summary>
    /// Words/phrases that would turn the handoff into future-state/roadmap/modernization advice,
    /// which design doc §9 explicitly forbids: the handoff describes only current-state facts,
    /// gaps, and escalation questions. Checked case-insensitively against the assembled Markdown
    /// before it is ever written to disk (issue #5 acceptance criterion).
    /// </summary>
    private static readonly string[] FutureStateLanguageDenylist =
    {
        "should migrate", "recommend migrating", "recommend that", "we recommend",
        "modernize to", "modernization recommendation", "target architecture", "roadmap",
        "backlog", "should be rewritten", "should be refactored to", "future state",
        "proposed architecture", "next steps for modernization",
    };

    /// <summary>
    /// Exports the full 10-section Specification Factory handoff for a run (design doc §6, issue
    /// #5) as versioned Markdown and matching JSON. The risk register (design doc §6 section 9)
    /// must already have been built via <see cref="DiscoveryService.BuildRiskRegisterAsync"/>;
    /// this method only reads it via <see cref="DiscoveryHandoffAssembler"/>.
    /// </summary>
    public async Task<(string MarkdownPath, string JsonPath)> ExportHandoffAsync(
        string runId,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var assembler = new DiscoveryHandoffAssembler(_repository);
        var handoff = await assembler.AssembleAsync(runId, cancellationToken);

        var markdown = RenderHandoffMarkdown(handoff);
        AssertNoFutureStateLanguage(markdown);

        Directory.CreateDirectory(outputDirectory);
        var markdownPath = Path.Combine(outputDirectory, $"{runId}-handoff.md");
        var jsonPath = Path.Combine(outputDirectory, $"{runId}-handoff.json");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(handoff, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken);

        return (markdownPath, jsonPath);
    }

    /// <summary>
    /// Throws if the assembled handoff Markdown contains future-state/roadmap/modernization-
    /// recommendation language. Public so tests can validate the guard directly against injected
    /// text without regenerating a full handoff.
    /// </summary>
    public static void AssertNoFutureStateLanguage(string markdown)
    {
        foreach (var phrase in FutureStateLanguageDenylist)
        {
            if (markdown.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Handoff export contains disallowed future-state/roadmap language: \"{phrase}\". " +
                    "The Specification Factory handoff must describe only current-state facts, gaps, " +
                    "and escalation questions (design doc §9).");
            }
        }
    }

    private static string RenderHandoffMarkdown(HandoffDocument h)
    {
        var sb = new StringBuilder();
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        sb.AppendLine($"<!-- Discovery Factory Specification Factory handoff, contract v{h.ContractVersion} -->");
        sb.AppendLine($"# Specification Factory handoff — {h.Subject}");
        sb.AppendLine();
        sb.AppendLine($"- **Run:** `{h.RunId}`");
        sb.AppendLine($"- **Assembled (UTC):** {h.AssembledAtUtc:O}");
        sb.AppendLine();
        sb.AppendLine("_This is a derived, navigation-oriented view over the governed semantic model. Neither " +
                       "this Markdown document nor the matching JSON export is the source of truth; the governed " +
                       "records are (design doc §2, §6)._");
        sb.AppendLine();

        sb.AppendLine("## 1. Run scope and reconstruction status");
        sb.AppendLine();
        var s1 = h.Section1_RunScope;
        sb.AppendLine($"- **Subject:** {s1.Subject}");
        sb.AppendLine($"- **Source locator:** `{s1.SourceLocator}`");
        sb.AppendLine($"- **Source revision:** `{s1.SourceRevision}`");
        sb.AppendLine($"- **Inclusions:** {(s1.Inclusions.Count == 0 ? "_none declared_" : string.Join(", ", s1.Inclusions))}");
        sb.AppendLine($"- **Exclusions:** {(s1.Exclusions.Count == 0 ? "_none declared_" : string.Join(", ", s1.Exclusions))}");
        sb.AppendLine($"- **Evidence boundary:** {s1.EvidenceBoundary}");
        sb.AppendLine($"- **Run status (closure):** {s1.RunStatus}");
        if (s1.SupersedesRunId is not null)
        {
            sb.AppendLine($"- **Supersedes run:** `{s1.SupersedesRunId}`");
        }
        sb.AppendLine($"- **Declared (UTC):** {s1.CreatedAtUtc:O}");
        sb.AppendLine();

        sb.AppendLine("## 2. System purpose and domain landscape");
        sb.AppendLine();
        sb.AppendLine("_Candidate logical contexts, marked as candidate until human-published (design doc §5.4)._");
        sb.AppendLine();
        if (h.Section2_DomainLandscape.Contexts.Count == 0)
        {
            sb.AppendLine("_No candidate contexts recorded for this run._");
        }
        else
        {
            foreach (var c in h.Section2_DomainLandscape.Contexts)
            {
                sb.AppendLine($"- `{c.ContextCandidateId}` **{c.Name}** ({c.Kind}, {c.Status}, confidence {c.Confidence.ToString("0.00", ci)}, rule: {c.SeedingRule}) — {c.EvidenceIds.Count} evidence citation(s)");
            }
        }
        sb.AppendLine();
        if (h.Section2_DomainLandscape.CrossContextDependencies.Count > 0)
        {
            sb.AppendLine("**Cross-context dependencies:**");
            sb.AppendLine();
            foreach (var d in h.Section2_DomainLandscape.CrossContextDependencies)
            {
                sb.AppendLine($"- `{d.FromContextCandidateId}` → `{d.ToContextCandidateId}` (confidence {d.Confidence.ToString("0.00", ci)})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 3. Use cases and functional flows");
        sb.AppendLine();
        var s3 = h.Section3_UseCases;
        if (s3.IsSparse)
        {
            sb.AppendLine($"⚠️ **Gap:** {s3.GapExplanation}");
            if (s3.LinkedRiskId is not null)
            {
                sb.AppendLine($"See risk register entry `{s3.LinkedRiskId}`.");
            }
        }
        else
        {
            foreach (var id in s3.UseCaseFindingIds)
            {
                sb.AppendLine($"- Finding `{id}`");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 4. Business rules, policies, calculations, and state transitions");
        sb.AppendLine();
        var s4 = h.Section4_BusinessRules;
        if (s4.IsSparse)
        {
            sb.AppendLine($"⚠️ **Gap:** {s4.GapExplanation}");
            if (s4.LinkedRiskId is not null)
            {
                sb.AppendLine($"See risk register entry `{s4.LinkedRiskId}`.");
            }
        }
        else
        {
            foreach (var id in s4.BusinessRuleFindingIds)
            {
                sb.AppendLine($"- Finding `{id}`");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 5. Architecture and component responsibility map");
        sb.AppendLine();
        sb.AppendLine($"- **Nodes:** {h.Section5_Architecture.Nodes.Count}");
        sb.AppendLine($"- **Edges:** {h.Section5_Architecture.EdgeCount}");
        sb.AppendLine();
        if (h.Section5_Architecture.RoleTraceLocations.Count > 0)
        {
            sb.AppendLine("**Business-logic trace locations (role-classified symbols):**");
            sb.AppendLine();
            foreach (var r in h.Section5_Architecture.RoleTraceLocations.Take(200))
            {
                sb.AppendLine($"- `{r.SymbolLocator}` — {string.Join(", ", r.Roles)}{(r.RequiresReview ? " ⚠️ _requires review_" : string.Empty)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 6. Interfaces and integration topology");
        sb.AppendLine();
        if (h.Section6_Integrations.Integrations.Count == 0)
        {
            sb.AppendLine("_No integrations recorded for this run._");
        }
        else
        {
            foreach (var i in h.Section6_Integrations.Integrations)
            {
                sb.AppendLine($"- `{i.IntegrationId}` **{i.Category}** ({i.Classification}, {i.Direction}) — {i.TriggerOrCaller} → {i.LogicalTarget} via {i.ProtocolOrMechanism}, confidence {i.Confidence.ToString("0.00", ci)}{(i.RequiresReview ? " ⚠️ _requires review_" : string.Empty)}");
                if (i.BlindSpots.Count > 0)
                {
                    sb.AppendLine($"  - Blind spots: {string.Join("; ", i.BlindSpots)}");
                }
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 7. Data model and lifecycle");
        sb.AppendLine();
        if (h.Section7_DataModel.Entities.Count == 0)
        {
            sb.AppendLine("_No database/schema graph nodes recorded for this run._");
        }
        else
        {
            foreach (var e in h.Section7_DataModel.Entities)
            {
                sb.AppendLine($"- `{e.NodeId}` **{e.DisplayName}** (`{e.SymbolLocator}`) — {e.EvidenceIds.Count} evidence citation(s)");
            }
        }
        sb.AppendLine();
        if (h.Section7_DataModel.DataStoreIntegrations.Count > 0)
        {
            sb.AppendLine("**Database/shared-store integrations:**");
            sb.AppendLine();
            foreach (var i in h.Section7_DataModel.DataStoreIntegrations)
            {
                sb.AppendLine($"- `{i.IntegrationId}` — {i.TriggerOrCaller} → {i.LogicalTarget} via {i.ProtocolOrMechanism}{(i.ConfigurationKeySemantics is null ? string.Empty : $" (config key: `{i.ConfigurationKeySemantics}`)")}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 8. Security, configuration, operations, and non-functional constraints");
        sb.AppendLine();
        sb.AppendLine("**Platform/identity dependencies:**");
        sb.AppendLine();
        if (h.Section8_NonFunctional.PlatformIdentityIntegrations.Count == 0)
        {
            sb.AppendLine("_None recorded._");
        }
        foreach (var i in h.Section8_NonFunctional.PlatformIdentityIntegrations)
        {
            sb.AppendLine($"- `{i.IntegrationId}` — {i.Category} via {i.ProtocolOrMechanism}{(i.AuthenticationSemantics is null ? string.Empty : $" ({i.AuthenticationSemantics})")}");
        }
        sb.AppendLine();
        sb.AppendLine("**Observability dependencies:**");
        sb.AppendLine();
        if (h.Section8_NonFunctional.ObservabilityIntegrations.Count == 0)
        {
            sb.AppendLine("_None recorded._");
        }
        foreach (var i in h.Section8_NonFunctional.ObservabilityIntegrations)
        {
            sb.AppendLine($"- `{i.IntegrationId}` — {i.Category} via {i.ProtocolOrMechanism}");
        }
        sb.AppendLine();
        sb.AppendLine("**Delivery dependencies (build/deploy/migration tooling; not runtime business behavior):**");
        sb.AppendLine();
        if (h.Section8_NonFunctional.DeliveryIntegrations.Count == 0)
        {
            sb.AppendLine("_None recorded._");
        }
        foreach (var i in h.Section8_NonFunctional.DeliveryIntegrations)
        {
            sb.AppendLine($"- `{i.IntegrationId}` — {i.Category} via {i.ProtocolOrMechanism}");
        }
        sb.AppendLine();

        sb.AppendLine("## 9. Risks, gaps, assumptions, and decisions needed");
        sb.AppendLine();
        sb.AppendLine("_Current-state risks, gaps, conflicts, and analyzer limitations only — no future-state advice (design doc §9)._");
        sb.AppendLine();
        if (h.Section9_Risks.Risks.Count == 0)
        {
            sb.AppendLine("_No open risks recorded for this run._");
        }
        else
        {
            foreach (var r in h.Section9_Risks.Risks.OrderByDescending(r => r.Severity))
            {
                sb.AppendLine($"### `{r.RiskId}` — {r.Title}");
                sb.AppendLine();
                sb.AppendLine($"- **Category:** {r.Category}");
                sb.AppendLine($"- **Severity:** {r.Severity}");
                sb.AppendLine($"- **Status:** {r.Status}");
                sb.AppendLine($"- **Derivation rule:** {r.DerivationRule} (source: `{r.SourceRecordId}`)");
                sb.AppendLine($"- **Description:** {r.Description}");
                sb.AppendLine($"- **Escalation question:** {r.EscalationQuestion}");
                if (r.EvidenceIds.Count > 0)
                {
                    sb.AppendLine($"- **Evidence:** {string.Join(", ", r.EvidenceIds.Select(id => $"`{id}`"))}");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("## 10. Evidence, provenance, and navigation");
        sb.AppendLine();
        var s10 = h.Section10_Navigation;
        sb.AppendLine($"- **Artifacts:** {s10.TotalArtifacts}");
        sb.AppendLine($"- **Evidence records:** {s10.TotalEvidence}");
        sb.AppendLine($"- **Findings:** {s10.TotalFindings}");
        sb.AppendLine($"- **Candidate contexts:** {s10.TotalContextCandidates}");
        sb.AppendLine($"- **Integrations:** {s10.TotalIntegrations}");
        sb.AppendLine($"- **Open risks:** {s10.TotalRisks}");
        sb.AppendLine();
        sb.AppendLine("**Read-only MCP/API query navigation reference (design doc §8 — not implemented in this phase):**");
        sb.AppendLine();
        foreach (var op in s10.McpQueryOperationsReference)
        {
            sb.AppendLine($"- {op}");
        }
        sb.AppendLine();

        return sb.ToString();
    }

    private static string RenderMarkdown(FindingExport export)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<!-- Discovery Factory export contract v{export.ContractVersion} -->");
        sb.AppendLine($"# Finding {export.FindingId} (revision {export.RevisionNumber})");
        sb.AppendLine();
        sb.AppendLine($"- **Finding revision ID:** `{export.FindingRevisionId}`");
        sb.AppendLine($"- **Run:** `{export.RunId}`");
        sb.AppendLine($"- **Status:** {export.Status}");
        sb.AppendLine($"- **Confidence:** {export.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- **Provenance:** `{export.ProvenanceId}`");
        if (export.SupersedesRevisionId is not null)
        {
            sb.AppendLine($"- **Supersedes:** `{export.SupersedesRevisionId}`");
        }
        sb.AppendLine($"- **Created (UTC):** {export.CreatedAtUtc:O}");
        sb.AppendLine();
        sb.AppendLine("## Statement");
        sb.AppendLine();
        sb.AppendLine(export.Statement);
        sb.AppendLine();

        if (export.LlmAssessment is not null)
        {
            sb.AppendLine("## Non-authoritative LLM review priority");
            sb.AppendLine();
            sb.AppendLine($"- **Priority:** {export.LlmAssessment.ReviewPriority.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} (model: `{export.LlmAssessment.ModelId}`)");
            sb.AppendLine($"- **Why:** {export.LlmAssessment.WhyExplanation}");
            sb.AppendLine();
        }

        sb.AppendLine("## Evidence and citations");
        sb.AppendLine();
        if (export.Evidence.Count == 0)
        {
            sb.AppendLine("_No cited evidence._");
        }
        else
        {
            foreach (var e in export.Evidence)
            {
                sb.AppendLine($"- `{e.EvidenceId}` ({e.Type}) — {e.Locator}{(e.WasRedacted ? " _(redacted)_" : string.Empty)}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Review history");
        sb.AppendLine();
        if (export.ReviewDecisions.Count == 0)
        {
            sb.AppendLine("_No reviewer decisions recorded yet._");
        }
        else
        {
            foreach (var d in export.ReviewDecisions)
            {
                sb.AppendLine($"- {d.DecidedAtUtc:O} — **{d.Decision}** by {d.ReviewerIdentity}: {d.Rationale}");
            }
        }
        sb.AppendLine();

        if (export.RoleAssignments.Count > 0)
        {
            sb.AppendLine("## Artifact roles");
            sb.AppendLine();
            sb.AppendLine("_Deterministic role classification of the source artifacts cited by this finding's evidence (design doc §5, §5.1). Non-business/mixed/unknown roles are excluded from LLM business-use-case prompts by default._");
            sb.AppendLine();
            foreach (var r in export.RoleAssignments)
            {
                sb.AppendLine($"- `{r.SymbolLocator}` — **{string.Join(", ", r.Roles)}** (confidence {r.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}, rule: {r.ClassificationRule}){(r.RequiresReview ? " ⚠️ _requires review_" : string.Empty)}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private sealed record FindingExport(
        string ContractVersion,
        string FindingId,
        string FindingRevisionId,
        int RevisionNumber,
        string RunId,
        string Statement,
        double Confidence,
        string Status,
        string? SupersedesRevisionId,
        string ProvenanceId,
        DateTimeOffset CreatedAtUtc,
        IReadOnlyList<EvidenceExport> Evidence,
        IReadOnlyList<ReviewDecisionExport> ReviewDecisions,
        LlmAssessmentExport? LlmAssessment,
        IReadOnlyList<RoleAssignmentExport> RoleAssignments);

    private sealed record EvidenceExport(string EvidenceId, string Type, string Locator, bool WasRedacted);

    private sealed record ReviewDecisionExport(string ReviewDecisionId, string ReviewerIdentity, string Decision, string Rationale, DateTimeOffset DecidedAtUtc);

    private sealed record LlmAssessmentExport(double ReviewPriority, string WhyExplanation, string ModelId);

    private sealed record RoleAssignmentExport(
        string RoleAssignmentId,
        string ArtifactId,
        string SymbolLocator,
        IReadOnlyList<string> Roles,
        double Confidence,
        string ClassificationRule,
        bool RequiresReview);

    private sealed record IntegrationInventoryExport(
        string ContractVersion,
        string RunId,
        string Subject,
        DateTimeOffset ExportedAtUtc,
        int TotalIntegrations,
        IReadOnlyDictionary<string, List<IntegrationExport>> ByClassification);

    private sealed record IntegrationExport(
        string IntegrationId,
        string Category,
        string Classification,
        string Direction,
        string TriggerOrCaller,
        string ProtocolOrMechanism,
        string LogicalTarget,
        string? ConfigurationKeySemantics,
        string? RedactedContractShape,
        string? AuthenticationSemantics,
        string? ReliabilityBehavior,
        string? OwningContextCandidateId,
        IReadOnlyList<string> EvidenceIds,
        double Confidence,
        string ClassificationRule,
        IReadOnlyList<string> BlindSpots,
        bool RequiresReview,
        DateTimeOffset CreatedAtUtc);
}
