using CobolToQuarkusMigration.Models;

namespace CobolToQuarkusMigration.SourceAnalysis.Interfaces;

/// <summary>Optional persistence for language-specific source records retained by legacy stores.</summary>
public interface ISourceFilePersistence
{
    Task SaveSourceFilesAsync(int runId, IReadOnlyList<SourceFile> sourceFiles, CancellationToken cancellationToken = default);
}
