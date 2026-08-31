using CobolToQuarkusMigration.Discovery.Graph;
using CobolToQuarkusMigration.Discovery.Models;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

public sealed class ContextSeederTests
{
    private static DependencyGraphNode Node(string id, GraphNodeKind kind, string symbolLocator, string displayName) => new()
    {
        NodeId = id,
        RunId = "RUN-1",
        Kind = kind,
        SymbolLocator = symbolLocator,
        DisplayName = displayName,
        EvidenceIds = Array.Empty<string>(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static DependencyGraphEdge Edge(string id, string from, string to, GraphEdgeKind kind, string[] evidenceIds) => new()
    {
        EdgeId = id,
        RunId = "RUN-1",
        FromNodeId = from,
        ToNodeId = to,
        Kind = kind,
        EvidenceIds = evidenceIds,
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static RoleAssignment Role(string symbolLocator, params ArtifactRoleTag[] roles) => new()
    {
        RoleAssignmentId = $"ROLE-{symbolLocator.GetHashCode():X}",
        RunId = "RUN-1",
        ArtifactId = "ART-1",
        SymbolLocator = symbolLocator,
        Roles = roles,
        Confidence = 0.9,
        EvidenceIds = Array.Empty<string>(),
        ClassificationRule = "Test",
        RequiresReview = false,
        ProvenanceId = "PROV-1",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void SeedCandidates_NoTypeNodes_ReturnsEmptyResult()
    {
        var result = ContextSeeder.SeedCandidates(
            Array.Empty<DependencyGraphNode>(), Array.Empty<DependencyGraphEdge>(), Array.Empty<RoleAssignment>());

        result.Candidates.Should().BeEmpty();
        result.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void SeedCandidates_ClustersTypesByNamespaceSegment_NeverEqualsOneProjectToOneContext()
    {
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "App.Forecast.ForecastService", "ForecastService"),
            Node("N2", GraphNodeKind.Type, "App.Forecast.ForecastController", "ForecastController"),
            Node("N3", GraphNodeKind.Type, "App.Billing.InvoiceService", "InvoiceService"),
        };

        var result = ContextSeeder.SeedCandidates(nodes, Array.Empty<DependencyGraphEdge>(), Array.Empty<RoleAssignment>());

        // Two distinct clusters from one "project"/solution: never a single project==context grouping.
        result.Candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "Forecast", "Billing" });
        result.Candidates.Should().OnlyContain(c => c.Kind == ContextCandidateKind.BusinessContext);
    }

    [Fact]
    public void SeedCandidates_SharedInfrastructureRoleDominant_SeedsSharedInfrastructureKind()
    {
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "App.Platform.StartupExtensions", "StartupExtensions"),
        };
        var roles = new[] { Role("App.Platform.StartupExtensions", ArtifactRoleTag.CompositionDI) };

        var result = ContextSeeder.SeedCandidates(nodes, Array.Empty<DependencyGraphEdge>(), roles);

        result.Candidates.Should().ContainSingle(c => c.Kind == ContextCandidateKind.SharedInfrastructure);
    }

    [Fact]
    public void SeedCandidates_BusinessRole_SeedsBusinessContextKind()
    {
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "App.Forecast.ForecastService", "ForecastService"),
        };
        var roles = new[] { Role("App.Forecast.ForecastService", ArtifactRoleTag.Business) };

        var result = ContextSeeder.SeedCandidates(nodes, Array.Empty<DependencyGraphEdge>(), roles);

        result.Candidates.Should().ContainSingle(c => c.Kind == ContextCandidateKind.BusinessContext);
    }

    [Fact]
    public void SeedCandidates_CrossClusterReferenceEdge_RecordedAsContextDependency_NotMerged()
    {
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "App.Forecast.ForecastService", "ForecastService"),
            Node("N2", GraphNodeKind.Type, "App.Billing.InvoiceService", "InvoiceService"),
        };
        var edges = new[]
        {
            Edge("E1", "N1", "N2", GraphEdgeKind.References, new[] { "EVD-1" }),
        };

        var result = ContextSeeder.SeedCandidates(nodes, edges, Array.Empty<RoleAssignment>());

        result.Candidates.Should().HaveCount(2);
        result.Dependencies.Should().ContainSingle(d => d.FromCandidateName == "Forecast" && d.ToCandidateName == "Billing");
    }

    [Fact]
    public void SeedCandidates_OwnershipHeuristic_MarksNodeWithNoExternalReferencerAsOwner()
    {
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "App.Forecast.ForecastService", "ForecastService"),
            Node("N2", GraphNodeKind.Type, "App.Forecast.ForecastController", "ForecastController"),
        };
        // N2 (controller) references N1 (service) — both intra-cluster, so both are "owned" by the
        // cluster since neither is referenced from outside it.
        var edges = new[]
        {
            Edge("E1", "N2", "N1", GraphEdgeKind.References, new[] { "EVD-1" }),
        };

        var result = ContextSeeder.SeedCandidates(nodes, edges, Array.Empty<RoleAssignment>());

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.OwnerNodeIds.Should().BeEquivalentTo(new[] { "N1", "N2" });
    }

    [Fact]
    public void SeedCandidates_LowConfidenceCluster_StillProducesCandidateWithConfidenceBelowOne()
    {
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "App.Forecast.ForecastService", "ForecastService"),
        };

        var result = ContextSeeder.SeedCandidates(nodes, Array.Empty<DependencyGraphEdge>(), Array.Empty<RoleAssignment>());

        result.Candidates.Should().ContainSingle().Which.Confidence.Should().BeInRange(0.0, 0.9);
    }

    [Fact]
    public void SeedCandidates_FrameworkNamespaceDominantCluster_SeedsSharedInfrastructure_EvenWithoutRoleAssignments()
    {
        // No role assignments at all (e.g. classify-roles was never run for this artifact):
        // the framework-namespace hard filter must still exclude this from BusinessContext.
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "Microsoft.Extensions.Hosting.HostBuilderExtensions", "HostBuilderExtensions"),
            Node("N2", GraphNodeKind.Type, "Microsoft.Extensions.Hosting.OptionsSetup", "OptionsSetup"),
        };

        var result = ContextSeeder.SeedCandidates(nodes, Array.Empty<DependencyGraphEdge>(), Array.Empty<RoleAssignment>());

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.Kind.Should().Be(ContextCandidateKind.SharedInfrastructure);
        candidate.SeedingRule.Should().Contain("FrameworkNamespaceFilter");
    }

    [Fact]
    public void SeedCandidates_PlanBoardCodeDeclaredInFrameworkNamespace_RecordedAsSharedInfraEvidence_NotHidden()
    {
        // Mirrors the real PlanBoard case: PrefixKeyVaultSecretManager/InstrumentationSource/
        // Extensions are PlanBoard's own types but declared inside Microsoft.Extensions.Hosting.
        // This is a real code smell that must still be recorded — as evidence on a
        // SharedInfrastructure cluster, not silently folded into a business context.
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "Microsoft.Extensions.Hosting.PrefixKeyVaultSecretManager", "PrefixKeyVaultSecretManager"),
            Node("N2", GraphNodeKind.Type, "Microsoft.Extensions.Hosting.InstrumentationSource", "InstrumentationSource"),
            Node("N3", GraphNodeKind.Type, "Microsoft.Extensions.Hosting.Extensions", "Extensions"),
        };

        var result = ContextSeeder.SeedCandidates(nodes, Array.Empty<DependencyGraphEdge>(), Array.Empty<RoleAssignment>());

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.Kind.Should().Be(ContextCandidateKind.SharedInfrastructure);
        candidate.MemberNodeIds.Should().BeEquivalentTo(new[] { "N1", "N2", "N3" });
    }

    [Fact]
    public void SeedCandidates_MixedFrameworkAndOwnNamespaceCluster_MajorityFrameworkStillSharedInfrastructure()
    {
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "System.Text.Json.CustomConverter", "CustomConverter"),
            Node("N2", GraphNodeKind.Type, "System.Text.Json.AnotherConverter", "AnotherConverter"),
        };

        var result = ContextSeeder.SeedCandidates(nodes, Array.Empty<DependencyGraphEdge>(), Array.Empty<RoleAssignment>());

        result.Candidates.Should().ContainSingle().Which.Kind.Should().Be(ContextCandidateKind.SharedInfrastructure);
    }

    [Fact]
    public void SeedCandidates_RootNamespaceTechnicalCatchAll_FlaggedAndCappedConfidence_NotHighConfidenceBusinessContext()
    {
        // All non-root segments are well-known technical/layer names (Worker, DataAccess,
        // Extensions): this must not collapse into a single incoherent high-confidence
        // "Planbordv2" business context (the real bug seen against PlanBoard).
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "Planbordv2.Worker.BackgroundJob", "BackgroundJob"),
            Node("N2", GraphNodeKind.Type, "Planbordv2.Worker.JobScheduler", "JobScheduler"),
        };

        var result = ContextSeeder.SeedCandidates(nodes, Array.Empty<DependencyGraphEdge>(), Array.Empty<RoleAssignment>());

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.SeedingRule.Should().Contain("TechnicalCatchAll");
        candidate.Confidence.Should().BeLessOrEqualTo(0.45);
        candidate.Name.Should().Be("Planbordv2.Worker");
    }

    [Fact]
    public void SeedCandidates_RootNamespaceTechnicalCatchAll_SplitsDistinctTechnicalSegmentsIntoSeparateClusters()
    {
        var nodes = new[]
        {
            Node("N1", GraphNodeKind.Type, "Planbordv2.Worker.BackgroundJob", "BackgroundJob"),
            Node("N2", GraphNodeKind.Type, "Planbordv2.DataAccess.ApplicationContext", "ApplicationContext"),
        };

        var result = ContextSeeder.SeedCandidates(nodes, Array.Empty<DependencyGraphEdge>(), Array.Empty<RoleAssignment>());

        result.Candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "Planbordv2.Worker", "Planbordv2.DataAccess" });
        result.Candidates.Should().OnlyContain(c => c.SeedingRule.Contains("TechnicalCatchAll"));
    }
}
