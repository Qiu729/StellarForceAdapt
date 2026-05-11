using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StellarForceAdapt.Mapping;

/// <summary>
/// Defines a complete trigger profile with mapping rules.
/// </summary>
public class TriggerProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";

    [JsonPropertyName("game")]
    public string Game { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("rules")]
    public List<MappingRule> Rules { get; set; } = [];

    /// <summary>
    /// Load a profile from a JSON file.
    /// </summary>
    public static TriggerProfile? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TriggerProfile>(json, s_jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Profile] Failed to load {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Save profile to JSON file.
    /// </summary>
    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, s_jsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Load all profiles from the profiles directory.
    /// </summary>
    public static List<(string Path, TriggerProfile Profile)> LoadAll(string directory)
    {
        var result = new List<(string, TriggerProfile)>();
        if (!Directory.Exists(directory)) return result;

        foreach (var file in System.IO.Directory.GetFiles(directory, "*.json"))
        {
            var profile = Load(file);
            if (profile != null)
                result.Add((file, profile));
        }
        return result;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>
/// One mapping rule: when condition is met, trigger an effect.
/// </summary>
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

/// <summary>
/// Conditions that trigger a rule.
/// </summary>
public class TriggerCondition
{
    [JsonPropertyName("action")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlayerActionCondition Action { get; set; } = PlayerActionCondition.Any;

    [JsonPropertyName("in_combat")]
    public bool? InCombat { get; set; } = null; // null = don't care

    [JsonPropertyName("trigger_min")]
    public byte? TriggerMin { get; set; } = null;

    [JsonPropertyName("trigger_max")]
    public byte? TriggerMax { get; set; } = null;

    [JsonPropertyName("combo_min")]
    public int? ComboMin { get; set; } = null;

    // CE-data conditions — require CE bridge to be connected
    [JsonPropertyName("health_percent_max")]
    public float? HealthPercentMax { get; set; } = null; // trigger when HP% <= this (e.g. 0.3 for low health)

    [JsonPropertyName("beta_energy_min")]
    public float? BetaEnergyMin { get; set; } = null; // trigger when BetaEnergy >= this

    [JsonPropertyName("tachy_active")]
    public bool? TachyActive { get; set; } = null; // trigger only when Tachy mode is on/off
}

public enum PlayerActionCondition
{
    Any,
    Idle,
    Moving,
    Sprinting,
    MeleeAttack,
    Shooting,
    Aiming,
    AimingAndShooting,
    Blocking,
    Dodging,
    UsingSkill,
    Reloading,
    RunningAndShooting,
    TachyMode,
}

/// <summary>
/// A trigger effect to apply.
/// </summary>
public class TriggerEffect
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EffectType Type { get; set; } = EffectType.None;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = ""; // "pushback", "lock", "vibrate", "rumble"

    // Position where effect activates (0-255)
    [JsonPropertyName("position")]
    public byte Position { get; set; } = 0;

    // Effect intensity (0-255)
    [JsonPropertyName("intensity")]
    public byte Intensity { get; set; } = 128;

    // Effect speed/frequency (0-255)
    [JsonPropertyName("speed")]
    public byte Speed { get; set; } = 128;

    // Duration in milliseconds (0 = continuous)
    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; } = 0;

    // Which trigger: left, right, or both
    [JsonPropertyName("target")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TriggerTarget Target { get; set; } = TriggerTarget.Both;

    // For sequenced effects - list of sub-effects
    [JsonPropertyName("sequence")]
    public List<TriggerEffect>? Sequence { get; set; } = null;

    // For vibration patterns
    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; } = null; // "rapid", "pulse", "constant"
}

public enum EffectType
{
    None,
    ForceAdapt,   // ForceAdapt mechanical effect
    Rumble,       // Trigger rumble motor
    Sequence,     // Timed sequence of effects
}

public enum TriggerTarget
{
    Left,
    Right,
    Both,
}

/// <summary>
/// Runtime evaluation of a rule, including cooldown tracking.
/// </summary>
public class RuleState
{
    public MappingRule Rule { get; init; } = null!;
    public DateTime LastTriggered { get; set; } = DateTime.MinValue;
    public bool IsActive { get; set; }

    public bool CanTrigger()
    {
        if (Rule.CooldownMs <= 0) return true;
        return (DateTime.UtcNow - LastTriggered).TotalMilliseconds >= Rule.CooldownMs;
    }

    public void Triggered()
    {
        LastTriggered = DateTime.UtcNow;
        IsActive = true;
    }

    public void Deactivate()
    {
        IsActive = false;
    }
}
