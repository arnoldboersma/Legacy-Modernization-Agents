using CobolToQuarkusMigration.Models;

namespace CobolToQuarkusMigration.SourceAnalysis.Interfaces;

public interface IDependencyAnalyzer
{
    SourceLanguage Language { get; }
    Task<DependencyMap> AnalyzeAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        IReadOnlyList<Models.SourceAnalysis> analyses,
        CancellationToken cancellationToken = default);
}
