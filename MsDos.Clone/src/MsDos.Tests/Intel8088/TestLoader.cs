using System.IO.Compression;
using System.Text.Json;

namespace MsDos.Tests.Intel8088;

/// <summary>
/// Loads 8088 CPU test cases from the gzipped JSON files in the v2 test suite.
/// Supports loading by opcode string (e.g., "00", "80.0") and provides
/// caching to avoid re-reading the same file during parameterized test runs.
/// </summary>
public static class TestLoader
{
    // Relative path from the test output directory to the 8088-tests/v2 folder.
    // Adjust if your test runner uses a different working directory.
    private static readonly string TestDataRoot = FindTestDataRoot();

    private static readonly Dictionary<string, List<CpuTestCase>> _cache = new();
    private static TestMetadata? _metadataCache;
    private static readonly object _lock = new();

    /// <summary>
    /// Walk up from the test output folder to find the 8088-tests/v2 directory.
    /// </summary>
    private static string FindTestDataRoot()
    {
        // Start from the test assembly location
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        // Walk up until we find the 8088-tests directory
        for (int i = 0; i < 15; i++)
        {
            string candidate = Path.Combine(dir, "8088-tests", "v2");
            if (Directory.Exists(candidate))
                return candidate;
            string? parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        // Fallback: try common workspace layout
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrEmpty(workspace))
        {
            string candidate = Path.Combine(workspace, "MsDos.Clone", "8088-tests", "v2");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            "Could not find 8088-tests/v2 directory. " +
            "Ensure the test suite is present in the workspace.");
    }

    // JSON options: skip unknown properties (like "cycles", "queue") for speed
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Load all test cases for the given opcode file (e.g., "00", "80.0").
    /// </summary>
    public static List<CpuTestCase> LoadTests(string opcodeFile)
    {
        return LoadTests(opcodeFile, int.MaxValue);
    }

    /// <summary>
    /// Load a subset of tests for the given opcode (first N tests).
    /// Uses a streaming Utf8JsonReader so we can stop reading the
    /// gzipped file as soon as we have enough tests - avoiding
    /// parsing the massive "cycles" arrays for remaining tests.
    /// </summary>
    public static List<CpuTestCase> LoadTests(string opcodeFile, int maxCount)
    {
        string cacheKey = $"{opcodeFile}:{maxCount}";
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        string path = Path.Combine(TestDataRoot, $"{opcodeFile}.json.gz");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Test file not found: {path}");

        var tests = StreamDeserializeTests(path, maxCount);

        lock (_lock)
        {
            _cache[cacheKey] = tests;
        }
        return tests;
    }

    /// <summary>
    /// Stream-deserialize test cases from a gzipped JSON array.
    /// Uses brace-counting on a StreamReader to extract only the first N
    /// complete JSON objects from the gzip stream, then stops reading.
    /// This avoids decompressing and parsing the entire file (which can be
    /// 50-100 MB decompressed with massive "cycles" arrays).
    /// </summary>
    private static List<CpuTestCase> StreamDeserializeTests(string path, int maxCount)
    {
        var tests = new List<CpuTestCase>(Math.Min(maxCount, 1000));

        using var fileStream = File.OpenRead(path);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536);

        // Skip whitespace and the opening '[' of the JSON array
        SkipWhitespace(reader);
        int firstChar = reader.Read();
        if (firstChar != '[')
            throw new InvalidDataException("Expected JSON array");

        while (tests.Count < maxCount)
        {
            SkipWhitespace(reader);
            int peek = reader.Peek();
            if (peek == -1 || peek == ']')
                break;

            // Skip comma between array elements
            if (peek == ',')
            {
                reader.Read();
                SkipWhitespace(reader);
                peek = reader.Peek();
                if (peek == -1 || peek == ']')
                    break;
            }

            // Extract one complete JSON object using brace-counting
            string objectJson = ReadOneJsonObject(reader);
            if (objectJson.Length == 0)
                break;

            var test = JsonSerializer.Deserialize<CpuTestCase>(objectJson, _jsonOptions);
            if (test != null)
                tests.Add(test);
        }

        return tests;
    }

    /// <summary>
    /// Read one complete JSON object from the stream using brace-counting.
    /// Handles nested braces, strings (including escaped quotes), and returns
    /// the complete JSON object as a string.
    /// </summary>
    private static string ReadOneJsonObject(StreamReader reader)
    {
        var sb = new System.Text.StringBuilder(2048);
        int depth = 0;
        bool inString = false;
        bool escape = false;

        while (true)
        {
            int c = reader.Read();
            if (c == -1) break;

            char ch = (char)c;
            sb.Append(ch);

            if (escape)
            {
                escape = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (ch == '{')
                    depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                        return sb.ToString();
                }
            }
        }

        return sb.ToString();
    }

    private static void SkipWhitespace(StreamReader reader)
    {
        while (true)
        {
            int c = reader.Peek();
            if (c == -1) return;
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                reader.Read();
            else
                return;
        }
    }

    /// <summary>
    /// Load the metadata.json file that describes opcode status and flag masks.
    /// </summary>
    public static TestMetadata LoadMetadata()
    {
        lock (_lock)
        {
            if (_metadataCache != null)
                return _metadataCache;

            string path = Path.Combine(TestDataRoot, "metadata.json");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Metadata file not found: {path}");

            string json = File.ReadAllText(path);
            _metadataCache = JsonSerializer.Deserialize<TestMetadata>(json)
                ?? throw new InvalidDataException("Failed to deserialize metadata.json");

            return _metadataCache;
        }
    }

    /// <summary>
    /// Get the flags mask for a given opcode. Returns 0xFFFF if no mask is specified
    /// (all flags are defined). The mask should be ANDed with flags before comparison
    /// to clear undefined flag bits.
    /// </summary>
    public static ushort GetFlagsMask(string opcodeHex, int? regField = null)
    {
        var metadata = LoadMetadata();
        if (!metadata.Opcodes.TryGetValue(opcodeHex, out var info))
            return 0xFFFF;

        // If there's a reg-specific entry, use it
        if (regField.HasValue && info.Reg != null &&
            info.Reg.TryGetValue(regField.Value.ToString(), out var regInfo))
        {
            return regInfo.FlagsMask.HasValue ? (ushort)regInfo.FlagsMask.Value : (ushort)0xFFFF;
        }

        return info.FlagsMask.HasValue ? (ushort)info.FlagsMask.Value : (ushort)0xFFFF;
    }

    /// <summary>
    /// Check if an opcode file exists in the test data.
    /// </summary>
    public static bool TestFileExists(string opcodeFile)
    {
        string path = Path.Combine(TestDataRoot, $"{opcodeFile}.json.gz");
        return File.Exists(path);
    }
}
