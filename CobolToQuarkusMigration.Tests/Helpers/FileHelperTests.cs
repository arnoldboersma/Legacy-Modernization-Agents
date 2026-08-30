using CobolToQuarkusMigration.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Helpers;

public sealed class FileHelperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"file-helper-{Guid.NewGuid():N}");
    private readonly string? _originalSelectorMode;

    public FileHelperTests()
    {
        _originalSelectorMode = Environment.GetEnvironmentVariable("SELECTOR_MODE");
        Environment.SetEnvironmentVariable("SELECTOR_MODE", null);
    }

    [Fact]
    public async Task ScanDirectoryForCobolFilesAsync_ReturnsProgramsBeforeCopybooksAndExcludesStagingAndOtherFiles()
    {
        WriteFile("MAIN.CBL", "program one");
        WriteFile(Path.Combine("batch", "SECOND.cob"), "program two");
        WriteFile(Path.Combine("copybooks", "COMMON.cpy"), "copybook");
        WriteFile(Path.Combine(".rekt-staging", "duplicate.cbl"), "staged program");
        WriteFile("Worker.cs", "public class Worker { }");

        var files = await CreateFileHelper().ScanDirectoryForCobolFilesAsync(_root);

        files.Should().HaveCount(3);
        files.Take(2).Should().OnlyContain(file => !file.IsCopybook);
        files.Skip(2).Should().OnlyContain(file => file.IsCopybook);
        files.Select(file => file.FileName).Should().BeEquivalentTo(
            new[] { "MAIN.CBL", "SECOND.cob", "COMMON.cpy" });
        files.Single(file => file.FileName == "COMMON.cpy").Content.Should().Be("copybook");
        files.Should().NotContain(file => file.FilePath.Contains(".rekt-staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanDirectoryForCobolFilesAsync_SelectorModeSkipsStandaloneCopybooksButKeepsPrograms()
    {
        WriteFile("MAIN.cbl", "program");
        WriteFile("COMMON.cpy", "copybook");
        var originalValue = Environment.GetEnvironmentVariable("SELECTOR_MODE");

        try
        {
            Environment.SetEnvironmentVariable("SELECTOR_MODE", "TrUe");

            var files = await CreateFileHelper().ScanDirectoryForCobolFilesAsync(_root);

            files.Should().ContainSingle();
            files[0].Should().BeEquivalentTo(new
            {
                FileName = "MAIN.cbl",
                Content = "program",
                IsCopybook = false
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("SELECTOR_MODE", originalValue);
        }
    }

    [Fact]
    public async Task ScanDirectoryForCobolFilesAsync_ExistingEmptyDirectory_ReturnsNoFiles()
    {
        Directory.CreateDirectory(_root);

        var files = await CreateFileHelper().ScanDirectoryForCobolFilesAsync(_root);

        files.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanDirectoryForCobolFilesAsync_MissingDirectory_ThrowsDirectoryNotFoundException()
    {
        var act = () => CreateFileHelper().ScanDirectoryForCobolFilesAsync(_root);

        var exception = await act.Should().ThrowAsync<DirectoryNotFoundException>();

        exception.Which.Message.Should().Be($"Directory not found: {_root}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SELECTOR_MODE", _originalSelectorMode);
        }
    }

    private static FileHelper CreateFileHelper() =>
        new(new Mock<ILogger<FileHelper>>().Object);

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
