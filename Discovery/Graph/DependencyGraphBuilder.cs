using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Roles;

namespace CobolToQuarkusMigration.Discovery.Graph;

/// <summary>One cited piece of syntax-level evidence backing a produced graph node or edge.</summary>
public sealed record GraphEvidenceCitation(string RuleName, string Locator, string Excerpt);

/// <summary>A deterministically produced graph node before persistence, with its citing evidence.</summary>
public sealed record GraphNodeCandidate(
    GraphNodeKind Kind,
    string SymbolLocator,
    string DisplayName,
    string? ArtifactPath,
    IReadOnlyList<GraphEvidenceCitation> Citations);

/// <summary>A deterministically produced graph edge before persistence, referencing node symbol locators.</summary>
public sealed record GraphEdgeCandidate(
    GraphEdgeKind Kind,
    string FromSymbolLocator,
    string ToSymbolLocator,
    double Confidence,
    IReadOnlyList<GraphEvidenceCitation> Citations);

/// <summary>Result of building the dependency graph over one analysis unit (a project's source tree).</summary>
public sealed record GraphBuildResult(
    IReadOnlyList<GraphNodeCandidate> Nodes,
    IReadOnlyList<GraphEdgeCandidate> Edges);

/// <summary>
/// Deterministic, evidence-backed dependency graph builder (design doc §5, §5.1, issue #3).
/// Like <see cref="ArtifactRoleClassifier"/>, this is syntax-tree based (Roslyn
/// <c>Microsoft.CodeAnalysis.CSharp</c> parsing), not a full-compilation/semantic analysis: every
/// node/edge cites the exact syntax evidence (base type, attribute, invocation, using directive,
/// project reference line, or file path) that produced it. Full semantic symbol resolution across
/// the solution remains deferred (design doc §12).
/// </summary>
public static class DependencyGraphBuilder
{
    /// <summary>
    /// Builds project-level nodes/edges from a set of <c>.csproj</c> files: one Project node per
    /// file, plus a DependsOnProject edge per <c>&lt;ProjectReference&gt;</c> element.
    /// </summary>
    public static GraphBuildResult BuildProjectGraph(IReadOnlyList<ClassifierSourceFile> csprojFiles)
    {
        var nodes = new List<GraphNodeCandidate>();
        var edges = new List<GraphEdgeCandidate>();

        foreach (var file in csprojFiles)
        {
            var projectName = Path.GetFileNameWithoutExtension(file.Path);
            nodes.Add(new GraphNodeCandidate(
                GraphNodeKind.Project,
                SymbolLocator: projectName,
                DisplayName: projectName,
                ArtifactPath: file.Path,
                Citations: new[] { new GraphEvidenceCitation("ProjectFile", $"{file.Path}:L1", $"Project file: {file.Path}") }));

            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                file.Text, @"<ProjectReference\s+Include=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var referencedPath = match.Groups[1].Value.Replace('\\', '/');
                var referencedName = Path.GetFileNameWithoutExtension(referencedPath);
                edges.Add(new GraphEdgeCandidate(
                    GraphEdgeKind.DependsOnProject,
                    FromSymbolLocator: projectName,
                    ToSymbolLocator: referencedName,
                    Confidence: 0.95,
                    Citations: new[] { new GraphEvidenceCitation("ProjectReferenceElement", $"{file.Path}:L1", $"'{projectName}' references '{referencedName}' via <ProjectReference Include=\"{match.Groups[1].Value}\" />") }));
            }
        }

        return new GraphBuildResult(nodes, edges);
    }

    /// <summary>
    /// Builds namespace/type/route/DI/EF-level nodes and edges from a project's C# source files.
    /// <paramref name="projectSymbolLocator"/> anchors every emitted node under a ContainsType edge
    /// back to its owning Project node so per-project structure and cross-project structure share
    /// one graph.
    /// </summary>
    public static GraphBuildResult BuildSourceGraph(IReadOnlyList<ClassifierSourceFile> files, string? projectSymbolLocator = null)
    {
        var nodes = new List<GraphNodeCandidate>();
        var edges = new List<GraphEdgeCandidate>();
        var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);

        var eligible = files.Where(f => !ArtifactRoleClassifier.IsExcludedBuildOutputPath(f.Path) &&
            f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();

        var parsed = eligible
            .Select(f => (File: f, Root: (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(f.Text, path: f.Path).GetRoot()))
            .ToList();

        // Cross-namespace textual reference proxy, mirroring ArtifactRoleClassifier's approach:
        // identifier usages of a type from a namespace other than its own declaring namespace.
        var typeDeclarationsByName = parsed
            .SelectMany(p => p.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Select(t => (Type: t, File: p.File, Namespace: GetNamespace(t))))
            .ToList();

        foreach (var (file, root) in parsed)
        {
            var fileNamespace = root.DescendantNodes()
                .Select(GetNamespaceForNode)
                .FirstOrDefault(ns => ns is not null) ?? string.Empty;

            if (!string.IsNullOrEmpty(fileNamespace) && seenNamespaces.Add(fileNamespace))
            {
                nodes.Add(new GraphNodeCandidate(
                    GraphNodeKind.Namespace,
                    SymbolLocator: fileNamespace,
                    DisplayName: fileNamespace,
                    ArtifactPath: null,
                    Citations: new[] { new GraphEvidenceCitation("NamespaceDeclaration", $"{file.Path}:L1", $"Namespace declared: {fileNamespace}") }));

                if (projectSymbolLocator is not null)
                {
                    edges.Add(new GraphEdgeCandidate(
                        GraphEdgeKind.ContainsType,
                        FromSymbolLocator: projectSymbolLocator,
                        ToSymbolLocator: fileNamespace,
                        Confidence: 0.9,
                        Citations: new[] { new GraphEvidenceCitation("NamespaceUnderProject", $"{file.Path}:L1", $"Namespace '{fileNamespace}' declared within project '{projectSymbolLocator}'.") }));
                }
            }

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                BuildTypeNode(file, root, typeDecl, fileNamespace, nodes, edges, typeDeclarationsByName);
            }
        }

        return new GraphBuildResult(nodes, edges);
    }

    private static void BuildTypeNode(
        ClassifierSourceFile file,
        CompilationUnitSyntax root,
        TypeDeclarationSyntax typeDecl,
        string fileNamespace,
        List<GraphNodeCandidate> nodes,
        List<GraphEdgeCandidate> edges,
        IReadOnlyList<(TypeDeclarationSyntax Type, ClassifierSourceFile File, string Namespace)> allTypes)
    {
        var typeName = typeDecl.Identifier.Text;
        var symbolLocator = string.IsNullOrEmpty(fileNamespace) ? typeName : $"{fileNamespace}.{typeName}";
        var span = typeDecl.GetLocation().GetLineSpan();
        var locator = $"{file.Path}:L{span.StartLinePosition.Line + 1}-L{span.EndLinePosition.Line + 1}";

        nodes.Add(new GraphNodeCandidate(
            GraphNodeKind.Type,
            SymbolLocator: symbolLocator,
            DisplayName: typeName,
            ArtifactPath: file.Path,
            Citations: new[] { new GraphEvidenceCitation("TypeDeclaration", locator, $"Type '{typeName}' declared in namespace '{fileNamespace}'.") }));

        if (!string.IsNullOrEmpty(fileNamespace))
        {
            edges.Add(new GraphEdgeCandidate(
                GraphEdgeKind.ContainsType,
                FromSymbolLocator: fileNamespace,
                ToSymbolLocator: symbolLocator,
                Confidence: 0.95,
                Citations: new[] { new GraphEvidenceCitation("TypeUnderNamespace", locator, $"Type '{typeName}' declared within namespace '{fileNamespace}'.") }));
        }

        var baseTypes = typeDecl.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? new List<string>();
        var attributeNames = typeDecl.AttributeLists.SelectMany(al => al.Attributes).Select(a => a.Name.ToString()).ToList();

        // --- DbContext / DbSet<T> -> MapsToEntity edges ---
        var derivesFromDbContext = baseTypes.Any(b => b.Contains("DbContext", StringComparison.Ordinal));
        if (derivesFromDbContext)
        {
            nodes.Add(new GraphNodeCandidate(
                GraphNodeKind.DbContext,
                SymbolLocator: symbolLocator,
                DisplayName: typeName,
                ArtifactPath: file.Path,
                Citations: new[] { new GraphEvidenceCitation("EfDbContextBaseType", locator, $"Type '{typeName}' derives from a DbContext base type.") }));

            foreach (var property in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (property.Type is not GenericNameSyntax { Identifier.Text: "DbSet" } generic ||
                    generic.TypeArgumentList.Arguments.Count != 1)
                {
                    continue;
                }

                var entityName = generic.TypeArgumentList.Arguments[0].ToString();
                var propertySpan = property.GetLocation().GetLineSpan();
                var propertyLocator = $"{file.Path}:L{propertySpan.StartLinePosition.Line + 1}";

                nodes.Add(new GraphNodeCandidate(
                    GraphNodeKind.DbEntity,
                    SymbolLocator: entityName,
                    DisplayName: entityName,
                    ArtifactPath: null,
                    Citations: new[] { new GraphEvidenceCitation("DbSetEntityProperty", propertyLocator, $"DbSet<{entityName}> property '{property.Identifier.Text}' declared on '{typeName}'.") }));

                edges.Add(new GraphEdgeCandidate(
                    GraphEdgeKind.MapsToEntity,
                    FromSymbolLocator: symbolLocator,
                    ToSymbolLocator: entityName,
                    Confidence: 0.95,
                    Citations: new[] { new GraphEvidenceCitation("DbSetEntityProperty", propertyLocator, $"'{typeName}' exposes DbSet<{entityName}> via property '{property.Identifier.Text}'.") }));
            }
        }

        // --- Route nodes/edges (controller base type or API routing attributes) ---
        var derivesFromController = baseTypes.Any(b => b.Contains("Controller", StringComparison.Ordinal));
        var hasRouteAttribute = attributeNames.Any(a => a.Contains("Route", StringComparison.Ordinal) || a.Contains("Http", StringComparison.Ordinal));
        if (derivesFromController || hasRouteAttribute)
        {
            foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var httpAttributes = method.AttributeLists.SelectMany(al => al.Attributes)
                    .Select(a => a.Name.ToString())
                    .Where(a => a is "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch" or "Route")
                    .ToList();
                if (httpAttributes.Count == 0)
                {
                    continue;
                }

                var routeLocator = $"{symbolLocator}.{method.Identifier.Text}";
                var methodSpan = method.GetLocation().GetLineSpan();
                var methodLocatorText = $"{file.Path}:L{methodSpan.StartLinePosition.Line + 1}";

                nodes.Add(new GraphNodeCandidate(
                    GraphNodeKind.Route,
                    SymbolLocator: routeLocator,
                    DisplayName: $"{typeName}.{method.Identifier.Text}",
                    ArtifactPath: file.Path,
                    Citations: new[] { new GraphEvidenceCitation("HttpRouteAttribute", methodLocatorText, $"Method '{method.Identifier.Text}' carries route attribute(s): {string.Join(", ", httpAttributes)}.") }));

                edges.Add(new GraphEdgeCandidate(
                    GraphEdgeKind.ExposesRoute,
                    FromSymbolLocator: symbolLocator,
                    ToSymbolLocator: routeLocator,
                    Confidence: 0.9,
                    Citations: new[] { new GraphEvidenceCitation("HttpRouteAttribute", methodLocatorText, $"Controller '{typeName}' exposes route '{method.Identifier.Text}'.") }));
            }
        }

        // --- DI: constructor parameter injection (interface consumer signal) ---
        foreach (var ctor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var parameter in ctor.ParameterList.Parameters)
            {
                var parameterTypeName = parameter.Type?.ToString();
                if (parameterTypeName is null || !parameterTypeName.StartsWith('I') || parameterTypeName.Length < 2 || !char.IsUpper(parameterTypeName[1]))
                {
                    continue;
                }

                var ctorSpan = ctor.GetLocation().GetLineSpan();
                var ctorLocator = $"{file.Path}:L{ctorSpan.StartLinePosition.Line + 1}";
                edges.Add(new GraphEdgeCandidate(
                    GraphEdgeKind.Injects,
                    FromSymbolLocator: symbolLocator,
                    ToSymbolLocator: parameterTypeName,
                    Confidence: 0.7,
                    Citations: new[] { new GraphEvidenceCitation("ConstructorInjectedInterfaceParameter", ctorLocator, $"'{typeName}' constructor injects '{parameterTypeName}' parameter '{parameter.Identifier.Text}'.") }));
            }
        }

        // --- Cross-namespace textual reference proxy (References edges) ---
        var referencedTypeNames = root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(id => id.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (candidateType, candidateFile, candidateNamespace) in allTypes)
        {
            if (candidateFile.Path == file.Path || string.Equals(candidateNamespace, fileNamespace, StringComparison.Ordinal))
            {
                continue;
            }

            if (!referencedTypeNames.Contains(candidateType.Identifier.Text))
            {
                continue;
            }

            var targetLocator = string.IsNullOrEmpty(candidateNamespace) ? candidateType.Identifier.Text : $"{candidateNamespace}.{candidateType.Identifier.Text}";
            edges.Add(new GraphEdgeCandidate(
                GraphEdgeKind.References,
                FromSymbolLocator: symbolLocator,
                ToSymbolLocator: targetLocator,
                Confidence: 0.5,
                Citations: new[] { new GraphEvidenceCitation("CrossNamespaceIdentifierReference", locator, $"'{typeName}' references identifier '{candidateType.Identifier.Text}' declared in namespace '{candidateNamespace}'.") }));
        }
    }

    private static string GetNamespace(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case FileScopedNamespaceDeclarationSyntax fileScoped:
                    return fileScoped.Name.ToString();
                case NamespaceDeclarationSyntax nsDecl:
                    return nsDecl.Name.ToString();
            }
        }
        return string.Empty;
    }

    private static string? GetNamespaceForNode(SyntaxNode node) =>
        node is FileScopedNamespaceDeclarationSyntax fileScoped ? fileScoped.Name.ToString()
        : node is NamespaceDeclarationSyntax nsDecl ? nsDecl.Name.ToString()
        : null;
}
