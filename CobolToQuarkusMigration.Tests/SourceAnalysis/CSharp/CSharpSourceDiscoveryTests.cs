using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.CSharp;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.SourceAnalysis.CSharp;

public sealed class CSharpSourceDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"csharp-source-discovery-{Guid.NewGuid():N}");

    public CSharpSourceDiscoveryTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Language_ReturnsCSharp()
    {
        var discovery = new CSharpSourceDiscovery();

        discovery.Language.Should().Be(SourceLanguage.CSharp);
    }

    [Fact]
    public async Task DiscoverAsync_MissingRoot_ThrowsDirectoryNotFoundException()
    {
        var discovery = new CSharpSourceDiscovery();
        var missingRoot = Path.Combine(_root, "does-not-exist");

        Func<Task> act = () => discovery.DiscoverAsync(missingRoot);

        await act.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage($"Directory not found: {missingRoot}");
    }

    [Fact]
    public async Task DiscoverAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        WriteFile("Class.cs", "public sealed class Class { }");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var discovery = new CSharpSourceDiscovery();

        Func<Task> act = () => discovery.DiscoverAsync(_root, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DiscoverAsync_RecursiveLocalTree_ReturnsPopulatedNonSupportCSharpFiles()
    {
        WriteFile("RootProject.csproj", "<Project />");
        WriteFile("RootFile.cs", "public sealed class RootFile { }");
        WriteFile(Path.Combine("src", "FeatureProject.csproj"), "<Project />");
        WriteFile(
            Path.Combine("src", "Feature", "NestedFile.cs"),
            "namespace Demo; public sealed class NestedFile { }");
        var discovery = new CSharpSourceDiscovery();

        var discovered = await discovery.DiscoverAsync(_root);
        var filesByPath = discovered.ToDictionary(
            file => Normalize(Path.GetRelativePath(_root, file.FilePath)),
            StringComparer.Ordinal);

        filesByPath.Keys.Should().BeEquivalentTo(new[]
        {
            "RootFile.cs",
            "src/Feature/NestedFile.cs"
        });
        var rootFile = filesByPath["RootFile.cs"];
        rootFile.Should().BeEquivalentTo(new
        {
            Language = SourceLanguage.CSharp,
            FileName = "RootFile.cs",
            Content = "public sealed class RootFile { }",
            IsSupportFile = false,
            ProjectName = "RootProject"
        });
        Normalize(Path.GetRelativePath(_root, rootFile.FilePath)).Should().Be("RootFile.cs");

        var nestedFile = filesByPath["src/Feature/NestedFile.cs"];
        nestedFile.Should().BeEquivalentTo(new
        {
            Language = SourceLanguage.CSharp,
            FileName = "NestedFile.cs",
            Content = "namespace Demo; public sealed class NestedFile { }",
            IsSupportFile = false,
            ProjectName = "FeatureProject"
        });
        Normalize(Path.GetRelativePath(_root, nestedFile.FilePath)).Should().Be("src/Feature/NestedFile.cs");
    }

    [Fact]
    public async Task DiscoverAsync_PathsInBinObjAndGitSegments_AreExcluded()
    {
        WriteFile(Path.Combine("bin", "Generated.cs"), "public class Generated { }");
        WriteFile(Path.Combine("src", "obj", "Generated.cs"), "public class Generated { }");
        WriteFile(Path.Combine(".git", "hooks", "Tracked.cs"), "public class Tracked { }");
        var discovery = new CSharpSourceDiscovery();

        var discovered = await discovery.DiscoverAsync(_root);

        discovered.Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteFile(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
}
