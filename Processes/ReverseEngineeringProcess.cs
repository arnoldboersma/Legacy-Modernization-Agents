using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CobolToQuarkusMigration.Helpers;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.Persistence;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.Processes;

/// <summary>Orchestrates discovery, analysis, business extraction, and dependency mapping for one source language.</summary>
public class ReverseEngineeringProcess
{
    private readonly ISourceDiscovery _sourceDiscovery;
    private readonly ISourceAnalyzer _sourceAnalyzer;
    private readonly ISourceBusinessLogicExtractor _businessLogicExtractor;
    private readonly IDependencyAnalyzer _dependencyAnalyzer;
    private readonly ISourceAnalysisReportFormatter _reportFormatter;
    private readonly ILogger<ReverseEngineeringProcess> _logger;
    private readonly EnhancedLogger _enhancedLogger;
    private readonly IMigrationRepository? _migrationRepository;
    private readonly ISourceFilePersistence? _sourceFilePersistence;
    private Glossary? _glossary;

    public ReverseEngineeringProcess(
        ISourceDiscovery sourceDiscovery,
        ISourceAnalyzer sourceAnalyzer,
        ISourceBusinessLogicExtractor businessLogicExtractor,
        IDependencyAnalyzer dependencyAnalyzer,
        ISourceAnalysisReportFormatter reportFormatter,
        ILogger<ReverseEngineeringProcess> logger,
        EnhancedLogger enhancedLogger,
        IMigrationRepository? migrationRepository = null,
        ISourceFilePersistence? sourceFilePersistence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDiscovery.Language.ToString());
        if (sourceDiscovery.Language != sourceAnalyzer.Language ||
            sourceDiscovery.Language != businessLogicExtractor.Language ||
            sourceDiscovery.Language != dependencyAnalyzer.Language)
            throw new ArgumentException("All source-analysis services must target the same language.");

        _sourceDiscovery = sourceDiscovery;
        _sourceAnalyzer = sourceAnalyzer;
        _businessLogicExtractor = businessLogicExtractor;
        _dependencyAnalyzer = dependencyAnalyzer;
        _reportFormatter = reportFormatter;
        _logger = logger;
        _enhancedLogger = enhancedLogger;
        _migrationRepository = migrationRepository;
        _sourceFilePersistence = sourceFilePersistence;
    }

    public async Task<ReverseEngineeringResult> RunAsync(
        string sourceFolder,
        string outputFolder,
        Action<string, int, int>? progressCallback = null,
        int? existingRunId = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ReverseEngineeringResult();
        try
        {
            _enhancedLogger.ShowSectionHeader("REVERSE ENGINEERING PROCESS", "Extracting Business Logic and Technical Details");
            _logger.LogInformation("Starting {Language} reverse engineering. Source: {SourceFolder}; output: {OutputFolder}",
                _sourceDiscovery.Language, sourceFolder, outputFolder);
            var runId = existingRunId ?? 0;
            if (!existingRunId.HasValue && _migrationRepository is not null)
                runId = await _migrationRepository.StartRunAsync(sourceFolder, outputFolder, cancellationToken);

            await LoadGlossaryAsync(cancellationToken);
            const int totalSteps = 4;

            _enhancedLogger.ShowStep(1, totalSteps, "File Discovery", $"Scanning for {_sourceDiscovery.Language} files");
            progressCallback?.Invoke("Discovering source files", 1, totalSteps);
            var sourceFiles = await _sourceDiscovery.DiscoverAsync(sourceFolder, cancellationToken);
            result.TotalFilesAnalyzed = sourceFiles.Count;
            if (sourceFiles.Count == 0)
            {
                _enhancedLogger.ShowWarning($"No {_sourceDiscovery.Language} files found. Nothing to reverse engineer.");
                return result;
            }
            if (_sourceFilePersistence is not null && runId > 0)
                await _sourceFilePersistence.SaveSourceFilesAsync(runId, sourceFiles, cancellationToken);

            _enhancedLogger.ShowStep(2, totalSteps, "Technical Analysis", "Running deterministic source analysis");
            progressCallback?.Invoke("Analyzing source structure", 2, totalSteps);
            var analyses = await _sourceAnalyzer.AnalyzeAsync(
                sourceFiles,
                (processed, total) => _enhancedLogger.ShowProgressBar(processed, total, "files analyzed"),
                cancellationToken);
            result.TechnicalAnalyses = analyses.ToList();

            _enhancedLogger.ShowStep(3, totalSteps, "Business Logic Extraction", "Extracting feature descriptions and use cases");
            progressCallback?.Invoke("Extracting business logic", 3, totalSteps);
            var businessLogic = await _businessLogicExtractor.ExtractAsync(
                sourceFiles,
                analyses,
                _glossary,
                (processed, total) => _enhancedLogger.ShowProgressBar(processed, total, "files processed"),
                cancellationToken);
            result.BusinessLogicExtracts = businessLogic.ToList();
            result.TotalUserStories = businessLogic.Sum(item => item.UserStories.Count);
            result.TotalFeatures = businessLogic.Sum(item => item.Features.Count);
            result.TotalBusinessRules = businessLogic.Sum(item => item.BusinessRules.Count);

            _enhancedLogger.ShowStep(4, totalSteps, "Dependency Mapping", "Mapping statically detectable dependencies");
            progressCallback?.Invoke("Mapping dependencies", 4, totalSteps);
            var dependencyMap = await _dependencyAnalyzer.AnalyzeAsync(sourceFiles, analyses, cancellationToken);
            result.DependencyMap = dependencyMap;
            _enhancedLogger.ShowSuccess($"Dependency analysis complete - {dependencyMap.Dependencies.Count} relationships found");

            if (_migrationRepository is not null && runId > 0)
            {
                await _migrationRepository.SaveBusinessLogicAsync(runId, businessLogic, cancellationToken);
                await _migrationRepository.SaveDependencyMapAsync(runId, dependencyMap, cancellationToken);
                result.RunId = runId;
            }

            await GenerateOutputAsync(outputFolder, result, cancellationToken);
            if (_migrationRepository is not null && runId > 0 && !existingRunId.HasValue)
                await _migrationRepository.CompleteRunAsync(runId, "Completed", "Reverse Engineering Only", cancellationToken);

            result.Success = true;
            result.OutputFolder = outputFolder;
            _enhancedLogger.ShowSuccess("Reverse engineering complete!");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during reverse engineering process");
            _enhancedLogger.ShowError($"Reverse engineering failed: {ex.Message}");
            result.ErrorMessage = ex.Message;
            throw;
        }
    }

    private async Task LoadGlossaryAsync(CancellationToken cancellationToken)
    {
        var glossaryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "glossary.json");
        if (!File.Exists(glossaryPath))
            return;

        await using var stream = File.OpenRead(glossaryPath);
        _glossary = await JsonSerializer.DeserializeAsync<Glossary>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);
    }

    private async Task GenerateOutputAsync(string outputFolder, ReverseEngineeringResult result, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolder);
        await File.WriteAllTextAsync(
            Path.Combine(outputFolder, "reverse-engineering-details.md"),
            GenerateReverseEngineeringDetailsMarkdown(result),
            cancellationToken);
        if (result.DependencyMap is not null)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            await File.WriteAllTextAsync(Path.Combine(outputFolder, "dependency-map.json"),
                JsonSerializer.Serialize(result.DependencyMap, options), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outputFolder, "dependency-diagram.md"),
                $"# {(_sourceDiscovery.Language == SourceLanguage.Cobol ? "COBOL" : "Source")} Dependency Diagram\n\n```mermaid\n{result.DependencyMap.MermaidDiagram}\n```", cancellationToken);
        }
    }

    private string GenerateReverseEngineeringDetailsMarkdown(ReverseEngineeringResult result)
    {
        var builder = new StringBuilder();
        var programCount = result.BusinessLogicExtracts.Count(item => !item.IsCopybook);
        var supportFileCount = result.BusinessLogicExtracts.Count(item => item.IsCopybook);
        builder.AppendLine("# Reverse Engineering Details");
        builder.AppendLine();
        builder.AppendLine($"**Generated**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var supportFileLabel = _sourceDiscovery.Language == SourceLanguage.Cobol ? "copybooks" : "support files";
        builder.AppendLine($"**Total Files Analyzed**: {result.TotalFilesAnalyzed} ({programCount} programs, {supportFileCount} {supportFileLabel})");
        BusinessLogicMarkdownFormatter.AppendTotals(builder, result.TotalUserStories, result.TotalFeatures, result.TotalBusinessRules);
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("## Business Logic");
        builder.AppendLine();
        foreach (var businessLogic in result.BusinessLogicExtracts)
        {
            var fileTypeLabel = businessLogic.IsCopybook ? " [Copybook]" : "";
            builder.AppendLine($"## {businessLogic.FileName}{fileTypeLabel}");
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(businessLogic.BusinessPurpose))
            {
                builder.AppendLine("### Business Purpose");
                builder.AppendLine(businessLogic.BusinessPurpose);
                builder.AppendLine();
            }
            BusinessLogicMarkdownFormatter.AppendUserStories(builder, businessLogic);
            if (businessLogic.Features.Any())
            {
                builder.AppendLine("### Features");
                builder.AppendLine();
                foreach (var feature in businessLogic.Features)
                {
                    builder.AppendLine($"#### {feature.Id}: {feature.Name}");
                    builder.AppendLine();
                    if (!string.IsNullOrWhiteSpace(feature.Description))
                        builder.AppendLine($"**Description:** {feature.Description}\n");
                    if (feature.BusinessRules.Any())
                        builder.AppendLine($"**Business Rules:**\n{string.Join("\n", feature.BusinessRules.Select(rule => $"- {rule}"))}\n");
                    if (feature.Inputs.Any())
                        builder.AppendLine($"**Inputs:**\n{string.Join("\n", feature.Inputs.Select(input => $"- {input}"))}\n");
                    if (feature.Outputs.Any())
                        builder.AppendLine($"**Outputs:**\n{string.Join("\n", feature.Outputs.Select(output => $"- {output}"))}\n");
                    if (feature.ProcessingSteps.Any())
                    {
                        builder.AppendLine("**Processing Steps:**");
                        for (var index = 0; index < feature.ProcessingSteps.Count; index++)
                            builder.AppendLine($"{index + 1}. {feature.ProcessingSteps[index]}");
                        builder.AppendLine();
                    }
                    if (!string.IsNullOrWhiteSpace(feature.SourceLocation))
                        builder.AppendLine($"*Source: {feature.SourceLocation}*\n");
                }
            }
            BusinessLogicMarkdownFormatter.AppendBusinessRules(builder, businessLogic);
            builder.AppendLine("---");
            builder.AppendLine();
        }
        _reportFormatter.AppendTechnicalAnalysis(builder, result.TechnicalAnalyses);
        return builder.ToString();
    }
}

public class ReverseEngineeringResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public int RunId { get; set; }
    public int TotalFilesAnalyzed { get; set; }
    public int TotalUserStories { get; set; }
    public int TotalFeatures { get; set; }
    public int TotalBusinessRules { get; set; }
    public int TotalModernizationOpportunities { get; set; }
    public List<Models.SourceAnalysis> TechnicalAnalyses { get; set; } = new();
    public List<BusinessLogic> BusinessLogicExtracts { get; set; } = new();
    public DependencyMap? DependencyMap { get; set; }
}
