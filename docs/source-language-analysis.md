**Last updated**: 2026-08-29

# Source Language Analysis

`ReverseEngineeringProcess` uses language-neutral source contracts: `SourceFile`,
`SourceAnalysis`, `ISourceDiscovery`, `ISourceAnalyzer`, and
`IDependencyAnalyzer`. The COBOL implementation adapts the existing agents and
models. C# uses `MSBuildWorkspace` and Roslyn semantic models before any
business-logic agent can run.

Run deterministic C# analysis without configuring an AI provider:

```bash
dotnet run -- reverse-engineer --source /path/to/solution --source-language CSharp --output output
```

To generate business documentation after Roslyn has collected deterministic
facts, configure an AI provider and add `--extract-business-logic`:

```bash
dotnet run -- reverse-engineer --source /path/to/solution --source-language CSharp --extract-business-logic --output output
```

The business-logic agent receives the source alongside its deterministic facts
and relationships. It must not use an LLM to establish technical facts; the
LLM's output is limited to grounded, human-readable documentation.

The C# analyzer records solutions, projects, project references, package
references, namespaces, types, inheritance, interfaces, methods, constructors,
fields, properties, invocations, controller endpoints, DI registrations, EF
Core contexts/entity sets/database calls, and statically visible event
producers/consumers. Relationships are written through the existing
`DependencyMap` and persisted to SQLite or Neo4j. Neo4j uses the shared
`SourceArtifact` label and retains the `CobolFile` label for existing COBOL
queries.

## Static-analysis limits

Roslyn only reports facts available in the loaded solution and compilation. It
cannot resolve reflection, dynamic dispatch, runtime configuration, convention-
based DI registration, generated source excluded from the workspace, or
database and messaging destinations assembled at runtime. Endpoint routes are
reported from directly declared HTTP attributes; route composition, middleware,
and runtime endpoint mapping need additional analysis.
