using CobolToQuarkusMigration.Cli;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Cli;

public sealed class RektScanCacheCommandGraphTests : IDisposable
{
    private readonly string _stagingDirectory = Path.Combine(
        Path.GetTempPath(),
        $"rekt-graph-{Guid.NewGuid():N}");

    public RektScanCacheCommandGraphTests()
    {
        Directory.CreateDirectory(_stagingDirectory);
    }

    [Fact]
    public void BuildGraphFromStagingDir_NestedCobolAndCopybooks_IndexesProgramsAndTransitiveCopybooks()
    {
        WriteFile("programs/BILLING.cbl", "       COPY COMMON.\n       STOP RUN.");
        WriteFile("programs/LEDGER.cob", "       COPY SHARED.\n       STOP RUN.");
        WriteFile("copybooks/COMMON.cpy", "       COPY SHARED.");
        WriteFile("copybooks/nested/SHARED.cpy", "       01 SHARED-FIELD PIC X.");

        var graph = RektScanCacheCommand.BuildGraphFromStagingDir(
            _stagingDirectory,
            NullLogger.Instance);

        graph.GetHash("BILLING.cbl").Should().NotBeNull();
        graph.GetHash("LEDGER.cob").Should().NotBeNull();
        graph.GetHash("COMMON.cpy").Should().NotBeNull();
        graph.GetHash("SHARED.cpy").Should().NotBeNull();

        var billingDependencies = graph.BuildDependencySnapshot("BILLING.cbl");
        billingDependencies.Keys.Should().ContainInOrder("COMMON.cpy", "SHARED.cpy");
        billingDependencies.Values.Should().OnlyContain(hash => hash.Length == 64);

        graph.BuildDependencySnapshot("LEDGER.cob").Keys
            .Should().ContainSingle()
            .Which.Should().Be("SHARED.cpy");
    }

    [Fact]
    public void BuildGraphFromStagingDir_CSharpFile_IsExcludedEvenWhenItContainsCopyDirective()
    {
        WriteFile("programs/BILLING.cbl", "       COPY COMMON.\n       STOP RUN.");
        WriteFile("copybooks/COMMON.cpy", "       01 COMMON-FIELD PIC X.");
        WriteFile("source/IGNORED.cs", "COPY CS_ONLY");
        WriteFile("source/CS_ONLY.cpy", "       01 CSHARP-ONLY PIC X.");

        var graph = RektScanCacheCommand.BuildGraphFromStagingDir(
            _stagingDirectory,
            NullLogger.Instance);

        graph.GetHash("IGNORED.cs").Should().BeNull();
        graph.BuildDependencySnapshot("BILLING.cbl").Keys.Should().ContainSingle()
            .Which.Should().Be("COMMON.cpy");
        graph.GetHash("CS_ONLY.cpy").Should().NotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_stagingDirectory))
            Directory.Delete(_stagingDirectory, recursive: true);
    }

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(
            _stagingDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
