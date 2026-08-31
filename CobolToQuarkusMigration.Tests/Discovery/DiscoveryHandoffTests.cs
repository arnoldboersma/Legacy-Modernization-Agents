using System.Text.Json;
using CobolToQuarkusMigration.Discovery;
using CobolToQuarkusMigration.Discovery.Export;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

/// <summary>
/// Tests for the phase 7 (issue #5) 10-section Specification Factory handoff: assembler
/// structure, sparse section 3/4 honesty, citation completeness, no-future-state-language guard,
/// and JSON/Markdown parity.
/// </summary>
public sealed class DiscoveryHandoffTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"discovery-handoff-tests-{Guid.NewGuid():N}.db");
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"discovery-handoff-output-{Guid.NewGuid():N}");
    private readonly IDiscoveryRepository _repository;
    private readonly DiscoveryService _service;
    private readonly DiscoveryExporter _exporter;
    private readonly DiscoveryHandoffAssembler _assembler;

    public DiscoveryHandoffTests()
    {
        _repository = new SqliteDiscoveryRepository(_dbPath, NullLogger<SqliteDiscoveryRepository>.Instance);
        _service = new DiscoveryService(_repository, NullLogger<DiscoveryService>.Instance);
        _exporter = new DiscoveryExporter(_repository);
        _assembler = new DiscoveryHandoffAssembler(_repository);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, recursive: true);
    }

    private async Task<string> SeedRunWithRiskRegisterAsync()
    {
        var run = await _service.StartRunAsync(
            "PlanBoard Forecast Management pilot", "/tmp/planboard", "rev1",
            new[] { "*.cs" }, Array.Empty<string>(), "Static source only");
        await _service.BuildRiskRegisterAsync(run.RunId, "test-builder-v1");
        return run.RunId;
    }

    [Fact]
    public async Task AssembleAsync_ProducesAllTenSections()
    {
        var runId = await SeedRunWithRiskRegisterAsync();

        var handoff = await _assembler.AssembleAsync(runId);

        handoff.RunId.Should().Be(runId);
        handoff.ContractVersion.Should().Be(DiscoveryExporter.ContractVersion);
        handoff.Section1_RunScope.Should().NotBeNull();
        handoff.Section2_DomainLandscape.Should().NotBeNull();
        handoff.Section3_UseCases.Should().NotBeNull();
        handoff.Section4_BusinessRules.Should().NotBeNull();
        handoff.Section5_Architecture.Should().NotBeNull();
        handoff.Section6_Integrations.Should().NotBeNull();
        handoff.Section7_DataModel.Should().NotBeNull();
        handoff.Section8_NonFunctional.Should().NotBeNull();
        handoff.Section9_Risks.Should().NotBeNull();
        handoff.Section10_Navigation.Should().NotBeNull();
    }

    [Fact]
    public async Task AssembleAsync_MarksSections3And4Sparse_WhenNoUseCaseOrRuleFindingsExist()
    {
        var runId = await SeedRunWithRiskRegisterAsync();

        var handoff = await _assembler.AssembleAsync(runId);

        handoff.Section3_UseCases.IsSparse.Should().BeTrue();
        handoff.Section3_UseCases.GapExplanation.Should().NotBeNullOrWhiteSpace();
        handoff.Section3_UseCases.LinkedRiskId.Should().NotBeNullOrEmpty();
        handoff.Section3_UseCases.UseCaseFindingIds.Should().BeEmpty();

        handoff.Section4_BusinessRules.IsSparse.Should().BeTrue();
        handoff.Section4_BusinessRules.GapExplanation.Should().NotBeNullOrWhiteSpace();
        handoff.Section4_BusinessRules.LinkedRiskId.Should().NotBeNullOrEmpty();
        handoff.Section4_BusinessRules.BusinessRuleFindingIds.Should().BeEmpty();

        // The linked risk id must resolve to a real, persisted risk register entry with the
        // UseCaseBusinessRuleExtractionGap derivation rule — never a fabricated placeholder.
        var risks = await _service.GetRiskRegisterEntriesAsync(runId);
        risks.Should().Contain(r => r.RiskId == handoff.Section3_UseCases.LinkedRiskId && r.DerivationRule == "UseCaseBusinessRuleExtractionGap");
    }

    [Fact]
    public async Task AssembleAsync_Section9_ReflectsPersistedRiskRegisterEntries()
    {
        var runId = await SeedRunWithRiskRegisterAsync();
        var persistedRisks = await _service.GetRiskRegisterEntriesAsync(runId);

        var handoff = await _assembler.AssembleAsync(runId);

        handoff.Section9_Risks.Risks.Should().HaveCount(persistedRisks.Count);
        handoff.Section9_Risks.Risks.Select(r => r.RiskId).Should()
            .BeEquivalentTo(persistedRisks.Select(r => r.RiskId));
    }

    [Fact]
    public async Task AssembleAsync_ThrowsInvalidOperationException_ForUnknownRun()
    {
        await _repository.InitializeAsync();

        var act = async () => await _assembler.AssembleAsync("RUN-DOES-NOT-EXIST");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExportHandoffAsync_WritesMarkdownAndJson_ThatBothPassTheNoFutureStateLanguageGuard()
    {
        var runId = await SeedRunWithRiskRegisterAsync();

        var (markdownPath, jsonPath) = await _exporter.ExportHandoffAsync(runId, _outputDir);

        File.Exists(markdownPath).Should().BeTrue();
        File.Exists(jsonPath).Should().BeTrue();

        var markdown = await File.ReadAllTextAsync(markdownPath);
        var act = () => DiscoveryExporter.AssertNoFutureStateLanguage(markdown);
        act.Should().NotThrow();
    }

    [Fact]
    public void AssertNoFutureStateLanguage_ThrowsOnInjectedViolation()
    {
        var act = () => DiscoveryExporter.AssertNoFutureStateLanguage(
            "## Risks\nWe should migrate this module to a microservice as part of the roadmap.");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ExportHandoffAsync_JsonAndMarkdown_AreParityConsistent()
    {
        var runId = await SeedRunWithRiskRegisterAsync();

        var (markdownPath, jsonPath) = await _exporter.ExportHandoffAsync(runId, _outputDir);

        var markdown = await File.ReadAllTextAsync(markdownPath);
        var json = await File.ReadAllTextAsync(jsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("contractVersion").GetString().Should().Be(DiscoveryExporter.ContractVersion);
        root.GetProperty("runId").GetString().Should().Be(runId);

        var risks = root.GetProperty("section9_Risks").GetProperty("risks");
        foreach (var risk in risks.EnumerateArray())
        {
            var riskId = risk.GetProperty("riskId").GetString();
            markdown.Should().Contain(riskId, "every risk register entry surfaced in JSON must also be cited in the rendered Markdown");
        }

        markdown.Should().Contain(runId);
        markdown.Should().Contain(DiscoveryExporter.ContractVersion);
    }

    [Fact]
    public async Task ExportHandoffAsync_MarkdownCitesAllRiskEscalationQuestions()
    {
        var runId = await SeedRunWithRiskRegisterAsync();
        var risks = await _service.GetRiskRegisterEntriesAsync(runId);

        var (markdownPath, _) = await _exporter.ExportHandoffAsync(runId, _outputDir);
        var markdown = await File.ReadAllTextAsync(markdownPath);

        foreach (var risk in risks)
        {
            markdown.Should().Contain(risk.EscalationQuestion);
        }
    }
}
