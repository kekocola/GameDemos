using System.Text.Json.Serialization;

namespace PokeBattle.Domain;

public sealed class BattleConfig
{
    public string Version { get; init; } = "1.0.0";
    public string Hash { get; set; } = string.Empty;
    public List<RuleSetDefinition> RuleSets { get; init; } = [];
    public List<GenerationConstants> GenerationConstants { get; init; } = [];
    public List<TypeChartEntry> TypeChart { get; init; } = [];
    public List<SpeciesDefinition> Species { get; init; } = [];
    public List<MoveDefinition> Moves { get; init; } = [];
    public List<EffectDefinition> Effects { get; init; } = [];
    public List<PresetTeamDefinition> PresetTeams { get; init; } = [];
    public List<SimpleDefinition> Abilities { get; init; } = [];
    public List<SimpleDefinition> Items { get; init; } = [];
    public List<PveDungeonDefinition> PveDungeons { get; init; } = [];

    [JsonIgnore] public IReadOnlyDictionary<string, RuleSetDefinition> RuleSetById => _ruleSets ??= RuleSets.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public IReadOnlyDictionary<int, GenerationConstants> ConstantsByGeneration => _constants ??= GenerationConstants.ToDictionary(x => x.Generation);
    [JsonIgnore] public IReadOnlyDictionary<string, SpeciesDefinition> SpeciesById => _species ??= Species.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public IReadOnlyDictionary<string, MoveDefinition> MoveById => _moves ??= Moves.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public IReadOnlyDictionary<string, EffectDefinition> EffectById => _effects ??= Effects.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public IReadOnlyDictionary<string, PresetTeamDefinition> TeamById => _teams ??= PresetTeams.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public IReadOnlyDictionary<string, SimpleDefinition> AbilityById => _abilities ??= Abilities.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public IReadOnlyDictionary<string, PveDungeonDefinition> DungeonById => _dungeons ??= PveDungeons.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, RuleSetDefinition>? _ruleSets;
    private Dictionary<int, GenerationConstants>? _constants;
    private Dictionary<string, SpeciesDefinition>? _species;
    private Dictionary<string, MoveDefinition>? _moves;
    private Dictionary<string, EffectDefinition>? _effects;
    private Dictionary<string, PresetTeamDefinition>? _teams;
    private Dictionary<string, SimpleDefinition>? _abilities;
    private Dictionary<string, PveDungeonDefinition>? _dungeons;
}

public sealed record RuleSetDefinition(string Id, int Generation, string NameZh, string VersionGroup, string SpecialMechanic);

public sealed record GenerationConstants(
    int Generation,
    double CriticalMultiplier,
    int RandomMin,
    int RandomMax,
    double MaxDamageFractionPerMove,
    int BurnResidualDenominator,
    bool CategoryByType,
    bool HasAbilities,
    bool HasItems,
    string IceWeather,
    string SpecialMechanic);

public sealed record TypeChartEntry(string AttackType, string DefenseType, double Multiplier, int FromGeneration, int ToGeneration = 9);

public sealed class SpeciesDefinition
{
    public string Id { get; init; } = string.Empty;
    public int PokedexNumber { get; init; }
    public string NameZh { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string Type1 { get; init; } = "Normal";
    public string? Type2 { get; init; }
    public int Hp { get; init; }
    public int Attack { get; init; }
    public int Defense { get; init; }
    public int SpecialAttack { get; init; }
    public int SpecialDefense { get; init; }
    public int Speed { get; init; }
    public string SpriteUrl { get; init; } = string.Empty;
}

public sealed class MoveDefinition
{
    public string Id { get; init; } = string.Empty;
    public string NameZh { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string Type { get; init; } = "Normal";
    public MoveCategory Category { get; init; }
    public int Power { get; init; }
    public int Accuracy { get; init; } = 100;
    public int Pp { get; init; } = 10;
    public int Priority { get; init; }
    public MoveTarget Target { get; init; } = MoveTarget.Opponent;
    public string EffectId { get; init; } = "damage";
    public int FromGeneration { get; init; } = 1;
    public int ToGeneration { get; init; } = 9;
    public bool MakesContact { get; init; }
    public bool Sound { get; init; }
}

public sealed class EffectDefinition
{
    public string Id { get; init; } = string.Empty;
    public string NameZh { get; init; } = string.Empty;
    public string HandlerKey { get; init; } = string.Empty;
    public List<EffectStepDefinition> Steps { get; init; } = [];
}

public sealed class EffectStepDefinition
{
    public int Order { get; init; }
    public EffectOperation Operation { get; init; }
    public string Parameter { get; init; } = string.Empty;
    public double Value { get; init; }
    public int Chance { get; init; } = 100;
    public EffectTarget Target { get; init; } = EffectTarget.Target;
}

public sealed class PresetTeamDefinition
{
    public string Id { get; init; } = string.Empty;
    public string NameZh { get; init; } = string.Empty;
    public List<TeamMemberDefinition> Members { get; init; } = [];
}

public sealed class TeamMemberDefinition
{
    public string SpeciesId { get; init; } = string.Empty;
    public int Level { get; init; } = 50;
    public string AbilityId { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string TeraType { get; init; } = string.Empty;
    public List<string> Moves { get; init; } = [];
}

public sealed class PveDungeonDefinition
{
    public string Id { get; init; } = string.Empty;
    public string NameZh { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string RulesetId { get; init; } = "gen9-sv";
    public BattleFormat Format { get; init; } = BattleFormat.Singles;
    public string DefaultPlayerTeamId { get; init; } = string.Empty;
    public int RecommendedLevel { get; init; } = 50;
    public string RewardType { get; init; } = string.Empty;
    public int RewardAmount { get; init; }
    public List<PveDungeonWaveDefinition> Waves { get; init; } = [];
}

public sealed class PveDungeonWaveDefinition
{
    public int Wave { get; init; }
    public string NameZh { get; init; } = string.Empty;
    public string EnemyTeamId { get; init; } = string.Empty;
    public int EnemyPartySize { get; init; } = 6;
    public bool RestorePlayerTeam { get; init; }
    public bool Boss { get; init; }
    public int SeedOffset { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed record SimpleDefinition(string Id, string NameZh, int FromGeneration, int ToGeneration, string EffectKey);

[JsonConverter(typeof(JsonStringEnumConverter<MoveCategory>))]
public enum MoveCategory { Physical, Special, Status }

[JsonConverter(typeof(JsonStringEnumConverter<MoveTarget>))]
public enum MoveTarget { Self, Opponent, AllOpponents, Ally, AllActive }

[JsonConverter(typeof(JsonStringEnumConverter<EffectTarget>))]
public enum EffectTarget { User, Target, UserSide, TargetSide, Field }

[JsonConverter(typeof(JsonStringEnumConverter<EffectOperation>))]
public enum EffectOperation
{
    Damage,
    FixedDamage,
    Status,
    StatStage,
    HealPercent,
    DrainPercent,
    RecoilPercent,
    Weather,
    Terrain,
    Protect,
    MultiHit,
    ForceSwitch,
    ClearStages
}
