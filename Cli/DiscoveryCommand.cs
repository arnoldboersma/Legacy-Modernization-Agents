using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using CobolToQuarkusMigration.Discovery;
using CobolToQuarkusMigration.Discovery.Export;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
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
        root.AddCommand(BuildExportCommand(loggerFactory));

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

    private static string[] Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
