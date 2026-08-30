namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Closure/lifecycle state for a discovery run's processing (distinct from finding review status).
/// </summary>
public enum DiscoveryRunStatus
{
    Declared,
    InProgress,
    ReadyForReview,
    Completed,
    Blocked
}

/// <summary>
/// A versioned, append-only Discovery Factory run declaration. Represents one reconstruction
/// attempt over a declared subject, source revision, and evidence boundary. Corrections to a
/// run's declaration create a new <see cref="DiscoveryRun"/> row linked via
/// <see cref="SupersedesRunId"/>; existing rows are never mutated.
/// </summary>
public sealed class DiscoveryRun
{
    /// <summary>Stable run-scoped identifier, e.g. "RUN-0001".</summary>
    public required string RunId { get; init; }

    /// <summary>Human-readable subject under assessment, e.g. "PlanBord Forecast Management".</summary>
    public required string Subject { get; init; }

    /// <summary>Immutable source locator (repo URL, path, etc.) for the declared subject.</summary>
    public required string SourceLocator { get; init; }

    /// <summary>Immutable source revision (commit SHA, tag, or content hash) the run is scoped to.</summary>
    public required string SourceRevision { get; init; }

    /// <summary>Declared inclusions (paths, modules, surfaces) in scope for this run.</summary>
    public IReadOnlyList<string> Inclusions { get; init; } = Array.Empty<string>();

    /// <summary>Declared exclusions explicitly out of scope for this run.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = Array.Empty<string>();

    /// <summary>Declared evidence boundary, e.g. "static source, configuration keys, schema; no runtime logs".</summary>
    public required string EvidenceBoundary { get; init; }

    /// <summary>Intent/purpose of this run, e.g. "pilot vertical slice".</summary>
    public string? Intent { get; init; }

    /// <summary>Processing closure state. Distinct from any finding's review status.</summary>
    public DiscoveryRunStatus Status { get; init; } = DiscoveryRunStatus.Declared;

    /// <summary>UTC timestamp the run was declared/appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>If this run corrects/supersedes a prior run declaration, the prior run's ID.</summary>
    public string? SupersedesRunId { get; init; }
}
