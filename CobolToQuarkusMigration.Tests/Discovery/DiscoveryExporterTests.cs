using System.Text.Json;
using CobolToQuarkusMigration.Discovery;
using CobolToQuarkusMigration.Discovery.Export;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

public sealed class DiscoveryExporterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"discovery-exporter-tests-{Guid.NewGuid():N}.db");
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"discovery-exporter-output-{Guid.NewGuid():N}");
    private readonly IDiscoveryRepository _repository;
    private readonly DiscoveryService _service;
    private readonly DiscoveryExporter _exporter;

    public DiscoveryExporterTests()
    {
        _repository = new SqliteDiscoveryRepository(_dbPath, NullLogger<SqliteDiscoveryRepository>.Instance);
        _service = new DiscoveryService(_repository, NullLogger<DiscoveryService>.Instance);
        _exporter = new DiscoveryExporter(_repository);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, recursive: true);
    }

    [Fact]
    public async Task ExportFindingAsync_RejectsCandidateRevisions()
    {
        var run = await _service.StartRunAsync("Subject", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");
        var evidence = await _service.AppendEvidenceAsync(run.RunId, EvidenceType.SourceCode, "source/TEST.cbl", rawExcerpt: "excerpt");
        var (_, revision) = await _service.AppendCandidateFindingAsync(run.RunId, "Statement", new[] { evidence.EvidenceId }, 0.9, "extractor-v1");

        var act = async () => await _exporter.ExportFindingAsync(revision.FindingRevisionId, _outputDir);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExportFindingAsync_ForPublishedFinding_WritesMarkdownAndJson_WithStableIdsAndCitations()
    {
        var run = await _service.StartRunAsync("Subject", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");
        var evidence = await _service.AppendEvidenceAsync(run.RunId, EvidenceType.SourceCode, "source/TEST.cbl", rawExcerpt: "PROCEDURE DIVISION found; 2 paragraphs.");
        var (finding, revision) = await _service.AppendCandidateFindingAsync(run.RunId, "Program 'TEST' declares 2 paragraphs.", new[] { evidence.EvidenceId }, 0.95, "extractor-v1");
        await _service.PublishAsync(revision.FindingRevisionId, "Verified manually against source.");

        var publishedRevision = await _repository.GetLatestFindingRevisionAsync(finding.FindingId);

        var (markdownPath, jsonPath) = await _exporter.ExportFindingAsync(publishedRevision!.FindingRevisionId, _outputDir);

        File.Exists(markdownPath).Should().BeTrue();
        File.Exists(jsonPath).Should().BeTrue();

        var markdown = await File.ReadAllTextAsync(markdownPath);
        markdown.Should().Contain(finding.FindingId);
        markdown.Should().Contain(publishedRevision.FindingRevisionId);
        markdown.Should().Contain(evidence.EvidenceId);
        markdown.Should().Contain("Published");
        markdown.Should().Contain("0.95"); // confidence must render culture-invariant, not "0,95"
        markdown.Should().Contain("Verified manually against source."); // review history included

        var json = await File.ReadAllTextAsync(jsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("contractVersion").GetString().Should().Be(DiscoveryExporter.ContractVersion);
        root.GetProperty("findingId").GetString().Should().Be(finding.FindingId);
        root.GetProperty("findingRevisionId").GetString().Should().Be(publishedRevision.FindingRevisionId);
        root.GetProperty("status").GetString().Should().Be("Published");
        root.GetProperty("confidence").GetDouble().Should().Be(0.95);
        root.GetProperty("evidence").EnumerateArray().Should().ContainSingle(e => e.GetProperty("evidenceId").GetString() == evidence.EvidenceId);
        root.GetProperty("reviewDecisions").EnumerateArray().Should().ContainSingle(d => d.GetProperty("rationale").GetString() == "Verified manually against source.");
    }

    [Fact]
    public async Task ExportFindingAsync_ForRejectedFinding_IsAllowed_AndReflectsRejectedStatus()
    {
        var run = await _service.StartRunAsync("Subject", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");
        var evidence = await _service.AppendEvidenceAsync(run.RunId, EvidenceType.SourceCode, "source/TEST.cbl", rawExcerpt: "excerpt");
        var (finding, revision) = await _service.AppendCandidateFindingAsync(run.RunId, "Statement", new[] { evidence.EvidenceId }, 0.9, "extractor-v1");
        await _service.RejectAsync(revision.FindingRevisionId, "Not reproducible.");

        var latest = await _repository.GetLatestFindingRevisionAsync(finding.FindingId);
        var (_, jsonPath) = await _exporter.ExportFindingAsync(latest!.FindingRevisionId, _outputDir);

        var json = await File.ReadAllTextAsync(jsonPath);
        json.Should().Contain("\"status\": \"Rejected\"");
    }

    [Fact]
    public async Task ExportIntegrationInventoryAsync_GroupsByClassification_AndIncludesAllRequiredFields()
    {
        var run = await _service.StartRunAsync("PlanBoard pilot", "/tmp", "rev1", Array.Empty<string>(), Array.Empty<string>(), "Static source only");
        var artifact = await _service.AppendArtifactAsync(run.RunId, "Data/ApplicationContext.cs", "CSharp", "h1");

        var runtimeCandidate = new CobolToQuarkusMigration.Discovery.Integrations.IntegrationCandidate(
            IntegrationCategory.DatabaseOrSharedStore, IntegrationClassification.RuntimeApplication, IntegrationDirection.Outbound,
            "ApplicationContext", "EF Core / SQL Server", "ApplicationContext", null, null, null, null,
            0.9, "EfDbContextBaseType", Array.Empty<string>(), false, artifact.Path,
            new[] { new CobolToQuarkusMigration.Discovery.Integrations.IntegrationEvidenceCitation("EfDbContextBaseType", "Data/ApplicationContext.cs:L1", "DbContext base type.") });

        var platformCandidate = new CobolToQuarkusMigration.Discovery.Integrations.IntegrationCandidate(
            IntegrationCategory.IdentityOrAuthorization, IntegrationClassification.PlatformIdentity, IntegrationDirection.Outbound,
            "Data/ApplicationContext.cs", "Microsoft.Graph", "Microsoft.Graph", null, null, "Microsoft Entra ID / Microsoft Graph.", null,
            0.85, "IdentitySdkUsingDirective", Array.Empty<string>(), false, artifact.Path,
            new[] { new CobolToQuarkusMigration.Discovery.Integrations.IntegrationEvidenceCitation("IdentitySdkUsingDirective", "Data/ApplicationContext.cs:L1", "'using Microsoft.Graph;' declared.") });

        await _service.AppendIntegrationsAsync(
            run.RunId,
            new[] { (runtimeCandidate, (string?)artifact.ArtifactId), (platformCandidate, (string?)artifact.ArtifactId) },
            producerVersion: "test-integration-classifier-v1");

        var jsonPath = await _exporter.ExportIntegrationInventoryAsync(run.RunId, _outputDir);
        var json = await File.ReadAllTextAsync(jsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("totalIntegrations").GetInt32().Should().Be(2);
        var byClassification = root.GetProperty("byClassification");
        byClassification.TryGetProperty("RuntimeApplication", out var runtimeArray).Should().BeTrue();
        byClassification.TryGetProperty("PlatformIdentity", out var platformArray).Should().BeTrue();
        runtimeArray.GetArrayLength().Should().Be(1);
        platformArray.GetArrayLength().Should().Be(1);

        var runtimeEntry = runtimeArray[0];
        runtimeEntry.GetProperty("category").GetString().Should().Be("DatabaseOrSharedStore");
        runtimeEntry.GetProperty("requiresReview").GetBoolean().Should().BeFalse();
        runtimeEntry.GetProperty("evidenceIds").GetArrayLength().Should().BeGreaterThan(0);

        var platformEntry = platformArray[0];
        platformEntry.GetProperty("authenticationSemantics").GetString().Should().Contain("Entra");
    }
}
