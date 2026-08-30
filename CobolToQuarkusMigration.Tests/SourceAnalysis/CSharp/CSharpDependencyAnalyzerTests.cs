using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.CSharp;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.SourceAnalysis.CSharp;

public sealed class CSharpDependencyAnalyzerTests
{
    private readonly CSharpDependencyAnalyzer _analyzer = new();

    [Fact]
    public void Language_ReturnsCSharp()
    {
        _analyzer.Language.Should().Be(SourceLanguage.CSharp);
    }

    [Fact]
    public async Task AnalyzeAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> act = () => _analyzer.AnalyzeAsync(
            Array.Empty<SourceFile>(),
            Array.Empty<Models.SourceAnalysis>(),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AnalyzeAsync_AnalysesWithDuplicateFiveFieldDependencies_DeduplicatesAndCalculatesMetrics()
    {
        var duplicate = Dependency("Payments.cs", "Ledger.cs", "INVOKES", 42, "payment posted");
        var analyses = new[]
        {
            new Models.SourceAnalysis
            {
                Dependencies =
                [
                    duplicate,
                    Dependency("Payments.cs", "Ledger.cs", "INVOKES", 42, "payment posted")
                ]
            },
            new Models.SourceAnalysis
            {
                Dependencies =
                [
                    Dependency("Payments.cs", "Ledger.cs", "INVOKES", 42, "payment reversed")
                ]
            }
        };
        var files = new[]
        {
            new SourceFile { FileName = "Orders.cs" },
            new SourceFile { FileName = "orders.cs" }
        };

        var map = await _analyzer.AnalyzeAsync(files, analyses);

        map.Dependencies.Should().HaveCount(2);
        map.Dependencies.Should().BeEquivalentTo(new[]
        {
            new
            {
                SourceFile = "Payments.cs",
                TargetFile = "Ledger.cs",
                DependencyType = "INVOKES",
                LineNumber = 42,
                Context = "payment posted"
            },
            new
            {
                SourceFile = "Payments.cs",
                TargetFile = "Ledger.cs",
                DependencyType = "INVOKES",
                LineNumber = 42,
                Context = "payment reversed"
            }
        });
        map.Metrics.TotalPrograms.Should().Be(3);
        map.Metrics.TotalDependencies.Should().Be(2);
        map.Metrics.AverageDependenciesPerProgram.Should().BeApproximately(2d / 3d, 0.0000001);
    }

    [Fact]
    public async Task AnalyzeAsync_NoDependencies_ReturnsZeroSafeMetrics()
    {
        var map = await _analyzer.AnalyzeAsync(
            Array.Empty<SourceFile>(),
            new[] { new Models.SourceAnalysis() });

        map.Dependencies.Should().BeEmpty();
        map.Metrics.TotalPrograms.Should().Be(0);
        map.Metrics.TotalDependencies.Should().Be(0);
        map.Metrics.AverageDependenciesPerProgram.Should().Be(0);
        map.MermaidDiagram.Should().Be($"flowchart LR{Environment.NewLine}");
    }

    [Fact]
    public async Task AnalyzeAsync_EncounterOrderedDependencies_EmitsStableMermaidNodesAndLabels()
    {
        var map = await _analyzer.AnalyzeAsync(
            Array.Empty<SourceFile>(),
            new[]
            {
                new Models.SourceAnalysis
                {
                    Dependencies =
                    [
                        Dependency("Entry.cs", "Service.cs", "CALLS", 3, "entry point"),
                        Dependency("Service.cs", "Store.cs", "READS", 8, "load data")
                    ]
                }
            });

        var firstEdge = "n0[\"Entry.cs\"] -->|CALLS| n1[\"Service.cs\"]";
        var secondEdge = "n1[\"Service.cs\"] -->|READS| n2[\"Store.cs\"]";
        map.MermaidDiagram.Should().Contain(firstEdge).And.Contain(secondEdge);
        map.MermaidDiagram.IndexOf(firstEdge, StringComparison.Ordinal)
            .Should().BeLessThan(map.MermaidDiagram.IndexOf(secondEdge, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_QuotedNames_EscapesDoubleQuotesToSingleQuotesInMermaid()
    {
        var map = await _analyzer.AnalyzeAsync(
            Array.Empty<SourceFile>(),
            new[]
            {
                new Models.SourceAnalysis
                {
                    Dependencies =
                    [
                        Dependency("\"Entry\".cs", "Target\"Service.cs", "READ\"", 1, "quoted")
                    ]
                }
            });

        map.MermaidDiagram.Should()
            .Contain("n0[\"'Entry'.cs\"] -->|READ'| n1[\"Target'Service.cs\"]")
            .And.NotContain("\"Entry\".cs")
            .And.NotContain("Target\"Service.cs");
    }

    private static SourceDependency Dependency(
        string source,
        string target,
        string kind,
        int lineNumber,
        string context) =>
        new()
        {
            Source = source,
            Target = target,
            Kind = kind,
            LineNumber = lineNumber,
            Context = context
        };
}
