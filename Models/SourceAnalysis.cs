namespace CobolToQuarkusMigration.Models;

/// <summary>Language-neutral, deterministic or agent-enriched source analysis.</summary>
public class SourceAnalysis
{
    public SourceLanguage Language { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool IsSupportFile { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<SourceFact> Facts { get; set; } = new();
    public List<SourceDependency> Dependencies { get; set; } = new();
}

/// <summary>A statically established source-level fact.</summary>
public sealed class SourceFact
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Container { get; set; }
    public string? Detail { get; set; }
    public int LineNumber { get; set; }
}

/// <summary>A relationship discovered from source or build metadata.</summary>
public sealed class SourceDependency
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string Context { get; set; } = string.Empty;
}
