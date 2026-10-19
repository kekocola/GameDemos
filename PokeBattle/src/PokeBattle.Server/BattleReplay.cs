using System.Security.Cryptography;
using System.Text.Json;
using PokeBattle.Domain;

namespace PokeBattle.Server;

public sealed record BattleReplayArtifact(
    int ReplayVersion,
    int ProtocolVersion,
    string BattleId,
    string ConfigHash,
    string RulesetId,
    string Format,
    string Mode,
    string DungeonId,
    ulong Seed,
    string Team1Id,
    string Team2Id,
    JsonElement InitialState,
    JsonElement FinalState,
    IReadOnlyList<ReplayCommand> Commands,
    IReadOnlyList<BattleEvent> Events,
    string EventHash,
    bool Verified,
    IReadOnlyList<string> VerificationErrors);

public static class BattleReplayFactory
{
    public static BattleReplayArtifact Create(BattleConfig config, BattleState state, JsonElement initialState, JsonSerializerOptions json)
    {
        var events = state.EventLog.ToList();
        var commands = state.CommandLog.ToList();
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(events, json))).ToLowerInvariant();
        var errors = new List<string>();
        if (!state.ConfigHash.Equals(config.Hash, StringComparison.OrdinalIgnoreCase)) errors.Add("config_hash_mismatch");
        if (events.Where((item, index) => item.Sequence != index + 1L).Any()) errors.Add("event_sequence_gap");
        if (commands.Where((item, index) => item.Sequence != index + 1L).Any()) errors.Add("command_sequence_gap");
        if (events.Count == 0 || events[0].Type != "BattleStarted") errors.Add("missing_battle_start");
        if (state.Phase == BattlePhase.Ended && events.All(x => x.Type != "BattleEnded")) errors.Add("missing_battle_end");

        return new(
            1,
            1,
            state.BattleId,
            state.ConfigHash,
            state.RulesetId,
            state.Format.ToString(),
            state.Mode.ToString(),
            state.DungeonId,
            state.Seed,
            state.Team1Id,
            state.Team2Id,
            initialState,
            JsonSerializer.SerializeToElement(state, json),
            commands,
            events,
            hash,
            errors.Count == 0,
            errors);
    }
}
