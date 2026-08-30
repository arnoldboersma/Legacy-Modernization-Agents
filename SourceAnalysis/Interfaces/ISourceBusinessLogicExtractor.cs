using CobolToQuarkusMigration.Models;

namespace CobolToQuarkusMigration.SourceAnalysis.Interfaces;

public interface ISourceBusinessLogicExtractor
{
    SourceLanguage Language { get; }
    Task<IReadOnlyList<BusinessLogic>> ExtractAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        IReadOnlyList<Models.SourceAnalysis> analyses,
        Glossary? glossary,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default);
}
