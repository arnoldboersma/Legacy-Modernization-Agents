using CobolToQuarkusMigration.Agents;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.Cobol;

public sealed class CobolBusinessLogicExtractor(BusinessLogicExtractorAgent extractor) : ISourceBusinessLogicExtractor
{
    public SourceLanguage Language => SourceLanguage.Cobol;

    public Task<IReadOnlyList<BusinessLogic>> ExtractAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        IReadOnlyList<Models.SourceAnalysis> analyses,
        Glossary? glossary,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = sourceFiles.OfType<CobolFile>().ToList();
        var cobolAnalyses = analyses.OfType<CobolAnalysis>().ToList();
        if (files.Count != sourceFiles.Count || cobolAnalyses.Count != analyses.Count)
            throw new ArgumentException("COBOL business logic extraction requires COBOL source files and analyses.");

        return ExtractCoreAsync(files, cobolAnalyses, glossary, progressCallback);
    }

    private async Task<IReadOnlyList<BusinessLogic>> ExtractCoreAsync(
        List<CobolFile> files, List<CobolAnalysis> analyses, Glossary? glossary, Action<int, int>? progressCallback)
        => await extractor.ExtractBusinessLogicAsync(files, analyses, glossary, progressCallback);
}
