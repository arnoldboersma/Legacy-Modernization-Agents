using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CobolToQuarkusMigration.Discovery.Models;

namespace CobolToQuarkusMigration.Discovery.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IDiscoveryRepository"/>. Uses a dedicated database
/// file (separate from the legacy migration database) with versioned migrations embedded from
/// Discovery/Persistence/Migrations. Governed record tables are append-only: this class never
/// issues UPDATE or DELETE against runs, artifacts, evidence, findings, finding revisions,
/// provenance, LLM assessments, or review decisions.
/// </summary>
public sealed class SqliteDiscoveryRepository : IDiscoveryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteDiscoveryRepository> _logger;

    public SqliteDiscoveryRepository(string databasePath, ILogger<SqliteDiscoveryRepository> logger)
    {
        _databasePath = databasePath;
        _connectionString = $"Data Source={_databasePath};Cache=Shared";
        _logger = logger;
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var createMigrationsTable = connection.CreateCommand())
        {
            createMigrationsTable.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);";
            await createMigrationsTable.ExecuteNonQueryAsync(cancellationToken);
        }

        var applied = new HashSet<int>();
        await using (var readApplied = connection.CreateCommand())
        {
            readApplied.CommandText = "SELECT version FROM schema_migrations;";
            await using var reader = await readApplied.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        foreach (var (version, name, sql) in LoadEmbeddedMigrations())
        {
            if (applied.Contains(version))
            {
                continue;
            }

            _logger.LogInformation("[Discovery] applying migration {Version}: {Name}", version, name);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var migrationCommand = connection.CreateCommand())
            {
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = sql;
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var recordVersion = connection.CreateCommand())
            {
                recordVersion.Transaction = transaction;
                recordVersion.CommandText = "INSERT INTO schema_migrations (version, applied_at) VALUES ($version, $appliedAt);";
                recordVersion.Parameters.AddWithValue("$version", version);
                recordVersion.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                await recordVersion.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static IEnumerable<(int Version, string Name, string Sql)> LoadEmbeddedMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("Discovery.Persistence.Migrations", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        const string marker = "Migrations.";
        foreach (var resourceName in resourceNames)
        {
            // Resource names look like ...Migrations.0001_initial.sql
            var markerIndex = resourceName.LastIndexOf(marker, StringComparison.Ordinal);
            var fileName = markerIndex >= 0 ? resourceName[(markerIndex + marker.Length)..] : resourceName;
            var versionToken = fileName.Split('_', 2)[0];
            if (!int.TryParse(versionToken, out var version))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration resource not found: {resourceName}");
            using var streamReader = new StreamReader(stream);
            yield return (version, fileName, streamReader.ReadToEnd());
        }
    }

    public async Task AppendRunAsync(DiscoveryRun run, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO discovery_runs
    (run_id, subject, source_locator, source_revision, inclusions_json, exclusions_json,
     evidence_boundary, intent, status, supersedes_run_id, created_at_utc)
VALUES
    ($runId, $subject, $sourceLocator, $sourceRevision, $inclusionsJson, $exclusionsJson,
     $evidenceBoundary, $intent, $status, $supersedesRunId, $createdAtUtc);";
        command.Parameters.AddWithValue("$runId", run.RunId);
        command.Parameters.AddWithValue("$subject", run.Subject);
        command.Parameters.AddWithValue("$sourceLocator", run.SourceLocator);
        command.Parameters.AddWithValue("$sourceRevision", run.SourceRevision);
        command.Parameters.AddWithValue("$inclusionsJson", JsonSerializer.Serialize(run.Inclusions, JsonOptions));
        command.Parameters.AddWithValue("$exclusionsJson", JsonSerializer.Serialize(run.Exclusions, JsonOptions));
        command.Parameters.AddWithValue("$evidenceBoundary", run.EvidenceBoundary);
        command.Parameters.AddWithValue("$intent", (object?)run.Intent ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$supersedesRunId", (object?)run.SupersedesRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DiscoveryRun?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id, subject, source_locator, source_revision, inclusions_json, exclusions_json, evidence_boundary, intent, status, supersedes_run_id, created_at_utc FROM discovery_runs WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadRun(reader);
    }

    public async Task<IReadOnlyList<DiscoveryRun>> GetAllRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id, subject, source_locator, source_revision, inclusions_json, exclusions_json, evidence_boundary, intent, status, supersedes_run_id, created_at_utc FROM discovery_runs ORDER BY created_at_utc DESC;";
        var results = new List<DiscoveryRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadRun(reader));
        }
        return results;
    }

    private static DiscoveryRun ReadRun(SqliteDataReader reader) => new()
    {
        RunId = reader.GetString(0),
        Subject = reader.GetString(1),
        SourceLocator = reader.GetString(2),
        SourceRevision = reader.GetString(3),
        Inclusions = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? new(),
        Exclusions = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), JsonOptions) ?? new(),
        EvidenceBoundary = reader.GetString(6),
        Intent = reader.IsDBNull(7) ? null : reader.GetString(7),
        Status = Enum.Parse<DiscoveryRunStatus>(reader.GetString(8)),
        SupersedesRunId = reader.IsDBNull(9) ? null : reader.GetString(9),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(10)),
    };

    public async Task AppendArtifactAsync(SourceArtifact artifact, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO source_artifacts (artifact_id, run_id, path, language, content_hash, size_bytes, created_at_utc)
VALUES ($artifactId, $runId, $path, $language, $contentHash, $sizeBytes, $createdAtUtc);";
        command.Parameters.AddWithValue("$artifactId", artifact.ArtifactId);
        command.Parameters.AddWithValue("$runId", artifact.RunId);
        command.Parameters.AddWithValue("$path", artifact.Path);
        command.Parameters.AddWithValue("$language", artifact.Language);
        command.Parameters.AddWithValue("$contentHash", artifact.ContentHash);
        command.Parameters.AddWithValue("$sizeBytes", (object?)artifact.SizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", artifact.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SourceArtifact>> GetArtifactsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact_id, run_id, path, language, content_hash, size_bytes, created_at_utc FROM source_artifacts WHERE run_id = $runId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<SourceArtifact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SourceArtifact
            {
                ArtifactId = reader.GetString(0),
                RunId = reader.GetString(1),
                Path = reader.GetString(2),
                Language = reader.GetString(3),
                ContentHash = reader.GetString(4),
                SizeBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(6)),
            });
        }
        return results;
    }

    public async Task AppendEvidenceAsync(Evidence evidence, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO evidence (evidence_id, run_id, evidence_type, artifact_id, locator, redacted_excerpt, was_redacted, redaction_summary, created_at_utc)
VALUES ($evidenceId, $runId, $evidenceType, $artifactId, $locator, $redactedExcerpt, $wasRedacted, $redactionSummary, $createdAtUtc);";
        command.Parameters.AddWithValue("$evidenceId", evidence.EvidenceId);
        command.Parameters.AddWithValue("$runId", evidence.RunId);
        command.Parameters.AddWithValue("$evidenceType", evidence.Type.ToString());
        command.Parameters.AddWithValue("$artifactId", (object?)evidence.ArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$locator", evidence.Locator);
        command.Parameters.AddWithValue("$redactedExcerpt", (object?)evidence.RedactedExcerpt ?? DBNull.Value);
        command.Parameters.AddWithValue("$wasRedacted", evidence.WasRedacted ? 1 : 0);
        command.Parameters.AddWithValue("$redactionSummary", (object?)evidence.RedactionSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", evidence.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Evidence>> GetEvidenceAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT evidence_id, run_id, evidence_type, artifact_id, locator, redacted_excerpt, was_redacted, redaction_summary, created_at_utc FROM evidence WHERE run_id = $runId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<Evidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEvidence(reader));
        }
        return results;
    }

    public async Task<IReadOnlyList<Evidence>> GetEvidenceByIdsAsync(IEnumerable<string> evidenceIds, CancellationToken cancellationToken = default)
    {
        var idList = evidenceIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return Array.Empty<Evidence>();
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameterNames = idList.Select((_, i) => $"$id{i}").ToList();
        command.CommandText = $"SELECT evidence_id, run_id, evidence_type, artifact_id, locator, redacted_excerpt, was_redacted, redaction_summary, created_at_utc FROM evidence WHERE evidence_id IN ({string.Join(",", parameterNames)});";
        for (var i = 0; i < idList.Count; i++)
        {
            command.Parameters.AddWithValue(parameterNames[i], idList[i]);
        }
        var results = new List<Evidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEvidence(reader));
        }
        return results;
    }

    private static Evidence ReadEvidence(SqliteDataReader reader) => new()
    {
        EvidenceId = reader.GetString(0),
        RunId = reader.GetString(1),
        Type = Enum.Parse<EvidenceType>(reader.GetString(2)),
        ArtifactId = reader.IsDBNull(3) ? null : reader.GetString(3),
        Locator = reader.GetString(4),
        RedactedExcerpt = reader.IsDBNull(5) ? null : reader.GetString(5),
        WasRedacted = reader.GetInt32(6) != 0,
        RedactionSummary = reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(8)),
    };

    public async Task AppendProvenanceAsync(Provenance provenance, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO provenance (provenance_id, run_id, producer_kind, producer_version, prompt_version, input_record_ids_json, created_at_utc)
VALUES ($provenanceId, $runId, $producerKind, $producerVersion, $promptVersion, $inputRecordIdsJson, $createdAtUtc);";
        command.Parameters.AddWithValue("$provenanceId", provenance.ProvenanceId);
        command.Parameters.AddWithValue("$runId", provenance.RunId);
        command.Parameters.AddWithValue("$producerKind", provenance.ProducerKind);
        command.Parameters.AddWithValue("$producerVersion", provenance.ProducerVersion);
        command.Parameters.AddWithValue("$promptVersion", (object?)provenance.PromptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$inputRecordIdsJson", JsonSerializer.Serialize(provenance.InputRecordIds, JsonOptions));
        command.Parameters.AddWithValue("$createdAtUtc", provenance.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendFindingAsync(Finding finding, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO findings (finding_id, run_id, correlation_key, created_at_utc)
VALUES ($findingId, $runId, $correlationKey, $createdAtUtc);";
        command.Parameters.AddWithValue("$findingId", finding.FindingId);
        command.Parameters.AddWithValue("$runId", finding.RunId);
        command.Parameters.AddWithValue("$correlationKey", (object?)finding.CorrelationKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", finding.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Finding?> GetFindingAsync(string findingId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT finding_id, run_id, correlation_key, created_at_utc FROM findings WHERE finding_id = $findingId;";
        command.Parameters.AddWithValue("$findingId", findingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new Finding
        {
            FindingId = reader.GetString(0),
            RunId = reader.GetString(1),
            CorrelationKey = reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(3)),
        };
    }

    public async Task AppendFindingRevisionAsync(FindingRevision revision, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO finding_revisions
    (finding_revision_id, finding_id, run_id, revision_number, statement, evidence_ids_json,
     confidence, status, provenance_id, supersedes_revision_id, created_at_utc)
VALUES
    ($findingRevisionId, $findingId, $runId, $revisionNumber, $statement, $evidenceIdsJson,
     $confidence, $status, $provenanceId, $supersedesRevisionId, $createdAtUtc);";
        command.Parameters.AddWithValue("$findingRevisionId", revision.FindingRevisionId);
        command.Parameters.AddWithValue("$findingId", revision.FindingId);
        command.Parameters.AddWithValue("$runId", revision.RunId);
        command.Parameters.AddWithValue("$revisionNumber", revision.RevisionNumber);
        command.Parameters.AddWithValue("$statement", revision.Statement);
        command.Parameters.AddWithValue("$evidenceIdsJson", JsonSerializer.Serialize(revision.EvidenceIds, JsonOptions));
        command.Parameters.AddWithValue("$confidence", revision.Confidence);
        command.Parameters.AddWithValue("$status", revision.Status.ToString());
        command.Parameters.AddWithValue("$provenanceId", revision.ProvenanceId);
        command.Parameters.AddWithValue("$supersedesRevisionId", (object?)revision.SupersedesRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", revision.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FindingRevision?> GetFindingRevisionAsync(string findingRevisionId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT finding_revision_id, finding_id, run_id, revision_number, statement, evidence_ids_json, confidence, status, provenance_id, supersedes_revision_id, created_at_utc FROM finding_revisions WHERE finding_revision_id = $id;";
        command.Parameters.AddWithValue("$id", findingRevisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadFindingRevision(reader);
    }

    public async Task<IReadOnlyList<FindingRevision>> GetFindingRevisionsAsync(string findingId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT finding_revision_id, finding_id, run_id, revision_number, statement, evidence_ids_json, confidence, status, provenance_id, supersedes_revision_id, created_at_utc FROM finding_revisions WHERE finding_id = $findingId ORDER BY revision_number;";
        command.Parameters.AddWithValue("$findingId", findingId);
        var results = new List<FindingRevision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadFindingRevision(reader));
        }
        return results;
    }

    public async Task<FindingRevision?> GetLatestFindingRevisionAsync(string findingId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT finding_revision_id, finding_id, run_id, revision_number, statement, evidence_ids_json, confidence, status, provenance_id, supersedes_revision_id, created_at_utc FROM finding_revisions WHERE finding_id = $findingId ORDER BY revision_number DESC LIMIT 1;";
        command.Parameters.AddWithValue("$findingId", findingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadFindingRevision(reader);
    }

    private static FindingRevision ReadFindingRevision(SqliteDataReader reader) => new()
    {
        FindingRevisionId = reader.GetString(0),
        FindingId = reader.GetString(1),
        RunId = reader.GetString(2),
        RevisionNumber = reader.GetInt32(3),
        Statement = reader.GetString(4),
        EvidenceIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), JsonOptions) ?? new(),
        Confidence = reader.GetDouble(6),
        Status = Enum.Parse<ReviewStatus>(reader.GetString(7)),
        ProvenanceId = reader.GetString(8),
        SupersedesRevisionId = reader.IsDBNull(9) ? null : reader.GetString(9),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(10)),
    };

    public async Task AppendLlmAssessmentAsync(LlmAssessment assessment, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO llm_assessments
    (llm_assessment_id, run_id, finding_revision_id, review_priority, why_explanation,
     cited_evidence_ids_json, conflicts_or_unknowns, model_id, provenance_id, created_at_utc)
VALUES
    ($id, $runId, $findingRevisionId, $reviewPriority, $whyExplanation,
     $citedEvidenceIdsJson, $conflictsOrUnknowns, $modelId, $provenanceId, $createdAtUtc);";
        command.Parameters.AddWithValue("$id", assessment.LlmAssessmentId);
        command.Parameters.AddWithValue("$runId", assessment.RunId);
        command.Parameters.AddWithValue("$findingRevisionId", assessment.FindingRevisionId);
        command.Parameters.AddWithValue("$reviewPriority", assessment.ReviewPriority);
        command.Parameters.AddWithValue("$whyExplanation", assessment.WhyExplanation);
        command.Parameters.AddWithValue("$citedEvidenceIdsJson", JsonSerializer.Serialize(assessment.CitedEvidenceIds, JsonOptions));
        command.Parameters.AddWithValue("$conflictsOrUnknowns", (object?)assessment.ConflictsOrUnknowns ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelId", assessment.ModelId);
        command.Parameters.AddWithValue("$provenanceId", assessment.ProvenanceId);
        command.Parameters.AddWithValue("$createdAtUtc", assessment.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LlmAssessment?> GetLlmAssessmentForRevisionAsync(string findingRevisionId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT llm_assessment_id, run_id, finding_revision_id, review_priority, why_explanation, cited_evidence_ids_json, conflicts_or_unknowns, model_id, provenance_id, created_at_utc FROM llm_assessments WHERE finding_revision_id = $id ORDER BY created_at_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$id", findingRevisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new LlmAssessment
        {
            LlmAssessmentId = reader.GetString(0),
            RunId = reader.GetString(1),
            FindingRevisionId = reader.GetString(2),
            ReviewPriority = reader.GetDouble(3),
            WhyExplanation = reader.GetString(4),
            CitedEvidenceIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), JsonOptions) ?? new(),
            ConflictsOrUnknowns = reader.IsDBNull(6) ? null : reader.GetString(6),
            ModelId = reader.GetString(7),
            ProvenanceId = reader.GetString(8),
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(9)),
        };
    }

    public async Task AppendReviewDecisionAsync(ReviewDecision decision, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO review_decisions (review_decision_id, run_id, finding_revision_id, reviewer_identity, decision, rationale, decided_at_utc)
VALUES ($id, $runId, $findingRevisionId, $reviewerIdentity, $decision, $rationale, $decidedAtUtc);";
        command.Parameters.AddWithValue("$id", decision.ReviewDecisionId);
        command.Parameters.AddWithValue("$runId", decision.RunId);
        command.Parameters.AddWithValue("$findingRevisionId", decision.FindingRevisionId);
        command.Parameters.AddWithValue("$reviewerIdentity", decision.ReviewerIdentity);
        command.Parameters.AddWithValue("$decision", decision.Decision.ToString());
        command.Parameters.AddWithValue("$rationale", decision.Rationale);
        command.Parameters.AddWithValue("$decidedAtUtc", decision.DecidedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReviewDecision>> GetReviewDecisionsAsync(string findingRevisionId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT review_decision_id, run_id, finding_revision_id, reviewer_identity, decision, rationale, decided_at_utc FROM review_decisions WHERE finding_revision_id = $id ORDER BY decided_at_utc;";
        command.Parameters.AddWithValue("$id", findingRevisionId);
        var results = new List<ReviewDecision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ReviewDecision
            {
                ReviewDecisionId = reader.GetString(0),
                RunId = reader.GetString(1),
                FindingRevisionId = reader.GetString(2),
                ReviewerIdentity = reader.GetString(3),
                Decision = Enum.Parse<ReviewStatus>(reader.GetString(4)),
                Rationale = reader.GetString(5),
                DecidedAtUtc = DateTimeOffset.Parse(reader.GetString(6)),
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<FindingRevision>> GetReviewQueueAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Latest revision per finding whose status is still actionable by a reviewer.
        command.CommandText = @"
SELECT fr.finding_revision_id, fr.finding_id, fr.run_id, fr.revision_number, fr.statement,
       fr.evidence_ids_json, fr.confidence, fr.status, fr.provenance_id, fr.supersedes_revision_id, fr.created_at_utc
FROM finding_revisions fr
INNER JOIN (
    SELECT finding_id, MAX(revision_number) AS max_rev
    FROM finding_revisions
    WHERE run_id = $runId
    GROUP BY finding_id
) latest ON fr.finding_id = latest.finding_id AND fr.revision_number = latest.max_rev
WHERE fr.run_id = $runId AND fr.status IN ('Candidate', 'HumanReview', 'NeedsEvidence')
ORDER BY fr.created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<FindingRevision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadFindingRevision(reader));
        }
        return results;
    }
}
