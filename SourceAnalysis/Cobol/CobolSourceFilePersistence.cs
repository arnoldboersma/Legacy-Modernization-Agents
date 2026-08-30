using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.Persistence;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.Cobol;

public sealed class CobolSourceFilePersistence(IMigrationRepository repository) : ISourceFilePersistence
{
    public Task SaveSourceFilesAsync(int runId, IReadOnlyList<SourceFile> sourceFiles, CancellationToken cancellationToken = default)
    {
        var cobolFiles = sourceFiles.OfType<CobolFile>().ToList();
        if (cobolFiles.Count != sourceFiles.Count)
            throw new ArgumentException("COBOL persistence requires COBOL source files.", nameof(sourceFiles));
        return repository.SaveCobolFilesAsync(runId, cobolFiles, cancellationToken);
    }
}
