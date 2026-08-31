using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using CobolToQuarkusMigration.Discovery;
using CobolToQuarkusMigration.Discovery.Export;
using CobolToQuarkusMigration.Discovery.Graph;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
using CobolToQuarkusMigration.Discovery.Roles;
using Microsoft.Extensions.Logging;

namespace CobolToQuarkusMigration.Cli;

/// <summary>
/// CLI surface for the Discovery Factory pilot slice: declare a run, seed a deterministic
/// demo candidate finding from the local COBOL sample, and export a reviewed finding.
/// </summary>
public static class DiscoveryCommand
{
    public const string DefaultDatabasePath = "Data/discovery.db";

    public static Command Build(ILoggerFactory loggerFactory)
    {
        var root = new Command("discovery", "Discovery Factory pilot slice: runs, review lifecycle, and exports.");

        root.AddCommand(BuildStartRunCommand(loggerFactory));
        root.AddCommand(BuildSeedDemoCommand(loggerFactory));
        root.AddCommand(BuildClassifyRolesCommand(loggerFactory));
        root.AddCommand(BuildGraphCommand(loggerFactory));
        root.AddCommand(BuildSeedContextsCommand(loggerFactory));
        root.AddCommand(BuildClassifyIntegrationsCommand(loggerFactory));
        root.AddCommand(BuildExportCommand(loggerFactory));
        root.AddCommand(BuildExportIntegrationsCommand(loggerFactory));
        root.AddCommand(BuildRiskRegisterCommand(loggerFactory));
        root.AddCommand(BuildExportHandoffCommand(loggerFactory));

        return root;
    }

    private static IDiscoveryRepository CreateRepository(ILoggerFactory loggerFactory, string databasePath)
        => new SqliteDiscoveryRepository(databasePath, loggerFactory.CreateLogger<SqliteDiscoveryRepository>());

    private static Command BuildStartRunCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("start-run", "Declare a new versioned Discovery Factory run.");

        var subjectOption = new Option<string>("--subject", "Subject under assessment.") { IsRequired = true };
        cmd.AddOption(subjectOption);

        var sourceLocatorOption = new Option<string>("--source-locator", "Immutable source locator (repo URL or path).") { IsRequired = true };
        cmd.AddOption(sourceLocatorOption);

        var sourceRevisionOption = new Option<string>("--source-revision", "Immutable source revision (commit SHA or content hash).") { IsRequired = true };
        cmd.AddOption(sourceRevisionOption);

        var inclusionsOption = new Option<string>("--inclusions", () => "", "Comma-separated declared inclusions.");
        cmd.AddOption(inclusionsOption);

        var exclusionsOption = new Option<string>("--exclusions", () => "", "Comma-separated declared exclusions.");
        cmd.AddOption(exclusionsOption);

        var evidenceBoundaryOption = new Option<string>("--evidence-boundary", () => "Static source evidence only; no runtime logs.", "Declared evidence boundary.");
        cmd.AddOption(evidenceBoundaryOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string subject, string sourceLocator, string sourceRevision, string inclusions, string exclusions, string evidenceBoundary, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            var service = new DiscoveryService(repository, loggerFactory.CreateLogger<DiscoveryService>());
            var run = await service.StartRunAsync(
                subject,
                sourceLocator,
                sourceRevision,
                Split(inclusions),
                Split(exclusions),
                evidenceBoundary);

            Console.Out.WriteLine($"Started run {run.RunId} for subject '{run.Subject}' at revision {run.SourceRevision}.");
        }, subjectOption, sourceLocatorOption, sourceRevisionOption, inclusionsOption, exclusionsOption, evidenceBoundaryOption, databaseOption);

        return cmd;
    }

    private static Command BuildSeedDemoCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("seed-demo",
            "Deterministically inventory the local COBOL sample under --source-dir, appending one artifact, " +
            "evidence record, and candidate finding to a run for pilot review.");

        var runIdOption = new Option<string?>("--run-id", "Existing run to append to. If omitted, a new run is declared automatically.");
        cmd.AddOption(runIdOption);

        var sourceDirOption = new Option<string>("--source-dir", () => "source", "Directory containing the sample COBOL program(s) to inventory.");
        cmd.AddOption(sourceDirOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string? runId, string sourceDir, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            var service = new DiscoveryService(repository, loggerFactory.CreateLogger<DiscoveryService>());

            DiscoveryRun run;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                await repository.InitializeAsync();
                run = await repository.GetRunAsync(runId)
                    ?? throw new InvalidOperationException($"Run not found: {runId}");
            }
            else
            {
                run = await service.StartRunAsync(
                    subject: "PlanBord Forecast Management (local sample substitute)",
                    sourceLocator: Path.GetFullPath(sourceDir),
                    sourceRevision: "local-working-tree",
                    inclusions: new[] { sourceDir },
                    exclusions: Array.Empty<string>(),
                    evidenceBoundary: "Static COBOL source only; no configuration, schema, or runtime logs; PlanBord repository unavailable, local sample substituted for the pilot slice.");
            }

            if (!Directory.Exists(sourceDir))
            {
                Console.Error.WriteLine($"Source directory not found: {sourceDir}");
                Environment.ExitCode = 2;
                return;
            }

            var cobolFile = Directory.EnumerateFiles(sourceDir, "*.cbl", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (cobolFile is null)
            {
                Console.Error.WriteLine($"No .cbl files found in {sourceDir}");
                Environment.ExitCode = 2;
                return;
            }

            var content = await File.ReadAllTextAsync(cobolFile);
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), cobolFile);

            var artifact = await service.AppendArtifactAsync(run.RunId, relativePath, "COBOL", contentHash, content.Length);

            // Deterministic, mechanical fact: count PROCEDURE DIVISION paragraph headers (lines
            // ending in '.' at column 8 that are not COBOL reserved section/division headers).
            var lines = content.Split('\n');
            var procedureDivisionIndex = Array.FindIndex(lines, l => l.Contains("PROCEDURE DIVISION", StringComparison.OrdinalIgnoreCase));
            var paragraphCount = 0;
            if (procedureDivisionIndex >= 0)
            {
                for (var i = procedureDivisionIndex + 1; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (line.Length <= 7 || !char.IsLetter(line[7]))
                    {
                        continue;
                    }

                    var label = line[7..].TrimEnd();
                    // A paragraph header is a bare "NAME." with no further statement content and
                    // no embedded whitespace in the name itself.
                    if (label.EndsWith('.') && !label[..^1].Contains(' ', StringComparison.Ordinal))
                    {
                        paragraphCount++;
                    }
                }
            }

            var evidence = await service.AppendEvidenceAsync(
                run.RunId,
                EvidenceType.SourceCode,
                $"{relativePath}",
                artifact.ArtifactId,
                rawExcerpt: $"PROCEDURE DIVISION found at line {procedureDivisionIndex + 1}; {paragraphCount} paragraph header(s) detected.");

            var (finding, revision) = await service.AppendCandidateFindingAsync(
                run.RunId,
                statement: $"Program '{Path.GetFileNameWithoutExtension(cobolFile)}' declares a PROCEDURE DIVISION with {paragraphCount} paragraph(s), deterministically counted from source.",
                evidenceIds: new[] { evidence.EvidenceId },
                confidence: 0.95,
                producerVersion: "DiscoverySeedDemo/1.0");

            Console.Out.WriteLine($"Run: {run.RunId}");
            Console.Out.WriteLine($"Artifact: {artifact.ArtifactId} ({artifact.Path})");
            Console.Out.WriteLine($"Evidence: {evidence.EvidenceId} (redacted={evidence.WasRedacted})");
            Console.Out.WriteLine($"Candidate finding: {finding.FindingId} / revision {revision.FindingRevisionId} — {revision.Statement}");
        }, runIdOption, sourceDirOption, databaseOption);

        return cmd;
    }

    private static Command BuildClassifyRolesCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("classify-roles",
            "Deterministically classify C# source under --source-dir into evidence-backed artifact roles " +
            "(business, composition/DI, middleware, framework adapter, persistence, integration adapter, " +
            "shared, generated, test, build tooling, unknown), appending role assignments and evidence to a run.");

        var runIdOption = new Option<string?>("--run-id", "Existing run to append to. If omitted, a new run is declared automatically.");
        cmd.AddOption(runIdOption);

        var sourceDirOption = new Option<string>("--source-dir", "Directory containing the C# project source to classify (e.g. a PlanBoard project directory). Scanned recursively; bin/obj are always excluded.") { IsRequired = true };
        cmd.AddOption(sourceDirOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string? runId, string sourceDir, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            var service = new DiscoveryService(repository, loggerFactory.CreateLogger<DiscoveryService>());

            if (!Directory.Exists(sourceDir))
            {
                Console.Error.WriteLine($"Source directory not found: {sourceDir}");
                Environment.ExitCode = 2;
                return;
            }

            DiscoveryRun run;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                await repository.InitializeAsync();
                run = await repository.GetRunAsync(runId)
                    ?? throw new InvalidOperationException($"Run not found: {runId}");
            }
            else
            {
                run = await service.StartRunAsync(
                    subject: "PlanBoard artifact role classification (issue #8)",
                    sourceLocator: Path.GetFullPath(sourceDir),
                    sourceRevision: "local-working-tree",
                    inclusions: new[] { sourceDir },
                    exclusions: new[] { "bin/", "obj/" },
                    evidenceBoundary: "Static C# source only (Roslyn syntax-tree parsing); no configuration, schema, or runtime logs.");
            }

            var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                .Where(f => !ArtifactRoleClassifier.IsExcludedBuildOutputPath(Path.GetRelativePath(sourceDir, f)))
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0)
            {
                Console.Error.WriteLine($"No classifiable files found under {sourceDir}");
                Environment.ExitCode = 2;
                return;
            }

            var artifacts = new List<(SourceArtifact Artifact, string SourceText)>();
            foreach (var file in files)
            {
                var content = await File.ReadAllTextAsync(file);
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
                var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
                var artifact = await service.AppendArtifactAsync(run.RunId, relativePath, "CSharp", contentHash, content.Length);
                artifacts.Add((artifact, content));
            }

            var assignments = await service.ClassifyArtifactRolesAsync(run.RunId, artifacts, producerVersion: "ArtifactRoleClassifier/1.0");

            Console.Out.WriteLine($"Run: {run.RunId}");
            Console.Out.WriteLine($"Classified {artifacts.Count} artifact(s) into {assignments.Count} role assignment(s).");
            foreach (var group in assignments.GroupBy(a => string.Join("+", a.Roles)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.Out.WriteLine($"  {group.Key}: {group.Count()}{(group.Any(a => a.RequiresReview) ? " (some require review)" : string.Empty)}");
            }
        }, runIdOption, sourceDirOption, databaseOption);

        return cmd;
    }

    private static Command BuildGraphCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("build-graph",
            "Deterministically build a typed dependency graph (projects, namespaces, types, routes, " +
            "DbContext/entity mappings, DI registrations, cross-type references) from C# source under " +
            "--source-dir, appending graph nodes/edges and evidence to a run (design doc §5.1, issue #3).");

        var runIdOption = new Option<string?>("--run-id", "Existing run to append to. If omitted, a new run is declared automatically.");
        cmd.AddOption(runIdOption);

        var sourceDirOption = new Option<string>("--source-dir", "Directory containing the C# solution/project source to graph (e.g. a PlanBoard checkout). Scanned recursively; bin/obj are always excluded.") { IsRequired = true };
        cmd.AddOption(sourceDirOption);

        var focusNamespacePrefixOption = new Option<string?>("--focus-namespace-prefix",
            "Optional namespace prefix (e.g. \"Planbordv2.Api.Forecast\") to additionally report a focused " +
            "node/edge count for, without limiting what is persisted. Supports a detailed pilot focus area " +
            "(e.g. Forecast Management) on top of the whole-solution pass.");
        cmd.AddOption(focusNamespacePrefixOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string? runId, string sourceDir, string? focusNamespacePrefix, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            var service = new DiscoveryService(repository, loggerFactory.CreateLogger<DiscoveryService>());

            if (!Directory.Exists(sourceDir))
            {
                Console.Error.WriteLine($"Source directory not found: {sourceDir}");
                Environment.ExitCode = 2;
                return;
            }

            DiscoveryRun run;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                await repository.InitializeAsync();
                run = await repository.GetRunAsync(runId)
                    ?? throw new InvalidOperationException($"Run not found: {runId}");
            }
            else
            {
                run = await service.StartRunAsync(
                    subject: "PlanBoard dependency graph (issue #3)",
                    sourceLocator: Path.GetFullPath(sourceDir),
                    sourceRevision: "local-working-tree",
                    inclusions: new[] { sourceDir },
                    exclusions: new[] { "bin/", "obj/" },
                    evidenceBoundary: "Static C# source only (Roslyn syntax-tree parsing, no semantic binding); no configuration, schema, or runtime logs.");
            }

            var csprojFiles = Directory.EnumerateFiles(sourceDir, "*.csproj", SearchOption.AllDirectories)
                .Where(f => !ArtifactRoleClassifier.IsExcludedBuildOutputPath(Path.GetRelativePath(sourceDir, f)))
                .ToList();

            var csFiles = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !ArtifactRoleClassifier.IsExcludedBuildOutputPath(Path.GetRelativePath(sourceDir, f)))
                .ToList();

            if (csprojFiles.Count == 0 && csFiles.Count == 0)
            {
                Console.Error.WriteLine($"No .csproj or .cs files found under {sourceDir}");
                Environment.ExitCode = 2;
                return;
            }

            async Task<ClassifierSourceFile> ToClassifierFileAsync(string path)
            {
                var content = await File.ReadAllTextAsync(path);
                var relativePath = Path.GetRelativePath(sourceDir, path);
                return new ClassifierSourceFile(relativePath, content);
            }

            var csprojClassifierFiles = new List<ClassifierSourceFile>();
            foreach (var f in csprojFiles) csprojClassifierFiles.Add(await ToClassifierFileAsync(f));

            var csClassifierFiles = new List<ClassifierSourceFile>();
            foreach (var f in csFiles) csClassifierFiles.Add(await ToClassifierFileAsync(f));

            var artifactIdByRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (relativePath, content) in csprojClassifierFiles.Select(f => (f.Path, f.Text))
                         .Concat(csClassifierFiles.Select(f => (f.Path, f.Text))))
            {
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
                var artifact = await service.AppendArtifactAsync(run.RunId, relativePath, "CSharp", contentHash, content.Length);
                artifactIdByRelativePath[relativePath] = artifact.ArtifactId;
            }

            var projectGraph = DependencyGraphBuilder.BuildProjectGraph(csprojClassifierFiles);
            var sourceGraph = DependencyGraphBuilder.BuildSourceGraph(csClassifierFiles);

            var allNodeCandidates = projectGraph.Nodes.Concat(sourceGraph.Nodes)
                .Select(n => (Candidate: n, ArtifactId: n.ArtifactPath is not null && artifactIdByRelativePath.TryGetValue(n.ArtifactPath, out var id) ? id : (string?)null))
                .ToList();
            var allEdgeCandidates = projectGraph.Edges.Concat(sourceGraph.Edges).ToList();

            var (nodes, edges) = await service.AppendDependencyGraphAsync(
                run.RunId, allNodeCandidates, allEdgeCandidates, producerVersion: "DependencyGraphBuilder/1.0");

            Console.Out.WriteLine($"Run: {run.RunId}");
            Console.Out.WriteLine($"Graph nodes appended: {nodes.Count} (of {allNodeCandidates.Count} candidate(s) seen).");
            foreach (var group in nodes.GroupBy(n => n.Kind).OrderBy(g => g.Key))
            {
                Console.Out.WriteLine($"  {group.Key}: {group.Count()}");
            }
            Console.Out.WriteLine($"Graph edges appended: {edges.Count} (of {allEdgeCandidates.Count} candidate(s) seen).");
            foreach (var group in edges.GroupBy(e => e.Kind).OrderBy(g => g.Key))
            {
                Console.Out.WriteLine($"  {group.Key}: {group.Count()}");
            }

            if (!string.IsNullOrWhiteSpace(focusNamespacePrefix))
            {
                var focusNodes = nodes.Where(n => n.SymbolLocator.StartsWith(focusNamespacePrefix, StringComparison.Ordinal)).ToList();
                Console.Out.WriteLine($"Focus '{focusNamespacePrefix}': {focusNodes.Count} node(s).");
            }
        }, runIdOption, sourceDirOption, focusNamespacePrefixOption, databaseOption);

        return cmd;
    }

    private static Command BuildSeedContextsCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("seed-contexts",
            "Deterministically seed candidate logical contexts / module boundaries from a run's dependency " +
            "graph and role assignments (design doc §5.1, issue #3). Candidates remain Plausible/Unconfirmed " +
            "until a human reviewer confirms them; this command never publishes a context claim.");

        var runIdOption = new Option<string>("--run-id", "Run whose dependency graph and role assignments to seed contexts from.") { IsRequired = true };
        cmd.AddOption(runIdOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string runId, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            await repository.InitializeAsync();
            var run = await repository.GetRunAsync(runId)
                ?? throw new InvalidOperationException($"Run not found: {runId}");
            var service = new DiscoveryService(repository, loggerFactory.CreateLogger<DiscoveryService>());

            var candidates = await service.SeedContextCandidatesAsync(run.RunId, producerVersion: "ContextSeeder/1.0");

            Console.Out.WriteLine($"Run: {run.RunId}");
            Console.Out.WriteLine($"Seeded {candidates.Count} candidate context(s).");
            foreach (var candidate in candidates.OrderByDescending(c => c.Confidence))
            {
                Console.Out.WriteLine($"  [{candidate.Status}] {candidate.Kind} '{candidate.Name}' " +
                    $"(confidence={candidate.Confidence:F2}, rule={candidate.SeedingRule}, id={candidate.ContextCandidateId})");
            }

            var dependencyEdges = await service.GetContextDependencyEdgesAsync(run.RunId);
            if (dependencyEdges.Count > 0)
            {
                var byId = candidates.ToDictionary(c => c.ContextCandidateId, c => c.Name);
                Console.Out.WriteLine($"Cross-context dependencies: {dependencyEdges.Count}");
                foreach (var edge in dependencyEdges)
                {
                    var from = byId.GetValueOrDefault(edge.FromContextCandidateId, edge.FromContextCandidateId);
                    var to = byId.GetValueOrDefault(edge.ToContextCandidateId, edge.ToContextCandidateId);
                    Console.Out.WriteLine($"  {from} -> {to} (confidence={edge.Confidence:F2})");
                }
            }
        }, runIdOption, databaseOption);

        return cmd;
    }

    private static Command BuildClassifyIntegrationsCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("classify-integrations",
            "Deterministically inventory statically observable integrations (HTTP/API, messaging, files, " +
            "databases/shared stores, identity/authorization, notifications, service discovery/platform " +
            "config, observability, scheduled/background processes) under --source-dir, classified as " +
            "runtime application, platform/identity, observability, or delivery (design doc §7, issue #6). " +
            "Best-effort links each integration to a Phase 5 candidate context when the source is already " +
            "graphed and context-seeded on the same run.");

        var runIdOption = new Option<string?>("--run-id", "Existing run to append to. If omitted, a new run is declared automatically.");
        cmd.AddOption(runIdOption);

        var sourceDirOption = new Option<string>("--source-dir", "Directory containing the C# project source to inventory (e.g. a PlanBoard project directory). Scanned recursively; bin/obj are always excluded.") { IsRequired = true };
        cmd.AddOption(sourceDirOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string? runId, string sourceDir, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            var service = new DiscoveryService(repository, loggerFactory.CreateLogger<DiscoveryService>());

            if (!Directory.Exists(sourceDir))
            {
                Console.Error.WriteLine($"Source directory not found: {sourceDir}");
                Environment.ExitCode = 2;
                return;
            }

            DiscoveryRun run;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                await repository.InitializeAsync();
                run = await repository.GetRunAsync(runId)
                    ?? throw new InvalidOperationException($"Run not found: {runId}");
            }
            else
            {
                run = await service.StartRunAsync(
                    subject: "PlanBoard integration inventory (issue #6)",
                    sourceLocator: Path.GetFullPath(sourceDir),
                    sourceRevision: "local-working-tree",
                    inclusions: new[] { sourceDir },
                    exclusions: new[] { "bin/", "obj/" },
                    evidenceBoundary: "Static C# source and configuration-key names only (Roslyn syntax-tree parsing); no resolved configuration values, secrets, or runtime logs.");
            }

            var csFiles = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !ArtifactRoleClassifier.IsExcludedBuildOutputPath(Path.GetRelativePath(sourceDir, f)))
                .ToList();
            var jsonFiles = Directory.EnumerateFiles(sourceDir, "appsettings*.json", SearchOption.AllDirectories)
                .Where(f => !ArtifactRoleClassifier.IsExcludedBuildOutputPath(Path.GetRelativePath(sourceDir, f)))
                .ToList();

            // Reuse artifacts already appended by an earlier classify-roles/build-graph pass on the same
            // run (keyed by path+content hash) so that owning-context linking via graph nodes can resolve.
            // Only append a fresh artifact record when this run has not already seen this exact content.
            var existingArtifacts = (await repository.GetArtifactsAsync(run.RunId))
                .GroupBy(a => (a.Path, a.ContentHash))
                .ToDictionary(g => g.Key, g => g.First());

            if (csFiles.Count == 0 && jsonFiles.Count == 0)
            {
                Console.Error.WriteLine($"No .cs or appsettings*.json files found under {sourceDir}");
                Environment.ExitCode = 2;
                return;
            }

            var candidates = new List<(CobolToQuarkusMigration.Discovery.Integrations.IntegrationCandidate Candidate, string? ArtifactId)>();

            foreach (var file in csFiles)
            {
                var content = await File.ReadAllTextAsync(file);
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
                if (!existingArtifacts.TryGetValue((relativePath, contentHash), out var artifact))
                {
                    artifact = await service.AppendArtifactAsync(run.RunId, relativePath, "CSharp", contentHash, content.Length);
                }

                var classifierFile = new ClassifierSourceFile(relativePath, content);
                foreach (var candidate in CobolToQuarkusMigration.Discovery.Integrations.IntegrationClassifier.ClassifyProject(new[] { classifierFile }))
                {
                    candidates.Add((candidate, artifact.ArtifactId));
                }
            }

            foreach (var file in jsonFiles)
            {
                var content = await File.ReadAllTextAsync(file);
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
                if (!existingArtifacts.TryGetValue((relativePath, contentHash), out var artifact))
                {
                    artifact = await service.AppendArtifactAsync(run.RunId, relativePath, "Config", contentHash, content.Length);
                }

                var classifierFile = new ClassifierSourceFile(relativePath, content);
                foreach (var candidate in CobolToQuarkusMigration.Discovery.Integrations.IntegrationClassifier.ClassifyConfigurationFile(classifierFile))
                {
                    candidates.Add((candidate, artifact.ArtifactId));
                }
            }

            var integrations = await service.AppendIntegrationsAsync(run.RunId, candidates, producerVersion: "IntegrationClassifier/1.0");

            Console.Out.WriteLine($"Run: {run.RunId}");
            Console.Out.WriteLine($"Classified {candidates.Count} integration candidate(s) into {integrations.Count} integration record(s).");
            Console.Out.WriteLine("By classification:");
            foreach (var group in integrations.GroupBy(i => i.Classification).OrderBy(g => g.Key))
            {
                Console.Out.WriteLine($"  {group.Key}: {group.Count()}");
            }
            Console.Out.WriteLine("By category:");
            foreach (var group in integrations.GroupBy(i => i.Category).OrderBy(g => g.Key))
            {
                Console.Out.WriteLine($"  {group.Key}: {group.Count()}");
            }
            var requiresReviewCount = integrations.Count(i => i.RequiresReview);
            var linkedCount = integrations.Count(i => i.OwningContextCandidateId is not null);
            Console.Out.WriteLine($"Requires review: {requiresReviewCount}; linked to a candidate context: {linkedCount}.");
        }, runIdOption, sourceDirOption, databaseOption);

        return cmd;
    }

    private static Command BuildExportCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("export", "Export a reviewed finding revision to versioned Markdown and JSON.");

        var findingRevisionOption = new Option<string>("--finding-revision", "Finding revision ID to export, e.g. F-XXXXXXXXX-R1.") { IsRequired = true };
        cmd.AddOption(findingRevisionOption);

        var outputDirOption = new Option<string>("--output-dir", () => "output/discovery", "Directory to write the Markdown/JSON export pair to.");
        cmd.AddOption(outputDirOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string findingRevisionId, string outputDir, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            await repository.InitializeAsync();
            var exporter = new DiscoveryExporter(repository);

            try
            {
                var (markdownPath, jsonPath) = await exporter.ExportFindingAsync(findingRevisionId, outputDir);
                Console.Out.WriteLine($"Exported {findingRevisionId} to:");
                Console.Out.WriteLine($"  {markdownPath}");
                Console.Out.WriteLine($"  {jsonPath}");
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 2;
            }
        }, findingRevisionOption, outputDirOption, databaseOption);

        return cmd;
    }

    private static Command BuildExportIntegrationsCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("export-integrations",
            "Export a run's full integration inventory to a single JSON file, grouped by " +
            "classification (RuntimeApplication/PlatformIdentity/Observability/Delivery) so " +
            "consumers can distinguish business behavior from platform, observability, and " +
            "delivery concerns without re-deriving it (design doc §7, issue #6).");

        var runIdOption = new Option<string>("--run-id", "Run whose integration inventory to export.") { IsRequired = true };
        cmd.AddOption(runIdOption);

        var outputDirOption = new Option<string>("--output-dir", () => "output/discovery", "Directory to write the integration inventory JSON to.");
        cmd.AddOption(outputDirOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string runId, string outputDir, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            await repository.InitializeAsync();
            var exporter = new DiscoveryExporter(repository);

            try
            {
                var jsonPath = await exporter.ExportIntegrationInventoryAsync(runId, outputDir);
                Console.Out.WriteLine($"Exported integration inventory for {runId} to:");
                Console.Out.WriteLine($"  {jsonPath}");
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 2;
            }
        }, runIdOption, outputDirOption, databaseOption);

        return cmd;
    }

    private static string[] Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Command BuildRiskRegisterCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("build-risk-register",
            "Deterministically derive the risk register (design doc §6 section 9, issue #5) from " +
            "already-persisted context candidates, integrations, role assignments, and documented " +
            "analyzer limitations for a run. Idempotent: re-running skips entries already recorded " +
            "for the same (derivation rule, source record) pair.");

        var runIdOption = new Option<string>("--run-id", "Run to derive the risk register for.") { IsRequired = true };
        cmd.AddOption(runIdOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string runId, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            await repository.InitializeAsync();
            var service = new DiscoveryService(repository, loggerFactory.CreateLogger<DiscoveryService>());

            var appended = await service.BuildRiskRegisterAsync(runId, producerVersion: "RiskRegisterBuilder/1.0");
            Console.Out.WriteLine($"Appended {appended.Count} new risk register entries for {runId}.");
            if (appended.Count > 0)
            {
                foreach (var group in appended.GroupBy(r => r.Severity).OrderByDescending(g => g.Key))
                {
                    Console.Out.WriteLine($"  {group.Key}: {group.Count()}");
                }
            }
        }, runIdOption, databaseOption);

        return cmd;
    }

    private static Command BuildExportHandoffCommand(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("export-handoff",
            "Export the full 10-section Specification Factory handoff (design doc §6, issue #5) " +
            "for a run to versioned Markdown and JSON. Run 'build-risk-register' first so section 9 " +
            "reflects the current risk register.");

        var runIdOption = new Option<string>("--run-id", "Run to export the handoff for.") { IsRequired = true };
        cmd.AddOption(runIdOption);

        var outputDirOption = new Option<string>("--output-dir", () => "output/discovery", "Directory to write the handoff Markdown/JSON pair to.");
        cmd.AddOption(outputDirOption);

        var databaseOption = new Option<string>("--database", () => DefaultDatabasePath, "Path to the Discovery Factory SQLite database.");
        cmd.AddOption(databaseOption);

        cmd.SetHandler(async (string runId, string outputDir, string database) =>
        {
            var repository = CreateRepository(loggerFactory, database);
            await repository.InitializeAsync();
            var exporter = new DiscoveryExporter(repository);

            try
            {
                var (markdownPath, jsonPath) = await exporter.ExportHandoffAsync(runId, outputDir);
                Console.Out.WriteLine($"Exported Specification Factory handoff for {runId} to:");
                Console.Out.WriteLine($"  {markdownPath}");
                Console.Out.WriteLine($"  {jsonPath}");
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 2;
            }
        }, runIdOption, outputDirOption, databaseOption);

        return cmd;
    }
}
