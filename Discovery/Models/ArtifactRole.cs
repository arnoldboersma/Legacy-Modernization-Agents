namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Deterministic, evidence-backed artifact role classification (design doc §5, §5.1). An
/// artifact/symbol may carry multiple tags at once when evidence is mixed; the classifier never
/// invents a single "primary" role for conflicting signals.
/// </summary>
public enum ArtifactRoleTag
{
    /// <summary>Real business behavior: no framework/generated/test/build-tooling signal fired,
    /// and the symbol contains executable logic. Never a default guess — only assigned when
    /// every other deterministic rule failed to match.</summary>
    Business,

    /// <summary>Dependency-injection/composition wiring (service registration, startup composition).</summary>
    CompositionDI,

    /// <summary>Request/response pipeline plumbing (ASP.NET Core middleware, `app.Use...` chains).</summary>
    Middleware,

    /// <summary>Framework-owned adapter surface such as MVC/minimal-API controllers and route registration.</summary>
    FrameworkAdapter,

    /// <summary>Explicit Entity Framework / persistence artifacts (DbContext, entity mappings, migrations).</summary>
    Persistence,

    /// <summary>Adapter wrapping an external system/SDK client (HTTP, cloud SDK, driver) — integration, not business rule.</summary>
    IntegrationAdapter,

    /// <summary>Cross-cutting shared utility referenced from multiple unrelated namespaces.</summary>
    Shared,

    /// <summary>Tool-generated code (e.g. EF Core migrations, designer/auto-generated files).</summary>
    Generated,

    /// <summary>Test code (test projects, test attributes).</summary>
    Test,

    /// <summary>Build/tooling artifacts: project files, CI workflow definitions, embedded SQL migrations, etc.</summary>
    BuildTooling,

    /// <summary>No deterministic signal matched at all. Always routed to reviewer attention.</summary>
    Unknown
}

/// <summary>
/// An append-only, evidence-backed role classification for one symbol/artifact locator within a
/// run. Corrections are new rows (higher <see cref="CreatedAtUtc"/>), never mutations of a prior
/// assignment — consistent with every other Discovery Factory governed record family.
/// </summary>
public sealed class RoleAssignment
{
    /// <summary>Stable run-scoped identifier, e.g. "ROLE-0001".</summary>
    public required string RoleAssignmentId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Artifact this classification applies to.</summary>
    public required string ArtifactId { get; init; }

    /// <summary>
    /// Symbol identity the classification is scoped to, e.g. a namespace-qualified type or
    /// member name from Roslyn ("Planbordv2.DataAccess.ApplicationContext"), distinct from the
    /// artifact's file path. Falls back to the artifact path when no finer symbol identity
    /// applies (e.g. a project file classified as build tooling).
    /// </summary>
    public required string SymbolLocator { get; init; }

    /// <summary>
    /// One or more role tags. Multiple tags may co-occur when evidence is mixed (e.g. a type
    /// that is both <see cref="ArtifactRoleTag.Persistence"/> and <see cref="ArtifactRoleTag.Business"/>).
    /// </summary>
    public required IReadOnlyList<ArtifactRoleTag> Roles { get; init; }

    /// <summary>Confidence in this classification, 0.0-1.0, based on deterministic evidence strength.</summary>
    public required double Confidence { get; init; }

    /// <summary>Cited evidence IDs supporting this classification.</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }

    /// <summary>
    /// Names of the deterministic classification rule(s) that fired for this assignment, joined
    /// with ';' when multiple rules contributed distinct role tags, e.g.
    /// "EfDbContextBaseType;ExecutableBusinessLogic".
    /// </summary>
    public required string ClassificationRule { get; init; }

    /// <summary>
    /// True when this assignment must not be treated as unambiguous business behavior without
    /// human review — set whenever <see cref="Roles"/> mixes business and non-business tags, or
    /// the only tag is <see cref="ArtifactRoleTag.Unknown"/>.
    /// </summary>
    public required bool RequiresReview { get; init; }

    /// <summary>Provenance record ID explaining how this assignment was produced.</summary>
    public required string ProvenanceId { get; init; }

    /// <summary>UTC timestamp this assignment was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
