using System.Text;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis;

public sealed class DefaultSourceAnalysisReportFormatter : ISourceAnalysisReportFormatter
{
    public void AppendTechnicalAnalysis(StringBuilder builder, IReadOnlyList<Models.SourceAnalysis> analyses)
    {
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("## Technical Analysis");
        builder.AppendLine();
        foreach (var analysis in analyses)
        {
            builder.AppendLine($"### {analysis.FileName}");
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(analysis.Summary))
                builder.AppendLine($"**Summary:** {analysis.Summary}");
            foreach (var fact in analysis.Facts)
                builder.AppendLine($"- **{fact.Kind}:** {fact.Name}{(string.IsNullOrWhiteSpace(fact.Detail) ? "" : $" ({fact.Detail})")}");
            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine();
        }
    }
}
