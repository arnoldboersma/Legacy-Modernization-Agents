using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.CSharp;

public sealed class CSharpSourceDiscovery : ISourceDiscovery
{
    public SourceLanguage Language => SourceLanguage.CSharp;

    public async Task<IReadOnlyList<SourceFile>> DiscoverAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Directory not found: {sourcePath}");

        var files = new List<SourceFile>();
        foreach (var path in Directory.EnumerateFiles(sourcePath, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !IsBuildArtifact(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(new SourceFile
            {
                Language = Language,
                FileName = Path.GetFileName(path),
                FilePath = path,
                Content = await File.ReadAllTextAsync(path, cancellationToken),
                ProjectName = FindProjectName(path, sourcePath)
            });
        }
        return files;
    }

    private static bool IsBuildArtifact(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj" or ".git");

    private static string FindProjectName(string filePath, string sourcePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(directory) && directory.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (project is not null)
                return Path.GetFileNameWithoutExtension(project);
            directory = Path.GetDirectoryName(directory);
        }
        return string.Empty;
    }
}
