using CobolToQuarkusMigration.Helpers;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Helpers;

public sealed class SourcePathHelperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"source-path-helper-{Guid.NewGuid():N}");

    [Fact]
    public void NormalizeRelativePath_RemovesLeadingCurrentAndRootSegmentsWhilePreservingParentSegments()
    {
        SourcePathHelper.NormalizeRelativePath(@" ./batch\PAYROLL.cbl ")
            .Should().Be("batch/PAYROLL.cbl");
        SourcePathHelper.NormalizeRelativePath("/copybooks/COMMON.cpy")
            .Should().Be("copybooks/COMMON.cpy");
        SourcePathHelper.NormalizeRelativePath("../outside/PROGRAM.cbl")
            .Should().Be("../outside/PROGRAM.cbl");
        SourcePathHelper.NormalizeRelativePath("  ")
            .Should().BeEmpty();
    }

    [Fact]
    public void ToOsRelativePath_NormalizesBothKindsOfSeparators()
    {
        SourcePathHelper.ToOsRelativePath(@".\batch/PROGRAM.cbl")
            .Should().Be(Path.Combine("batch", "PROGRAM.cbl"));
    }

    [Fact]
    public void EnumerateProgramRelativePaths_ReturnsRootAndNestedProgramsInCaseInsensitiveOrdinalOrder()
    {
        WriteFile("ZETA.cob");
        WriteFile(Path.Combine("batch", "alpha.cbl"));
        WriteFile(Path.Combine("copybooks", "COMMON.cpy"));
        WriteFile(Path.Combine(".rekt-staging", "duplicate.cbl"));

        var paths = SourcePathHelper.EnumerateProgramRelativePaths(_root);

        paths.Should().Equal("batch/alpha.cbl", "ZETA.cob");
        paths.Should().NotContain(path => path.Contains(".rekt-staging", StringComparison.Ordinal));
        paths.Should().OnlyContain(path => !Path.IsPathRooted(path));
    }

    [Fact]
    public void EnumerateProgramRelativePaths_MissingRoot_ReturnsEmptyList()
    {
        SourcePathHelper.EnumerateProgramRelativePaths(_root).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteFile(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "       IDENTIFICATION DIVISION.");
    }
}
