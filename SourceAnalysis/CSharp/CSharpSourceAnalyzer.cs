using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.CSharp;

/// <summary>Extracts compile-time C# facts using MSBuildWorkspace and Roslyn semantic models.</summary>
public sealed class CSharpSourceAnalyzer(ILogger<CSharpSourceAnalyzer> logger) : ISourceAnalyzer
{
    private static readonly SymbolDisplayFormat SymbolFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

    public SourceLanguage Language => SourceLanguage.CSharp;

    public async Task<IReadOnlyList<Models.SourceAnalysis>> AnalyzeAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceFiles.Any(file => file.Language != SourceLanguage.CSharp))
            throw new ArgumentException("C# analysis requires C# source files.", nameof(sourceFiles));
        if (sourceFiles.Count == 0)
            return [];

        RegisterMsBuild();
        var analyses = new List<Models.SourceAnalysis>();
        var processed = 0;
        var sourceFilesByWorkspace = sourceFiles.GroupBy(sourceFile =>
                FindWorkspacePath(sourceFile) ??
                throw new InvalidOperationException("No .sln or .csproj file was found for the C# source files."))
            .ToList();

        foreach (var workspaceFiles in sourceFilesByWorkspace)
        {
            using var workspace = MSBuildWorkspace.Create();
            var workspacePath = workspaceFiles.Key;
            workspace.WorkspaceFailed += (_, args) => logger.LogWarning("MSBuild workspace: {Message}", args.Diagnostic.Message);
            var solution = workspacePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                ? await workspace.OpenSolutionAsync(workspacePath, cancellationToken: cancellationToken)
                : (await workspace.OpenProjectAsync(workspacePath, cancellationToken: cancellationToken)).Solution;
            var solutionAnalysis = new Models.SourceAnalysis
            {
                Language = SourceLanguage.CSharp,
                FileName = Path.GetFileName(workspacePath),
                FilePath = workspacePath,
                Summary = "MSBuild solution"
            };
            AddFact(solutionAnalysis,
                workspacePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ? "Solution" : "ProjectWorkspace",
                Path.GetFileNameWithoutExtension(workspacePath),
                null,
                workspacePath,
                0);
            analyses.Add(solutionAnalysis);
            var requestedPaths = workspaceFiles.Select(file => Path.GetFullPath(file.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                analyses.Add(CreateProjectAnalysis(project, solution));
                foreach (var document in project.Documents.Where(document =>
                             document.FilePath is not null && requestedPaths.Contains(Path.GetFullPath(document.FilePath))))
                {
                    var analysis = await AnalyzeDocumentAsync(project, document, cancellationToken);
                    analyses.Add(analysis);
                    progressCallback?.Invoke(++processed, sourceFiles.Count);
                }
            }
        }
        return analyses;
    }

    private static void RegisterMsBuild()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    private static string? FindWorkspacePath(SourceFile sourceFile)
    {
        string? nearestProject = null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(sourceFile.FilePath));
        while (!string.IsNullOrEmpty(directory))
        {
            var solution = Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (solution is not null)
                return solution;
            nearestProject ??= Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            directory = Directory.GetParent(directory)?.FullName;
        }
        return nearestProject;
    }

    private static Models.SourceAnalysis CreateProjectAnalysis(Project project, Solution solution)
    {
        var analysis = new Models.SourceAnalysis
        {
            Language = SourceLanguage.CSharp,
            FileName = project.Name,
            FilePath = project.FilePath ?? project.Name,
            Summary = "MSBuild project"
        };
        AddFact(analysis, "Project", project.Name, null, project.FilePath, 0);
        foreach (var reference in project.ProjectReferences)
        {
            var referencedProject = solution.GetProject(reference.ProjectId);
            if (referencedProject is null)
                continue;
            AddDependency(analysis, project.Name, referencedProject.Name, "PROJECT_REFERENCE", 0, project.FilePath ?? string.Empty);
        }
        if (project.FilePath is not null)
        {
            foreach (var package in ReadPackageReferences(project.FilePath))
            {
                AddFact(analysis, "NuGetPackage", package.name, project.Name, package.version, 0);
                AddDependency(analysis, project.Name, package.name, "NUGET_PACKAGE", 0, project.FilePath);
            }
        }
        return analysis;
    }

    private static IEnumerable<(string name, string version)> ReadPackageReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document.Descendants().Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (
                element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? string.Empty,
                element.Attribute("Version")?.Value ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value ?? string.Empty))
            .Where(package => !string.IsNullOrEmpty(package.Item1));
    }

    private static async Task<Models.SourceAnalysis> AnalyzeDocumentAsync(Project project, Document document, CancellationToken cancellationToken)
    {
        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Roslyn did not produce syntax for {document.FilePath}.");
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Roslyn did not produce a semantic model for {document.FilePath}.");
        var analysis = new Models.SourceAnalysis
        {
            Language = SourceLanguage.CSharp,
            FileName = Path.GetFileName(document.FilePath),
            FilePath = document.FilePath ?? document.Name,
            Summary = $"C# document in {project.Name}"
        };

        foreach (var namespaceDeclaration in syntaxRoot.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            AddFact(analysis, "Namespace", namespaceDeclaration.Name.ToString(), null, null, LineOf(namespaceDeclaration));

        foreach (var typeDeclaration in syntaxRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol type)
                continue;
            var typeName = Display(type);
            AddFact(analysis, type.TypeKind.ToString(), typeName, null, null, LineOf(typeDeclaration));
            if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
                AddDependency(analysis, typeName, Display(baseType), "INHERITS", LineOf(typeDeclaration), analysis.FilePath);
            foreach (var implementedInterface in type.Interfaces)
                AddDependency(analysis, typeName, Display(implementedInterface), "IMPLEMENTS", LineOf(typeDeclaration), analysis.FilePath);

            var isController = typeName.EndsWith("Controller", StringComparison.Ordinal) ||
                InheritsFrom(type, "Microsoft.AspNetCore.Mvc.ControllerBase");
            if (isController)
                AddFact(analysis, "AspNetController", typeName, null, GetRoute(typeDeclaration), LineOf(typeDeclaration));
            if (InheritsFrom(type, "Microsoft.EntityFrameworkCore.DbContext"))
                AddFact(analysis, "EfCoreDbContext", typeName, null, null, LineOf(typeDeclaration));
        }

        foreach (var method in syntaxRoot.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(method, cancellationToken) is not IMethodSymbol symbol)
                continue;
            var methodName = Display(symbol);
            AddFact(analysis, "Method", methodName, Display(symbol.ContainingType), null, LineOf(method));
            var endpoint = GetHttpMethod(method);
            if (endpoint is not null)
            {
                AddFact(analysis, "AspNetEndpoint", methodName, Display(symbol.ContainingType), endpoint, LineOf(method));
                AddDependency(analysis, Display(symbol.ContainingType), methodName, endpoint, LineOf(method), analysis.FilePath);
            }
        }

        foreach (var constructor in syntaxRoot.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(constructor, cancellationToken) is IMethodSymbol symbol)
                AddFact(analysis, "Constructor", Display(symbol), Display(symbol.ContainingType), null, LineOf(constructor));
        }
        foreach (var field in syntaxRoot.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                if (semanticModel.GetDeclaredSymbol(variable, cancellationToken) is IFieldSymbol symbol)
                    AddFact(analysis, "Field", Display(symbol), Display(symbol.ContainingType), Display(symbol.Type), LineOf(variable));
            }
        }
        foreach (var eventField in syntaxRoot.DescendantNodes().OfType<EventFieldDeclarationSyntax>())
        {
            foreach (var eventVariable in eventField.Declaration.Variables)
            {
                if (semanticModel.GetDeclaredSymbol(eventVariable, cancellationToken) is IEventSymbol eventSymbol)
                {
                    AddFact(analysis, "Event", Display(eventSymbol), Display(eventSymbol.ContainingType), Display(eventSymbol.Type), LineOf(eventVariable));
                    AddDependency(analysis, Display(eventSymbol.ContainingType), Display(eventSymbol), "EVENT_PRODUCER", LineOf(eventVariable), analysis.FilePath);
                }
            }
        }
        foreach (var property in syntaxRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(property, cancellationToken) is not IPropertySymbol symbol)
                continue;
            AddFact(analysis, "Property", Display(symbol), Display(symbol.ContainingType), Display(symbol.Type), LineOf(property));
            if (IsDbSet(symbol.Type))
            {
                AddFact(analysis, "EfCoreEntitySet", Display(symbol.Type), Display(symbol.ContainingType), null, LineOf(property));
                AddDependency(analysis, Display(symbol.ContainingType), Display(symbol.Type), "EF_ENTITY_SET", LineOf(property), analysis.FilePath);
            }
        }

        foreach (var invocation in syntaxRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var target = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
            if (target is null)
                continue;
            var targetName = Display(target);
            AddDependency(analysis, analysis.FileName, targetName, "INVOKES", LineOf(invocation), analysis.FilePath);
            var invocationName = target.Name;
            if (invocationName is "AddSingleton" or "AddScoped" or "AddTransient")
            {
                var genericArguments = (invocation.Expression as MemberAccessExpressionSyntax)?.Name is GenericNameSyntax generic
                    ? generic.TypeArgumentList.Arguments.Select(argument => semanticModel.GetTypeInfo(argument, cancellationToken).Type).Where(type => type is not null).Cast<ITypeSymbol>().ToList()
                    : [];
                if (genericArguments.Count == 2)
                    AddDependency(analysis, Display(genericArguments[0]), Display(genericArguments[1]), "DI_REGISTRATION", LineOf(invocation), analysis.FilePath);
            }
            if (target.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                target.Name.StartsWith("FromSql", StringComparison.Ordinal))
                AddFact(analysis, "EfCoreDatabaseCall", targetName, null, null, LineOf(invocation));
            if (target.MethodKind == MethodKind.DelegateInvoke &&
                semanticModel.GetSymbolInfo(invocation.Expression, cancellationToken).Symbol is IEventSymbol eventSymbol)
                AddDependency(analysis, Display(eventSymbol), analysis.FileName, "EVENT_PRODUCER", LineOf(invocation), analysis.FilePath);
        }
        foreach (var assignment in syntaxRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                     .Where(assignment => assignment.IsKind(SyntaxKind.AddAssignmentExpression)))
        {
            if (semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is IEventSymbol eventSymbol)
                AddDependency(analysis, analysis.FileName, Display(eventSymbol), "EVENT_CONSUMER", LineOf(assignment), analysis.FilePath);
        }
        return analysis;
    }

    private static string? GetHttpMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(list => list.Attributes)
            .Select(attribute => attribute.Name.ToString().Replace("Attribute", string.Empty, StringComparison.Ordinal))
            .FirstOrDefault(name => name is "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch");

    private static string? GetRoute(BaseTypeDeclarationSyntax type) =>
        type.AttributeLists.SelectMany(list => list.Attributes)
            .Where(attribute => attribute.Name.ToString().Replace("Attribute", string.Empty, StringComparison.Ordinal) == "Route")
            .Select(attribute => attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString())
            .FirstOrDefault();

    private static bool InheritsFrom(INamedTypeSymbol type, string metadataName) =>
        type.BaseType is not null && (type.BaseType.ToDisplayString() == metadataName || InheritsFrom(type.BaseType, metadataName));

    private static bool IsDbSet(ITypeSymbol type) =>
        type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == "Microsoft.EntityFrameworkCore.DbSet<TEntity>";

    private static string Display(ISymbol symbol) => symbol.ToDisplayString(SymbolFormat);
    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static void AddFact(Models.SourceAnalysis analysis, string kind, string name, string? container, string? detail, int lineNumber)
    {
        if (analysis.Facts.Any(fact => fact.Kind == kind && fact.Name == name && fact.LineNumber == lineNumber))
            return;
        analysis.Facts.Add(new SourceFact { Kind = kind, Name = name, Container = container, Detail = detail, LineNumber = lineNumber });
    }

    private static void AddDependency(Models.SourceAnalysis analysis, string source, string target, string kind, int lineNumber, string context)
    {
        if (analysis.Dependencies.Any(dependency => dependency.Source == source && dependency.Target == target && dependency.Kind == kind && dependency.LineNumber == lineNumber))
            return;
        analysis.Dependencies.Add(new SourceDependency { Source = source, Target = target, Kind = kind, LineNumber = lineNumber, Context = context });
    }
}
