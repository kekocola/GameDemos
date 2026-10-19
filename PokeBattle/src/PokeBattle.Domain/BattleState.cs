using System.Text.Json.Serialization;

namespace PokeBattle.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<BattleFormat>))]
public enum BattleFormat { Singles, Doubles }

[JsonConverter(typeof(JsonStringEnumConverter<BattleMode>))]
public enum BattleMode { Practice, Pvp, Dungeon }

[JsonConverter(typeof(JsonStringEnumConverter<BattlePhase>))]
public enum BattlePhase { AwaitingChoices, Resolving, Ended }

[JsonConverter(typeof(JsonStringEnumConverter<ChoiceKind>))]
public enum ChoiceKind { Move, Switch, Forfeit }

[JsonConverter(typeof(JsonStringEnumConverter<SpecialAction>))]
public enum SpecialAction { None, Mega, ZMove, Dynamax, Terastallize }

[JsonConverter(typeof(JsonStringEnumConverter<MajorStatus>))]
public enum MajorStatus { None, Burn, Poison, BadPoison, Paralysis, Sleep, Freeze }

public sealed class BattleState
{
    public string BattleId { get; init; } = Guid.NewGuid().ToString("N");
    public string ConfigHash { get; init; } = string.Empty;
    public string RulesetId { get; init; } = string.Empty;
    public string Team1Id { get; init; } = string.Empty;
    public string Team2Id { get; set; } = string.Empty;
    public BattleMode Mode { get; init; } = BattleMode.Practice;
    public string DungeonId { get; init; } = string.Empty;
    public int DungeonWave { get; set; }
    public int DungeonTotalWaves { get; init; }
    public bool DungeonCompleted { get; set; }
    public BattleFormat Format { get; init; }
    public ulong Seed { get; init; }
    public ulong RandomState { get; set; }
    public int Turn { get; set; } = 1;
    public BattlePhase Phase { get; set; } = BattlePhase.AwaitingChoices;
    public BattleFieldState Field { get; init; } = new();
    public List<BattleSideState> Sides { get; init; } = [];
    public int? WinnerSide { get; set; }
    public long EventSequence { get; set; }
    public List<BattleEvent> EventLog { get; init; } = [];
    public Dictionary<int, bool> ChoiceLocked { get; init; } = new() { [0] = false, [1] = false };
    [JsonIgnore] internal Dictionary<int, List<BattleChoice>> PendingChoices { get; } = [];
    [JsonIgnore] public long DebugSequence { get; set; }
    [JsonIgnore] public List<BattleDebugEntry> DebugLog { get; init; } = [];
    [JsonIgnore] public long CommandSequence { get; set; }
    [JsonIgnore] public List<ReplayCommand> CommandLog { get; init; } = [];
}

public sealed class BattleFieldState
{
    public string Weather { get; set; } = string.Empty;
    public int WeatherTurns { get; set; }
    public string Terrain { get; set; } = string.Empty;
    public int TerrainTurns { get; set; }
}

public sealed class BattleSideState
{
    public int Side { get; init; }
    public string Name { get; set; } = string.Empty;
    public bool IsAi { get; set; }
    public List<PokemonState> Team { get; init; } = [];
    public List<int> Active { get; init; } = [];
    public bool MegaUsed { get; set; }
    public bool ZMoveUsed { get; set; }
    public bool DynamaxUsed { get; set; }
    public bool TeraUsed { get; set; }
}

public sealed class PokemonState
{
    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");
    public string SpeciesId { get; init; } = string.Empty;
    public int PokedexNumber { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SpriteUrl { get; init; } = string.Empty;
    public int Level { get; init; }
    public List<string> OriginalTypes { get; init; } = [];
    public List<string> CurrentTypes { get; set; } = [];
    public string TeraType { get; init; } = string.Empty;
    public int MaxHp { get; set; }
    public int CurrentHp { get; set; }
    public int Attack { get; init; }
    public int Defense { get; init; }
    public int SpecialAttack { get; init; }
    public int SpecialDefense { get; init; }
    public int Speed { get; init; }
    public MajorStatus Status { get; set; }
    public int StatusCounter { get; set; }
    public Dictionary<string, int> StatStages { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MoveSlotState> Moves { get; init; } = [];
    public string OriginalAbilityId { get; init; } = string.Empty;
    public string AbilityId { get; set; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public bool AbilityActivatedThisSwitch { get; set; }
    public bool FlashFireBoost { get; set; }
    public bool Protecting { get; set; }
    public bool IsMega { get; set; }
    public bool IsDynamaxed { get; set; }
    public int DynamaxTurns { get; set; }
    public bool IsTerastallized { get; set; }
    public bool Fainted => CurrentHp <= 0;

    public int Stage(string name) => StatStages.GetValueOrDefault(name);
}

public sealed class MoveSlotState
{
    public string MoveId { get; init; } = string.Empty;
    public int Pp { get; set; }
    public int MaxPp { get; init; }
}

public sealed record BattleChoice(
    int Slot,
    ChoiceKind Kind,
    string MoveId = "",
    int TargetSide = 1,
    int TargetSlot = 0,
    int SwitchIndex = -1,
    SpecialAction SpecialAction = SpecialAction.None);

public sealed record BattleEvent(long Sequence, int Turn, string Type, Dictionary<string, object?> Data);
public sealed record BattleDebugEntry(long Sequence, int Turn, string Level, string Category, string Message, Dictionary<string, object?> Data);
public sealed record ReplayCommand(long Sequence, int Turn, string Type, int Side, IReadOnlyList<BattleChoice> Choices, ulong RandomStateBefore);

public sealed record LegalChoiceSet(int Side, int Slot, IReadOnlyList<LegalMoveChoice> Moves, IReadOnlyList<int> Switches);
public sealed record LegalMoveChoice(
    string MoveId,
    string NameZh,
    string NameEn,
    int Pp,
    int MaxPp,
    IReadOnlyList<TargetRef> Targets,
    IReadOnlyList<SpecialAction> SpecialActions);
public sealed record TargetRef(int Side, int Slot, string InstanceId, string Name);

public sealed record CommandResult(bool Accepted, string? ErrorCode, string? ErrorMessage, IReadOnlyList<BattleEvent> Events)
{
    public static CommandResult Error(string code, string message) => new(false, code, message, []);
}

public static class BattleFactory
{
    public static BattleState Create(
        BattleConfig config,
        string rulesetId,
        BattleFormat format,
        string team1Id,
        string team2Id,
        ulong seed,
        bool playerAi,
        bool opponentAi,
        BattleMode mode = BattleMode.Practice,
        string dungeonId = "",
        int dungeonWave = 0,
        int dungeonTotalWaves = 0,
        int team2MemberLimit = 6,
        bool randomizeTeamOrder = false)
    {
        if (!config.RuleSetById.TryGetValue(rulesetId, out var ruleset)) throw new ArgumentException($"Unknown ruleset '{rulesetId}'.");
        if (!config.TeamById.TryGetValue(team1Id, out var team1)) throw new ArgumentException($"Unknown team '{team1Id}'.");
        if (!config.TeamById.TryGetValue(team2Id, out var team2)) throw new ArgumentException($"Unknown team '{team2Id}'.");

        var state = new BattleState
        {
            ConfigHash = config.Hash,
            RulesetId = ruleset.Id,
            Team1Id = team1.Id,
            Team2Id = team2.Id,
            Mode = mode,
            DungeonId = dungeonId,
            DungeonWave = dungeonWave,
            DungeonTotalWaves = dungeonTotalWaves,
            Format = format,
            Seed = seed,
            RandomState = seed,
            Sides =
            [
                CreateSide(config, 0, "玩家", team1, playerAi, ruleset.Generation, 6, "p0", randomizeTeamOrder ? seed ^ 0x9E3779B97F4A7C15UL : null),
                CreateSide(config, 1, mode == BattleMode.Dungeon ? "副本敌人" : "对手", team2, opponentAi, ruleset.Generation, team2MemberLimit, mode == BattleMode.Dungeon ? $"w{dungeonWave}-p1" : "p1", randomizeTeamOrder ? seed ^ 0xD1B54A32D192ED03UL : null)
            ]
        };
        var activeCount = format == BattleFormat.Doubles ? 2 : 1;
        foreach (var side in state.Sides)
            side.Active.AddRange(Enumerable.Range(0, Math.Min(activeCount, side.Team.Count)));
        return state;
    }

    public static BattleSideState CreateSide(BattleConfig config, int sideIndex, string name, PresetTeamDefinition team, bool ai, int generation, int memberLimit = 6, string? instancePrefix = null, ulong? memberOrderSeed = null)
    {
        var result = new BattleSideState { Side = sideIndex, Name = name, IsAi = ai };
        var members = team.Members.ToList();
        if (memberOrderSeed is { } orderSeed)
        {
            var random = new XorShiftRandom(orderSeed);
            for (var index = members.Count - 1; index > 0; index--)
            {
                var swapIndex = random.NextInt(0, index);
                (members[index], members[swapIndex]) = (members[swapIndex], members[index]);
            }
        }
        members = members.Take(Math.Clamp(memberLimit, 1, members.Count)).ToList();
        for (var memberIndex = 0; memberIndex < members.Count; memberIndex++)
        {
            var member = members[memberIndex];
            var species = config.SpeciesById[member.SpeciesId];
            var level = member.Level;
            var hp = ((2 * species.Hp + 31 + 63) * level / 100) + level + 10;
            int Stat(int value) => ((2 * value + 31 + 63) * level / 100) + 5;
            var types = new List<string> { species.Type1 };
            if (!string.IsNullOrWhiteSpace(species.Type2)) types.Add(species.Type2);
            var pokemon = new PokemonState
            {
                InstanceId = $"{instancePrefix ?? $"p{sideIndex}"}-m{memberIndex}-{species.Id}",
                SpeciesId = species.Id,
                PokedexNumber = species.PokedexNumber,
                Name = species.NameZh,
                SpriteUrl = species.SpriteUrl,
                Level = level,
                OriginalTypes = [.. types],
                CurrentTypes = [.. types],
                TeraType = string.IsNullOrWhiteSpace(member.TeraType) ? species.Type1 : member.TeraType,
                MaxHp = hp,
                CurrentHp = hp,
                Attack = Stat(species.Attack),
                Defense = Stat(species.Defense),
                SpecialAttack = Stat(generation == 1 ? species.SpecialAttack : species.SpecialAttack),
                SpecialDefense = Stat(generation == 1 ? species.SpecialAttack : species.SpecialDefense),
                Speed = Stat(species.Speed),
                OriginalAbilityId = member.AbilityId,
                AbilityId = member.AbilityId,
                ItemId = generation >= 2 ? member.ItemId : string.Empty
            };
            foreach (var moveId in member.Moves.Take(4))
            {
                var move = config.MoveById[moveId];
                pokemon.Moves.Add(new MoveSlotState { MoveId = move.Id, Pp = move.Pp, MaxPp = move.Pp });
            }
            result.Team.Add(pokemon);
        }
        return result;
    }
}
