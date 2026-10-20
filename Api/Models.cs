using System.Text.Json.Serialization;

namespace XIVMit.Api;

// Mirrors @xivmit/shared, keeping only the fields the plugin reads. Missing fields fall back to
// defaults, so the server can add or drop fields without breaking deserialization.

public sealed class Ability
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("job")] public string Job { get; set; } = "";
    [JsonPropertyName("cooldown")] public float? Cooldown { get; set; }
    [JsonPropertyName("duration")] public float Duration { get; set; }
    [JsonPropertyName("minLevel")] public int MinLevel { get; set; }
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("abilityId")] public uint? GameActionId { get; set; } // missing for some abilities, which then have no icon
    [JsonPropertyName("replaces")] public string? Replaces { get; set; }
    [JsonPropertyName("durationUpgrade")] public LevelDuration? DurationUpgrade { get; set; }
}

public sealed class LevelDuration
{
    [JsonPropertyName("minLevel")] public int MinLevel { get; set; }
    [JsonPropertyName("duration")] public float Duration { get; set; }
}

public sealed class Phase
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("startTime")] public float StartTime { get; set; }
    [JsonPropertyName("endTime")] public float EndTime { get; set; }
    [JsonPropertyName("clockReset")] public bool ClockReset { get; set; }

    // Short scripted transitions (FRU's Intermission, TOP's P3 Transition). Skipped when
    // numbering phases for display.
    [JsonPropertyName("intermission")] public bool Intermission { get; set; }
}

public sealed class BossAction
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("time")] public float Time { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "raid";
    [JsonPropertyName("castTime")] public float? CastTime { get; set; }

    // Not sent by the server yet. Boss actions only become cast/hit sync points once it is.
    [JsonPropertyName("actionId")] public uint? GameActionId { get; set; }
}

public sealed class Fight
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("shortName")] public string? ShortName { get; set; }
    [JsonPropertyName("duration")] public float Duration { get; set; }
    [JsonPropertyName("maxLevel")] public int MaxLevel { get; set; } = 100;
    [JsonPropertyName("phases")] public List<Phase> Phases { get; set; } = [];
    [JsonPropertyName("bossActions")] public List<BossAction> BossActions { get; set; } = [];

    public string Label => ShortName ?? Name;
}

public sealed class Player
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("job")] public string Job { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class Assignment
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("playerId")] public string PlayerId { get; set; } = "";
    [JsonPropertyName("abilityId")] public string AbilityId { get; set; } = "";
    [JsonPropertyName("startTime")] public float StartTime { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("durationOverride")] public float? DurationOverride { get; set; }
}

public sealed class Plan
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("fightId")] public string FightId { get; set; } = "";
    [JsonPropertyName("players")] public List<Player> Players { get; set; } = [];
    [JsonPropertyName("assignments")] public List<Assignment> Assignments { get; set; } = [];
}
