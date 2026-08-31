using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Roles;

namespace CobolToQuarkusMigration.Discovery.Integrations;

/// <summary>One cited piece of evidence backing a produced integration candidate.</summary>
public sealed record IntegrationEvidenceCitation(string RuleName, string Locator, string Excerpt);

/// <summary>A deterministically produced integration candidate before persistence.</summary>
public sealed record IntegrationCandidate(
    IntegrationCategory Category,
    IntegrationClassification Classification,
    IntegrationDirection Direction,
    string TriggerOrCaller,
    string ProtocolOrMechanism,
    string LogicalTarget,
    string? ConfigurationKeySemantics,
    string? RedactedContractShape,
    string? AuthenticationSemantics,
    string? ReliabilityBehavior,
    double Confidence,
    string ClassificationRule,
    IReadOnlyList<string> BlindSpots,
    bool RequiresReview,
    string? ArtifactPath,
    IReadOnlyList<IntegrationEvidenceCitation> Citations);

/// <summary>
/// Deterministic, evidence-backed integration classifier (design doc §7, issue #6). Like
/// <see cref="ArtifactRoleClassifier"/> and <see cref="Graph.DependencyGraphBuilder"/>, this is
/// syntax-tree based (Roslyn <c>Microsoft.CodeAnalysis.CSharp</c> parsing) — no
/// <c>CSharpCompilation</c>/<c>SemanticModel</c> is built. Every rule prefers an authoritative
/// signal (a referenced NuGet package's <c>using</c> namespace, a base type, an attribute, or an
/// actual configuration-key literal) over a naming/path guess, per the design doc §14 lessons from
/// Phases 4/5. Where a rule remains naming/path-based (currently only the delivery-vs-worker
/// project-path rule), every candidate it produces carries a non-empty <see cref="IntegrationCandidate.BlindSpots"/>
/// entry and <c>RequiresReview = true</c> rather than shipping silently as unqualified confidence.
/// </summary>
public static class IntegrationClassifier
{
    // Authoritative using-directive namespace roots per category/classification. Sourced from the
    // official package namespaces (Azure SDK, Microsoft.Identity.Web, Microsoft.Graph, EF Core,
    // ASP.NET Core, Aspire service defaults) — not guessed.
    private static readonly string[] MessagingNamespaces = { "Azure.Messaging.ServiceBus", "Azure.Messaging.EventHubs" };
    private static readonly string[] NotificationNamespaces = { "Azure.Communication.Email", "Azure.Communication.Sms" };
    private static readonly string[] IdentityNamespaces =
    {
        "Microsoft.Identity.Web", "Microsoft.AspNetCore.Authentication.AzureAD", "Azure.Identity", "Microsoft.Graph",
    };
    private static readonly string[] SecretConfigNamespaces = { "Azure.Extensions.AspNetCore.Configuration.Secrets", "Azure.Security.KeyVault" };
    private static readonly string[] ServiceDiscoveryNamespaces = { "Microsoft.Extensions.ServiceDiscovery", "Aspire" };
    private static readonly string[] ObservabilityNamespaces =
    {
        "Azure.Monitor.OpenTelemetry", "OpenTelemetry", "Microsoft.Extensions.Diagnostics.HealthChecks",
    };
    private static readonly string[] EfSqlServerNamespaces = { "Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.Data.SqlClient" };

    /// <summary>
    /// Classifies a project's C# source files into integration candidates. Passing the full
    /// project/solution file set enables base-type/attribute-shaped rules (DbContext, controller
    /// routes, background workers) plus using-directive-shaped rules (Azure/Entra/Graph/ACS SDKs).
    /// </summary>
    public static IReadOnlyList<IntegrationCandidate> ClassifyProject(IReadOnlyList<ClassifierSourceFile> csFiles)
    {
        var results = new List<IntegrationCandidate>();
        var eligible = csFiles.Where(f => !ArtifactRoleClassifier.IsExcludedBuildOutputPath(f.Path) &&
            !ArtifactRoleClassifier.IsTestPath(f.Path) &&
            f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var file in eligible)
        {
            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(file.Text, path: file.Path).GetRoot();
            var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Select(u => u.Name?.ToString())
                .Where(n => n is not null)
                .Cast<string>()
                .ToList();

            ClassifyUsingDirectiveIntegrations(file, usings, results);

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                ClassifyTypeShapeIntegrations(file, typeDecl, results);
            }
        }

        return results;
    }

    /// <summary>
    /// Classifies configuration-key-only evidence (e.g. <c>appsettings*.json</c>). Never reads or
    /// persists resolved configuration values — only the presence of a permitted configuration
    /// key name (design doc §2/§5.2 redaction rules).
    /// </summary>
    public static IReadOnlyList<IntegrationCandidate> ClassifyConfigurationFile(ClassifierSourceFile file)
    {
        var results = new List<IntegrationCandidate>();

        foreach (Match match in Regex.Matches(file.Text, "\"(ConnectionStrings)\"\\s*:\\s*\\{\\s*\"([^\"]+)\""))
        {
            var key = $"ConnectionStrings:{match.Groups[2].Value}";
            results.Add(new IntegrationCandidate(
                IntegrationCategory.DatabaseOrSharedStore,
                IntegrationClassification.RuntimeApplication,
                IntegrationDirection.Outbound,
                TriggerOrCaller: "Application startup (configuration binding)",
                ProtocolOrMechanism: "EF Core / SQL Server (connection string configuration key)",
                LogicalTarget: key,
                ConfigurationKeySemantics: key,
                RedactedContractShape: null,
                AuthenticationSemantics: null,
                ReliabilityBehavior: null,
                Confidence: 0.85,
                ClassificationRule: "ConnectionStringConfigurationKey",
                BlindSpots: Array.Empty<string>(),
                RequiresReview: false,
                ArtifactPath: file.Path,
                Citations: new[] { new IntegrationEvidenceCitation("ConnectionStringConfigurationKey", $"{file.Path}", $"Configuration key '{key}' declared (value never read or persisted).") }));
        }

        foreach (Match match in Regex.Matches(file.Text, "\"(KeyVaultName)\"\\s*:\\s*\"[^\"]*\""))
        {
            var key = match.Groups[1].Value;
            results.Add(new IntegrationCandidate(
                IntegrationCategory.ServiceDiscoveryOrPlatformConfig,
                IntegrationClassification.PlatformIdentity,
                IntegrationDirection.Outbound,
                TriggerOrCaller: "Application startup (configuration binding)",
                ProtocolOrMechanism: "Azure Key Vault (configuration key)",
                LogicalTarget: key,
                ConfigurationKeySemantics: key,
                RedactedContractShape: null,
                AuthenticationSemantics: "Managed identity/Entra ID assumed (Azure Key Vault convention); not directly verified from this key alone.",
                ReliabilityBehavior: null,
                Confidence: 0.8,
                ClassificationRule: "KeyVaultNameConfigurationKey",
                BlindSpots: Array.Empty<string>(),
                RequiresReview: false,
                ArtifactPath: file.Path,
                Citations: new[] { new IntegrationEvidenceCitation("KeyVaultNameConfigurationKey", $"{file.Path}", $"Configuration key '{key}' declared (value never read or persisted).") }));
        }

        return results;
    }

    private static void ClassifyUsingDirectiveIntegrations(ClassifierSourceFile file, List<string> usings, List<IntegrationCandidate> results)
    {
        foreach (var ns in usings)
        {
            if (Matches(ns, MessagingNamespaces))
            {
                Add(results, file, IntegrationCategory.Messaging, IntegrationClassification.RuntimeApplication,
                    IntegrationDirection.Bidirectional, protocol: ns, target: ns, rule: "MessagingSdkUsingDirective",
                    confidence: 0.85, requiresReview: false, blindSpots: Array.Empty<string>());
            }
            else if (Matches(ns, NotificationNamespaces))
            {
                Add(results, file, IntegrationCategory.Notification, IntegrationClassification.RuntimeApplication,
                    IntegrationDirection.Outbound, protocol: ns, target: ns, rule: "NotificationSdkUsingDirective",
                    confidence: 0.85, requiresReview: false, blindSpots: Array.Empty<string>());
            }
            else if (Matches(ns, IdentityNamespaces))
            {
                Add(results, file, IntegrationCategory.IdentityOrAuthorization, IntegrationClassification.PlatformIdentity,
                    IntegrationDirection.Outbound, protocol: ns, target: ns, rule: "IdentitySdkUsingDirective",
                    confidence: 0.85, requiresReview: false, blindSpots: Array.Empty<string>(),
                    authenticationSemantics: $"Microsoft Entra ID / Microsoft Graph via '{ns}'.");
            }
            else if (Matches(ns, SecretConfigNamespaces))
            {
                Add(results, file, IntegrationCategory.ServiceDiscoveryOrPlatformConfig, IntegrationClassification.PlatformIdentity,
                    IntegrationDirection.Outbound, protocol: ns, target: ns, rule: "SecretConfigSdkUsingDirective",
                    confidence: 0.85, requiresReview: false, blindSpots: Array.Empty<string>());
            }
            else if (Matches(ns, ServiceDiscoveryNamespaces))
            {
                Add(results, file, IntegrationCategory.ServiceDiscoveryOrPlatformConfig, IntegrationClassification.PlatformIdentity,
                    IntegrationDirection.Outbound, protocol: ns, target: ns, rule: "ServiceDiscoverySdkUsingDirective",
                    confidence: 0.8, requiresReview: false, blindSpots: Array.Empty<string>());
            }
            else if (Matches(ns, ObservabilityNamespaces))
            {
                Add(results, file, IntegrationCategory.ServiceDiscoveryOrPlatformConfig, IntegrationClassification.Observability,
                    IntegrationDirection.Outbound, protocol: ns, target: ns, rule: "ObservabilitySdkUsingDirective",
                    confidence: 0.85, requiresReview: false, blindSpots: Array.Empty<string>());
            }
            else if (Matches(ns, EfSqlServerNamespaces))
            {
                Add(results, file, IntegrationCategory.DatabaseOrSharedStore, IntegrationClassification.RuntimeApplication,
                    IntegrationDirection.Outbound, protocol: ns, target: ns, rule: "SqlServerSdkUsingDirective",
                    confidence: 0.85, requiresReview: false, blindSpots: Array.Empty<string>());
            }
        }
    }

    private static void ClassifyTypeShapeIntegrations(ClassifierSourceFile file, TypeDeclarationSyntax typeDecl, List<IntegrationCandidate> results)
    {
        var typeName = typeDecl.Identifier.Text;
        var baseTypes = typeDecl.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? new List<string>();
        var attributeNames = typeDecl.AttributeLists.SelectMany(al => al.Attributes).Select(a => a.Name.ToString()).ToList();
        var span = typeDecl.GetLocation().GetLineSpan();
        var locator = $"{file.Path}:L{span.StartLinePosition.Line + 1}-L{span.EndLinePosition.Line + 1}";

        // --- HTTP inbound: controller/route-attributed types (reuses Phase 5 signal shape) ---
        var derivesFromController = baseTypes.Any(b => b.Contains("Controller", StringComparison.Ordinal));
        var hasRouteAttribute = attributeNames.Any(a => a.Contains("Route", StringComparison.Ordinal) || a.Contains("Http", StringComparison.Ordinal));
        if (derivesFromController || hasRouteAttribute)
        {
            var authorize = attributeNames.Any(a => a.Contains("Authorize", StringComparison.Ordinal));
            results.Add(new IntegrationCandidate(
                IntegrationCategory.HttpApi,
                IntegrationClassification.RuntimeApplication,
                IntegrationDirection.Inbound,
                TriggerOrCaller: "External HTTP client",
                ProtocolOrMechanism: "HTTP/REST (ASP.NET Core controller)",
                LogicalTarget: typeName,
                ConfigurationKeySemantics: null,
                RedactedContractShape: null,
                AuthenticationSemantics: authorize ? "[Authorize] attribute present on controller." : null,
                ReliabilityBehavior: null,
                Confidence: 0.9,
                ClassificationRule: "ControllerBaseTypeOrRouteAttribute",
                BlindSpots: Array.Empty<string>(),
                RequiresReview: false,
                ArtifactPath: file.Path,
                Citations: new[] { new IntegrationEvidenceCitation("ControllerBaseTypeOrRouteAttribute", locator, $"Type '{typeName}' derives from a Controller base type or carries route attribute(s).") }));
        }

        // --- Database/shared store: EF Core DbContext (reuses Phase 5 signal shape) ---
        if (baseTypes.Any(b => b.Contains("DbContext", StringComparison.Ordinal)))
        {
            results.Add(new IntegrationCandidate(
                IntegrationCategory.DatabaseOrSharedStore,
                IntegrationClassification.RuntimeApplication,
                IntegrationDirection.Outbound,
                TriggerOrCaller: typeName,
                ProtocolOrMechanism: "EF Core / SQL Server",
                LogicalTarget: typeName,
                ConfigurationKeySemantics: null,
                RedactedContractShape: null,
                AuthenticationSemantics: null,
                ReliabilityBehavior: null,
                Confidence: 0.9,
                ClassificationRule: "EfDbContextBaseType",
                BlindSpots: Array.Empty<string>(),
                RequiresReview: false,
                ArtifactPath: file.Path,
                Citations: new[] { new IntegrationEvidenceCitation("EfDbContextBaseType", locator, $"Type '{typeName}' derives from a DbContext base type.") }));
        }

        // --- Scheduled/background process: BackgroundService/IHostedService ---
        var isBackgroundWorker = baseTypes.Any(b => b.Contains("BackgroundService", StringComparison.Ordinal)) ||
            typeDecl.BaseList?.Types.Any(t => t.Type.ToString().Contains("IHostedService", StringComparison.Ordinal)) == true;
        if (isBackgroundWorker)
        {
            // Delivery-vs-runtime split is project/file-path-based (not an authoritative
            // package/attribute signal): a worker project whose name/path signals a
            // migration/deploy-only tool (e.g. "*DatabaseMigration*") is classified Delivery;
            // everything else defaults to RuntimeApplication. This is a naming/path-adjacent
            // heuristic — the same class of risk as the Phase 5 generic-name issue — so it is
            // explicitly flagged via BlindSpots and RequiresReview rather than shipped silently.
            var normalizedPath = file.Path.Replace('\\', '/');
            var looksLikeDeliveryOnly = normalizedPath.Contains("Migration", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.Contains("Deploy", StringComparison.OrdinalIgnoreCase);
            var classification = looksLikeDeliveryOnly ? IntegrationClassification.Delivery : IntegrationClassification.RuntimeApplication;
            var blindSpot = "Delivery-vs-runtime classification for this background worker is based on the " +
                "containing file path/project name (e.g. a '*Migration*'/'*Deploy*' segment), not an authoritative " +
                "package/attribute/base-type signal. A worker whose path does not contain such a segment defaults " +
                "to RuntimeApplication even if it is actually a deployment/operations-only tool, and vice versa " +
                "(design doc §14 lesson from the Phase 5 generic-name heuristic).";

            results.Add(new IntegrationCandidate(
                IntegrationCategory.ScheduledOrBackgroundProcess,
                classification,
                IntegrationDirection.Outbound,
                TriggerOrCaller: "Host scheduler / hosted service lifecycle",
                ProtocolOrMechanism: "BackgroundService / IHostedService",
                LogicalTarget: typeName,
                ConfigurationKeySemantics: null,
                RedactedContractShape: null,
                AuthenticationSemantics: null,
                ReliabilityBehavior: null,
                Confidence: 0.6,
                ClassificationRule: "BackgroundServiceBaseTypeWithPathBasedDeliverySplit",
                BlindSpots: new[] { blindSpot },
                RequiresReview: true,
                ArtifactPath: file.Path,
                Citations: new[] { new IntegrationEvidenceCitation("BackgroundServiceBaseType", locator, $"Type '{typeName}' derives from BackgroundService or implements IHostedService.") }));
        }

        // --- HTTP outbound: types that consume IHttpClientFactory/HttpClient (constructor
        // parameter or field type) to call another service. Authoritative on "this type makes
        // outbound HTTP calls" (a real BCL/DI type), but the actual destination logical target
        // cannot be resolved from static syntax alone (the base URL is normally supplied via
        // configuration/DI at runtime), so every hit is flagged for review with an explicit
        // blind spot rather than asserting a specific downstream target with full confidence.
        var constructorParamTypes = typeDecl.Members.OfType<ConstructorDeclarationSyntax>()
            .SelectMany(c => c.ParameterList.Parameters)
            .Select(p => p.Type?.ToString())
            .Where(t => t is not null)
            .Cast<string>();
        var fieldTypes = typeDecl.Members.OfType<FieldDeclarationSyntax>()
            .Select(f => f.Declaration.Type.ToString());
        var usesHttpClient = constructorParamTypes.Concat(fieldTypes)
            .Any(t => t.Contains("IHttpClientFactory", StringComparison.Ordinal) || t.Contains("HttpClient", StringComparison.Ordinal));
        if (usesHttpClient)
        {
            results.Add(new IntegrationCandidate(
                IntegrationCategory.HttpApi,
                IntegrationClassification.RuntimeApplication,
                IntegrationDirection.Outbound,
                TriggerOrCaller: typeName,
                ProtocolOrMechanism: "HTTP/REST (IHttpClientFactory/HttpClient client)",
                LogicalTarget: typeName,
                ConfigurationKeySemantics: null,
                RedactedContractShape: null,
                AuthenticationSemantics: null,
                ReliabilityBehavior: null,
                Confidence: 0.6,
                ClassificationRule: "HttpClientFactoryOrHttpClientConsumer",
                BlindSpots: new[] { "Detected via IHttpClientFactory/HttpClient constructor-parameter or field type — an authoritative signal that this type performs outbound HTTP calls, but the actual destination host/service is normally supplied at runtime via configuration/DI and cannot be resolved from static syntax alone. LogicalTarget records the calling type, not the real downstream target." },
                RequiresReview: true,
                ArtifactPath: file.Path,
                Citations: new[] { new IntegrationEvidenceCitation("HttpClientFactoryOrHttpClientConsumer", locator, $"Type '{typeName}' takes an IHttpClientFactory/HttpClient dependency (constructor parameter or field).") }));
        }

        // --- Notification: types shaped like an email/notification delivery client ---
        if (typeName.Contains("EmailDeliveryClient", StringComparison.Ordinal) ||
            typeName.Contains("NotificationOutbox", StringComparison.Ordinal))
        {
            var isOutbox = typeName.Contains("Outbox", StringComparison.Ordinal);
            results.Add(new IntegrationCandidate(
                isOutbox ? IntegrationCategory.Messaging : IntegrationCategory.Notification,
                IntegrationClassification.RuntimeApplication,
                IntegrationDirection.Outbound,
                TriggerOrCaller: typeName,
                ProtocolOrMechanism: isOutbox ? "Outbox pattern (persisted queue)" : "Notification delivery client",
                LogicalTarget: typeName,
                ConfigurationKeySemantics: null,
                RedactedContractShape: null,
                AuthenticationSemantics: null,
                ReliabilityBehavior: isOutbox ? "Outbox pattern: message persisted before delivery, decoupling write from send for at-least-once delivery." : null,
                Confidence: 0.7,
                ClassificationRule: "NotificationOrOutboxTypeNameShape",
                BlindSpots: new[] { "Detected via type-name shape ('EmailDeliveryClient'/'NotificationOutbox'), not a resolved interface/attribute signal; a differently named type with the same behavior would be missed." },
                RequiresReview: true,
                ArtifactPath: file.Path,
                Citations: new[] { new IntegrationEvidenceCitation("NotificationOrOutboxTypeNameShape", locator, $"Type '{typeName}' name shape matches a notification/outbox delivery pattern.") }));
        }
    }

    private static void Add(
        List<IntegrationCandidate> results,
        ClassifierSourceFile file,
        IntegrationCategory category,
        IntegrationClassification classification,
        IntegrationDirection direction,
        string protocol,
        string target,
        string rule,
        double confidence,
        bool requiresReview,
        IReadOnlyList<string> blindSpots,
        string? authenticationSemantics = null)
    {
        results.Add(new IntegrationCandidate(
            category, classification, direction,
            TriggerOrCaller: file.Path,
            ProtocolOrMechanism: protocol,
            LogicalTarget: target,
            ConfigurationKeySemantics: null,
            RedactedContractShape: null,
            AuthenticationSemantics: authenticationSemantics,
            ReliabilityBehavior: null,
            Confidence: confidence,
            ClassificationRule: rule,
            BlindSpots: blindSpots,
            RequiresReview: requiresReview,
            ArtifactPath: file.Path,
            Citations: new[] { new IntegrationEvidenceCitation(rule, $"{file.Path}:L1", $"'using {target};' declared in '{file.Path}'.") }));
    }

    private static bool Matches(string ns, string[] roots) =>
        roots.Any(root => ns.Equals(root, StringComparison.Ordinal) || ns.StartsWith(root + ".", StringComparison.Ordinal));
}
