using System.Text;
using CobolToQuarkusMigration.Agents;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.SourceAnalysis.Interfaces;

namespace CobolToQuarkusMigration.SourceAnalysis.CSharp;

/// <summary>
/// Uses deterministic Roslyn facts as grounded context for optional C# business-logic documentation.
/// </summary>
public sealed class CSharpBusinessLogicExtractor(
    BusinessLogicExtractorAgent extractor,
    int maxParallelAnalysis = 4) : ISourceBusinessLogicExtractor
{
    private const string SystemPrompt = """
        Extract business logic from C# application source. Treat the supplied deterministic
        Roslyn facts as authoritative. Document only behavior directly supported by that
        source or those facts; do not infer undocumented business rules from framework names.

        Return only Markdown using this exact structure:

        ## Business Purpose
        [One or two paragraphs describing the business function.]

        ## Use Cases
        ### Use Case 1: [Operation name]
        **Trigger:** [What initiates the operation]
        **Description:** [What the operation does]
        **Benefit:** [Business outcome]
        **Key Steps:**
        1. [Business step]

        ## Business Rules
        ### Rule 1: [Short rule name]
        **Condition:** [When the rule applies]
        **Action:** [Required outcome]
        **Source:** [FileName:StartLine-EndLine — brief domain-neutral locator]

        Use `None identified.` under a section when the source contains no applicable items.
        """;

    public SourceLanguage Language => SourceLanguage.CSharp;

    public async Task<IReadOnlyList<BusinessLogic>> ExtractAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        IReadOnlyList<Models.SourceAnalysis> analyses,
        Glossary? glossary,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceFiles.Any(file => file.Language != Language) ||
            analyses.Any(analysis => analysis.Language != Language))
            throw new ArgumentException("C# business logic extraction requires C# source files and analyses.");
        var documentableSourceFiles = sourceFiles.Where(IsDocumentable).ToList();
        if (documentableSourceFiles.Count == 0)
            return [];

        var analysesByPath = analyses
            .Where(analysis => !string.IsNullOrEmpty(analysis.FilePath))
            .GroupBy(analysis => Path.GetFullPath(analysis.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var maxParallel = Math.Clamp(maxParallelAnalysis, 1, documentableSourceFiles.Count);
        using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var completed = 0;
        var tasks = documentableSourceFiles.Select(async (sourceFile, index) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                analysesByPath.TryGetValue(Path.GetFullPath(sourceFile.FilePath), out var analysis);
                var response = await extractor.ExtractBusinessLogicAsync(
                    sourceFile,
                    SystemPrompt,
                    CreateUserPrompt(sourceFile, analysis, glossary));
                progressCallback?.Invoke(Interlocked.Increment(ref completed), documentableSourceFiles.Count);
                return (index, response);
            }
            finally
            {
                semaphore.Release();
            }
        });

        return (await Task.WhenAll(tasks))
            .OrderBy(result => result.index)
            .Select(result => result.response)
            .ToList();
    }

    internal static bool IsDocumentable(SourceFile sourceFile)
    {
        if (sourceFile.FileName is "GlobalUsings.cs" ||
            sourceFile.FileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
            sourceFile.FileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            sourceFile.FileName.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
            sourceFile.ProjectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
            return false;

        return !sourceFile.FilePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("Test", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals("Tests", StringComparison.OrdinalIgnoreCase));
    }

    internal static string CreateUserPrompt(SourceFile sourceFile, Models.SourceAnalysis? analysis, Glossary? glossary)
    {
        var deterministicFacts = analysis?.Facts.Count > 0
            ? string.Join('\n', analysis.Facts.Select(fact =>
                $"- Line {fact.LineNumber}: {fact.Kind} {fact.Name}" +
                (string.IsNullOrWhiteSpace(fact.Detail) ? string.Empty : $" ({fact.Detail})")))
            : "None.";
        var deterministicDependencies = analysis?.Dependencies.Count > 0
            ? string.Join('\n', analysis.Dependencies.Select(dependency =>
                $"- Line {dependency.LineNumber}: {dependency.Source} {dependency.Kind} {dependency.Target}"))
            : "None.";
        var glossaryContext = glossary?.Terms?.Any() == true
            ? string.Join('\n', glossary.Terms.Select(term => $"- {term.Term} = {term.Translation}"))
            : "None.";

        return new StringBuilder()
            .AppendLine("## Source File")
            .AppendLine(sourceFile.FileName)
            .AppendLine()
            .AppendLine("## Deterministic Roslyn Facts")
            .AppendLine(deterministicFacts)
            .AppendLine()
            .AppendLine("## Deterministic Roslyn Relationships")
            .AppendLine(deterministicDependencies)
            .AppendLine()
            .AppendLine("## Glossary Context")
            .AppendLine(glossaryContext)
            .AppendLine()
            .AppendLine("## C# Source")
            .AppendLine("The numeric prefixes are source line references, not C# syntax.")
            .AppendLine("```csharp")
            .AppendLine(BusinessLogicExtractorAgent.AddSourceLineNumbers(sourceFile.Content))
            .AppendLine("```")
            .ToString();
    }
}
