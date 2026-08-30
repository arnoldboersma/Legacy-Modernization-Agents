using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis;

/// <summary>Deterministic source analysis does not invent business facts with an LLM.</summary>
public sealed class NoOpBusinessLogicExtractor(SourceLanguage language) : ISourceBusinessLogicExtractor
{
    public SourceLanguage Language => language;

    public Task<IReadOnlyList<BusinessLogic>> ExtractAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        IReadOnlyList<Models.SourceAnalysis> analyses,
        Glossary? glossary,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<BusinessLogic>>([]);
    }
}
