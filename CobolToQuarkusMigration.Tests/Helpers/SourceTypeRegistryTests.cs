using CobolToQuarkusMigration.Helpers;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Helpers;

public sealed class SourceTypeRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"source-type-registry-{Guid.NewGuid():N}");

    [Fact]
    public void Classification_SeparatesProgramsCopybooksAndUnknownFilesCaseInsensitively()
    {
        SourceTypeRegistry.Classify("BATCH.CBL").Should().Be(SourceKind.CobolProgram);
        SourceTypeRegistry.Classify("batch.cob").Should().Be(SourceKind.CobolProgram);
        SourceTypeRegistry.Classify("COMMON.CPY").Should().Be(SourceKind.Copybook);
        SourceTypeRegistry.Classify("Worker.cs").Should().Be(SourceKind.Unknown);

        SourceTypeRegistry.IsCobolProgram("COMMON.CPY").Should().BeFalse();
        SourceTypeRegistry.IsCopybook("BATCH.CBL").Should().BeFalse();
        SourceTypeRegistry.IsKnown("notes.txt").Should().BeFalse();
    }

    [Fact]
    public void EnumerateProgramAndCopybookFiles_IncludesNestedCobolFilesAndExcludesStagingAndOtherFiles()
    {
        WriteFile("root.cob");
        WriteFile(Path.Combine("batch", "PAYROLL.CBL"));
        WriteFile(Path.Combine("copybooks", "CUSTOMER.cpy"));
        WriteFile(Path.Combine(".rekt-staging", "duplicate.cbl"));
        WriteFile(Path.Combine(".preprocessed", "duplicate.cpy"));
        WriteFile("Worker.cs");

        var programs = SourceTypeRegistry.EnumerateProgramFiles(_root)
            .Select(path => Path.GetRelativePath(_root, path))
            .ToList();
        var copybooks = SourceTypeRegistry.EnumerateCopybookFiles(_root)
            .Select(path => Path.GetRelativePath(_root, path))
            .ToList();

        programs.Should().BeEquivalentTo(
            new[] { "root.cob", Path.Combine("batch", "PAYROLL.CBL") });
        copybooks.Should().BeEquivalentTo(
            new[] { Path.Combine("copybooks", "CUSTOMER.cpy") });
        programs.Concat(copybooks).Should().OnlyContain(path => SourceTypeRegistry.IsKnown(path));
    }

    [Fact]
    public void EnumerateProgramFiles_MissingDirectory_ReturnsEmptySequence()
    {
        SourceTypeRegistry.EnumerateProgramFiles(_root).Should().BeEmpty();
        SourceTypeRegistry.EnumerateCopybookFiles(_root).Should().BeEmpty();
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
