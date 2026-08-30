using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.CSharp;
using FluentAssertions;
using Xunit;
using SourceAnalysisModel = CobolToQuarkusMigration.Models.SourceAnalysis;

namespace CobolToQuarkusMigration.Tests.SourceAnalysis.CSharp;

public sealed class CSharpBusinessLogicExtractorTests
{
    [Fact]
    public void CreateUserPrompt_IncludesSourceAndDeterministicFacts()
    {
        var sourceFile = new SourceFile
        {
            Language = SourceLanguage.CSharp,
            FileName = "OrdersController.cs",
            FilePath = "/src/OrdersController.cs",
            Content = "public sealed class OrdersController { }"
        };
        var analysis = new SourceAnalysisModel
        {
            Language = SourceLanguage.CSharp,
            FilePath = sourceFile.FilePath,
            Facts = [new SourceFact { Kind = "AspNetEndpoint", Name = "GetOrder", Detail = "GET /orders/{id}", LineNumber = 12 }],
            Dependencies = [new SourceDependency { Source = "OrdersController", Target = "IOrderService", Kind = "INVOKES", LineNumber = 14 }]
        };

        var prompt = CSharpBusinessLogicExtractor.CreateUserPrompt(sourceFile, analysis, null);

        prompt.Should().Contain("OrdersController.cs")
            .And.Contain("Line 12: AspNetEndpoint GetOrder (GET /orders/{id})")
            .And.Contain("Line 14: OrdersController INVOKES IOrderService")
            .And.Contain("     1 | public sealed class OrdersController { }");
    }

    [Theory]
    [InlineData("OrdersController.cs", "Web", true)]
    [InlineData("OrdersControllerTests.cs", "Web.Tests", false)]
    [InlineData("ErrorMessages.Designer.cs", "Web", false)]
    [InlineData("GlobalUsings.cs", "Web", false)]
    public void IsDocumentable_ExcludesTestsAndGeneratedSources(
        string fileName,
        string projectName,
        bool expected)
    {
        var sourceFile = new SourceFile
        {
            FileName = fileName,
            FilePath = Path.Combine("/src", projectName, fileName),
            ProjectName = projectName
        };

        CSharpBusinessLogicExtractor.IsDocumentable(sourceFile).Should().Be(expected);
    }
}
