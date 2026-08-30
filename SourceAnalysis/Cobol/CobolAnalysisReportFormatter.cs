using System.Text;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.Cobol;

public sealed class CobolAnalysisReportFormatter : ISourceAnalysisReportFormatter
{
    public void AppendTechnicalAnalysis(StringBuilder builder, IReadOnlyList<Models.SourceAnalysis> analyses)
    {
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("## Technical Analysis");
        builder.AppendLine();
        foreach (var analysis in analyses.OfType<CobolAnalysis>())
        {
            var fileTypeLabel = analysis.IsCopybook ? " [Copybook]" : "";
            builder.AppendLine($"### {analysis.FileName}{fileTypeLabel}");
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(analysis.ProgramDescription))
            {
                builder.AppendLine($"**Program Description:** {analysis.ProgramDescription}");
                builder.AppendLine();
            }
            AppendList(builder, "Data Divisions", analysis.DataDivisions);
            AppendList(builder, "Procedure Divisions", analysis.ProcedureDivisions);
            AppendList(builder, "Copybooks Referenced", analysis.CopybooksReferenced);
            var hasStructuredData = analysis.DataDivisions.Any() || analysis.ProcedureDivisions.Any()
                || analysis.CopybooksReferenced.Any() || analysis.Paragraphs.Any();
            if (!hasStructuredData && !string.IsNullOrWhiteSpace(analysis.RawAnalysisData))
            {
                builder.AppendLine(analysis.RawAnalysisData);
                builder.AppendLine();
            }
            builder.AppendLine("---");
            builder.AppendLine();
        }
    }

    private static void AppendList(StringBuilder builder, string heading, IEnumerable<string> values)
    {
        if (!values.Any())
            return;
        builder.AppendLine($"**{heading}:**");
        foreach (var value in values)
            builder.AppendLine($"- {value}");
        builder.AppendLine();
    }
}
