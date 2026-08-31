using CobolToQuarkusMigration.Discovery;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

public sealed class DiscoveryServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"discovery-service-tests-{Guid.NewGuid():N}.db");
    private readonly IDiscoveryRepository _repository;
    private readonly DiscoveryService _service;

    public DiscoveryServiceTests()
    {
        _repository = new SqliteDiscoveryRepository(_dbPath, NullLogger<SqliteDiscoveryRepository>.Instance);
        _service = new DiscoveryService(_repository, NullLogger<DiscoveryService>.Instance);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task<(DiscoveryRun Run, Finding Finding, FindingRevision Revision)> SeedCandidateAsync()
    {
        var run = await _service.StartRunAsync(
            subject: "Test subject",
            sourceLocator: "/tmp/source",
            sourceRevision: "rev1",
            inclusions: new[] { "*.cbl" },
            exclusions: Array.Empty<string>(),
            evidenceBoundary: "Static source only");

        var evidence = await _service.AppendEvidenceAsync(
            run.RunId, EvidenceType.SourceCode, "source/TEST.cbl", rawExcerpt: "PROCEDURE DIVISION found; 1 paragraph.");

        var (finding, revision) = await _service.AppendCandidateFindingAsync(
            run.RunId, "Program 'TEST' declares 1 paragraph.", new[] { evidence.EvidenceId }, confidence: 0.95, producerVersion: "test-extractor-v1");

        return (run, finding, revision);
    }

    [Fact]
    public async Task AppendEvidenceAsync_RedactsBeforePersistence()
    {
        var run = await _service.StartRunAsync("Subject", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");

        var evidence = await _service.AppendEvidenceAsync(
            run.RunId, EvidenceType.Configuration, "config/app.properties",
            rawExcerpt: "db.password=SuperSecret123");

        evidence.WasRedacted.Should().BeTrue();
        evidence.RedactedExcerpt.Should().NotContain("SuperSecret123");
    }

    [Fact]
    public async Task AppendCandidateFindingAsync_CreatesRevisionOne_WithCandidateStatus()
    {
        var (_, finding, revision) = await SeedCandidateAsync();

        finding.FindingId.Should().NotBeNullOrEmpty();
        revision.RevisionNumber.Should().Be(1);
        revision.Status.Should().Be(ReviewStatus.Candidate);
        revision.FindingRevisionId.Should().Be($"{finding.FindingId}-R1");
    }

    [Fact]
    public async Task AttachLlmAssessmentAsync_TransitionsCandidateToHumanReview_ButNeverPublishes()
    {
        var (_, finding, revision) = await SeedCandidateAsync();

        var assessment = await _service.AttachLlmAssessmentAsync(
            revision.FindingRevisionId, reviewPriority: 0.8, whyExplanation: "High-signal deterministic count.",
            citedEvidenceIds: revision.EvidenceIds, modelId: "test-model-v1");

        assessment.ReviewPriority.Should().Be(0.8);

        var latest = await _repository.GetLatestFindingRevisionAsync(finding.FindingId);
        latest!.Status.Should().Be(ReviewStatus.HumanReview);
        latest.Status.Should().NotBe(ReviewStatus.Published); // LLM output never establishes/publishes a fact
    }

    [Fact]
    public async Task PublishAsync_RequiresRationale()
    {
        var (_, _, revision) = await SeedCandidateAsync();

        var act = async () => await _service.PublishAsync(revision.FindingRevisionId, rationale: "  ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PublishAsync_TransitionsToPublished_AndRecordsReviewerDecision()
    {
        var (_, finding, revision) = await SeedCandidateAsync();

        var decision = await _service.PublishAsync(revision.FindingRevisionId, "Verified against source manually.");

        decision.Decision.Should().Be(ReviewStatus.Published);
        decision.ReviewerIdentity.Should().Be(DiscoveryService.DefaultReviewerIdentity);
        decision.Rationale.Should().Be("Verified against source manually.");

        var latest = await _repository.GetLatestFindingRevisionAsync(finding.FindingId);
        latest!.Status.Should().Be(ReviewStatus.Published);
    }

    [Fact]
    public async Task RejectAsync_TransitionsToRejected()
    {
        var (_, finding, revision) = await SeedCandidateAsync();

        await _service.RejectAsync(revision.FindingRevisionId, "Does not reflect current source.");

        var latest = await _repository.GetLatestFindingRevisionAsync(finding.FindingId);
        latest!.Status.Should().Be(ReviewStatus.Rejected);
    }

    [Fact]
    public async Task RequestEvidenceAsync_TransitionsToNeedsEvidence()
    {
        var (_, finding, revision) = await SeedCandidateAsync();

        await _service.RequestEvidenceAsync(revision.FindingRevisionId, "Need a second source citation.");

        var latest = await _repository.GetLatestFindingRevisionAsync(finding.FindingId);
        latest!.Status.Should().Be(ReviewStatus.NeedsEvidence);
    }

    [Fact]
    public async Task RecordDecision_OnTerminalRevision_Throws_AndRequiresCorrectionInstead()
    {
        var (_, finding, revision) = await SeedCandidateAsync();
        await _service.PublishAsync(revision.FindingRevisionId, "Initial publish.");

        var latest = await _repository.GetLatestFindingRevisionAsync(finding.FindingId);

        var act = async () => await _service.RejectAsync(latest!.FindingRevisionId, "Trying to reject a published fact.");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateCorrectionAsync_SupersedesPublishedRevision_WithoutMutatingHistory()
    {
        var (_, finding, revision) = await SeedCandidateAsync();
        await _service.PublishAsync(revision.FindingRevisionId, "Initial publish.");

        var publishedRevision = await _repository.GetLatestFindingRevisionAsync(finding.FindingId);
        publishedRevision!.Status.Should().Be(ReviewStatus.Published);

        var correction = await _service.CreateCorrectionAsync(
            finding.FindingId,
            newStatement: "Program 'TEST' actually declares 2 paragraphs; correcting prior count.",
            evidenceIds: revision.EvidenceIds,
            confidence: 0.9,
            producerVersion: "test-extractor-v2");

        // The correction is the new latest revision and starts life as a fresh Candidate.
        var latest = await _repository.GetLatestFindingRevisionAsync(finding.FindingId);
        latest!.FindingRevisionId.Should().Be(correction.FindingRevisionId);
        latest.Status.Should().Be(ReviewStatus.Candidate);
        latest.Statement.Should().Contain("2 paragraphs");

        // Original published revision row must remain intact and retrievable -- never overwritten.
        var originalRevisionAfterCorrection = await _repository.GetFindingRevisionAsync(publishedRevision.FindingRevisionId);
        originalRevisionAfterCorrection.Should().NotBeNull();
        originalRevisionAfterCorrection!.Statement.Should().Be(publishedRevision.Statement);

        // The full chain must include the original candidate, the published marker, a superseded
        // marker, and the new correction -- nothing removed, only appended.
        var allRevisions = await _repository.GetFindingRevisionsAsync(finding.FindingId);
        allRevisions.Select(r => r.Status).Should().Contain(new[]
        {
            ReviewStatus.Candidate, ReviewStatus.Published, ReviewStatus.Superseded,
        });
    }

    [Fact]
    public async Task GetReviewQueueAsync_ExcludesPublishedAndRejectedFindings()
    {
        var (run, finding, revision) = await SeedCandidateAsync();
        var queueBeforeDecision = await _service.GetReviewQueueAsync(run.RunId);
        queueBeforeDecision.Should().Contain(r => r.FindingId == finding.FindingId);

        await _service.PublishAsync(revision.FindingRevisionId, "Approved.");

        var queueAfterDecision = await _service.GetReviewQueueAsync(run.RunId);
        queueAfterDecision.Should().NotContain(r => r.FindingId == finding.FindingId);
    }

    [Fact]
    public async Task ClassifyArtifactRolesAsync_AppendsRoleAssignmentsWithEvidenceAndProvenance()
    {
        var run = await _service.StartRunAsync("Subject", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");

        var businessArtifact = await _service.AppendArtifactAsync(run.RunId, "Domain/Calculator.cs", "CSharp", "hash1");
        var controllerArtifact = await _service.AppendArtifactAsync(run.RunId, "Controllers/FooController.cs", "CSharp", "hash2");

        var artifacts = new List<(SourceArtifact, string)>
        {
            (businessArtifact, "namespace App.Domain; public class Calculator { public int Add(int a, int b) => a + b; }"),
            (controllerArtifact, "namespace App.Controllers; [Microsoft.AspNetCore.Mvc.ApiController] public class FooController : Microsoft.AspNetCore.Mvc.ControllerBase { [Microsoft.AspNetCore.Mvc.HttpGet] public string Get() => \"ok\"; }"),
        };

        var assignments = await _service.ClassifyArtifactRolesAsync(run.RunId, artifacts, producerVersion: "test-classifier-v1");

        assignments.Should().HaveCount(2);
        assignments.Should().Contain(a => a.ArtifactId == businessArtifact.ArtifactId && a.Roles.Contains(ArtifactRoleTag.Business));
        assignments.Should().Contain(a => a.ArtifactId == controllerArtifact.ArtifactId && a.Roles.Contains(ArtifactRoleTag.FrameworkAdapter));

        foreach (var assignment in assignments)
        {
            assignment.EvidenceIds.Should().NotBeEmpty();
            assignment.ProvenanceId.Should().NotBeNullOrWhiteSpace();
            assignment.ClassificationRule.Should().NotBeNullOrWhiteSpace();

            var citedEvidence = await _service.GetEvidenceByIdsAsync(assignment.EvidenceIds);
            citedEvidence.Should().HaveCount(assignment.EvidenceIds.Count);
            citedEvidence.Should().OnlyContain(e => e.ArtifactId == assignment.ArtifactId);
        }

        var persisted = await _service.GetRoleAssignmentsAsync(run.RunId);
        persisted.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetBusinessEligibleArtifactsAsync_ExcludesTechnicalMixedAndUnknownRoles()
    {
        var run = await _service.StartRunAsync("Subject", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");

        var businessArtifact = await _service.AppendArtifactAsync(run.RunId, "Domain/Calculator.cs", "CSharp", "h1");
        var controllerArtifact = await _service.AppendArtifactAsync(run.RunId, "Controllers/FooController.cs", "CSharp", "h2");
        var mixedArtifact = await _service.AppendArtifactAsync(run.RunId, "Domain/Employee.cs", "CSharp", "h3");
        var unknownArtifact = await _service.AppendArtifactAsync(run.RunId, "Interfaces/IFoo.cs", "CSharp", "h4");

        var artifacts = new List<(SourceArtifact, string)>
        {
            (businessArtifact, "namespace App.Domain; public class Calculator { public int Add(int a, int b) => a + b; }"),
            (controllerArtifact, "namespace App.Controllers; [Microsoft.AspNetCore.Mvc.ApiController] public class FooController : Microsoft.AspNetCore.Mvc.ControllerBase { [Microsoft.AspNetCore.Mvc.HttpGet] public string Get() => \"ok\"; }"),
            (mixedArtifact, "using Microsoft.EntityFrameworkCore; namespace App.Domain; [Table(\"Employees\")] public class Employee { public int CalculateTenure(System.DateTime hire, System.DateTime now) => now.Year - hire.Year; }"),
            (unknownArtifact, "namespace App.Interfaces; public interface IFoo { System.Threading.Tasks.Task<int> GetAsync(); }"),
        };

        await _service.ClassifyArtifactRolesAsync(run.RunId, artifacts, producerVersion: "test-classifier-v1");

        var eligible = await _service.GetBusinessEligibleArtifactsAsync(run.RunId);

        eligible.Should().ContainSingle(a => a.ArtifactId == businessArtifact.ArtifactId);
        eligible.Should().NotContain(a => a.ArtifactId == controllerArtifact.ArtifactId);
        eligible.Should().NotContain(a => a.ArtifactId == mixedArtifact.ArtifactId);
        eligible.Should().NotContain(a => a.ArtifactId == unknownArtifact.ArtifactId);

        // Mixed/unknown assignments must remain visible in the full governed record set, never
        // silently dropped -- they are only excluded from the LLM-prompt-eligible subset.
        var all = await _service.GetRoleAssignmentsAsync(run.RunId);
        all.Should().HaveCount(4);
    }
}

