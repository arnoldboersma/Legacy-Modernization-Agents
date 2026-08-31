namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Kind of a deterministic dependency-graph node (design doc §5, §5.1, issue #3). Node kinds
/// span both structural units (projects, namespaces, types) and typed evidence surfaces (routes,
/// DI registrations, EF DbContext/entities) so the graph captures more than a file-first project
/// inventory.
/// </summary>
public enum GraphNodeKind
{
    Project,
    Namespace,
    Type,
    Route,
    DbContext,
    DbEntity,
    ServiceRegistration,
    ExternalDependency
}

/// <summary>
/// Kind of a deterministic dependency-graph edge connecting two <see cref="DependencyGraphNode"/>
/// records. Every edge is evidence-backed; it is never inferred from naming alone.
/// </summary>
public enum GraphEdgeKind
{
    /// <summary>A project references another project (ProjectReference/PackageReference-shaped).</summary>
    DependsOnProject,

    /// <summary>A namespace/type contains or is declared within another node (structural containment).</summary>
    ContainsType,

    /// <summary>A type references or is invoked from another type/namespace (syntactic call/reference proxy).</summary>
    References,

    /// <summary>A type is registered into the DI container (constructor injection consumer signal).</summary>
    Injects,

    /// <summary>A DbContext exposes a DbSet mapped to an entity type.</summary>
    MapsToEntity,

    /// <summary>A controller/minimal-API type exposes an HTTP route.</summary>
    ExposesRoute
}

/// <summary>
/// A deterministic, evidence-backed node in the dependency graph (design doc §5.1). Nodes are
/// append-only per run; corrections are new rows, never in-place edits, consistent with every
/// other Discovery Factory governed record family.
/// </summary>
public sealed class DependencyGraphNode
{
    /// <summary>Stable run-scoped identifier, e.g. "GNODE-0001".</summary>
    public required string NodeId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Structural/semantic kind this node represents.</summary>
    public required GraphNodeKind Kind { get; init; }

    /// <summary>
    /// Namespace-qualified symbol identity (type/method) or project/route locator this node
    /// represents. Distinct from <see cref="ArtifactId"/>'s file path.
    /// </summary>
    public required string SymbolLocator { get; init; }

    /// <summary>Human-readable display name, e.g. a short type or route name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Originating artifact this node was extracted from, if applicable.</summary>
    public string? ArtifactId { get; init; }

    /// <summary>Cited evidence IDs supporting this node's existence.</summary>
    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();

    /// <summary>UTC timestamp this node was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// A deterministic, evidence-backed dependency edge between two graph nodes. Append-only; the
/// same edge kind between the same two nodes may be re-asserted across runs but is never mutated.
/// </summary>
public sealed class DependencyGraphEdge
{
    /// <summary>Stable run-scoped identifier, e.g. "GEDGE-0001".</summary>
    public required string EdgeId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Source node ID.</summary>
    public required string FromNodeId { get; init; }

    /// <summary>Target node ID.</summary>
    public required string ToNodeId { get; init; }

    /// <summary>Relationship kind between the two nodes.</summary>
    public required GraphEdgeKind Kind { get; init; }

    /// <summary>Cited evidence IDs supporting this edge.</summary>
    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();

    /// <summary>Confidence in this edge, 0.0-1.0, based on deterministic evidence strength.</summary>
    public required double Confidence { get; init; }

    /// <summary>UTC timestamp this edge was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
