namespace CobolToQuarkusMigration.Discovery.Graph;

/// <summary>
/// Authoritative, documented list of known .NET/ASP.NET Core/EF Core/Azure framework namespace
/// roots, used as a hard pre-filter so <see cref="ContextSeeder"/> never proposes a
/// <c>BusinessContext</c> candidate whose members are entirely framework-owned namespaces
/// (design doc §5.1/§5.2, issue #3 scope item 4: "keep shared, platform, and infrastructure
/// material distinct from business-context ownership").
/// </summary>
/// <remarks>
/// Sourced from the official Microsoft .NET API namespace documentation, not guessed or inferred:
/// <list type="bullet">
/// <item>Base Class Library (<c>System.*</c>): https://learn.microsoft.com/dotnet/api/system</item>
/// <item>ASP.NET Core (<c>Microsoft.AspNetCore.*</c>): https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore</item>
/// <item>Generic Host / <c>Microsoft.Extensions.*</c> (hosting, DI, configuration, logging, options):
///   https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting,
///   https://learn.microsoft.com/dotnet/api/microsoft.extensions.dependencyinjection</item>
/// <item>EF Core (<c>Microsoft.EntityFrameworkCore.*</c>): https://learn.microsoft.com/dotnet/api/microsoft.entityframeworkcore</item>
/// <item>Azure SDK (<c>Azure.*</c>): https://learn.microsoft.com/dotnet/api/azure</item>
/// <item>Microsoft.Identity / Graph client libraries used for auth/telemetry plumbing:
///   https://learn.microsoft.com/dotnet/api/microsoft.identity.web</item>
/// </list>
/// This is a namespace-text pre-filter, not a resolved-symbol/assembly-identity check: it can only
/// tell us that a *type's own declared namespace* falls under a reserved framework root. It
/// deliberately does not attempt to resolve whether a referenced type actually originates from the
/// named framework assembly (that would require a full <c>CSharpCompilation</c>/<c>SemanticModel</c>
/// pass, out of scope for this phase per §12 — same limitation already documented for
/// <see cref="DependencyGraphBuilder"/> and Phase 4's <c>ArtifactRoleClassifier</c>). Because it only
/// matches a symbol's own declaring namespace text, a PlanBoard type deliberately declared inside a
/// reserved namespace (e.g. a custom class under <c>Microsoft.Extensions.Hosting</c>) is still
/// correctly flagged here — that is a real, evidence-worth-recording code smell, not a false
/// negative.
/// </remarks>
public static class FrameworkNamespaces
{
    /// <summary>
    /// Namespace-root prefixes (matched as "equals" or "starts with prefix + '.'") that are
    /// authoritatively framework/platform-owned per the official Microsoft namespace documentation
    /// cited above.
    /// </summary>
    public static readonly IReadOnlyList<string> Roots = new[]
    {
        "System",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Identity",
        "Microsoft.Graph",
        "Microsoft.Data",
        "Azure",
    };

    /// <summary>
    /// Returns true when <paramref name="namespaceName"/> is exactly one of, or a sub-namespace of,
    /// a known framework root (e.g. <c>Microsoft.Extensions.Hosting</c> matches the
    /// <c>Microsoft.Extensions</c> root).
    /// </summary>
    public static bool IsFrameworkNamespace(string? namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName))
        {
            return false;
        }

        foreach (var root in Roots)
        {
            if (namespaceName.Equals(root, StringComparison.Ordinal) ||
                namespaceName.StartsWith(root + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the declaring namespace from a dotted symbol locator (e.g.
    /// <c>Microsoft.Extensions.Hosting.InstrumentationSource</c> → <c>Microsoft.Extensions.Hosting</c>).
    /// </summary>
    public static string GetNamespace(string symbolLocator)
    {
        var lastDot = symbolLocator.LastIndexOf('.');
        return lastDot >= 0 ? symbolLocator[..lastDot] : string.Empty;
    }
}
