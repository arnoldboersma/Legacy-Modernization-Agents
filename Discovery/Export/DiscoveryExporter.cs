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
    public const string ContractVersion = "1.0.0";

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
                : new LlmAssessmentExport(llmAssessment.ReviewPriority, llmAssessment.WhyExplanation, llmAssessment.ModelId));

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(exportModel, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, RenderMarkdown(exportModel), cancellationToken);

        return (markdownPath, jsonPath);
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
        LlmAssessmentExport? LlmAssessment);

    private sealed record EvidenceExport(string EvidenceId, string Type, string Locator, bool WasRedacted);

    private sealed record ReviewDecisionExport(string ReviewDecisionId, string ReviewerIdentity, string Decision, string Rationale, DateTimeOffset DecidedAtUtc);

    private sealed record LlmAssessmentExport(double ReviewPriority, string WhyExplanation, string ModelId);
}
