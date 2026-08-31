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

    public async Task AppendRoleAssignmentAsync(RoleAssignment assignment, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO role_assignments
    (role_assignment_id, run_id, artifact_id, symbol_locator, roles_json, confidence,
     evidence_ids_json, classification_rule, requires_review, provenance_id, created_at_utc)
VALUES
    ($roleAssignmentId, $runId, $artifactId, $symbolLocator, $rolesJson, $confidence,
     $evidenceIdsJson, $classificationRule, $requiresReview, $provenanceId, $createdAtUtc);";
        command.Parameters.AddWithValue("$roleAssignmentId", assignment.RoleAssignmentId);
        command.Parameters.AddWithValue("$runId", assignment.RunId);
        command.Parameters.AddWithValue("$artifactId", assignment.ArtifactId);
        command.Parameters.AddWithValue("$symbolLocator", assignment.SymbolLocator);
        command.Parameters.AddWithValue("$rolesJson", JsonSerializer.Serialize(assignment.Roles.Select(r => r.ToString()), JsonOptions));
        command.Parameters.AddWithValue("$confidence", assignment.Confidence);
        command.Parameters.AddWithValue("$evidenceIdsJson", JsonSerializer.Serialize(assignment.EvidenceIds, JsonOptions));
        command.Parameters.AddWithValue("$classificationRule", assignment.ClassificationRule);
        command.Parameters.AddWithValue("$requiresReview", assignment.RequiresReview ? 1 : 0);
        command.Parameters.AddWithValue("$provenanceId", assignment.ProvenanceId);
        command.Parameters.AddWithValue("$createdAtUtc", assignment.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RoleAssignment>> GetRoleAssignmentsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT role_assignment_id, run_id, artifact_id, symbol_locator, roles_json, confidence,
       evidence_ids_json, classification_rule, requires_review, provenance_id, created_at_utc
FROM role_assignments WHERE run_id = $runId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<RoleAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadRoleAssignment(reader));
        }
        return results;
    }

    public async Task<IReadOnlyList<RoleAssignment>> GetRoleAssignmentsForArtifactAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT role_assignment_id, run_id, artifact_id, symbol_locator, roles_json, confidence,
       evidence_ids_json, classification_rule, requires_review, provenance_id, created_at_utc
FROM role_assignments WHERE artifact_id = $artifactId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$artifactId", artifactId);
        var results = new List<RoleAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadRoleAssignment(reader));
        }
        return results;
    }

    private static RoleAssignment ReadRoleAssignment(SqliteDataReader reader) => new()
    {
        RoleAssignmentId = reader.GetString(0),
        RunId = reader.GetString(1),
        ArtifactId = reader.GetString(2),
        SymbolLocator = reader.GetString(3),
        Roles = (JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? new List<string>())
            .Select(Enum.Parse<ArtifactRoleTag>)
            .ToList(),
        Confidence = reader.GetDouble(5),
        EvidenceIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(6), JsonOptions) ?? new List<string>(),
        ClassificationRule = reader.GetString(7),
        RequiresReview = reader.GetInt32(8) != 0,
        ProvenanceId = reader.GetString(9),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(10)),
    };

    // ---------------------------------------------------------------------------------------
    // Discovery Factory phase 5 (issue #3): dependency graph and candidate context persistence.
    // ---------------------------------------------------------------------------------------

    public async Task AppendGraphNodeAsync(DependencyGraphNode node, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO graph_nodes
    (node_id, run_id, kind, symbol_locator, display_name, artifact_id, evidence_ids_json, created_at_utc)
VALUES
    ($nodeId, $runId, $kind, $symbolLocator, $displayName, $artifactId, $evidenceIdsJson, $createdAtUtc);";
        command.Parameters.AddWithValue("$nodeId", node.NodeId);
        command.Parameters.AddWithValue("$runId", node.RunId);
        command.Parameters.AddWithValue("$kind", node.Kind.ToString());
        command.Parameters.AddWithValue("$symbolLocator", node.SymbolLocator);
        command.Parameters.AddWithValue("$displayName", node.DisplayName);
        command.Parameters.AddWithValue("$artifactId", (object?)node.ArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidenceIdsJson", JsonSerializer.Serialize(node.EvidenceIds, JsonOptions));
        command.Parameters.AddWithValue("$createdAtUtc", node.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DependencyGraphNode>> GetGraphNodesAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT node_id, run_id, kind, symbol_locator, display_name, artifact_id, evidence_ids_json, created_at_utc
FROM graph_nodes WHERE run_id = $runId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<DependencyGraphNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadGraphNode(reader));
        }
        return results;
    }

    private static DependencyGraphNode ReadGraphNode(SqliteDataReader reader) => new()
    {
        NodeId = reader.GetString(0),
        RunId = reader.GetString(1),
        Kind = Enum.Parse<GraphNodeKind>(reader.GetString(2)),
        SymbolLocator = reader.GetString(3),
        DisplayName = reader.GetString(4),
        ArtifactId = reader.IsDBNull(5) ? null : reader.GetString(5),
        EvidenceIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(6), JsonOptions) ?? new List<string>(),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(7)),
    };

    public async Task AppendGraphEdgeAsync(DependencyGraphEdge edge, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO graph_edges
    (edge_id, run_id, from_node_id, to_node_id, kind, evidence_ids_json, confidence, created_at_utc)
VALUES
    ($edgeId, $runId, $fromNodeId, $toNodeId, $kind, $evidenceIdsJson, $confidence, $createdAtUtc);";
        command.Parameters.AddWithValue("$edgeId", edge.EdgeId);
        command.Parameters.AddWithValue("$runId", edge.RunId);
        command.Parameters.AddWithValue("$fromNodeId", edge.FromNodeId);
        command.Parameters.AddWithValue("$toNodeId", edge.ToNodeId);
        command.Parameters.AddWithValue("$kind", edge.Kind.ToString());
        command.Parameters.AddWithValue("$evidenceIdsJson", JsonSerializer.Serialize(edge.EvidenceIds, JsonOptions));
        command.Parameters.AddWithValue("$confidence", edge.Confidence);
        command.Parameters.AddWithValue("$createdAtUtc", edge.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DependencyGraphEdge>> GetGraphEdgesAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT edge_id, run_id, from_node_id, to_node_id, kind, evidence_ids_json, confidence, created_at_utc
FROM graph_edges WHERE run_id = $runId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<DependencyGraphEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadGraphEdge(reader));
        }
        return results;
    }

    private static DependencyGraphEdge ReadGraphEdge(SqliteDataReader reader) => new()
    {
        EdgeId = reader.GetString(0),
        RunId = reader.GetString(1),
        FromNodeId = reader.GetString(2),
        ToNodeId = reader.GetString(3),
        Kind = Enum.Parse<GraphEdgeKind>(reader.GetString(4)),
        EvidenceIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), JsonOptions) ?? new List<string>(),
        Confidence = reader.GetDouble(6),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(7)),
    };

    public async Task AppendContextCandidateAsync(ContextCandidate candidate, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO context_candidates
    (context_candidate_id, run_id, name, kind, status, confidence, evidence_ids_json,
     seeding_rule, provenance_id, supersedes_context_candidate_id, created_at_utc)
VALUES
    ($contextCandidateId, $runId, $name, $kind, $status, $confidence, $evidenceIdsJson,
     $seedingRule, $provenanceId, $supersedesContextCandidateId, $createdAtUtc);";
        command.Parameters.AddWithValue("$contextCandidateId", candidate.ContextCandidateId);
        command.Parameters.AddWithValue("$runId", candidate.RunId);
        command.Parameters.AddWithValue("$name", candidate.Name);
        command.Parameters.AddWithValue("$kind", candidate.Kind.ToString());
        command.Parameters.AddWithValue("$status", candidate.Status.ToString());
        command.Parameters.AddWithValue("$confidence", candidate.Confidence);
        command.Parameters.AddWithValue("$evidenceIdsJson", JsonSerializer.Serialize(candidate.EvidenceIds, JsonOptions));
        command.Parameters.AddWithValue("$seedingRule", candidate.SeedingRule);
        command.Parameters.AddWithValue("$provenanceId", candidate.ProvenanceId);
        command.Parameters.AddWithValue("$supersedesContextCandidateId", (object?)candidate.SupersedesContextCandidateId ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", candidate.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContextCandidate>> GetContextCandidatesAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT context_candidate_id, run_id, name, kind, status, confidence, evidence_ids_json,
       seeding_rule, provenance_id, supersedes_context_candidate_id, created_at_utc
FROM context_candidates WHERE run_id = $runId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<ContextCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadContextCandidate(reader));
        }
        return results;
    }

    public async Task<ContextCandidate?> GetContextCandidateAsync(string contextCandidateId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT context_candidate_id, run_id, name, kind, status, confidence, evidence_ids_json,
       seeding_rule, provenance_id, supersedes_context_candidate_id, created_at_utc
FROM context_candidates WHERE context_candidate_id = $contextCandidateId;";
        command.Parameters.AddWithValue("$contextCandidateId", contextCandidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadContextCandidate(reader) : null;
    }

    private static ContextCandidate ReadContextCandidate(SqliteDataReader reader) => new()
    {
        ContextCandidateId = reader.GetString(0),
        RunId = reader.GetString(1),
        Name = reader.GetString(2),
        Kind = Enum.Parse<ContextCandidateKind>(reader.GetString(3)),
        Status = Enum.Parse<ReviewStatus>(reader.GetString(4)),
        Confidence = reader.GetDouble(5),
        EvidenceIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(6), JsonOptions) ?? new List<string>(),
        SeedingRule = reader.GetString(7),
        ProvenanceId = reader.GetString(8),
        SupersedesContextCandidateId = reader.IsDBNull(9) ? null : reader.GetString(9),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(10)),
    };

    public async Task AppendContextMembershipAsync(ContextMembership membership, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO context_memberships
    (context_membership_id, run_id, context_candidate_id, node_id, role, evidence_ids_json, created_at_utc)
VALUES
    ($contextMembershipId, $runId, $contextCandidateId, $nodeId, $role, $evidenceIdsJson, $createdAtUtc);";
        command.Parameters.AddWithValue("$contextMembershipId", membership.ContextMembershipId);
        command.Parameters.AddWithValue("$runId", membership.RunId);
        command.Parameters.AddWithValue("$contextCandidateId", membership.ContextCandidateId);
        command.Parameters.AddWithValue("$nodeId", membership.NodeId);
        command.Parameters.AddWithValue("$role", membership.Role.ToString());
        command.Parameters.AddWithValue("$evidenceIdsJson", JsonSerializer.Serialize(membership.EvidenceIds, JsonOptions));
        command.Parameters.AddWithValue("$createdAtUtc", membership.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContextMembership>> GetContextMembershipsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT context_membership_id, run_id, context_candidate_id, node_id, role, evidence_ids_json, created_at_utc
FROM context_memberships WHERE run_id = $runId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<ContextMembership>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadContextMembership(reader));
        }
        return results;
    }

    public async Task<IReadOnlyList<ContextMembership>> GetContextMembershipsForContextAsync(string contextCandidateId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT context_membership_id, run_id, context_candidate_id, node_id, role, evidence_ids_json, created_at_utc
FROM context_memberships WHERE context_candidate_id = $contextCandidateId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$contextCandidateId", contextCandidateId);
        var results = new List<ContextMembership>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadContextMembership(reader));
        }
        return results;
    }

    private static ContextMembership ReadContextMembership(SqliteDataReader reader) => new()
    {
        ContextMembershipId = reader.GetString(0),
        RunId = reader.GetString(1),
        ContextCandidateId = reader.GetString(2),
        NodeId = reader.GetString(3),
        Role = Enum.Parse<ContextMembershipRole>(reader.GetString(4)),
        EvidenceIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), JsonOptions) ?? new List<string>(),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(6)),
    };

    public async Task AppendContextDependencyEdgeAsync(ContextDependencyEdge edge, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO context_dependency_edges
    (context_dependency_edge_id, run_id, from_context_candidate_id, to_context_candidate_id,
     evidence_ids_json, confidence, created_at_utc)
VALUES
    ($contextDependencyEdgeId, $runId, $fromContextCandidateId, $toContextCandidateId,
     $evidenceIdsJson, $confidence, $createdAtUtc);";
        command.Parameters.AddWithValue("$contextDependencyEdgeId", edge.ContextDependencyEdgeId);
        command.Parameters.AddWithValue("$runId", edge.RunId);
        command.Parameters.AddWithValue("$fromContextCandidateId", edge.FromContextCandidateId);
        command.Parameters.AddWithValue("$toContextCandidateId", edge.ToContextCandidateId);
        command.Parameters.AddWithValue("$evidenceIdsJson", JsonSerializer.Serialize(edge.EvidenceIds, JsonOptions));
        command.Parameters.AddWithValue("$confidence", edge.Confidence);
        command.Parameters.AddWithValue("$createdAtUtc", edge.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContextDependencyEdge>> GetContextDependencyEdgesAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT context_dependency_edge_id, run_id, from_context_candidate_id, to_context_candidate_id,
       evidence_ids_json, confidence, created_at_utc
FROM context_dependency_edges WHERE run_id = $runId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<ContextDependencyEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadContextDependencyEdge(reader));
        }
        return results;
    }

    private static ContextDependencyEdge ReadContextDependencyEdge(SqliteDataReader reader) => new()
    {
        ContextDependencyEdgeId = reader.GetString(0),
        RunId = reader.GetString(1),
        FromContextCandidateId = reader.GetString(2),
        ToContextCandidateId = reader.GetString(3),
        EvidenceIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? new List<string>(),
        Confidence = reader.GetDouble(5),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(6)),
    };

    // -------------------------------------------------------------------------------------
    // Discovery Factory phase 6 (issue #6): integration inventory and topology links.
    // -------------------------------------------------------------------------------------

    public async Task AppendIntegrationAsync(Integration integration, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO integrations
    (integration_id, run_id, category, classification, direction, trigger_or_caller,
     protocol_or_mechanism, logical_target, configuration_key_semantics, redacted_contract_shape,
     authentication_semantics, reliability_behavior, owning_context_candidate_id,
     evidence_ids_json, confidence, classification_rule, blind_spots_json, requires_review,
     provenance_id, created_at_utc)
VALUES
    ($integrationId, $runId, $category, $classification, $direction, $triggerOrCaller,
     $protocolOrMechanism, $logicalTarget, $configurationKeySemantics, $redactedContractShape,
     $authenticationSemantics, $reliabilityBehavior, $owningContextCandidateId,
     $evidenceIdsJson, $confidence, $classificationRule, $blindSpotsJson, $requiresReview,
     $provenanceId, $createdAtUtc);";
        command.Parameters.AddWithValue("$integrationId", integration.IntegrationId);
        command.Parameters.AddWithValue("$runId", integration.RunId);
        command.Parameters.AddWithValue("$category", integration.Category.ToString());
        command.Parameters.AddWithValue("$classification", integration.Classification.ToString());
        command.Parameters.AddWithValue("$direction", integration.Direction.ToString());
        command.Parameters.AddWithValue("$triggerOrCaller", integration.TriggerOrCaller);
        command.Parameters.AddWithValue("$protocolOrMechanism", integration.ProtocolOrMechanism);
        command.Parameters.AddWithValue("$logicalTarget", integration.LogicalTarget);
        command.Parameters.AddWithValue("$configurationKeySemantics", (object?)integration.ConfigurationKeySemantics ?? DBNull.Value);
        command.Parameters.AddWithValue("$redactedContractShape", (object?)integration.RedactedContractShape ?? DBNull.Value);
        command.Parameters.AddWithValue("$authenticationSemantics", (object?)integration.AuthenticationSemantics ?? DBNull.Value);
        command.Parameters.AddWithValue("$reliabilityBehavior", (object?)integration.ReliabilityBehavior ?? DBNull.Value);
        command.Parameters.AddWithValue("$owningContextCandidateId", (object?)integration.OwningContextCandidateId ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidenceIdsJson", JsonSerializer.Serialize(integration.EvidenceIds, JsonOptions));
        command.Parameters.AddWithValue("$confidence", integration.Confidence);
        command.Parameters.AddWithValue("$classificationRule", integration.ClassificationRule);
        command.Parameters.AddWithValue("$blindSpotsJson", JsonSerializer.Serialize(integration.BlindSpots, JsonOptions));
        command.Parameters.AddWithValue("$requiresReview", integration.RequiresReview ? 1 : 0);
        command.Parameters.AddWithValue("$provenanceId", integration.ProvenanceId);
        command.Parameters.AddWithValue("$createdAtUtc", integration.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Integration>> GetIntegrationsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT integration_id, run_id, category, classification, direction, trigger_or_caller,
       protocol_or_mechanism, logical_target, configuration_key_semantics, redacted_contract_shape,
       authentication_semantics, reliability_behavior, owning_context_candidate_id,
       evidence_ids_json, confidence, classification_rule, blind_spots_json, requires_review,
       provenance_id, created_at_utc
FROM integrations WHERE run_id = $runId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$runId", runId);
        var results = new List<Integration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadIntegration(reader));
        }
        return results;
    }

    public async Task<Integration?> GetIntegrationAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT integration_id, run_id, category, classification, direction, trigger_or_caller,
       protocol_or_mechanism, logical_target, configuration_key_semantics, redacted_contract_shape,
       authentication_semantics, reliability_behavior, owning_context_candidate_id,
       evidence_ids_json, confidence, classification_rule, blind_spots_json, requires_review,
       provenance_id, created_at_utc
FROM integrations WHERE integration_id = $integrationId;";
        command.Parameters.AddWithValue("$integrationId", integrationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntegration(reader) : null;
    }

    private static Integration ReadIntegration(SqliteDataReader reader) => new()
    {
        IntegrationId = reader.GetString(0),
        RunId = reader.GetString(1),
        Category = Enum.Parse<IntegrationCategory>(reader.GetString(2)),
        Classification = Enum.Parse<IntegrationClassification>(reader.GetString(3)),
        Direction = Enum.Parse<IntegrationDirection>(reader.GetString(4)),
        TriggerOrCaller = reader.GetString(5),
        ProtocolOrMechanism = reader.GetString(6),
        LogicalTarget = reader.GetString(7),
        ConfigurationKeySemantics = reader.IsDBNull(8) ? null : reader.GetString(8),
        RedactedContractShape = reader.IsDBNull(9) ? null : reader.GetString(9),
        AuthenticationSemantics = reader.IsDBNull(10) ? null : reader.GetString(10),
        ReliabilityBehavior = reader.IsDBNull(11) ? null : reader.GetString(11),
        OwningContextCandidateId = reader.IsDBNull(12) ? null : reader.GetString(12),
        EvidenceIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(13), JsonOptions) ?? new List<string>(),
        Confidence = reader.GetDouble(14),
        ClassificationRule = reader.GetString(15),
        BlindSpots = JsonSerializer.Deserialize<List<string>>(reader.GetString(16), JsonOptions) ?? new List<string>(),
        RequiresReview = reader.GetInt32(17) != 0,
        ProvenanceId = reader.GetString(18),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(19)),
    };

    public async Task AppendIntegrationLinkAsync(IntegrationLink link, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO integration_links
    (integration_link_id, run_id, integration_id, linked_record_kind, linked_record_id, created_at_utc)
VALUES
    ($integrationLinkId, $runId, $integrationId, $linkedRecordKind, $linkedRecordId, $createdAtUtc);";
        command.Parameters.AddWithValue("$integrationLinkId", link.IntegrationLinkId);
        command.Parameters.AddWithValue("$runId", link.RunId);
        command.Parameters.AddWithValue("$integrationId", link.IntegrationId);
        command.Parameters.AddWithValue("$linkedRecordKind", link.LinkedRecordKind.ToString());
        command.Parameters.AddWithValue("$linkedRecordId", link.LinkedRecordId);
        command.Parameters.AddWithValue("$createdAtUtc", link.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IntegrationLink>> GetIntegrationLinksAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT integration_link_id, run_id, integration_id, linked_record_kind, linked_record_id, created_at_utc
FROM integration_links WHERE integration_id = $integrationId ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$integrationId", integrationId);
        var results = new List<IntegrationLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new IntegrationLink
            {
                IntegrationLinkId = reader.GetString(0),
                RunId = reader.GetString(1),
                IntegrationId = reader.GetString(2),
                LinkedRecordKind = Enum.Parse<IntegrationLinkedRecordKind>(reader.GetString(3)),
                LinkedRecordId = reader.GetString(4),
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(5)),
            });
        }
        return results;
    }
}
