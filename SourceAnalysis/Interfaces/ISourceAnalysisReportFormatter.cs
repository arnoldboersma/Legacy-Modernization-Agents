using System.Text;
using CobolToQuarkusMigration.Models;

namespace CobolToQuarkusMigration.SourceAnalysis.Interfaces;

public interface ISourceAnalysisReportFormatter
{
    void AppendTechnicalAnalysis(StringBuilder builder, IReadOnlyList<Models.SourceAnalysis> analyses);
}
