using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StellarForceAdapt.Mapping;

public class TriggerProfile
{
    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("rules")]
    public List<MappingRule> Rules { get; set; } = [];

    public static TriggerProfile? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<TriggerProfile>(json, s_jsonOptions);
            if (profile != null) profile.FilePath = path;
            return profile;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Profile] Failed to load {path}: {ex.Message}");
            return null;
        }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, s_jsonOptions);
        File.WriteAllText(path, json);
    }

    public static List<(string Path, TriggerProfile Profile)> LoadAll(string directory)
    {
        var result = new List<(string, TriggerProfile)>();
        if (!Directory.Exists(directory)) return result;

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var profile = Load(file);
            if (profile != null && profile.Rules.Count > 0)
                result.Add((file, profile));
        }
        return result;
    }

    private static readonly JsonSerializerOptions s_jsonOptions;

    static TriggerProfile()
    {
        s_jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        s_jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }
}

public class MappingRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("condition")]
    public TriggerCondition Condition { get; set; } = new();

    [JsonPropertyName("effect")]
    public TriggerEffect Effect { get; set; } = new();

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 0;

    [JsonPropertyName("cooldown_ms")]
    public int CooldownMs { get; set; } = 0;
}

public class TriggerCondition
{
    [JsonPropertyName("buttons")]
    public ushort Buttons { get; set; }

    [JsonPropertyName("buttons_any")]
    public ushort ButtonsAny { get; set; }

    [JsonPropertyName("left_trigger_min")]
    public byte LeftTriggerMin { get; set; }

    [JsonPropertyName("left_trigger_max")]
    public byte LeftTriggerMax { get; set; } = 255;

    [JsonPropertyName("right_trigger_min")]
    public byte RightTriggerMin { get; set; }

    [JsonPropertyName("right_trigger_max")]
    public byte RightTriggerMax { get; set; } = 255;

    [JsonPropertyName("left_stick_magnitude_min")]
    public short LeftStickMagnitudeMin { get; set; }

    [JsonPropertyName("right_stick_magnitude_min")]
    public short RightStickMagnitudeMin { get; set; }
}

public class TriggerEffect
{
    [JsonPropertyName("type")]
    public EffectType Type { get; set; } = EffectType.ForceAdapt;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "racing";

    [JsonPropertyName("position")]
    public byte Position { get; set; }

    [JsonPropertyName("intensity")]
    public byte Intensity { get; set; } = 128;

    [JsonPropertyName("speed")]
    public byte Speed { get; set; } = 128;

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }

    [JsonPropertyName("target")]
    public TriggerTarget Target { get; set; } = TriggerTarget.Both;

    [JsonPropertyName("sequence")]
    public List<TriggerEffect>? Sequence { get; set; }
}

public enum EffectType
{
    None,
    ForceAdapt,
    Rumble,
    Sequence,
}

public enum TriggerTarget
{
    Left,
    Right,
    Both,
}

public class RuleState
{
    public MappingRule Rule { get; init; } = null!;
    public DateTime LastTriggered { get; set; } = DateTime.MinValue;

    public bool CanTrigger()
    {
        if (Rule.CooldownMs <= 0) return true;
        return (DateTime.UtcNow - LastTriggered).TotalMilliseconds >= Rule.CooldownMs;
    }

    public void Triggered()
    {
        LastTriggered = DateTime.UtcNow;
    }
}
