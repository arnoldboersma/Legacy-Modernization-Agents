using System.Text;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.CSharp;

public sealed class CSharpDependencyAnalyzer : IDependencyAnalyzer
{
    public SourceLanguage Language => SourceLanguage.CSharp;

    public Task<DependencyMap> AnalyzeAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        IReadOnlyList<Models.SourceAnalysis> analyses,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dependencies = analyses
            .SelectMany(analysis => analysis.Dependencies)
            .GroupBy(dependency => (dependency.Source, dependency.Target, dependency.Kind, dependency.LineNumber, dependency.Context))
            .Select(group => group.First())
            .Select(dependency => new DependencyRelationship
            {
                SourceFile = dependency.Source,
                TargetFile = dependency.Target,
                DependencyType = dependency.Kind,
                LineNumber = dependency.LineNumber,
                Context = dependency.Context
            })
            .ToList();

        var sourceNodes = sourceFiles.Select(file => file.FileName)
            .Concat(dependencies.Select(dependency => dependency.SourceFile))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var map = new DependencyMap
        {
            Dependencies = dependencies,
            Metrics = new DependencyMetrics
            {
                TotalPrograms = sourceNodes.Count,
                TotalDependencies = dependencies.Count,
                AverageDependenciesPerProgram = sourceNodes.Count == 0 ? 0 : (double)dependencies.Count / sourceNodes.Count
            }
        };
        map.MermaidDiagram = BuildMermaid(map.Dependencies);
        return Task.FromResult(map);
    }

    private static string BuildMermaid(IEnumerable<DependencyRelationship> dependencies)
    {
        var builder = new StringBuilder("flowchart LR\n");
        var identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        var nextId = 0;
        foreach (var dependency in dependencies)
        {
            var source = GetIdentifier(dependency.SourceFile);
            var target = GetIdentifier(dependency.TargetFile);
            builder.AppendLine($"    {source}[\"{Escape(dependency.SourceFile)}\"] -->|{Escape(dependency.DependencyType)}| {target}[\"{Escape(dependency.TargetFile)}\"]");
        }
        return builder.ToString();

        string GetIdentifier(string value)
        {
            if (!identifiers.TryGetValue(value, out var identifier))
            {
                identifier = $"n{nextId++}";
                identifiers[value] = identifier;
            }
            return identifier;
        }
    }

    private static string Escape(string value) => value.Replace("\"", "'");
}
