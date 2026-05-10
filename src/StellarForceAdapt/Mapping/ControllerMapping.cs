using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StellarForceAdapt.Mapping;

/// <summary>
/// Maps physical controller buttons to HID report byte positions and bit masks.
/// Loaded from/saved to controller_mapping.json so users can bind buttons in-app.
/// </summary>
public class ControllerMapping
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "八爪鱼5 默认映射";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("mappings")]
    public List<ButtonMapping> Mappings { get; set; } = [];

    private Dictionary<string, ButtonMapping>? _map;

    /// <summary>
    /// Get the bitmask for a named button. Returns 0 if unmapped.
    /// </summary>
    public ushort GetMask(string buttonName)
    {
        _map ??= Mappings.ToDictionary(m => m.Button, m => m);
        return _map.TryGetValue(buttonName, out var m) ? (ushort)(1 << m.BitIndex) : (ushort)0;
    }

    /// <summary>
    /// Get the byte index for a named button. Returns -1 if unmapped.
    /// </summary>
    public int GetByteIndex(string buttonName)
    {
        _map ??= Mappings.ToDictionary(m => m.Button, m => m);
        return _map.TryGetValue(buttonName, out var m) ? m.ByteIndex : -1;
    }

    /// <summary>
    /// Check if a button is pressed in the raw HID report.
    /// </summary>
    public bool IsPressed(string buttonName, byte[] rawReport)
    {
        int byteIdx = GetByteIndex(buttonName);
        ushort mask = GetMask(buttonName);
        if (byteIdx < 0 || byteIdx >= rawReport.Length || mask == 0) return false;
        return (rawReport[byteIdx] & mask) != 0;
    }

    /// <summary>
    /// Add or update a button mapping.
    /// </summary>
    public void SetMapping(string buttonName, int byteIndex, int bitIndex)
    {
        Mappings.RemoveAll(m => m.Button == buttonName);
        Mappings.Add(new ButtonMapping { Button = buttonName, ByteIndex = byteIndex, BitIndex = bitIndex });
        _map = null; // invalidate cache
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, s_jsonOptions);
        File.WriteAllText(path, json);
    }

    public static ControllerMapping Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ControllerMapping>(json, s_jsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

public class ButtonMapping
{
    [JsonPropertyName("button")]
    public string Button { get; set; } = "";

    [JsonPropertyName("byteIndex")]
    public int ByteIndex { get; set; }

    [JsonPropertyName("bitIndex")]
    public int BitIndex { get; set; }
}
