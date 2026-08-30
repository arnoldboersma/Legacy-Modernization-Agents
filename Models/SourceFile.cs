namespace CobolToQuarkusMigration.Models;

/// <summary>Language-neutral representation of a discovered source artifact.</summary>
public class SourceFile
{
    public SourceLanguage Language { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsSupportFile { get; set; }
    public string ProjectName { get; set; } = string.Empty;
}
