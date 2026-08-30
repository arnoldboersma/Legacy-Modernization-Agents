namespace CobolToQuarkusMigration.Models;

/// <summary>
/// Represents a COBOL source file or copybook.
/// </summary>
public class CobolFile : SourceFile
{
    public CobolFile() => Language = SourceLanguage.Cobol;

    /// <summary>
    /// Gets or sets whether this file is a copybook.
    /// </summary>
    public bool IsCopybook
    {
        get => IsSupportFile;
        set => IsSupportFile = value;
    }
}
