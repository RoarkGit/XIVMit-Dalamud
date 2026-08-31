using System.Text.Json.Serialization;

namespace XIVMit.Api;

// DTOs mirroring @xivmit/shared (shared/index.d.ts in the XIVMit repo). Only the fields this
// plugin actually reads are declared; System.Text.Json ignores the rest. Everything here is
// additive-safe: a field the server stops sending deserialises to null/default rather than
// throwing, which matches the API's own additive-change contract.

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

    // The FFXIV game action id. Present for ~87% of abilities; the ones without simply
    // can't be icon-rendered or press-detected yet.
    [JsonPropertyName("abilityId")] public uint? GameActionId { get; set; }

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

    // A short scripted transition (FRU's Intermission, TOP's P3 Transition): a real phase for
    // boundary/timing purposes, but excluded when phases are numbered for display - see
    // PlanContext.DisplayPhaseNumber. Additive field; absent on a stale cached /api/fights
    // response, which just means every phase counts as before.
    [JsonPropertyName("intermission")] public bool Intermission { get; set; }
}

public sealed class BossAction
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("time")] public float Time { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "raid";
    [JsonPropertyName("castTime")] public float? CastTime { get; set; }

    // Not in the server schema today. Reserved so that boss actions gaining a game action id
    // upstream is picked up automatically; until then a boss action with no id here just isn't
    // a sync candidate (FightTracker's tier 2 - see its class doc comment).
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
