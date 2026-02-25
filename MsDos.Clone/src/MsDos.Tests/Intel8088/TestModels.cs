using System.Text.Json.Serialization;

namespace MsDos.Tests.Intel8088;

/// <summary>
/// Deserialization models for the 8088 CPU test suite JSON format (V2).
/// See: 8088-tests/README.md for format specification.
/// </summary>
/// 
public sealed class CpuTestCase
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("bytes")]
    public List<int> Bytes { get; set; } = new();

    [JsonPropertyName("initial")]
    public CpuState Initial { get; set; } = new();

    [JsonPropertyName("final")]
    public CpuState Final { get; set; } = new();

    /// <summary>
    /// Bus-cycle data (very large, never used for validation).
    /// Skipped during deserialization for performance.
    /// </summary>
    [JsonIgnore]
    public List<List<object>>? Cycles { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("idx")]
    public int Idx { get; set; }
}

public sealed class CpuState
{
    [JsonPropertyName("regs")]
    public Dictionary<string, int> Regs { get; set; } = new();

    [JsonPropertyName("ram")]
    public List<List<int>> Ram { get; set; } = new();

    /// <summary>Prefetch queue state (not used for validation).</summary>
    [JsonIgnore]
    public List<int>? Queue { get; set; }
}

/// <summary>
/// Metadata for the 8088 opcode test suite. Describes which opcodes are
/// normal, undefined, prefix, alias, etc., and provides flag masks for
/// undefined flag behavior.
/// </summary>
public sealed class TestMetadata
{
    [JsonPropertyName("opcodes")]
    public Dictionary<string, OpcodeInfo> Opcodes { get; set; } = new();
}

public sealed class OpcodeInfo
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "normal";

    [JsonPropertyName("flags")]
    public string? Flags { get; set; }

    [JsonPropertyName("flags-mask")]
    public int? FlagsMask { get; set; }

    [JsonPropertyName("reg")]
    public Dictionary<string, OpcodeInfo>? Reg { get; set; }
}
