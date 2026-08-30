using CobolToQuarkusMigration.Models;

namespace CobolToQuarkusMigration.SourceAnalysis.Interfaces;

public interface ISourceDiscovery
{
    SourceLanguage Language { get; }
    Task<IReadOnlyList<SourceFile>> DiscoverAsync(string sourcePath, CancellationToken cancellationToken = default);
}
