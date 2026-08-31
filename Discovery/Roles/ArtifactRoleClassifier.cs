using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CobolToQuarkusMigration.Discovery.Models;

namespace CobolToQuarkusMigration.Discovery.Roles;

/// <summary>Source file handed to the classifier: a relative path plus its text content.</summary>
public sealed record ClassifierSourceFile(string Path, string Text);

/// <summary>One cited piece of syntax-level evidence backing a fired classification rule.</summary>
public sealed record RoleEvidenceCitation(string RuleName, string Locator, string Excerpt);

/// <summary>
/// Result of classifying one symbol (a top-level type, or a whole non-C# file) before
/// persistence. <see cref="Roles"/> may contain more than one tag when evidence is mixed.
/// </summary>
public sealed record RoleClassification(
    string SymbolLocator,
    IReadOnlyList<ArtifactRoleTag> Roles,
    double Confidence,
    string ClassificationRule,
    bool RequiresReview,
    IReadOnlyList<RoleEvidenceCitation> Citations);

/// <summary>
/// Deterministic, evidence-backed artifact role classifier (design doc §5, §5.1, issue #8).
/// Classification is syntax-tree based (Roslyn <c>Microsoft.CodeAnalysis.CSharp</c> parsing),
/// not a full-compilation/semantic analysis — every rule cites the exact syntax evidence
/// (base type list, attribute, invocation, using directive, or file path) that fired it.
/// Multiple role tags may co-occur for the same symbol; the classifier never collapses mixed
/// evidence into a single "primary" role. <c>bin</c>/<c>obj</c> paths are excluded entirely
/// from primary analysis and never produce a classification (design doc §5.1).
/// </summary>
public static class ArtifactRoleClassifier
{
    private static readonly HashSet<ArtifactRoleTag> NonBusinessTechnicalTags = new()
    {
        ArtifactRoleTag.CompositionDI,
        ArtifactRoleTag.Middleware,
        ArtifactRoleTag.FrameworkAdapter,
        ArtifactRoleTag.Persistence,
        ArtifactRoleTag.IntegrationAdapter,
        ArtifactRoleTag.Generated,
        ArtifactRoleTag.Test,
        ArtifactRoleTag.BuildTooling,
        ArtifactRoleTag.Shared,
    };

    /// <summary>True when the path falls under a <c>bin</c> or <c>obj</c> directory segment.</summary>
    public static bool IsExcludedBuildOutputPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the path is under a conventional test-project directory (e.g. <c>*.Tests/</c>,
    /// <c>/Tests/</c>). Shared with <see cref="Integrations.IntegrationClassifier"/> so test
    /// projects — which reference production SDKs/base types purely for test setup (e.g. a
    /// fake controller, a `WebApplicationFactory`) — are never counted as production
    /// integrations (issue #6 follow-up).
    /// </summary>
    public static bool IsTestPath(string relativePath) =>
        relativePath.Contains("/Tests/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Contains(".Tests/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Contains(".Tests.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies an entire set of files that make up one analysis unit (typically one project's
    /// source tree). Passing multiple files together enables cross-file fan-in evidence for the
    /// <see cref="ArtifactRoleTag.Shared"/> tag; classifying a single file in isolation is also
    /// supported (pass a list of one) but will never detect cross-namespace fan-in.
    /// </summary>
    public static IReadOnlyList<RoleClassification> ClassifyProject(IReadOnlyList<ClassifierSourceFile> files)
    {
        var results = new List<RoleClassification>();
        var eligible = files.Where(f => !IsExcludedBuildOutputPath(f.Path)).ToList();

        var parsed = new List<(ClassifierSourceFile File, CompilationUnitSyntax Root)>();
        foreach (var file in eligible)
        {
            if (!file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var nonCs = ClassifyNonCSharpFile(file);
                if (nonCs is not null)
                {
                    results.Add(nonCs);
                }
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(file.Text, path: file.Path);
            parsed.Add((file, (CompilationUnitSyntax)tree.GetRoot()));
        }

        // Best-effort, syntax-only fan-in evidence: static types invoked (SimpleName.Member) from
        // a namespace other than their own declaring namespace. Full semantic symbol resolution
        // across the solution is deferred (design doc §12); this is a deterministic textual proxy.
        var staticTypeDeclarations = parsed
            .SelectMany(p => p.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Where(t => t.Modifiers.Any(SyntaxKind.StaticKeyword))
                .Select(t => (Type: t, File: p.File, Namespace: GetNamespace(t))))
            .ToList();

        var crossNamespaceReferences = new Dictionary<string, HashSet<string>>();
        foreach (var (file, root) in parsed)
        {
            var fileNamespace = root.DescendantNodes()
                .Select(GetNamespaceForNode)
                .FirstOrDefault(ns => ns is not null) ?? string.Empty;

            foreach (var invocation in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (invocation.Expression is not IdentifierNameSyntax identifier)
                {
                    continue;
                }

                var candidate = staticTypeDeclarations.FirstOrDefault(s => s.Type.Identifier.Text == identifier.Identifier.Text);
                if (candidate.Type is null || candidate.File.Path == file.Path)
                {
                    continue;
                }

                if (!string.Equals(candidate.Namespace, fileNamespace, StringComparison.Ordinal))
                {
                    var key = $"{candidate.Namespace}.{candidate.Type.Identifier.Text}";
                    if (!crossNamespaceReferences.TryGetValue(key, out var namespaces))
                    {
                        namespaces = new HashSet<string>(StringComparer.Ordinal);
                        crossNamespaceReferences[key] = namespaces;
                    }
                    namespaces.Add(fileNamespace);
                }
            }
        }

        foreach (var (file, root) in parsed)
        {
            var typeDecls = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
            if (typeDecls.Count == 0)
            {
                // Top-level-statement files (e.g. Program.cs) declare no type; classify the file
                // as a single pseudo-unit so DI/middleware/routing evidence in Main isn't lost.
                var pseudo = ClassifyTopLevelStatements(file, root);
                if (pseudo is not null)
                {
                    results.Add(pseudo);
                }
                continue;
            }

            foreach (var typeDecl in typeDecls)
            {
                results.Add(ClassifyType(file, root, typeDecl, crossNamespaceReferences));
            }
        }

        return results;
    }

    private static RoleClassification? ClassifyNonCSharpFile(ClassifierSourceFile file)
    {
        var path = file.Path.Replace('\\', '/');
        var extension = Path.GetExtension(path);
        var isBuildTooling =
            extension is ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" or ".sql" ||
            path.Contains("/.github/workflows/", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);

        if (!isBuildTooling)
        {
            return null;
        }

        var citation = new RoleEvidenceCitation("BuildToolingFileExtension", $"{path}:L1", $"File extension/path identifies build tooling: {path}");
        return new RoleClassification(
            SymbolLocator: path,
            Roles: new[] { ArtifactRoleTag.BuildTooling },
            Confidence: 0.95,
            ClassificationRule: "BuildToolingFileExtension",
            RequiresReview: false,
            Citations: new[] { citation });
    }

    private static RoleClassification? ClassifyTopLevelStatements(ClassifierSourceFile file, CompilationUnitSyntax root)
    {
        var globalStatements = root.Members.OfType<GlobalStatementSyntax>().ToList();
        if (globalStatements.Count == 0)
        {
            return null;
        }

        var roles = new HashSet<ArtifactRoleTag>();
        var citations = new List<RoleEvidenceCitation>();
        var rules = new List<string>();

        var text = file.Text;

        CollectCompositionDiSignals(file.Path, root, text, roles, citations, rules);
        CollectMiddlewareSignals(file.Path, root, text, roles, citations, rules);
        CollectFrameworkAdapterSignals(file.Path, root, text, roles, citations, rules);
        CollectIntegrationAdapterSignals(file.Path, root, text, roles, citations, rules);

        if (roles.Count == 0)
        {
            roles.Add(ArtifactRoleTag.Unknown);
            rules.Add("NoDeterministicSignalTopLevel");
            citations.Add(new RoleEvidenceCitation("NoDeterministicSignalTopLevel", $"{file.Path}:L1", "Top-level statement file with no matched deterministic rule."));
        }

        return Finalize(file.Path, roles, citations, rules);
    }

    private static RoleClassification ClassifyType(
        ClassifierSourceFile file,
        CompilationUnitSyntax root,
        TypeDeclarationSyntax typeDecl,
        IReadOnlyDictionary<string, HashSet<string>> crossNamespaceReferences)
    {
        var ns = GetNamespace(typeDecl);
        var symbolLocator = string.IsNullOrEmpty(ns) ? typeDecl.Identifier.Text : $"{ns}.{typeDecl.Identifier.Text}";
        var typeSpan = typeDecl.GetLocation().GetLineSpan();
        var typeLocator = $"{file.Path}:L{typeSpan.StartLinePosition.Line + 1}-L{typeSpan.EndLinePosition.Line + 1}";

        var roles = new HashSet<ArtifactRoleTag>();
        var citations = new List<RoleEvidenceCitation>();
        var rules = new List<string>();

        var baseTypes = typeDecl.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? new List<string>();
        var attributeNames = typeDecl.AttributeLists.SelectMany(al => al.Attributes).Select(a => a.Name.ToString()).ToList();
        var usings = root.Usings.Select(u => u.Name?.ToString() ?? string.Empty).ToList();
        var text = file.Text;
        var isTestPath = IsTestPath(file.Path);

        var leadingTrivia = typeDecl.GetLeadingTrivia().ToFullString() + root.GetLeadingTrivia().ToFullString();
        var isAutoGeneratedComment = leadingTrivia.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
        var isMigrationsFolder = file.Path.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase);
        var derivesFromEfMigration = baseTypes.Any(b => b.Contains("Migration", StringComparison.Ordinal));

        // --- Generated ---
        if (isAutoGeneratedComment)
        {
            roles.Add(ArtifactRoleTag.Generated);
            rules.Add("AutoGeneratedComment");
            citations.Add(new RoleEvidenceCitation("AutoGeneratedComment", $"{file.Path}:L1", "File carries an <auto-generated> header comment."));
        }
        if (isMigrationsFolder && derivesFromEfMigration)
        {
            roles.Add(ArtifactRoleTag.Generated);
            roles.Add(ArtifactRoleTag.Persistence);
            rules.Add("EfMigrationBaseType");
            citations.Add(new RoleEvidenceCitation("EfMigrationBaseType", typeLocator, $"Type '{typeDecl.Identifier.Text}' under a Migrations folder derives from {string.Join(", ", baseTypes.Where(b => b.Contains("Migration", StringComparison.Ordinal)))}."));
        }

        // --- Test ---
        var testAttributes = attributeNames.Where(a => a is "Fact" or "Theory" or "Test" or "TestMethod" or "TestClass" or "TestFixture").ToList();
        var typeMethodTestAttributes = typeDecl.Members.OfType<MethodDeclarationSyntax>()
            .SelectMany(m => m.AttributeLists.SelectMany(al => al.Attributes))
            .Select(a => a.Name.ToString())
            .Where(a => a is "Fact" or "Theory" or "Test" or "TestMethod")
            .ToList();
        if (isTestPath || testAttributes.Count > 0 || typeMethodTestAttributes.Count > 0)
        {
            roles.Add(ArtifactRoleTag.Test);
            var rule = isTestPath ? "TestProjectPath" : "TestFrameworkAttribute";
            rules.Add(rule);
            var evidenceText = isTestPath
                ? $"File path indicates a test project: {file.Path}"
                : $"Type/member carries test attribute(s): {string.Join(", ", testAttributes.Concat(typeMethodTestAttributes).Distinct())}.";
            citations.Add(new RoleEvidenceCitation(rule, typeLocator, evidenceText));
        }

        // --- Persistence (explicit Entity Framework) ---
        var efAttributes = attributeNames.Where(a => a is "Table" or "Key" or "Column").ToList();
        var derivesFromDbContext = baseTypes.Any(b => b.Contains("DbContext", StringComparison.Ordinal));
        var usesEfNamespace = usings.Any(u => u.Contains("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        if (derivesFromDbContext)
        {
            roles.Add(ArtifactRoleTag.Persistence);
            rules.Add("EfDbContextBaseType");
            citations.Add(new RoleEvidenceCitation("EfDbContextBaseType", typeLocator, $"Type '{typeDecl.Identifier.Text}' derives from {string.Join(", ", baseTypes.Where(b => b.Contains("DbContext", StringComparison.Ordinal)))}."));
        }
        else if (efAttributes.Count > 0)
        {
            roles.Add(ArtifactRoleTag.Persistence);
            rules.Add("EfMappingAttribute");
            citations.Add(new RoleEvidenceCitation("EfMappingAttribute", typeLocator, $"Type/members carry Entity Framework mapping attribute(s): {string.Join(", ", efAttributes.Distinct())}."));
        }
        else if (usesEfNamespace && !isTestPath)
        {
            roles.Add(ArtifactRoleTag.Persistence);
            rules.Add("EntityFrameworkNamespaceUsing");
            citations.Add(new RoleEvidenceCitation("EntityFrameworkNamespaceUsing", $"{file.Path}:L1", "File uses the Microsoft.EntityFrameworkCore namespace."));
        }

        // --- CompositionDI ---
        CollectCompositionDiSignals(file.Path, typeDecl, text, roles, citations, rules, typeLocator);

        // --- Middleware ---
        var implementsMiddleware = baseTypes.Any(b => b.Contains("IMiddleware", StringComparison.Ordinal));
        var nameEndsMiddleware = typeDecl.Identifier.Text.EndsWith("Middleware", StringComparison.Ordinal);
        if (implementsMiddleware || nameEndsMiddleware)
        {
            roles.Add(ArtifactRoleTag.Middleware);
            var rule = implementsMiddleware ? "ImplementsIMiddleware" : "MiddlewareTypeNameSuffix";
            rules.Add(rule);
            citations.Add(new RoleEvidenceCitation(rule, typeLocator, implementsMiddleware
                ? $"Type '{typeDecl.Identifier.Text}' implements IMiddleware."
                : $"Type name '{typeDecl.Identifier.Text}' ends with 'Middleware'."));
        }

        // --- FrameworkAdapter ---
        var derivesFromController = baseTypes.Any(b => b.Contains("Controller", StringComparison.Ordinal));
        var hasApiControllerAttribute = attributeNames.Any(a => a.Contains("ApiController", StringComparison.Ordinal));
        var hasRouteAttribute = attributeNames.Any(a => a.Contains("Route", StringComparison.Ordinal) || a.Contains("HttpGet", StringComparison.Ordinal) || a.Contains("HttpPost", StringComparison.Ordinal));
        if (derivesFromController || hasApiControllerAttribute || hasRouteAttribute)
        {
            roles.Add(ArtifactRoleTag.FrameworkAdapter);
            var rule = derivesFromController ? "ControllerBaseType" : "ApiRoutingAttribute";
            rules.Add(rule);
            citations.Add(new RoleEvidenceCitation(rule, typeLocator, derivesFromController
                ? $"Type '{typeDecl.Identifier.Text}' derives from a Controller base type."
                : "Type carries API routing attribute(s) (ApiController/Route/HttpGet/HttpPost)."));
        }

        // --- IntegrationAdapter ---
        CollectIntegrationAdapterSignals(file.Path, typeDecl, text, roles, citations, rules, typeLocator, usings);

        // --- Shared (cross-namespace fan-in) ---
        if (typeDecl.Modifiers.Any(SyntaxKind.StaticKeyword) &&
            crossNamespaceReferences.TryGetValue(symbolLocator, out var referencingNamespaces) &&
            referencingNamespaces.Count >= 2)
        {
            roles.Add(ArtifactRoleTag.Shared);
            rules.Add("CrossNamespaceFanIn");
            citations.Add(new RoleEvidenceCitation("CrossNamespaceFanIn", typeLocator,
                $"Static type '{typeDecl.Identifier.Text}' is referenced from {referencingNamespaces.Count} distinct namespaces: {string.Join(", ", referencingNamespaces)}."));
        }

        // --- Business (fallback only) / Unknown ---
        var hasExecutableMembers = typeDecl.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Body is not null || m.ExpressionBody is not null)
            || typeDecl.Members.OfType<ConstructorDeclarationSyntax>().Any(c => c.Body is not null || c.ExpressionBody is not null);

        if (roles.Count == 0)
        {
            if (hasExecutableMembers)
            {
                roles.Add(ArtifactRoleTag.Business);
                rules.Add("ExecutableLogicNoFrameworkSignal");
                citations.Add(new RoleEvidenceCitation("ExecutableLogicNoFrameworkSignal", typeLocator,
                    $"Type '{typeDecl.Identifier.Text}' contains executable member(s) and matched no framework/generated/test/build-tooling rule."));
            }
            else
            {
                roles.Add(ArtifactRoleTag.Unknown);
                rules.Add("NoDeterministicSignal");
                citations.Add(new RoleEvidenceCitation("NoDeterministicSignal", typeLocator,
                    $"Type '{typeDecl.Identifier.Text}' matched no deterministic rule and has no executable members."));
            }
        }

        return Finalize(symbolLocator, roles, citations, rules);
    }

    private static RoleClassification Finalize(string symbolLocator, HashSet<ArtifactRoleTag> roles, List<RoleEvidenceCitation> citations, List<string> rules)
    {
        var orderedRoles = roles.OrderBy(r => r.ToString(), StringComparer.Ordinal).ToList();
        var hasBusiness = orderedRoles.Contains(ArtifactRoleTag.Business);
        var hasNonBusiness = orderedRoles.Any(NonBusinessTechnicalTags.Contains);
        var isUnknown = orderedRoles.Contains(ArtifactRoleTag.Unknown);

        // Mixed business + non-business evidence, or multiple non-business tags implying
        // ambiguity, or an outright Unknown result: route to reviewer attention rather than
        // silently picking a "primary" role (design doc §5.1).
        var requiresReview = isUnknown || (hasBusiness && hasNonBusiness) || (orderedRoles.Count(NonBusinessTechnicalTags.Contains) > 1);

        // Confidence reflects rule strength: a single unambiguous rule is high confidence; mixed
        // or unknown results are deliberately lower to steer reviewer prioritization.
        var confidence = isUnknown ? 0.2 : requiresReview ? 0.6 : 0.9;

        return new RoleClassification(
            SymbolLocator: symbolLocator,
            Roles: orderedRoles,
            Confidence: confidence,
            ClassificationRule: string.Join(";", rules.Distinct()),
            RequiresReview: requiresReview,
            Citations: citations);
    }

    private static void CollectCompositionDiSignals(string path, SyntaxNode scope, string text, HashSet<ArtifactRoleTag> roles, List<RoleEvidenceCitation> citations, List<string> rules, string? locator = null)
    {
        var diCalls = scope.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(IsDiRegistrationCall)
            .ToList();
        if (diCalls.Count == 0)
        {
            return;
        }

        roles.Add(ArtifactRoleTag.CompositionDI);
        rules.Add("ServiceCollectionRegistrationCall");
        var span = diCalls[0].GetLocation().GetLineSpan();
        citations.Add(new RoleEvidenceCitation(
            "ServiceCollectionRegistrationCall",
            locator ?? $"{path}:L{span.StartLinePosition.Line + 1}",
            $"Service registration call site: {Truncate(diCalls[0].ToString())}"));
    }

    private static bool IsDiRegistrationCall(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
        {
            return false;
        }
        var name = member.Name is GenericNameSyntax generic ? generic.Identifier.Text : member.Name.Identifier.Text;
        return name is "AddSingleton" or "AddScoped" or "AddTransient" or "AddHostedService" or "Configure";
    }

    private static void CollectMiddlewareSignals(string path, SyntaxNode scope, string text, HashSet<ArtifactRoleTag> roles, List<RoleEvidenceCitation> citations, List<string> rules)
    {
        var useCalls = scope.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.Text.StartsWith("Use", StringComparison.Ordinal))
            .ToList();
        if (useCalls.Count == 0)
        {
            return;
        }

        roles.Add(ArtifactRoleTag.Middleware);
        rules.Add("MiddlewarePipelineUseCall");
        var span = useCalls[0].GetLocation().GetLineSpan();
        citations.Add(new RoleEvidenceCitation("MiddlewarePipelineUseCall", $"{path}:L{span.StartLinePosition.Line + 1}", $"Middleware pipeline call site: {Truncate(useCalls[0].ToString())}"));
    }

    private static void CollectFrameworkAdapterSignals(string path, SyntaxNode scope, string text, HashSet<ArtifactRoleTag> roles, List<RoleEvidenceCitation> citations, List<string> rules)
    {
        var mapCalls = scope.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax m &&
                (m.Name.Identifier.Text is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch"))
            .ToList();
        if (mapCalls.Count == 0)
        {
            return;
        }

        roles.Add(ArtifactRoleTag.FrameworkAdapter);
        rules.Add("MinimalApiRouteRegistration");
        var span = mapCalls[0].GetLocation().GetLineSpan();
        citations.Add(new RoleEvidenceCitation("MinimalApiRouteRegistration", $"{path}:L{span.StartLinePosition.Line + 1}", $"Minimal API route registration: {Truncate(mapCalls[0].ToString())}"));
    }

    private static void CollectIntegrationAdapterSignals(string path, SyntaxNode scope, string text, HashSet<ArtifactRoleTag> roles, List<RoleEvidenceCitation> citations, List<string> rules)
        => CollectIntegrationAdapterSignals(path, scope, text, roles, citations, rules, $"{path}:L1", Array.Empty<string>());

    private static readonly string[] IntegrationSdkNamespacePrefixes = { "Azure.", "Neo4j.", "GitHub.Copilot", "System.Net.Http" };

    private static void CollectIntegrationAdapterSignals(string path, SyntaxNode scope, string text, HashSet<ArtifactRoleTag> roles, List<RoleEvidenceCitation> citations, List<string> rules, string locator, IReadOnlyList<string> usings)
    {
        var sdkUsings = usings.Where(u => IntegrationSdkNamespacePrefixes.Any(p => u.StartsWith(p, StringComparison.Ordinal))).ToList();
        var identifier = (scope as TypeDeclarationSyntax)?.Identifier.Text;
        var nameSignal = identifier is not null && (identifier.EndsWith("Client", StringComparison.Ordinal) || identifier.EndsWith("Gateway", StringComparison.Ordinal));

        if (sdkUsings.Count == 0 && !nameSignal)
        {
            return;
        }

        roles.Add(ArtifactRoleTag.IntegrationAdapter);
        var rule = sdkUsings.Count > 0 ? "ExternalSdkNamespaceUsing" : "IntegrationClientTypeNameSuffix";
        rules.Add(rule);
        citations.Add(new RoleEvidenceCitation(rule, locator, sdkUsings.Count > 0
            ? $"File uses external SDK namespace(s): {string.Join(", ", sdkUsings)}."
            : $"Type name '{identifier}' matches an integration client/gateway naming convention."));
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

    private static string Truncate(string value) => value.Length <= 160 ? value : value[..160] + "…";
}
