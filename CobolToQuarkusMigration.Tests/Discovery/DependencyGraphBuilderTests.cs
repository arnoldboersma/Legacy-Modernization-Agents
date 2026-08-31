using CobolToQuarkusMigration.Discovery.Graph;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Roles;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

public sealed class DependencyGraphBuilderTests
{
    [Fact]
    public void BuildProjectGraph_EmitsProjectNodeAndReferenceEdge()
    {
        var appProj = new ClassifierSourceFile("App/App.csproj", @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\Data\Data.csproj"" />
  </ItemGroup>
</Project>");
        var dataProj = new ClassifierSourceFile("Data/Data.csproj", @"<Project Sdk=""Microsoft.NET.Sdk"" />");

        var result = DependencyGraphBuilder.BuildProjectGraph(new[] { appProj, dataProj });

        result.Nodes.Should().Contain(n => n.Kind == GraphNodeKind.Project && n.SymbolLocator == "App");
        result.Nodes.Should().Contain(n => n.Kind == GraphNodeKind.Project && n.SymbolLocator == "Data");
        result.Edges.Should().ContainSingle(e =>
            e.Kind == GraphEdgeKind.DependsOnProject &&
            e.FromSymbolLocator == "App" &&
            e.ToSymbolLocator == "Data");
        result.Edges.Single().Citations.Should().NotBeEmpty();
    }

    [Fact]
    public void BuildSourceGraph_DbContextWithDbSet_EmitsMapsToEntityEdge()
    {
        var file = new ClassifierSourceFile("Data/ApplicationContext.cs", @"
using Microsoft.EntityFrameworkCore;
namespace App.Data;
public class ApplicationContext : DbContext
{
    public DbSet<Widget> Widgets { get; set; }
}");

        var result = DependencyGraphBuilder.BuildSourceGraph(new[] { file });

        result.Nodes.Should().Contain(n => n.Kind == GraphNodeKind.DbContext && n.SymbolLocator == "App.Data.ApplicationContext");
        result.Nodes.Should().Contain(n => n.Kind == GraphNodeKind.DbEntity && n.SymbolLocator == "Widget");
        result.Edges.Should().Contain(e =>
            e.Kind == GraphEdgeKind.MapsToEntity &&
            e.FromSymbolLocator == "App.Data.ApplicationContext" &&
            e.ToSymbolLocator == "Widget");
    }

    [Fact]
    public void BuildSourceGraph_ControllerWithHttpGet_EmitsRouteNodeAndExposesRouteEdge()
    {
        var file = new ClassifierSourceFile("Controllers/WidgetController.cs", @"
using Microsoft.AspNetCore.Mvc;
namespace App.Controllers;
public class WidgetController : ControllerBase
{
    [HttpGet]
    public string Get() => ""ok"";
}");

        var result = DependencyGraphBuilder.BuildSourceGraph(new[] { file });

        result.Nodes.Should().Contain(n => n.Kind == GraphNodeKind.Route && n.SymbolLocator.Contains("WidgetController.Get", StringComparison.Ordinal));
        result.Edges.Should().Contain(e =>
            e.Kind == GraphEdgeKind.ExposesRoute &&
            e.FromSymbolLocator == "App.Controllers.WidgetController");
    }

    [Fact]
    public void BuildSourceGraph_ConstructorInterfaceParameter_EmitsInjectsEdge()
    {
        var file = new ClassifierSourceFile("Services/WidgetService.cs", @"
namespace App.Services;
public class WidgetService
{
    private readonly IWidgetRepository _repository;
    public WidgetService(IWidgetRepository repository)
    {
        _repository = repository;
    }
}");

        var result = DependencyGraphBuilder.BuildSourceGraph(new[] { file });

        result.Edges.Should().Contain(e =>
            e.Kind == GraphEdgeKind.Injects &&
            e.FromSymbolLocator == "App.Services.WidgetService" &&
            e.ToSymbolLocator == "IWidgetRepository");
    }

    [Fact]
    public void BuildSourceGraph_TypeDeclaration_EmitsContainsTypeEdgeUnderNamespace()
    {
        var file = new ClassifierSourceFile("Models/Widget.cs", @"
namespace App.Models;
public class Widget
{
}");

        var result = DependencyGraphBuilder.BuildSourceGraph(new[] { file });

        result.Nodes.Should().Contain(n => n.Kind == GraphNodeKind.Namespace && n.SymbolLocator == "App.Models");
        result.Nodes.Should().Contain(n => n.Kind == GraphNodeKind.Type && n.SymbolLocator == "App.Models.Widget");
        result.Edges.Should().Contain(e =>
            e.Kind == GraphEdgeKind.ContainsType &&
            e.FromSymbolLocator == "App.Models" &&
            e.ToSymbolLocator == "App.Models.Widget");
    }

    [Fact]
    public void BuildSourceGraph_ExcludesBuildOutputPaths()
    {
        var files = new[]
        {
            new ClassifierSourceFile("bin/Debug/Generated.cs", "namespace Gen; public class Generated {}"),
            new ClassifierSourceFile("src/Real.cs", "namespace App; public class Real {}"),
        };

        var result = DependencyGraphBuilder.BuildSourceGraph(files);

        result.Nodes.Should().NotContain(n => n.SymbolLocator.Contains("Generated", StringComparison.Ordinal));
        result.Nodes.Should().Contain(n => n.SymbolLocator == "App.Real");
    }
}
