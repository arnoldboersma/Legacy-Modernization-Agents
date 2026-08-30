using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.CSharp;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.SourceAnalysis.CSharp;

[CollectionDefinition(nameof(CSharpSourceAnalyzerCollection), DisableParallelization = true)]
public sealed class CSharpSourceAnalyzerCollection;

[Collection(nameof(CSharpSourceAnalyzerCollection))]
public sealed class CSharpSourceAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "test-artifacts",
        $"csharp-source-analyzer-{Guid.NewGuid():N}");
    private readonly string _orphanRoot = Path.Combine(
        Path.GetTempPath(),
        $"csharp-source-analyzer-orphan-{Guid.NewGuid():N}");

    public CSharpSourceAnalyzerTests()
    {
        CreateFixtureSolution();
    }

    [Fact]
    public void Language_ReturnsCSharp()
    {
        CreateAnalyzer().Language.Should().Be(SourceLanguage.CSharp);
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyFiles_ReturnsEmptyList()
    {
        var analyses = await CreateAnalyzer().AnalyzeAsync([]);

        analyses.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_NonCSharpFile_ThrowsArgumentException()
    {
        var files = new[]
        {
            new SourceFile
            {
                Language = SourceLanguage.Cobol,
                FileName = "Program.cbl",
                FilePath = Path.Combine(_root, "Program.cbl")
            }
        };

        Func<Task> act = () => CreateAnalyzer().AnalyzeAsync(files);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sourceFiles")
            .WithMessage("C# analysis requires C# source files.*");
    }

    [Fact]
    public async Task AnalyzeAsync_CSharpFileWithoutAncestorSolution_ThrowsInvalidOperationException()
    {
        var orphan = Path.Combine(_orphanRoot, "Orphan.cs");
        Directory.CreateDirectory(_orphanRoot);
        File.WriteAllText(orphan, "public sealed class Orphan { }");

        Func<Task> act = () => CreateAnalyzer().AnalyzeAsync([CreateSourceFile(orphan)]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No .sln or .csproj file was found for the C# source files.");
    }

    [Fact]
    public async Task AnalyzeAsync_TwoProjectFixture_EmitsProjectFactsAndReferences()
    {
        var semanticPath = FixturePath("App", "Semantic.cs");

        var analyses = await CreateAnalyzer().AnalyzeAsync([CreateSourceFile(semanticPath)]);

        var projects = analyses.Where(analysis => analysis.Summary == "MSBuild project").ToList();
        projects.Should().HaveCount(2);

        var library = projects.Should().ContainSingle(analysis => analysis.FileName == "Library").Which;
        library.FilePath.Should().Be(FixturePath("Library", "Library.csproj"));
        AssertFact(library, "Project", "Library", null, FixturePath("Library", "Library.csproj"), 0);

        var app = projects.Should().ContainSingle(analysis => analysis.FileName == "App").Which;
        app.FilePath.Should().Be(FixturePath("App", "App.csproj"));
        AssertFact(app, "Project", "App", null, FixturePath("App", "App.csproj"), 0);
        AssertFact(app, "NuGetPackage", "Demo.Package", "App", "1.2.3", 0);
        AssertDependency(
            app,
            "App",
            "Library",
            "PROJECT_REFERENCE",
            0,
            FixturePath("App", "App.csproj"));
        AssertDependency(
            app,
            "App",
            "Demo.Package",
            "NUGET_PACKAGE",
            0,
            FixturePath("App", "App.csproj"));
    }

    [Fact]
    public async Task AnalyzeAsync_RequestedDocument_EmitsOnlyRequestedDocumentAnalysis()
    {
        var semanticPath = FixturePath("App", "Semantic.cs");
        var ignoredPath = FixturePath("App", "Ignored.cs");

        var analyses = await CreateAnalyzer().AnalyzeAsync([CreateSourceFile(semanticPath)]);

        var document = analyses.Should().ContainSingle(analysis => analysis.FilePath == semanticPath).Which;
        document.Should().BeEquivalentTo(new
        {
            Language = SourceLanguage.CSharp,
            FileName = "Semantic.cs",
            FilePath = semanticPath,
            IsSupportFile = false,
            Summary = "C# document in App"
        });
        analyses.Should().NotContain(analysis => analysis.FilePath == ignoredPath);
    }

    [Fact]
    public async Task AnalyzeAsync_SemanticFixture_EmitsFactsAndRelationshipsWithLineNumbers()
    {
        var semanticPath = FixturePath("App", "Semantic.cs");

        var analyses = await CreateAnalyzer().AnalyzeAsync([CreateSourceFile(semanticPath)]);

        var analysis = analyses.Should().ContainSingle(item => item.FilePath == semanticPath).Which;
        AssertFact(analysis, "Namespace", "Demo.Domain", null, null, 3);
        AssertFact(analysis, "Interface", "Demo.Domain.IWorker", null, null, 5);
        AssertFact(analysis, "Class", "Demo.Domain.Worker", null, null, 14);
        AssertFact(analysis, "Field", "Demo.Domain.Worker._count", "Demo.Domain.Worker", "int", 16);
        AssertFact(analysis, "Property", "Demo.Domain.Worker.Count", "Demo.Domain.Worker", "int", 17);
        AssertFact(
            analysis,
            "Event",
            "Demo.Domain.Worker.Changed",
            "Demo.Domain.Worker",
            "System.EventHandler",
            18);
        AssertFact(
            analysis,
            "Constructor",
            "Demo.Domain.Worker.Worker()",
            "Demo.Domain.Worker",
            null,
            20);
        AssertFact(analysis, "Method", "Demo.Domain.Worker.Run()", "Demo.Domain.Worker", null, 24);
        AssertDependency(analysis, "Demo.Domain.Worker", "Demo.Domain.BaseWorker", "INHERITS", 14, semanticPath);
        AssertDependency(analysis, "Demo.Domain.Worker", "Demo.Domain.IWorker", "IMPLEMENTS", 14, semanticPath);
        AssertDependency(analysis, "Semantic.cs", "Demo.Domain.Worker.Helper()", "INVOKES", 26, semanticPath);
        AssertDependency(
            analysis,
            "Demo.Domain.Worker",
            "Demo.Domain.Worker.Changed",
            "EVENT_PRODUCER",
            18,
            semanticPath);
        AssertDependency(
            analysis,
            "Semantic.cs",
            "Demo.Domain.Worker.Changed",
            "EVENT_CONSUMER",
            39,
            semanticPath);
    }

    [Fact]
    public async Task AnalyzeAsync_ControllerAndEndpointFixture_EmitsRouteAndHttpFactsAndEdges()
    {
        var controllerPath = FixturePath("App", "Controller.cs");

        var analyses = await CreateAnalyzer().AnalyzeAsync([CreateSourceFile(controllerPath)]);

        var analysis = analyses.Should().ContainSingle(item => item.FilePath == controllerPath).Which;
        AssertFact(analysis, "AspNetController", "Demo.Web.WidgetsController", null, "\"api/widgets\"", 12);
        AssertFact(
            analysis,
            "AspNetEndpoint",
            "Demo.Web.WidgetsController.Get()",
            "Demo.Web.WidgetsController",
            "HttpGet",
            15);
        AssertDependency(
            analysis,
            "Demo.Web.WidgetsController",
            "Demo.Web.WidgetsController.Get()",
            "HttpGet",
            15,
            controllerPath);
    }

    [Fact]
    public async Task AnalyzeAsync_LocalGenericAddSingletonInvocation_EmitsDiRegistration()
    {
        var diPath = FixturePath("App", "DependencyInjection.cs");

        var analyses = await CreateAnalyzer().AnalyzeAsync([CreateSourceFile(diPath)]);

        var analysis = analyses.Should().ContainSingle(item => item.FilePath == diPath).Which;
        AssertDependency(
            analysis,
            "Demo.Di.IService",
            "Demo.Di.Service",
            "DI_REGISTRATION",
            23,
            diPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_orphanRoot))
            Directory.Delete(_orphanRoot, recursive: true);
    }

    private static CSharpSourceAnalyzer CreateAnalyzer() =>
        new(NullLogger<CSharpSourceAnalyzer>.Instance);

    private string FixturePath(params string[] segments) => Path.Combine([_root, .. segments]);

    private static SourceFile CreateSourceFile(string path) =>
        new()
        {
            Language = SourceLanguage.CSharp,
            FileName = Path.GetFileName(path),
            FilePath = path,
            Content = File.ReadAllText(path)
        };

    private void CreateFixtureSolution()
    {
        WriteFile(
            "Fixture.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Library", "Library/Library.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App/App.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    {22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
                EndGlobalSection
            EndGlobal
            """);
        WriteFile(
            Path.Combine("Library", "Library.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        WriteFile(
            Path.Combine("Library", "Library.cs"),
            """
            namespace Demo.Library;

            public sealed class SharedService
            {
            }
            """);
        WriteFile(
            Path.Combine("App", "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>disable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Library/Library.csproj" />
                <PackageReference Include="Demo.Package" Version="1.2.3" />
              </ItemGroup>
            </Project>
            """);
        WriteFile(
            Path.Combine("App", "Semantic.cs"),
            """
            using System;

            namespace Demo.Domain;

            public interface IWorker
            {
                void Run();
            }

            public class BaseWorker
            {
            }

            public sealed class Worker : BaseWorker, IWorker
            {
                private int _count;
                public int Count => _count;
                public event EventHandler Changed;

                public Worker()
                {
                }

                public void Run()
                {
                    Helper();
                    Changed?.Invoke(this, EventArgs.Empty);
                }

                private void Helper()
                {
                }
            }

            public sealed class Subscriber
            {
                public void Subscribe(Worker worker)
                {
                    worker.Changed += OnChanged;
                }

                private void OnChanged(object sender, EventArgs args) { }
            }
            """);
        WriteFile(
            Path.Combine("App", "Controller.cs"),
            """
            using System;

            namespace Demo.Web;

            public sealed class RouteAttribute : Attribute
            { public RouteAttribute(string template) { } }

            public sealed class HttpGetAttribute : Attribute
            {
            }

            [Route("api/widgets")]
            public sealed class WidgetsController
            {
                [HttpGet]
                public void Get()
                {
                }
            }
            """);
        WriteFile(
            Path.Combine("App", "DependencyInjection.cs"),
            """
            namespace Demo.Di;

            public interface IService
            {
            }

            public sealed class Service : IService
            {
            }

            public static class ServiceCollectionExtensions
            {
                public static void AddSingleton<TService, TImplementation>(this object services)
                    where TImplementation : TService
                {
                }
            }

            public sealed class Bootstrap
            {
                public void Configure(object services)
                {
                    services.AddSingleton<IService, Service>();
                }
            }
            """);
        WriteFile(
            Path.Combine("App", "Ignored.cs"),
            """
            namespace Demo.App;

            public sealed class Ignored
            {
            }
            """);
    }

    private void WriteFile(string relativePath, string contents)
    {
        var path = FixturePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void AssertFact(
        CobolToQuarkusMigration.Models.SourceAnalysis analysis,
        string kind,
        string name,
        string? container,
        string? detail,
        int lineNumber)
    {
        analysis.Facts.Should().ContainSingle(fact =>
            fact.Kind == kind && fact.Name == name && fact.LineNumber == lineNumber).Which
            .Should().BeEquivalentTo(new SourceFact
            {
                Kind = kind,
                Name = name,
                Container = container,
                Detail = detail,
                LineNumber = lineNumber
            });
    }

    private static void AssertDependency(
        CobolToQuarkusMigration.Models.SourceAnalysis analysis,
        string source,
        string target,
        string kind,
        int lineNumber,
        string context)
    {
        analysis.Dependencies.Should().ContainSingle(dependency =>
            dependency.Source == source &&
            dependency.Target == target &&
            dependency.Kind == kind &&
            dependency.LineNumber == lineNumber).Which
            .Should().BeEquivalentTo(new SourceDependency
            {
                Source = source,
                Target = target,
                Kind = kind,
                LineNumber = lineNumber,
                Context = context
            });
    }
}
