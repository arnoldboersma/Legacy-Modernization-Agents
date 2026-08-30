using CobolToQuarkusMigration.Agents.Interfaces;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.Cobol;

public sealed class CobolDependencyAnalyzer(IDependencyMapperAgent analyzer) : Interfaces.IDependencyAnalyzer
{
    public SourceLanguage Language => SourceLanguage.Cobol;

    public Task<DependencyMap> AnalyzeAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        IReadOnlyList<Models.SourceAnalysis> analyses,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = sourceFiles.OfType<CobolFile>().ToList();
        var cobolAnalyses = analyses.OfType<CobolAnalysis>().ToList();
        if (files.Count != sourceFiles.Count || cobolAnalyses.Count != analyses.Count)
            throw new ArgumentException("COBOL dependency analysis requires COBOL source files and analyses.");

        return analyzer.AnalyzeDependenciesAsync(files, cobolAnalyses);
    }
}
