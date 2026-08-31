using CobolToQuarkusMigration.Discovery;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
using CobolToQuarkusMigration.Discovery.Risks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

/// <summary>
/// Tests for the phase 7 (issue #5) risk register: deterministic derivation from gaps/conflicts/
/// unconfirmed findings/analyzer limitations, R- identifier stability, idempotent re-derivation,
/// and supersession.
/// </summary>
public sealed class RiskRegisterBuilderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"risk-register-tests-{Guid.NewGuid():N}.db");
    private readonly IDiscoveryRepository _repository;
    private readonly DiscoveryService _service;

    public RiskRegisterBuilderTests()
    {
        _repository = new SqliteDiscoveryRepository(_dbPath, NullLogger<SqliteDiscoveryRepository>.Instance);
        _service = new DiscoveryService(_repository, NullLogger<DiscoveryService>.Instance);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void Build_AlwaysEmitsUseCaseBusinessRuleExtractionGap_WhenNoUseCaseOrRuleFindingsExist()
    {
        var risks = RiskRegisterBuilder.Build(
            contextCandidates: Array.Empty<ContextCandidate>(),
            integrations: Array.Empty<Integration>(),
            roleAssignments: Array.Empty<RoleAssignment>(),
            findings: Array.Empty<Finding>(),
            runId: "RUN-TEST1");

        risks.Should().ContainSingle(r => r.DerivationRule == "UseCaseBusinessRuleExtractionGap");
        var gap = risks.Single(r => r.DerivationRule == "UseCaseBusinessRuleExtractionGap");
        gap.Category.Should().Be(RiskCategory.EvidenceGap);
        gap.Severity.Should().Be(RiskSeverity.High);
        gap.SourceRecordId.Should().Be("RUN-TEST1");
    }

    [Fact]
    public void Build_SuppressesUseCaseBusinessRuleGap_WhenUseCaseShapedFindingExists()
    {
        var finding = new Finding
        {
            FindingId = "F-1",
            RunId = "RUN-TEST1",
            CorrelationKey = "UseCase:ApproveForecast",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var risks = RiskRegisterBuilder.Build(
            Array.Empty<ContextCandidate>(), Array.Empty<Integration>(), Array.Empty<RoleAssignment>(),
            new[] { finding }, "RUN-TEST1");

        risks.Should().NotContain(r => r.DerivationRule == "UseCaseBusinessRuleExtractionGap");
    }

    [Fact]
    public void Build_DerivesUnconfirmedContextRisk_ForNeedsEvidenceContext()
    {
        var context = new ContextCandidate
        {
            ContextCandidateId = "CTX-1",
            RunId = "RUN-TEST1",
            Name = "ForecastApprovals",
            Kind = ContextCandidateKind.BusinessContext,
            Status = ReviewStatus.NeedsEvidence,
            Confidence = 0.4,
            SeedingRule = "test-rule",
            EvidenceIds = new[] { "EV-1" },
            ProvenanceId = "PROV-1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var risks = RiskRegisterBuilder.Build(
            new[] { context }, Array.Empty<Integration>(), Array.Empty<RoleAssignment>(),
            Array.Empty<Finding>(), "RUN-TEST1");

        risks.Should().ContainSingle(r => r.DerivationRule == "UnconfirmedContextCandidate" && r.SourceRecordId == "CTX-1");
    }

    [Fact]
    public void Build_AlsoFlagsGenericNamespaceClusteringLimitation_ForSharedContext()
    {
        var context = new ContextCandidate
        {
            ContextCandidateId = "CTX-2",
            RunId = "RUN-TEST1",
            Name = "Shared",
            Kind = ContextCandidateKind.BusinessContext,
            Status = ReviewStatus.NeedsEvidence,
            Confidence = 0.6,
            SeedingRule = "test-rule",
            EvidenceIds = Array.Empty<string>(),
            ProvenanceId = "PROV-2",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var risks = RiskRegisterBuilder.Build(
            new[] { context }, Array.Empty<Integration>(), Array.Empty<RoleAssignment>(),
            Array.Empty<Finding>(), "RUN-TEST1");

        risks.Should().ContainSingle(r => r.DerivationRule == "GenericNamespaceClusteringLimitation" && r.SourceRecordId == "CTX-2");
    }

    [Fact]
    public void Build_AlwaysEmitsSyntaxTreeOnlyAnalysisLimitation_ScopedToRun()
    {
        var risks = RiskRegisterBuilder.Build(
            Array.Empty<ContextCandidate>(), Array.Empty<Integration>(), Array.Empty<RoleAssignment>(),
            Array.Empty<Finding>(), "RUN-TEST1");

        risks.Should().ContainSingle(r => r.DerivationRule == "SyntaxTreeOnlyAnalysisLimitation" && r.SourceRecordId == "RUN-TEST1");
    }

    [Fact]
    public async Task BuildRiskRegisterAsync_IsIdempotent_OnRepeatedInvocation()
    {
        var run = await _service.StartRunAsync("Subject", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");

        var first = await _service.BuildRiskRegisterAsync(run.RunId, "test-builder-v1");
        first.Should().NotBeEmpty();

        var second = await _service.BuildRiskRegisterAsync(run.RunId, "test-builder-v1");
        second.Should().BeEmpty("re-running for the same run with no new source records must not create duplicates");

        var all = await _service.GetRiskRegisterEntriesAsync(run.RunId);
        all.Should().HaveCount(first.Count);
    }

    [Fact]
    public async Task BuildRiskRegisterAsync_ProducesStable_R_PrefixedIdentifiers()
    {
        var run = await _service.StartRunAsync("Subject", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");

        var risks = await _service.BuildRiskRegisterAsync(run.RunId, "test-builder-v1");

        risks.Should().NotBeEmpty();
        risks.Should().OnlyContain(r => r.RiskId.StartsWith("R-", StringComparison.Ordinal));
        risks.Select(r => r.RiskId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task SupersedingRiskEntry_IsExcludedFromReDerivationIdempotencyCheck()
    {
        var run = await _service.StartRunAsync("Subject", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");
        var risks = await _service.BuildRiskRegisterAsync(run.RunId, "test-builder-v1");
        var original = risks.First();

        // Append-only supersession: the original row is never mutated or re-inserted. A new row
        // with a new RiskId links back via SupersedesRiskId, mirroring the pattern used elsewhere
        // in Discovery Factory (e.g. FindingRevision.SupersedesRevisionId).
        var newEntry = new RiskRegisterEntry
        {
            RiskId = "R-SUPERSEDER",
            RunId = original.RunId,
            Title = original.Title + " (revised)",
            Category = original.Category,
            Severity = RiskSeverity.Low,
            Status = RiskStatus.Open,
            Description = "Revised description after human review.",
            EscalationQuestion = original.EscalationQuestion,
            DerivationRule = original.DerivationRule,
            SourceRecordId = original.SourceRecordId,
            EvidenceIds = original.EvidenceIds,
            ProvenanceId = original.ProvenanceId,
            SupersedesRiskId = original.RiskId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repository.AppendRiskRegisterEntryAsync(newEntry);

        // Re-running derivation must not resurrect the original (DerivationRule, SourceRecordId)
        // pair because the new open row already covers it — the natural-key check in
        // BuildRiskRegisterAsync only excludes Superseded rows, and the new row is Open.
        var rebuilt = await _service.BuildRiskRegisterAsync(run.RunId, "test-builder-v1");
        rebuilt.Should().NotContain(r => r.DerivationRule == original.DerivationRule && r.SourceRecordId == original.SourceRecordId);

        var all = await _service.GetRiskRegisterEntriesAsync(run.RunId);
        all.Should().Contain(r => r.RiskId == "R-SUPERSEDER" && r.SupersedesRiskId == original.RiskId);
    }
}
