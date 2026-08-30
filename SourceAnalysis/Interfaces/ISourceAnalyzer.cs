using CobolToQuarkusMigration.Models;

namespace CobolToQuarkusMigration.SourceAnalysis.Interfaces;

public interface ISourceAnalyzer
{
    SourceLanguage Language { get; }
    Task<IReadOnlyList<Models.SourceAnalysis>> AnalyzeAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default);
}
