using CobolToQuarkusMigration.Agents.Interfaces;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.Cobol;

public sealed class CobolSourceAnalyzer(ICobolAnalyzerAgent analyzer) : ISourceAnalyzer
{
    public SourceLanguage Language => SourceLanguage.Cobol;

    public async Task<IReadOnlyList<Models.SourceAnalysis>> AnalyzeAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cobolFiles = sourceFiles.OfType<CobolFile>().ToList();
        if (cobolFiles.Count != sourceFiles.Count)
            throw new ArgumentException("COBOL analysis requires COBOL source files.", nameof(sourceFiles));

        return (await analyzer.AnalyzeCobolFilesAsync(cobolFiles, progressCallback)).Cast<Models.SourceAnalysis>().ToList();
    }
}
