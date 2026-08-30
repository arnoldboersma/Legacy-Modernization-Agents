namespace CobolToQuarkusMigration.Discovery.Models;

/// <summary>
/// Records how a record (finding, evidence, artifact) was produced: model, prompt, tool, and
/// configuration provenance needed to explain a candidate without storing sensitive content.
/// </summary>
public sealed class Provenance
{
    /// <summary>Stable run-scoped identifier, e.g. "PROV-0001".</summary>
    public required string ProvenanceId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Kind of producer: "DeterministicExtractor" or "LlmSynthesis".</summary>
    public required string ProducerKind { get; init; }

    /// <summary>Extractor/adapter name and version, or model identity, that produced the record.</summary>
    public required string ProducerVersion { get; init; }

    /// <summary>Prompt template identifier/version, if an LLM was used.</summary>
    public string? PromptVersion { get; init; }

    /// <summary>Input record IDs consumed to produce this record (evidence/artifact IDs).</summary>
    public IReadOnlyList<string> InputRecordIds { get; init; } = Array.Empty<string>();

    /// <summary>UTC timestamp the provenance record was appended.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
