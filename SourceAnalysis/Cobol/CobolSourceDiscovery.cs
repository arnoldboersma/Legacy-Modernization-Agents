using CobolToQuarkusMigration.Helpers;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.Cobol;

public sealed class CobolSourceDiscovery(FileHelper fileHelper) : ISourceDiscovery
{
    public SourceLanguage Language => SourceLanguage.Cobol;

    public async Task<IReadOnlyList<SourceFile>> DiscoverAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = await fileHelper.ScanDirectoryForCobolFilesAsync(sourcePath);
        PromptLoader.CodebaseProfile = PromptLoader.GenerateCodebaseProfile(files);
        return files.Cast<SourceFile>().ToList();
    }
}
