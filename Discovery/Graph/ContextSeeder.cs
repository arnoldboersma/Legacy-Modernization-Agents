using CobolToQuarkusMigration.Discovery.Models;

namespace CobolToQuarkusMigration.Discovery.Graph;

/// <summary>A single seeded cluster of nodes proposed as a candidate context, before persistence.</summary>
public sealed record ContextSeedCandidate(
    string Name,
    ContextCandidateKind Kind,
    double Confidence,
    string SeedingRule,
    IReadOnlyList<string> MemberNodeIds,
    IReadOnlyList<string> OwnerNodeIds,
    IReadOnlyList<string> EvidenceGraphEdgeIds);

/// <summary>A seeded cross-context dependency between two seed candidates, keyed by candidate name.</summary>
public sealed record ContextSeedDependency(
    string FromCandidateName,
    string ToCandidateName,
    double Confidence,
    IReadOnlyList<string> EvidenceGraphEdgeIds);

/// <summary>Result of seeding candidate contexts from a dependency graph.</summary>
public sealed record ContextSeedResult(
    IReadOnlyList<ContextSeedCandidate> Candidates,
    IReadOnlyList<ContextSeedDependency> Dependencies);

/// <summary>
/// Seeds candidate logical contexts / module boundaries from a deterministic dependency graph
/// (design doc §5.1, issue #3). Candidates are proposed, never authoritative — every candidate
/// remains Plausible/Unconfirmed until a human reviewer confirms it (design doc §5.4). A project
/// or controller is never assumed to equal a bounded context: clusters are seeded from repeated
/// naming, co-location, ownership, coupling, and cohesion signals over <see cref="Type"/> nodes,
/// with shared/infrastructure-tagged nodes (per Phase 4 <see cref="ArtifactRoleTag"/> assignments)
/// excluded from business-context grouping by default.
/// </summary>
public static class ContextSeeder
{
    /// <summary>
    /// Seeds candidate contexts from graph nodes/edges plus Phase 4 role assignments (used purely
    /// as an input signal to separate shared/infrastructure material from business-context
    /// candidates — role classification itself is never re-derived here).
    /// </summary>
    public static ContextSeedResult SeedCandidates(
        IReadOnlyList<DependencyGraphNode> nodes,
        IReadOnlyList<DependencyGraphEdge> edges,
        IReadOnlyList<RoleAssignment> roleAssignments)
    {
        var typeNodes = nodes.Where(n => n.Kind == GraphNodeKind.Type).ToList();
        if (typeNodes.Count == 0)
        {
            return new ContextSeedResult(Array.Empty<ContextSeedCandidate>(), Array.Empty<ContextSeedDependency>());
        }

        var rolesBySymbol = roleAssignments
            .GroupBy(r => r.SymbolLocator, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(r => r.Roles).Distinct().ToList(), StringComparer.Ordinal);

        var sharedInfraTags = new HashSet<ArtifactRoleTag>
        {
            ArtifactRoleTag.CompositionDI, ArtifactRoleTag.Middleware, ArtifactRoleTag.FrameworkAdapter,
            ArtifactRoleTag.Persistence, ArtifactRoleTag.IntegrationAdapter, ArtifactRoleTag.Shared,
            ArtifactRoleTag.Generated, ArtifactRoleTag.Test, ArtifactRoleTag.BuildTooling,
        };

        // Naming-convention / co-location clustering: group by the first "business-looking"
        // namespace segment (skipping common technical segment names), which is the deterministic
        // proxy for co-location + naming-convention signals described in §5.1.
        var clusters = typeNodes
            .GroupBy(n => ClusterKey(n.SymbolLocator), StringComparer.Ordinal)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToList();

        var candidates = new List<ContextSeedCandidate>();
        var candidateNodeIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var cluster in clusters)
        {
            var members = cluster.ToList();
            var memberNodeIds = members.Select(m => m.NodeId).ToList();

            var businessCount = members.Count(m => IsBusinessDominant(m.SymbolLocator, rolesBySymbol, sharedInfraTags));
            var isSharedInfraDominant = businessCount == 0;

            var seedingRules = new List<string> { "NamespaceCoLocation", "NamingConvention" };

            // Ownership: a node is "owned" by this cluster when it is referenced only from within
            // the cluster's own member set (no external in-edge from a Type node outside the cluster).
            var memberSet = memberNodeIds.ToHashSet(StringComparer.Ordinal);
            var externalReferencers = edges
                .Where(e => e.Kind == GraphEdgeKind.References && memberSet.Contains(e.ToNodeId) && !memberSet.Contains(e.FromNodeId))
                .Select(e => e.ToNodeId)
                .ToHashSet(StringComparer.Ordinal);
            var ownerNodeIds = memberNodeIds.Where(id => !externalReferencers.Contains(id)).ToList();
            if (ownerNodeIds.Count > 0 && ownerNodeIds.Count < memberNodeIds.Count)
            {
                seedingRules.Add("Ownership");
            }

            // Coupling / cohesion: ratio of intra-cluster edges to all edges touching the cluster.
            var touchingEdges = edges.Where(e => memberSet.Contains(e.FromNodeId) || memberSet.Contains(e.ToNodeId)).ToList();
            var intraEdges = touchingEdges.Where(e => memberSet.Contains(e.FromNodeId) && memberSet.Contains(e.ToNodeId)).ToList();
            var cohesion = touchingEdges.Count == 0 ? 1.0 : (double)intraEdges.Count / touchingEdges.Count;
            if (touchingEdges.Count > 0)
            {
                seedingRules.Add(cohesion >= 0.5 ? "CohesionDensity" : "CouplingDensity");
            }

            // Confidence reflects heuristic agreement: more corroborating signals and higher
            // cohesion raise confidence; mixed/ambiguous clusters (low cohesion, no clear owner)
            // stay lower confidence and remain Unconfirmed rather than forced into one grouping.
            var confidence = Math.Clamp(0.4 + (0.15 * (seedingRules.Count - 1)) + (0.2 * cohesion), 0.3, 0.9);

            var evidenceEdgeIds = touchingEdges.Select(e => e.EdgeId).Distinct().ToList();

            candidates.Add(new ContextSeedCandidate(
                Name: cluster.Key,
                Kind: isSharedInfraDominant ? ContextCandidateKind.SharedInfrastructure : ContextCandidateKind.BusinessContext,
                Confidence: confidence,
                SeedingRule: string.Join(";", seedingRules),
                MemberNodeIds: memberNodeIds,
                OwnerNodeIds: ownerNodeIds,
                EvidenceGraphEdgeIds: evidenceEdgeIds));

            candidateNodeIds[cluster.Key] = memberSet;
        }

        // Cross-context dependency edges: any References/Injects/MapsToEntity edge whose endpoints
        // fall in two different candidate clusters becomes a recorded cross-context dependency
        // rather than being silently absorbed into one grouping (acceptance criteria: mixed or
        // unresolved boundaries are visible).
        var dependencies = new List<ContextSeedDependency>();
        var seenPairs = new HashSet<(string From, string To)>();
        foreach (var edge in edges)
        {
            if (edge.Kind is not (GraphEdgeKind.References or GraphEdgeKind.Injects or GraphEdgeKind.MapsToEntity))
            {
                continue;
            }

            var fromCandidate = candidateNodeIds.FirstOrDefault(kv => kv.Value.Contains(edge.FromNodeId)).Key;
            var toCandidate = candidateNodeIds.FirstOrDefault(kv => kv.Value.Contains(edge.ToNodeId)).Key;
            if (fromCandidate is null || toCandidate is null || fromCandidate == toCandidate)
            {
                continue;
            }

            var pairKey = (fromCandidate, toCandidate);
            if (!seenPairs.Add(pairKey))
            {
                continue;
            }

            var pairEdgeIds = edges
                .Where(e => e.Kind is GraphEdgeKind.References or GraphEdgeKind.Injects or GraphEdgeKind.MapsToEntity)
                .Where(e => candidateNodeIds[fromCandidate].Contains(e.FromNodeId) && candidateNodeIds[toCandidate].Contains(e.ToNodeId))
                .Select(e => e.EdgeId)
                .Distinct()
                .ToList();

            dependencies.Add(new ContextSeedDependency(fromCandidate, toCandidate, edge.Confidence, pairEdgeIds));
        }

        return new ContextSeedResult(candidates, dependencies);
    }

    private static readonly HashSet<string> TechnicalSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Controllers", "Services", "Models", "ViewModels", "BindingModels", "Entities", "Interfaces",
        "Repositories", "Extensions", "Utils", "Migrations", "DataAccess", "Api", "WebApp", "App",
        "AppHost", "ServiceDefaults", "Utilities", "Worker", "Tests", "DatabaseMigration",
    };

    /// <summary>
    /// Derives the deterministic clustering key for a namespace-qualified symbol locator: the
    /// first namespace segment that is not a well-known technical/layer segment name, i.e. the
    /// proxy for "repeated naming and co-location" per §5.1. Falls back to the full leading
    /// namespace prefix (assembly root) when every segment is a technical name.
    /// </summary>
    private static string ClusterKey(string symbolLocator)
    {
        var lastDot = symbolLocator.LastIndexOf('.');
        var ns = lastDot >= 0 ? symbolLocator[..lastDot] : string.Empty;
        var segments = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        foreach (var segment in segments.Skip(1))
        {
            if (!TechnicalSegments.Contains(segment))
            {
                return segment;
            }
        }

        return segments[0];
    }

    private static bool IsBusinessDominant(
        string symbolLocator,
        IReadOnlyDictionary<string, List<ArtifactRoleTag>> rolesBySymbol,
        HashSet<ArtifactRoleTag> sharedInfraTags)
    {
        if (!rolesBySymbol.TryGetValue(symbolLocator, out var roles) || roles.Count == 0)
        {
            // No role assignment available (e.g. role classification not yet run for this
            // artifact): treat as business-eligible rather than silently excluding it, consistent
            // with GetBusinessEligibleArtifactsAsync's conservative default elsewhere.
            return true;
        }

        return roles.Any(r => r == ArtifactRoleTag.Business) || !roles.Any(sharedInfraTags.Contains);
    }
}
