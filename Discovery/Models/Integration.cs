namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Distinguishes runtime business behavior from platform, observability, and delivery concerns
/// (design doc §7, issue #6 scope item 2). Prevents platform configuration from being presented
/// as business behavior.
/// </summary>
public enum IntegrationClassification
{
    /// <summary>Business-relevant runtime behavior: an application call, message, file, database,
    /// or notification exchange that is part of the modeled system's own logic.</summary>
    RuntimeApplication,

    /// <summary>Platform or identity dependency: authentication/authorization providers, secret
    /// stores, service discovery/hosting configuration. Infrastructure the application runs on,
    /// not business behavior it performs.</summary>
    PlatformIdentity,

    /// <summary>Observability dependency: telemetry, logging, tracing, health checks.</summary>
    Observability,

    /// <summary>Delivery dependency: build/deploy/migration tooling and pipelines that ship or
    /// operate the system rather than run within it.</summary>
    Delivery
}

/// <summary>Direction of an integration relative to the analyzed subject.</summary>
public enum IntegrationDirection
{
    Inbound,
    Outbound,
    Bidirectional
}

/// <summary>
/// Kind of statically observable integration surface (design doc §7, issue #6 scope item 1).
/// </summary>
public enum IntegrationCategory
{
    HttpApi,
    Messaging,
    FileImportExport,
    DatabaseOrSharedStore,
    IdentityOrAuthorization,
    Notification,
    ServiceDiscoveryOrPlatformConfig,
    ScheduledOrBackgroundProcess
}

/// <summary>
/// An append-only, evidence-backed integration record (design doc §7, issue #6). Unlike
/// <see cref="Finding"/>/<see cref="ContextCandidate"/>, an integration is a deterministic
/// presence fact about statically observable behavior — like <see cref="RoleAssignment"/>, it
/// carries confidence and <see cref="RequiresReview"/> but has no publish/reject review
/// lifecycle of its own. Corrections are new rows with a later <see cref="CreatedAtUtc"/>;
/// existing rows are never mutated.
/// </summary>
public sealed class Integration
{
    /// <summary>Stable run-scoped identifier, e.g. "INTG-0001".</summary>
    public required string IntegrationId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Statically observable integration surface kind.</summary>
    public required IntegrationCategory Category { get; init; }

    /// <summary>Business/platform/observability/delivery classification (issue #6 scope item 2).</summary>
    public required IntegrationClassification Classification { get; init; }

    /// <summary>Direction of the integration relative to the analyzed subject.</summary>
    public required IntegrationDirection Direction { get; init; }

    /// <summary>What triggers or calls this integration, e.g. an HTTP route, a scheduled worker,
    /// a constructor-injected client consumer type.</summary>
    public required string TriggerOrCaller { get; init; }

    /// <summary>Protocol or mechanism, e.g. "HTTP/REST", "Azure Service Bus", "EF Core/SQL Server",
    /// "Azure Communication Services Email", "OAuth2/Entra ID".</summary>
    public required string ProtocolOrMechanism { get; init; }

    /// <summary>Logical target of the integration, e.g. a type name, external SDK client, or
    /// configuration-key-identified store — never a resolved connection string or endpoint value.</summary>
    public required string LogicalTarget { get; init; }

    /// <summary>
    /// Permitted configuration-KEY semantics only (e.g. "ConnectionStrings:ApplicationDb",
    /// "KeyVaultName") — never a resolved configuration value, secret, or connection string.
    /// Redaction is applied before this field is ever set (design doc §2, §5.2).
    /// </summary>
    public string? ConfigurationKeySemantics { get; init; }

    /// <summary>Redacted description of the data contract shape, if statically observable
    /// (e.g. a DTO/message type name) — never raw payload content.</summary>
    public string? RedactedContractShape { get; init; }

    /// <summary>Authentication/identity semantics where visible in static evidence (e.g.
    /// "Microsoft Entra ID via Microsoft.Identity.Web", "[Authorize] attribute present").</summary>
    public string? AuthenticationSemantics { get; init; }

    /// <summary>Reliability behavior where evidenced statically (e.g. "outbox pattern via
    /// NotificationOutboxRepository", "retry/resilience policy attribute present"). Never inferred
    /// from runtime observation — static evidence only (issue #6 scope item 4).</summary>
    public string? ReliabilityBehavior { get; init; }

    /// <summary>Owning logical context, linking to a Phase 5 <see cref="ContextCandidate"/> when
    /// resolvable from existing graph/context data — never re-derived from scratch.</summary>
    public string? OwningContextCandidateId { get; init; }

    /// <summary>Cited evidence IDs supporting this integration's existence and shape.</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }

    /// <summary>Confidence in this classification, 0.0-1.0, based on deterministic evidence strength.</summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Name(s) of the deterministic classification rule(s) that fired, joined with ';' when
    /// multiple signals contributed, e.g. "EfDbContextConnectionStringKey;SqlServerPackageReference".
    /// </summary>
    public required string ClassificationRule { get; init; }

    /// <summary>
    /// Explicit, human-readable limitations of this specific record's evidence (design doc §14
    /// lesson: a heuristic that could misfire the way the Phase 5 generic-name issue did must be
    /// flagged, not silently shipped as unqualified confidence). Empty when no material blind spot
    /// applies; never omitted/null — reviewers must always see this field.
    /// </summary>
    public required IReadOnlyList<string> BlindSpots { get; init; }

    /// <summary>
    /// True when this record must not be treated as unambiguous without human review — set
    /// whenever the classifying rule is a naming/path-based proxy rather than an authoritative
    /// package/attribute/base-type signal, or when <see cref="BlindSpots"/> is non-empty.
    /// </summary>
    public required bool RequiresReview { get; init; }

    /// <summary>Provenance record ID explaining how this integration was produced.</summary>
    public required string ProvenanceId { get; init; }

    /// <summary>UTC timestamp this integration record was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>Kind of governed record an <see cref="IntegrationLink"/> connects an integration to.</summary>
public enum IntegrationLinkedRecordKind
{
    GraphNode,
    RoleAssignment,
    ContextCandidate
}

/// <summary>
/// An append-only record linking an <see cref="Integration"/> to a data, configuration/identity,
/// or operational record it depends on (design doc §7 scope item "link each integration to its
/// data, configuration/identity, and operational records"). An integration may link to more than
/// one record (e.g. a DbContext graph node and its owning context candidate).
/// </summary>
public sealed class IntegrationLink
{
    /// <summary>Stable run-scoped identifier, e.g. "ILNK-0001".</summary>
    public required string IntegrationLinkId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Owning integration.</summary>
    public required string IntegrationId { get; init; }

    /// <summary>Kind of record this integration is linked to.</summary>
    public required IntegrationLinkedRecordKind LinkedRecordKind { get; init; }

    /// <summary>Identifier of the linked record (a <see cref="DependencyGraphNode.NodeId"/>,
    /// <see cref="RoleAssignment.RoleAssignmentId"/>, or <see cref="ContextCandidate.ContextCandidateId"/>).</summary>
    public required string LinkedRecordId { get; init; }

    /// <summary>UTC timestamp this link was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
