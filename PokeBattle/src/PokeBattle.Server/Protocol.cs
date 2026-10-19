using System.Text.Json;

namespace PokeBattle.Server;

public sealed record ClientEnvelope(
    int ProtocolVersion,
    string MessageId,
    string? BattleId,
    long ClientSequence,
    string Type,
    JsonElement Payload);

public sealed record ServerEnvelope(
    int ProtocolVersion,
    string MessageId,
    string? BattleId,
    long ServerSequence,
    string Type,
    object Payload);

public sealed class CreateBattlePayload
{
    public string Mode { get; init; } = "Practice";
    public string RulesetId { get; init; } = "gen9-sv";
    public string Format { get; init; } = "Singles";
    public string Team1Id { get; init; } = "modern-violet";
    public string Team2Id { get; init; } = "modern-scarlet";
    public ulong Seed { get; init; } = 20260809;
    public bool PlayerAi { get; init; }
    public bool OpponentAi { get; init; } = true;
}

public sealed class JoinBattlePayload
{
    public string BattleId { get; init; } = string.Empty;
    public string JoinCode { get; init; } = string.Empty;
}

public sealed class CreateDungeonPayload
{
    public string DungeonId { get; init; } = "test-training-ground";
    public string PlayerTeamId { get; init; } = string.Empty;
    public ulong Seed { get; init; } = 20260810;
}

public sealed class ChoosePayload
{
    public int Side { get; init; }
    public List<PokeBattle.Domain.BattleChoice> Choices { get; init; } = [];
}

public sealed class ReconnectPayload
{
    public string BattleId { get; init; } = string.Empty;
    public string PlayerToken { get; init; } = string.Empty;
    public long AfterServerSequence { get; init; }
}
