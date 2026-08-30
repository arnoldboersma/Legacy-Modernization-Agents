using System.Text;
using CobolToQuarkusMigration.Helpers;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.Persistence;
using CobolToQuarkusMigration.Processes;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Processes;

public sealed class ReverseEngineeringProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "test-artifacts",
        $"reverse-engineering-process-{Guid.NewGuid():N}");
    private readonly string _outputFolder;
    private readonly Mock<ILogger<ReverseEngineeringProcess>> _processLogger = new();
    private readonly Mock<ILogger> _enhancedLoggerLogger = new();
    private readonly EnhancedLogger _enhancedLogger;

    public ReverseEngineeringProcessTests()
    {
        _outputFolder = Path.Combine(_root, "output");
        _enhancedLogger = new EnhancedLogger(
            _enhancedLoggerLogger.Object,
            Path.Combine(_root, "logs"),
            "reverse-engineering-tests");
    }

    [Fact]
    public void Constructor_MismatchedDiscoveryAndAnalyzerLanguage_ThrowsArgumentException()
    {
        var contracts = CreateContracts();
        contracts.Analyzer.SetupGet(analyzer => analyzer.Language).Returns(SourceLanguage.Cobol);

        Action act = () => CreateProcess(contracts);

        act.Should().Throw<ArgumentException>()
            .WithMessage("All source-analysis services must target the same language.");
    }

    [Fact]
    public void Constructor_MismatchedDiscoveryAndExtractorLanguage_ThrowsArgumentException()
    {
        var contracts = CreateContracts();
        contracts.Extractor.SetupGet(extractor => extractor.Language).Returns(SourceLanguage.Cobol);

        Action act = () => CreateProcess(contracts);

        act.Should().Throw<ArgumentException>()
            .WithMessage("All source-analysis services must target the same language.");
    }

    [Fact]
    public void Constructor_MismatchedDiscoveryAndDependencyAnalyzerLanguage_ThrowsArgumentException()
    {
        var contracts = CreateContracts();
        contracts.DependencyAnalyzer.SetupGet(analyzer => analyzer.Language).Returns(SourceLanguage.Cobol);

        Action act = () => CreateProcess(contracts);

        act.Should().Throw<ArgumentException>()
            .WithMessage("All source-analysis services must target the same language.");
    }

    [Fact]
    public void Constructor_FormatterWithoutLanguageContract_IsAccepted()
    {
        var contracts = CreateContracts();

        var process = CreateProcess(contracts);

        process.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_WithDiscoveredFiles_CallsContractsInOrderAndMapsResult()
    {
        var contracts = CreateContracts();
        var discoveredFiles = new[]
        {
            SourceFile("Orders.cs", isSupportFile: false),
            SourceFile("Shared.cs", isSupportFile: true)
        };
        var analyses = new[]
        {
            Analysis("Orders.cs", "Processes orders"),
            Analysis("Shared.cs", "Shared contracts")
        };
        var businessLogic = new[] { BusinessLogic("Orders.cs", isCopybook: false) };
        var dependencyMap = DependencyMap();
        var calls = new List<string>();
        IReadOnlyList<SourceFile>? analyzerFiles = null;
        IReadOnlyList<SourceFile>? extractorFiles = null;
        IReadOnlyList<Models.SourceAnalysis>? extractorAnalyses = null;
        IReadOnlyList<SourceFile>? dependencyFiles = null;
        IReadOnlyList<Models.SourceAnalysis>? dependencyAnalyses = null;

        contracts.Discovery
            .Setup(discovery => discovery.DiscoverAsync(SourceFolder, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("discovery"))
            .ReturnsAsync(discoveredFiles);
        contracts.Analyzer
            .Setup(analyzer => analyzer.AnalyzeAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<Action<int, int>?>(),
                It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<SourceFile> files, Action<int, int>? progress, CancellationToken token) =>
            {
                analyzerFiles = files;
                calls.Add("analysis");
            })
            .ReturnsAsync(analyses);
        contracts.Extractor
            .Setup(extractor => extractor.ExtractAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<IReadOnlyList<Models.SourceAnalysis>>(),
                It.IsAny<Glossary?>(),
                It.IsAny<Action<int, int>?>(),
                It.IsAny<CancellationToken>()))
            .Callback((
                IReadOnlyList<SourceFile> files,
                IReadOnlyList<Models.SourceAnalysis> sourceAnalyses,
                Glossary? glossary,
                Action<int, int>? progress,
                CancellationToken token) =>
            {
                extractorFiles = files;
                extractorAnalyses = sourceAnalyses;
                calls.Add("extraction");
            })
            .ReturnsAsync(businessLogic);
        contracts.DependencyAnalyzer
            .Setup(analyzer => analyzer.AnalyzeAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<IReadOnlyList<Models.SourceAnalysis>>(),
                It.IsAny<CancellationToken>()))
            .Callback((
                IReadOnlyList<SourceFile> files,
                IReadOnlyList<Models.SourceAnalysis> sourceAnalyses,
                CancellationToken token) =>
            {
                dependencyFiles = files;
                dependencyAnalyses = sourceAnalyses;
                calls.Add("dependency mapping");
            })
            .ReturnsAsync(dependencyMap);
        ConfigureFormatter(contracts);

        var result = await CreateProcess(contracts).RunAsync(SourceFolder, _outputFolder);

        calls.Should().Equal("discovery", "analysis", "extraction", "dependency mapping");
        analyzerFiles.Should().BeSameAs(discoveredFiles);
        extractorFiles.Should().BeSameAs(discoveredFiles);
        dependencyFiles.Should().BeSameAs(discoveredFiles);
        extractorAnalyses.Should().BeSameAs(analyses);
        dependencyAnalyses.Should().BeSameAs(analyses);
        result.TechnicalAnalyses.Should().ContainInOrder(analyses);
        result.DependencyMap.Should().BeSameAs(dependencyMap);
    }

    [Fact]
    public async Task RunAsync_WithBusinessLogic_DerivesStoryFeatureAndRuleTotalsAndReportsProgress()
    {
        var contracts = CreateContracts();
        var extractedLogic = new[]
        {
            BusinessLogic(
                "Orders.cs",
                isCopybook: false,
                userStories: 2,
                features: 1,
                businessRules: 3),
            BusinessLogic(
                "Validation.cs",
                isCopybook: true,
                userStories: 1,
                features: 2,
                businessRules: 1)
        };
        ConfigureSuccessfulPipeline(contracts, businessLogic: extractedLogic);
        var progress = new List<(string Phase, int Step, int Total)>();

        var result = await CreateProcess(contracts).RunAsync(
            SourceFolder,
            _outputFolder,
            (phase, step, total) => progress.Add((phase, step, total)));

        result.BusinessLogicExtracts.Should().ContainInOrder(extractedLogic);
        result.TotalUserStories.Should().Be(3);
        result.TotalFeatures.Should().Be(3);
        result.TotalBusinessRules.Should().Be(4);
        progress.Should().Equal(
            ("Discovering source files", 1, 4),
            ("Analyzing source structure", 2, 4),
            ("Extracting business logic", 3, 4),
            ("Mapping dependencies", 4, 4));
    }

    [Fact]
    public async Task RunAsync_WithResults_WritesExpectedOutputFilesAndSuccessMetadata()
    {
        var contracts = CreateContracts();
        var dependencyMap = DependencyMap();
        ConfigureSuccessfulPipeline(
            contracts,
            discoveredFiles:
            [
                SourceFile("Orders.cs", isSupportFile: false),
                SourceFile("Shared.cs", isSupportFile: true)
            ],
            analyses: [Analysis("Orders.cs", "Processes paid orders")],
            businessLogic:
            [
                BusinessLogic(
                    "Orders.cs",
                    isCopybook: false,
                    userStories: 1,
                    features: 1,
                    businessRules: 1,
                    purpose: "Processes paid orders"),
                BusinessLogic("Shared.cs", isCopybook: true)
            ],
            dependencyMap: dependencyMap,
            technicalReport: "## Technical Analysis\n\nOrders structure is deterministic.");

        var result = await CreateProcess(contracts).RunAsync(SourceFolder, _outputFolder);

        var detailsPath = Path.Combine(_outputFolder, "reverse-engineering-details.md");
        var dependencyMapPath = Path.Combine(_outputFolder, "dependency-map.json");
        var diagramPath = Path.Combine(_outputFolder, "dependency-diagram.md");
        result.Success.Should().BeTrue();
        result.OutputFolder.Should().Be(_outputFolder);
        result.TotalFilesAnalyzed.Should().Be(2);
        File.Exists(detailsPath).Should().BeTrue();
        File.Exists(dependencyMapPath).Should().BeTrue();
        File.Exists(diagramPath).Should().BeTrue();
        Directory.GetFiles(_outputFolder).Select(Path.GetFileName).Should().BeEquivalentTo(
            "reverse-engineering-details.md",
            "dependency-map.json",
            "dependency-diagram.md");
        File.ReadAllText(detailsPath).Should()
            .Contain("# Reverse Engineering Details")
            .And.Contain("**Total Files Analyzed**: 2 (1 programs, 1 support files)")
            .And.Contain("Processes paid orders")
            .And.Contain("Orders structure is deterministic.");
        File.ReadAllText(dependencyMapPath).Should()
            .Contain("\"sourceFile\": \"Orders.cs\"")
            .And.Contain("\"targetFile\": \"Ledger.cs\"");
        File.ReadAllText(diagramPath).Should()
            .Contain("# Source Dependency Diagram")
            .And.Contain("flowchart LR")
            .And.Contain("Orders.cs --> Ledger.cs");
    }

    [Fact]
    public async Task RunAsync_EmptyDiscovery_ReturnsZeroFilesWithoutInvokingLaterCollaboratorsOrWritingOutput()
    {
        var contracts = CreateContracts();
        contracts.Discovery
            .Setup(discovery => discovery.DiscoverAsync(SourceFolder, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SourceFile>());

        var result = await CreateProcess(contracts).RunAsync(SourceFolder, _outputFolder);

        result.TotalFilesAnalyzed.Should().Be(0);
        result.Success.Should().BeFalse();
        result.TechnicalAnalyses.Should().BeEmpty();
        result.BusinessLogicExtracts.Should().BeEmpty();
        result.DependencyMap.Should().BeNull();
        Directory.Exists(_outputFolder).Should().BeFalse();
        contracts.Analyzer.Verify(
            analyzer => analyzer.AnalyzeAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<Action<int, int>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        contracts.Extractor.Verify(
            extractor => extractor.ExtractAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<IReadOnlyList<Models.SourceAnalysis>>(),
                It.IsAny<Glossary?>(),
                It.IsAny<Action<int, int>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        contracts.DependencyAnalyzer.Verify(
            analyzer => analyzer.AnalyzeAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<IReadOnlyList<Models.SourceAnalysis>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        contracts.Formatter.Verify(
            formatter => formatter.AppendTechnicalAnalysis(
                It.IsAny<StringBuilder>(),
                It.IsAny<IReadOnlyList<Models.SourceAnalysis>>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_FreshRepositoryRun_StartsSavesBusinessLogicAndDependenciesAndCompletes()
    {
        var contracts = CreateContracts();
        var businessLogic = new[] { BusinessLogic("Orders.cs", isCopybook: false, userStories: 1) };
        var dependencyMap = DependencyMap();
        ConfigureSuccessfulPipeline(contracts, businessLogic: businessLogic, dependencyMap: dependencyMap);
        var repository = new Mock<IMigrationRepository>(MockBehavior.Strict);
        var sequence = new MockSequence();
        repository.InSequence(sequence)
            .Setup(store => store.StartRunAsync(SourceFolder, _outputFolder, It.IsAny<CancellationToken>()))
            .ReturnsAsync(73);
        repository.InSequence(sequence)
            .Setup(store => store.SaveBusinessLogicAsync(
                73,
                It.Is<IEnumerable<BusinessLogic>>(items => items.SequenceEqual(businessLogic)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.InSequence(sequence)
            .Setup(store => store.SaveDependencyMapAsync(73, dependencyMap, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.InSequence(sequence)
            .Setup(store => store.CompleteRunAsync(
                73,
                "Completed",
                "Reverse Engineering Only",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateProcess(contracts, repository.Object).RunAsync(SourceFolder, _outputFolder);

        result.RunId.Should().Be(73);
        result.Success.Should().BeTrue();
        repository.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_ExistingRunId_SavesUnderSuppliedRunWithoutStartingOrCompleting()
    {
        var contracts = CreateContracts();
        var businessLogic = new[] { BusinessLogic("Orders.cs", isCopybook: false, businessRules: 2) };
        var dependencyMap = DependencyMap();
        ConfigureSuccessfulPipeline(contracts, businessLogic: businessLogic, dependencyMap: dependencyMap);
        var repository = new Mock<IMigrationRepository>(MockBehavior.Strict);
        repository
            .Setup(store => store.SaveBusinessLogicAsync(
                812,
                It.Is<IEnumerable<BusinessLogic>>(items => items.SequenceEqual(businessLogic)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(store => store.SaveDependencyMapAsync(812, dependencyMap, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateProcess(contracts, repository.Object).RunAsync(
            SourceFolder,
            _outputFolder,
            existingRunId: 812);

        result.RunId.Should().Be(812);
        result.Success.Should().BeTrue();
        repository.Verify(store => store.StartRunAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(store => store.CompleteRunAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_CollaboratorFailure_LogsAndRethrows()
    {
        var contracts = CreateContracts();
        var failure = new InvalidOperationException("deterministic analyzer failure");
        contracts.Discovery
            .Setup(discovery => discovery.DiscoverAsync(SourceFolder, It.IsAny<CancellationToken>()))
            .ReturnsAsync([SourceFile("Orders.cs", isSupportFile: false)]);
        contracts.Analyzer
            .Setup(analyzer => analyzer.AnalyzeAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<Action<int, int>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        Func<Task> act = () => CreateProcess(contracts).RunAsync(SourceFolder, _outputFolder);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Should().BeSameAs(failure);
        _processLogger.Invocations.Should().Contain(invocation =>
            invocation.Method.Name == nameof(ILogger.Log) &&
            invocation.Arguments.Count >= 4 &&
            invocation.Arguments[0].Equals(LogLevel.Error) &&
            ReferenceEquals(invocation.Arguments[3], failure) &&
            invocation.Arguments[2].ToString()!.Contains("Error during reverse engineering process"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string SourceFolder => Path.Combine(_root, "source");

    private ReverseEngineeringProcess CreateProcess(ContractMocks contracts, IMigrationRepository? repository = null) =>
        new(
            contracts.Discovery.Object,
            contracts.Analyzer.Object,
            contracts.Extractor.Object,
            contracts.DependencyAnalyzer.Object,
            contracts.Formatter.Object,
            _processLogger.Object,
            _enhancedLogger,
            repository);

    private static ContractMocks CreateContracts()
    {
        var discovery = new Mock<ISourceDiscovery>(MockBehavior.Strict);
        var analyzer = new Mock<ISourceAnalyzer>(MockBehavior.Strict);
        var extractor = new Mock<ISourceBusinessLogicExtractor>(MockBehavior.Strict);
        var dependencyAnalyzer = new Mock<IDependencyAnalyzer>(MockBehavior.Strict);
        var formatter = new Mock<ISourceAnalysisReportFormatter>(MockBehavior.Strict);
        discovery.SetupGet(service => service.Language).Returns(SourceLanguage.CSharp);
        analyzer.SetupGet(service => service.Language).Returns(SourceLanguage.CSharp);
        extractor.SetupGet(service => service.Language).Returns(SourceLanguage.CSharp);
        dependencyAnalyzer.SetupGet(service => service.Language).Returns(SourceLanguage.CSharp);
        return new ContractMocks(discovery, analyzer, extractor, dependencyAnalyzer, formatter);
    }

    private static void ConfigureFormatter(ContractMocks contracts, string technicalReport = "")
    {
        contracts.Formatter
            .Setup(formatter => formatter.AppendTechnicalAnalysis(
                It.IsAny<StringBuilder>(),
                It.IsAny<IReadOnlyList<Models.SourceAnalysis>>()))
            .Callback((StringBuilder builder, IReadOnlyList<Models.SourceAnalysis> analyses) =>
            {
                if (!string.IsNullOrEmpty(technicalReport))
                    builder.AppendLine(technicalReport);
            });
    }

    private static void ConfigureSuccessfulPipeline(
        ContractMocks contracts,
        IReadOnlyList<SourceFile>? discoveredFiles = null,
        IReadOnlyList<Models.SourceAnalysis>? analyses = null,
        IReadOnlyList<BusinessLogic>? businessLogic = null,
        DependencyMap? dependencyMap = null,
        string technicalReport = "")
    {
        discoveredFiles ??= [SourceFile("Orders.cs", isSupportFile: false)];
        analyses ??= [Analysis("Orders.cs", "Processes orders")];
        businessLogic ??= [BusinessLogic("Orders.cs", isCopybook: false)];
        dependencyMap ??= DependencyMap();
        contracts.Discovery
            .Setup(discovery => discovery.DiscoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(discoveredFiles);
        contracts.Analyzer
            .Setup(analyzer => analyzer.AnalyzeAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<Action<int, int>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(analyses);
        contracts.Extractor
            .Setup(extractor => extractor.ExtractAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<IReadOnlyList<Models.SourceAnalysis>>(),
                It.IsAny<Glossary?>(),
                It.IsAny<Action<int, int>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(businessLogic);
        contracts.DependencyAnalyzer
            .Setup(analyzer => analyzer.AnalyzeAsync(
                It.IsAny<IReadOnlyList<SourceFile>>(),
                It.IsAny<IReadOnlyList<Models.SourceAnalysis>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(dependencyMap);
        ConfigureFormatter(contracts, technicalReport);
    }

    private const string SourceFolderStatic = "__reverse-engineering-source__";

    private static SourceFile SourceFile(string fileName, bool isSupportFile) =>
        new()
        {
            Language = SourceLanguage.CSharp,
            FileName = fileName,
            FilePath = Path.Combine(SourceFolderStatic, fileName),
            Content = $"public sealed class {Path.GetFileNameWithoutExtension(fileName)} {{ }}",
            IsSupportFile = isSupportFile,
            ProjectName = "Orders"
        };

    private static Models.SourceAnalysis Analysis(string fileName, string summary) =>
        new()
        {
            Language = SourceLanguage.CSharp,
            FileName = fileName,
            FilePath = Path.Combine(SourceFolderStatic, fileName),
            Summary = summary
        };

    private static BusinessLogic BusinessLogic(
        string fileName,
        bool isCopybook,
        int userStories = 0,
        int features = 0,
        int businessRules = 0,
        string purpose = "") =>
        new()
        {
            FileName = fileName,
            FilePath = Path.Combine(SourceFolderStatic, fileName),
            IsCopybook = isCopybook,
            BusinessPurpose = purpose,
            UserStories = Enumerable.Range(1, userStories)
                .Select(index => new UserStory { Id = $"US-{index}", Title = $"Story {index}" })
                .ToList(),
            Features = Enumerable.Range(1, features)
                .Select(index => new FeatureDescription { Id = $"FT-{index}", Name = $"Feature {index}" })
                .ToList(),
            BusinessRules = Enumerable.Range(1, businessRules)
                .Select(index => new BusinessRule { Id = $"BR-{index}", Description = $"Rule {index}" })
                .ToList()
        };

    private static DependencyMap DependencyMap() =>
        new()
        {
            Dependencies =
            [
                new DependencyRelationship
                {
                    SourceFile = "Orders.cs",
                    TargetFile = "Ledger.cs",
                    DependencyType = "INVOKES",
                    LineNumber = 42,
                    Context = "Posts confirmed order"
                }
            ],
            MermaidDiagram = "flowchart LR\nOrders.cs --> Ledger.cs",
            AnalysisInsights = "Orders invoke the ledger",
            Metrics = new DependencyMetrics { TotalPrograms = 1, TotalDependencies = 1 }
        };

    private sealed record ContractMocks(
        Mock<ISourceDiscovery> Discovery,
        Mock<ISourceAnalyzer> Analyzer,
        Mock<ISourceBusinessLogicExtractor> Extractor,
        Mock<IDependencyAnalyzer> DependencyAnalyzer,
        Mock<ISourceAnalysisReportFormatter> Formatter);
}
