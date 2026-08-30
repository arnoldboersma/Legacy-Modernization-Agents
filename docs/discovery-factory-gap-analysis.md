**Last updated**: 2026-08-30

# Discovery Factory Gap Analysis

This is a current-state comparison of this repository with
[vitasit/discovery-factory](https://github.com/vitasit/discovery-factory).
It supports a decision about whether the source-analysis subsystem can become
the technical base for Discovery Factory. It is not an implementation plan.

## Decision

The source-analysis subsystem is a viable **technical foundation**, but this
repository is not a drop-in Discovery Factory implementation. Its strongest
reusable capabilities are deterministic C# analysis, pluggable language
analysis, dependency mapping, and relational-plus-graph persistence. The
largest gaps are Discovery Factory's evidence, governance, functional-domain,
risk, and handoff models.

The recommended direction is to retain the source-analysis engine and build a
discovery-oriented layer around it. Conversion-specific agents and models must
not define that layer's public contracts or output.

## Discovery Factory target

Discovery Factory's README and `discovery-kit/` define a current-state
assessment product. It deliberately does not create a future-state solution,
backlog, implementation plan, or modernization recommendation.

Its baseline kit contains:

| Capability | Intended outcome |
|---|---|
| Assessment starter | Subject, scope, system framing, and guardrails. |
| Functional-domain discovery | Domain map with closure state: evidence-backed, partially investigated, or not investigated. |
| Integration discovery | Internal and external integration inventory. |
| Code evidence | Cited current-state facts from source, configuration, logs, or schema. |
| Findings register | Stable `F-` IDs and confidence continuity. |
| Risk register | Stable `R-` IDs, severity, escalation, and linked findings. |
| Handoff to Specification Factory | Scope, findings, risks, gaps, questions, and capability limitations. |

Discovery Factory treats deeper semantic reconstruction as on-demand work,
triggered by an existing finding, risk, explicit gap, or known limitation.

## Current capabilities

| Discovery need | Current implementation | Assessment |
|---|---|---|
| Source discovery | `ISourceDiscovery`, with COBOL and recursive C# implementations. | Strong |
| C# source facts | Roslyn and `MSBuildWorkspace` extract solution/project metadata, types, methods, calls, ASP.NET endpoints, DI, EF Core, and detectable events. | Strong |
| COBOL analysis | COBOL-specific agents and chunking adapters preserve the existing behavior. | Strong, but conversion-oriented |
| Language-neutral orchestration | `ReverseEngineeringProcess` uses neutral discovery, analysis, business-extraction, dependency, and formatting contracts. | Strong |
| Dependency graph | `DependencyMap`, C# and COBOL dependency analyzers, Mermaid output, optional Neo4j graph. | Strong |
| LLM business documentation | COBOL and optional C# extractors produce human-readable documentation grounded by source and C# facts. | Partial |
| Persistence | SQLite run records and dependency/business-documentation storage; optional Neo4j through `HybridMigrationRepository`. | Strong foundation |
| Multi-provider AI | Azure OpenAI, GitHub Copilot, GitHub Copilot SDK, and OpenAI client paths. | Reusable infrastructure |

Primary implementation locations:

- `Processes/ReverseEngineeringProcess.cs`
- `SourceAnalysis/`
- `Persistence/SqliteMigrationRepository.cs`
- `Persistence/HybridMigrationRepository.cs`
- `Persistence/Neo4jMigrationRepository.cs`
- `docs/current-source-analysis-architecture.md`

## Critical gaps

| Discovery Factory capability | Current state | Gap |
|---|---|---|
| Citable findings | `SourceFact` and `SourceDependency` exist, but no `Finding` model, `F-` ID, evidence type, confidence, or stable citation contract. | Critical |
| Functional domains/logical contexts | The current report is source-artifact oriented; no module/context classifier or closure state exists. | Critical |
| Integration inventory | Dependency edges exist, but no dedicated internal/external integration model or inventory. | Critical |
| Risk register | No `Risk` model, `R-` ID, severity, escalation, or finding linkage. | Critical |
| Handoff package | Reports contain technical facts and LLM prose, not a Discovery Factory handoff package. | Critical |
| Governance | No frozen run brief, canonical scope statement, producer audit, acceptance grading, or telemetry guard. | Critical |
| Evidence sources | Source facts are available. Configuration is partial; logs and schema are not collected as first-class evidence types. | High |
| Confidence model | LLM documentation has no human-validation record or Confirmed/Plausible/Unconfirmed status. | Critical |
| On-demand analysis policy | No policy gates deeper analysis on a recorded finding, risk, or gap. | High |
| Artifact-role classifier | Generated and test C# sources are excluded from LLM documentation, but framework composition and infrastructure can still be documented as business behavior. | High |

## Architectural mismatches

### Conversion framing

This repository is named and structured as a COBOL-to-Java/C# migration tool.
Discovery Factory explicitly excludes conversion and future-state design. The
source-analysis engine can be reused, but conversion agents must be isolated
from discovery contracts, data models, reports, and command surfaces.

### Evidence versus prose

Current `BusinessLogic` output is LLM-generated prose organized as purpose,
use cases, features, and rules. Discovery Factory needs evidence-backed
current-state statements. A source citation and confidence label must be
attached before a statement can enter a findings register. LLM prose must not
be treated as confirmed evidence.

### Reporting granularity

The current C# business report is per source artifact. Discovery Factory needs
an application landscape and functional-domain/module view. A deterministic
artifact-role classifier and logical-context discovery stage must sit between
technical analysis and LLM documentation.

### Persistence vocabulary

`IMigrationRepository` still exposes COBOL-specific members. The newer
source-analysis contracts are neutral, but persistence must become neutral too
before it can be a Discovery Factory boundary.

## Storage recommendation

Discovery Factory currently keeps run evidence in Markdown packages. A database
is still appropriate for a production implementation if generated Markdown
remains an export of the authoritative records.

Use a relational database as the system of record, starting with SQLite for
local runs and PostgreSQL for shared or production usage.

| Relational record | Why it belongs in SQLite/PostgreSQL |
|---|---|
| Run brief, scope, lifecycle, and operator approvals | Transactional run metadata and auditability. |
| Source artifacts and deterministic facts | Stable identifiers, filters, citations, and provenance. |
| Findings and evidence | `F-` IDs, confidence, evidence type, source location, human validation, and full-text search. |
| Risks and finding links | Structured status, severity, escalation, and foreign-key integrity. |
| Domains, module membership, integrations, and closure state | Queryable assessment state. |
| LLM documentation and telemetry | Auditable run output and cost/performance records. |

Neo4j should remain optional and derived from relational records:

| Graph use | Why Neo4j is useful |
|---|---|
| Artifact and project dependencies | Multi-hop impact analysis, cycles, and centrality. |
| Module/context boundaries | Cross-context dependencies and shared components. |
| Integration topology | Paths from endpoints through services, data stores, and external systems. |
| Semantic flows | Event-command-data relationships when deeper analysis is authorized. |

Findings, risks, scope, approvals, and evidence metadata should remain
relationally authoritative. Neo4j can link a finding to affected artifacts, but
must not be the sole store for its identity or governance state.

## PostgreSQL readiness

The current persistence layer embeds SQLite DDL and SQL in
`SqliteMigrationRepository`. Moving to PostgreSQL requires more than a
connection-string change:

1. Extract schema changes into versioned migrations.
2. Replace SQLite-specific identity syntax and upsert/query behavior.
3. Map JSON payloads to `JSONB` where server-side querying is valuable.
4. Introduce a neutral repository and database-provider boundary.
5. Treat Neo4j updates as an outbox/derived projection so a graph failure does
   not invalidate a relational discovery run.

The current `HybridMigrationRepository` already supports the desired topology:
relational persistence remains available when Neo4j is unavailable.

## Recommended direction before requirements planning

Use the current code as the **analysis and graph foundation**, not as the
Discovery Factory product model. The requirements phase should decide:

1. The canonical evidence/finding/risk schema and confidence lifecycle.
2. How deterministic logical contexts are discovered and reviewed.
3. The authoritative storage model and Markdown export contract.
4. Which on-demand capabilities are enabled, and what finding/risk gate
   authorizes each one.
5. Whether the conversion subsystem remains in this repository as an isolated
   optional consumer, or moves to a separate product boundary.

Until those decisions are made, avoid treating current per-file business prose
as an accepted Discovery Factory finding or using it to infer a modernization
roadmap.
