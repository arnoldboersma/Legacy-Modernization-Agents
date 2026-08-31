namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Kind of a candidate logical context/module boundary (design doc §5.1, issue #3). A
/// <see cref="ArtifactRoleTag.Business"/>-dominant cluster of nodes seeds a
/// <see cref="BusinessContext"/> candidate; clusters dominated by shared/infrastructure role
/// tags (CompositionDI, Middleware, FrameworkAdapter, Persistence, IntegrationAdapter, Shared,
/// Generated, Test, BuildTooling — see <c>ArtifactRoleTag</c>) are represented as graph nodes but
/// seeded as <see cref="SharedInfrastructure"/>, never emitted as a business context by default.
/// </summary>
public enum ContextCandidateKind
{
    BusinessContext,
    SharedInfrastructure
}

/// <summary>
/// An append-only, evidence-backed candidate logical context or module boundary. Reuses
/// <see cref="ReviewStatus"/> for its lifecycle: <c>Candidate</c>/<c>HumanReview</c> map to
/// "Plausible", <c>NeedsEvidence</c> maps to "Unconfirmed", and only a human reviewer can move a
/// candidate to <c>Published</c> ("confirmed") — LLM output may explain and prioritize a
/// candidate but can never publish or reject one (design doc §5.4, issue #3 scope item 5).
/// Corrections are new rows linked via <see cref="SupersedesContextCandidateId"/>; existing rows
/// are never mutated, consistent with every other Discovery Factory governed record family.
/// </summary>
public sealed class ContextCandidate
{
    /// <summary>Stable run-scoped identifier, e.g. "CTX-0001".</summary>
    public required string ContextCandidateId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Human-readable candidate name, e.g. "Forecast Management".</summary>
    public required string Name { get; init; }

    /// <summary>Business-context vs. shared/infrastructure separation (issue #3 scope item 4).</summary>
    public required ContextCandidateKind Kind { get; init; }

    /// <summary>
    /// Append-only review lifecycle status, reusing <see cref="ReviewStatus"/>. Never set to
    /// <see cref="ReviewStatus.Published"/> by any deterministic or LLM-assisted producer — only
    /// a human reviewer decision can do so.
    /// </summary>
    public required ReviewStatus Status { get; init; }

    /// <summary>Confidence in this candidate boundary, 0.0-1.0, based on deterministic evidence strength.</summary>
    public required double Confidence { get; init; }

    /// <summary>Cited evidence IDs supporting this candidate (naming, co-location, coupling, cohesion signals).</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }

    /// <summary>
    /// Names of the deterministic seeding heuristics that fired, joined with ';' when multiple
    /// signals contributed, e.g. "NamespaceCoLocation;NamingConvention;CohesionDensity".
    /// </summary>
    public required string SeedingRule { get; init; }

    /// <summary>Provenance record ID explaining how this candidate was produced.</summary>
    public required string ProvenanceId { get; init; }

    /// <summary>If this candidate corrects/supersedes a prior candidate declaration, its ID.</summary>
    public string? SupersedesContextCandidateId { get; init; }

    /// <summary>UTC timestamp this candidate was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>Role a member node plays within a candidate context.</summary>
public enum ContextMembershipRole
{
    Owner,
    Member,
    Referenced
}

/// <summary>
/// An append-only record linking a <see cref="DependencyGraphNode"/> to a candidate context it
/// belongs to. A node may belong to more than one candidate (e.g. as Member of one and
/// Referenced from another) — membership is never forced into a single exclusive grouping when
/// evidence is mixed.
/// </summary>
public sealed class ContextMembership
{
    /// <summary>Stable run-scoped identifier, e.g. "CMEM-0001".</summary>
    public required string ContextMembershipId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Owning candidate context.</summary>
    public required string ContextCandidateId { get; init; }

    /// <summary>Member graph node.</summary>
    public required string NodeId { get; init; }

    /// <summary>Role this node plays within the candidate context.</summary>
    public required ContextMembershipRole Role { get; init; }

    /// <summary>Cited evidence IDs supporting this membership.</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }

    /// <summary>UTC timestamp this membership was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// An append-only, evidence-backed cross-context dependency edge, recording that one candidate
/// context depends on another (design doc §5.1 acceptance criteria: cross-context dependencies
/// must be visible, not force-merged into a single grouping).
/// </summary>
public sealed class ContextDependencyEdge
{
    /// <summary>Stable run-scoped identifier, e.g. "CDEP-0001".</summary>
    public required string ContextDependencyEdgeId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Dependent candidate context.</summary>
    public required string FromContextCandidateId { get; init; }

    /// <summary>Depended-on candidate context.</summary>
    public required string ToContextCandidateId { get; init; }

    /// <summary>Cited evidence IDs (typically the underlying <see cref="DependencyGraphEdge"/> IDs) supporting this dependency.</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }

    /// <summary>Confidence in this cross-context dependency, 0.0-1.0.</summary>
    public required double Confidence { get; init; }

    /// <summary>UTC timestamp this edge was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
