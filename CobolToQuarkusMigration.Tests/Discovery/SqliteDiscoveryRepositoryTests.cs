using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

/// <summary>
/// Each test gets its own temp SQLite file so tests never share state and can run in parallel.
/// </summary>
public sealed class SqliteDiscoveryRepositoryFixture : IDisposable
{
    public string DatabasePath { get; }
    public SqliteDiscoveryRepository Repository { get; }

    public SqliteDiscoveryRepositoryFixture()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), $"discovery-tests-{Guid.NewGuid():N}.db");
        Repository = new SqliteDiscoveryRepository(DatabasePath, NullLogger<SqliteDiscoveryRepository>.Instance);
    }

    public void Dispose()
    {
        if (File.Exists(DatabasePath))
        {
            File.Delete(DatabasePath);
        }
    }
}

public class SqliteDiscoveryRepositoryTests : IDisposable
{
    private readonly SqliteDiscoveryRepositoryFixture _fixture = new();
    private IDiscoveryRepository Repository => _fixture.Repository;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndCreatesSchema()
    {
        await Repository.InitializeAsync();
        await Repository.InitializeAsync(); // must not throw or re-apply migrations

        var runs = await Repository.GetAllRunsAsync();
        runs.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendRunAsync_ThenGetRunAsync_RoundTripsAllFields()
    {
        await Repository.InitializeAsync();
        var run = new DiscoveryRun
        {
            RunId = "RUN-TEST0001",
            Subject = "Test subject",
            SourceLocator = "/tmp/source",
            SourceRevision = "abc123",
            Inclusions = new[] { "*.cbl" },
            Exclusions = new[] { "*.tmp" },
            EvidenceBoundary = "Static source only",
            Intent = "pilot",
            Status = DiscoveryRunStatus.Declared,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await Repository.AppendRunAsync(run);
        var fetched = await Repository.GetRunAsync(run.RunId);

        fetched.Should().NotBeNull();
        fetched!.Subject.Should().Be("Test subject");
        fetched.Inclusions.Should().ContainSingle().Which.Should().Be("*.cbl");
        fetched.Exclusions.Should().ContainSingle().Which.Should().Be("*.tmp");
        fetched.EvidenceBoundary.Should().Be("Static source only");
    }

    [Fact]
    public async Task AppendEvidenceAsync_NeverPersistsRawExcerpt_OnlySanitizedText()
    {
        await Repository.InitializeAsync();
        var run = NewRun();
        await Repository.AppendRunAsync(run);

        var evidence = new Evidence
        {
            EvidenceId = "EVD-TEST0001",
            RunId = run.RunId,
            Type = EvidenceType.Configuration,
            Locator = "config/app.properties",
            RedactedExcerpt = "DB_PASSWORD (role: database connection secret; value not retained)",
            WasRedacted = true,
            RedactionSummary = "api-key-assignment x1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await Repository.AppendEvidenceAsync(evidence);

        var stored = (await Repository.GetEvidenceAsync(run.RunId)).Single();
        stored.RedactedExcerpt.Should().NotContain("=");
        stored.WasRedacted.Should().BeTrue();
    }

    [Fact]
    public async Task FindingRevisions_AreAppendOnly_LatestRevisionReflectsMostRecentStatus()
    {
        await Repository.InitializeAsync();
        var run = NewRun();
        await Repository.AppendRunAsync(run);
        await Repository.AppendFindingAsync(new Finding { FindingId = "F-TEST0001", RunId = run.RunId, CreatedAtUtc = DateTimeOffset.UtcNow });

        var r1 = await NewRevisionAsync(run.RunId, "F-TEST0001", 1, ReviewStatus.Candidate);
        await Repository.AppendFindingRevisionAsync(r1);
        var r2 = await NewRevisionAsync(run.RunId, "F-TEST0001", 2, ReviewStatus.HumanReview);
        await Repository.AppendFindingRevisionAsync(r2);
        var r3 = await NewRevisionAsync(run.RunId, "F-TEST0001", 3, ReviewStatus.Published);
        await Repository.AppendFindingRevisionAsync(r3);

        var all = await Repository.GetFindingRevisionsAsync("F-TEST0001");
        all.Should().HaveCount(3);

        var latest = await Repository.GetLatestFindingRevisionAsync("F-TEST0001");
        latest.Should().NotBeNull();
        latest!.Status.Should().Be(ReviewStatus.Published);
        latest.RevisionNumber.Should().Be(3);

        // Earlier revisions remain retrievable and unchanged -- append-only guarantee.
        var first = await Repository.GetFindingRevisionAsync("F-TEST0001-R1");
        first!.Status.Should().Be(ReviewStatus.Candidate);
    }

    [Fact]
    public async Task GetReviewQueueAsync_ReturnsOnlyNonTerminalLatestRevisions()
    {
        await Repository.InitializeAsync();
        var run = NewRun();
        await Repository.AppendRunAsync(run);

        // Finding A: still a candidate -> should appear in queue.
        await Repository.AppendFindingAsync(new Finding { FindingId = "F-A", RunId = run.RunId, CreatedAtUtc = DateTimeOffset.UtcNow });
        await Repository.AppendFindingRevisionAsync(await NewRevisionAsync(run.RunId, "F-A", 1, ReviewStatus.Candidate));

        // Finding B: published -> should NOT appear in queue.
        await Repository.AppendFindingAsync(new Finding { FindingId = "F-B", RunId = run.RunId, CreatedAtUtc = DateTimeOffset.UtcNow });
        await Repository.AppendFindingRevisionAsync(await NewRevisionAsync(run.RunId, "F-B", 1, ReviewStatus.Candidate));
        await Repository.AppendFindingRevisionAsync(await NewRevisionAsync(run.RunId, "F-B", 2, ReviewStatus.Published));

        var queue = await Repository.GetReviewQueueAsync(run.RunId);

        queue.Should().ContainSingle(r => r.FindingId == "F-A");
        queue.Should().NotContain(r => r.FindingId == "F-B");
    }

    [Fact]
    public async Task AppendRoleAssignmentAsync_RoundTripsRolesEvidenceAndReviewFlag()
    {
        await Repository.InitializeAsync();
        var run = NewRun();
        await Repository.AppendRunAsync(run);

        var artifact = new SourceArtifact
        {
            ArtifactId = "ART-ROLE-1",
            RunId = run.RunId,
            Path = "Domain/Employee.cs",
            Language = "CSharp",
            ContentHash = "deadbeef",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await Repository.AppendArtifactAsync(artifact);

        await Repository.AppendProvenanceAsync(new Provenance
        {
            ProvenanceId = "PROV-ROLE-1",
            RunId = run.RunId,
            ProducerKind = "DeterministicExtractor",
            ProducerVersion = "test-harness",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        var assignment = new RoleAssignment
        {
            RoleAssignmentId = "ROLE-1",
            RunId = run.RunId,
            ArtifactId = artifact.ArtifactId,
            SymbolLocator = "App.Domain.Employee",
            Roles = new[] { ArtifactRoleTag.Business, ArtifactRoleTag.Persistence },
            Confidence = 0.6,
            EvidenceIds = new[] { "EVD-1", "EVD-2" },
            ClassificationRule = "PersistenceAttribute;BusinessFallback",
            RequiresReview = true,
            ProvenanceId = "PROV-ROLE-1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await Repository.AppendRoleAssignmentAsync(assignment);

        var byRun = await Repository.GetRoleAssignmentsAsync(run.RunId);
        var byArtifact = await Repository.GetRoleAssignmentsForArtifactAsync(artifact.ArtifactId);

        byRun.Should().ContainSingle();
        byArtifact.Should().ContainSingle();

        var roundTripped = byRun[0];
        roundTripped.Roles.Should().BeEquivalentTo(new[] { ArtifactRoleTag.Business, ArtifactRoleTag.Persistence });
        roundTripped.EvidenceIds.Should().BeEquivalentTo(new[] { "EVD-1", "EVD-2" });
        roundTripped.RequiresReview.Should().BeTrue();
        roundTripped.Confidence.Should().Be(0.6);
        roundTripped.ClassificationRule.Should().Be("PersistenceAttribute;BusinessFallback");
    }

    [Fact]
    public async Task AppendRoleAssignmentAsync_IsAppendOnly_MultipleAssignmentsForSameArtifactCoexist()
    {
        await Repository.InitializeAsync();
        var run = NewRun();
        await Repository.AppendRunAsync(run);

        var artifact = new SourceArtifact
        {
            ArtifactId = "ART-ROLE-2",
            RunId = run.RunId,
            Path = "Domain/Widget.cs",
            Language = "CSharp",
            ContentHash = "cafebabe",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await Repository.AppendArtifactAsync(artifact);

        await Repository.AppendProvenanceAsync(new Provenance
        {
            ProvenanceId = "PROV-ROLE-2",
            RunId = run.RunId,
            ProducerKind = "DeterministicExtractor",
            ProducerVersion = "test-harness",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        for (var i = 1; i <= 2; i++)
        {
            await Repository.AppendRoleAssignmentAsync(new RoleAssignment
            {
                RoleAssignmentId = $"ROLE-2-{i}",
                RunId = run.RunId,
                ArtifactId = artifact.ArtifactId,
                SymbolLocator = "App.Domain.Widget",
                Roles = new[] { ArtifactRoleTag.Unknown },
                Confidence = 0.2,
                EvidenceIds = Array.Empty<string>(),
                ClassificationRule = "NoRuleMatched",
                RequiresReview = true,
                ProvenanceId = "PROV-ROLE-2",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        var assignments = await Repository.GetRoleAssignmentsForArtifactAsync(artifact.ArtifactId);
        assignments.Should().HaveCount(2, "role assignments are append-only, never overwritten");
    }

    private static DiscoveryRun NewRun() => new()
    {
        RunId = $"RUN-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
        Subject = "Test subject",
        SourceLocator = "/tmp/source",
        SourceRevision = "abc123",
        EvidenceBoundary = "Static source only",
        Status = DiscoveryRunStatus.Declared,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private async Task<FindingRevision> NewRevisionAsync(string runId, string findingId, int revisionNumber, ReviewStatus status)
    {
        var provenanceId = $"PROV-{findingId}-{revisionNumber}";
        await Repository.AppendProvenanceAsync(new Provenance
        {
            ProvenanceId = provenanceId,
            RunId = runId,
            ProducerKind = "DeterministicExtractor",
            ProducerVersion = "test-harness",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        return new FindingRevision
        {
            FindingRevisionId = $"{findingId}-R{revisionNumber}",
            FindingId = findingId,
            RunId = runId,
            RevisionNumber = revisionNumber,
            Statement = $"Deterministic statement revision {revisionNumber}",
            Confidence = 0.9,
            Status = status,
            ProvenanceId = provenanceId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}
