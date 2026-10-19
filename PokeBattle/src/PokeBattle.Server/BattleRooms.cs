using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using PokeBattle.Domain;

namespace PokeBattle.Server;

public sealed class BattleRoom : IDisposable
{
    public static readonly TimeSpan DefaultChoiceTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BasicBattleAi _ai = new();
    private readonly List<WebSocketSession> _sessions = [];
    private readonly BattleConfig _config;
    private readonly JsonSerializerOptions _json;
    private readonly JsonElement _initialState;
    private readonly ILogger _logger;
    private readonly PveDungeonDefinition? _dungeon;
    private readonly HashSet<int> _claimedSides = [0];
    private readonly TimeSpan _choiceTimeout;
    private CancellationTokenSource? _choiceDeadlineCancellation;
    private DateTimeOffset? _choiceDeadlineUtc;
    private int _choiceDeadlineTurn;
    private long _serverSequence;

    public BattleRoom(BattleConfig config, BattleState state, JsonSerializerOptions json, ILogger logger, PveDungeonDefinition? dungeon = null, TimeSpan? choiceTimeout = null)
    {
        _config = config;
        _json = json;
        _logger = logger;
        _dungeon = dungeon;
        _choiceTimeout = choiceTimeout is { } timeout && timeout > TimeSpan.Zero ? timeout : DefaultChoiceTimeout;
        Engine = new BattleEngine(config, state);
        _initialState = JsonSerializer.SerializeToElement(Engine.State, json);
        PlayerTokens[0] = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        PlayerTokens[1] = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        if (state.Mode != BattleMode.Pvp) _claimedSides.Add(1);
        JoinCode = state.Mode == BattleMode.Pvp ? Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() : null;
        _logger.LogInformation("Battle {BattleId} created with {RulesetId} {Format} seed {Seed}", Id, state.RulesetId, state.Format, state.Seed);
    }

    public string Id => Engine.State.BattleId;
    public BattleEngine Engine { get; }
    public Dictionary<int, string> PlayerTokens { get; } = [];
    public string? JoinCode { get; }
    public bool OpponentClaimed => _claimedSides.Contains(1);
    public long ServerSequence => Interlocked.Read(ref _serverSequence);

    public async Task AttachAsync(WebSocketSession session, int? side, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            session.Room = this;
            session.Side = side;
            lock (_sessions)
                if (!_sessions.Contains(session)) _sessions.Add(session);
        }
        finally { _gate.Release(); }
    }

    public void Detach(WebSocketSession session)
    {
        lock (_sessions) _sessions.Remove(session);
    }

    public int? ResolveSide(string token) => PlayerTokens.FirstOrDefault(x => x.Value.Equals(token, StringComparison.Ordinal)).Key is var key && PlayerTokens.GetValueOrDefault(key) == token ? key : null;

    public async Task<(bool Accepted, string? Token, string? ErrorCode, string? ErrorMessage)> JoinOpponentAsync(WebSocketSession session, string joinCode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Engine.State.Mode != BattleMode.Pvp) return (false, null, "not_pvp_room", "该房间不是实时 PVP 房间。");
            if (!string.Equals(JoinCode, joinCode, StringComparison.OrdinalIgnoreCase)) return (false, null, "invalid_join_code", "加入码无效。");
            if (_claimedSides.Contains(1)) return (false, null, "side_already_claimed", "对手阵营已经被认领，请使用重连令牌。");
            _claimedSides.Add(1);
            session.Room = this;
            session.Side = 1;
            lock (_sessions)
                if (!_sessions.Contains(session)) _sessions.Add(session);
            EnsureChoiceDeadlineLocked();
            _logger.LogInformation("Battle {BattleId} PVP opponent joined", Id);
            return (true, PlayerTokens[1], null, null);
        }
        finally { _gate.Release(); }
    }

    public object RoomState() => new
    {
        mode = Engine.State.Mode.ToString(),
        ready = Engine.State.Mode != BattleMode.Pvp || OpponentClaimed,
        opponentClaimed = OpponentClaimed,
        connectedSides = SessionSnapshot().Where(x => x.IsOpen && x.Side is not null).Select(x => x.Side!.Value).Distinct().Order().ToArray(),
        choiceDeadlineUtc = _choiceDeadlineUtc,
        choiceTimeoutSeconds = (int)Math.Ceiling(_choiceTimeout.TotalSeconds),
        dungeon = _dungeon is null ? null : new
        {
            id = _dungeon.Id,
            nameZh = _dungeon.NameZh,
            wave = Engine.State.DungeonWave,
            totalWaves = Engine.State.DungeonTotalWaves,
            completed = Engine.State.DungeonCompleted,
            rewardType = _dungeon.RewardType,
            rewardAmount = _dungeon.RewardAmount
        }
    };

    public async Task SubmitAsync(WebSocketSession session, ChoosePayload payload, string messageId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Engine.State.Mode == BattleMode.Pvp && !OpponentClaimed)
            {
                await session.SendErrorAsync("opponent_not_joined", "请等待第二名玩家加入后再选择行动。", messageId, cancellationToken);
                return;
            }
            if (session.Side is null || session.Side != payload.Side)
            {
                await session.SendErrorAsync("not_controller", "当前连接不能控制该阵营。", messageId, cancellationToken);
                return;
            }
            var result = Engine.SubmitChoices(payload.Side, payload.Choices);
            if (!result.Accepted)
            {
                _logger.LogWarning("Battle {BattleId} rejected side {Side} choice: {ErrorCode}", Id, payload.Side, result.ErrorCode);
                await session.SendErrorAsync(result.ErrorCode!, result.ErrorMessage!, messageId, cancellationToken);
                return;
            }
            _logger.LogInformation("Battle {BattleId} accepted side {Side} choices for turn {Turn}", Id, payload.Side, Engine.State.Turn);
            await session.SendAsync("choiceAccepted", new { side = payload.Side, turn = Engine.State.Turn }, messageId, cancellationToken);
            await BroadcastEventsAsync(result.Events, cancellationToken);
            await AdvanceAiAsync(cancellationToken);
            await AdvanceDungeonAsync(cancellationToken);
            EnsureChoiceDeadlineLocked();
            await BroadcastSnapshotAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SetAiAsync(WebSocketSession? session, int side, bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (side is < 0 or > 1 || (session is not null && session.Side != side)) return;
            if (Engine.State.Mode == BattleMode.Pvp) return;
            Engine.State.Sides[side].IsAi = enabled;
            await AdvanceAiAsync(cancellationToken);
            await AdvanceDungeonAsync(cancellationToken);
            EnsureChoiceDeadlineLocked();
            await BroadcastSnapshotAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ForfeitAsync(int side, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = Engine.Forfeit(side);
            _logger.LogInformation("Battle {BattleId} forfeited by side {Side}", Id, side);
            await BroadcastEventsAsync(result.Events, cancellationToken);
            await AdvanceDungeonAsync(cancellationToken);
            EnsureChoiceDeadlineLocked();
            await BroadcastSnapshotAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SendSnapshotAsync(WebSocketSession session, CancellationToken cancellationToken)
    {
        var legal = session.Side is int side && !Engine.State.Sides[side].IsAi && !Engine.State.ChoiceLocked[side] && (Engine.State.Mode != BattleMode.Pvp || OpponentClaimed) ? Engine.GetLegalChoices(side) : [];
        await session.SendAsync("battleSnapshot", new { state = Engine.State, legalChoices = legal, controllingSide = session.Side, room = RoomState() }, Guid.NewGuid().ToString("N"), cancellationToken);
    }

    public Task BroadcastRoomStateAsync(CancellationToken cancellationToken) => BroadcastAsync("roomState", RoomState(), cancellationToken);
    public Task BroadcastSnapshotsAsync(CancellationToken cancellationToken) => BroadcastSnapshotAsync(cancellationToken);

    public async Task SendDebugAsync(WebSocketSession session, long after, string? level, string? category, int limit, CancellationToken cancellationToken)
    {
        var entries = await GetDebugAsync(after, level, category, limit, cancellationToken);
        await session.SendAsync("debugLog", new { entries, nextSequence = entries.LastOrDefault()?.Sequence ?? after }, Guid.NewGuid().ToString("N"), cancellationToken);
    }

    public async Task<IReadOnlyList<BattleDebugEntry>> GetDebugAsync(long after, string? level, string? category, int limit, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return Engine.State.DebugLog
                .Where(x => x.Sequence > after)
                .Where(x => string.IsNullOrWhiteSpace(level) || x.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(category) || x.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Clamp(limit, 1, 1000))
                .ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<BattleReplayArtifact> CreateReplayAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return BattleReplayFactory.Create(_config, Engine.State, _initialState, _json); }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Give presentation clients a stable pre-action state only when both sides will advance immediately.
            if (Engine.State.Sides.All(side => side.IsAi))
                await BroadcastSnapshotAsync(cancellationToken);
            await AdvanceAiAsync(cancellationToken);
            EnsureChoiceDeadlineLocked();
            await BroadcastSnapshotAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task AdvanceAiAsync(CancellationToken cancellationToken)
    {
        var safety = 0;
        while (Engine.State.Phase == BattlePhase.AwaitingChoices && safety++ < 400)
        {
            var turnBefore = Engine.State.Turn;
            var progressed = false;
            for (var side = 0; side < 2; side++)
            {
                if (!Engine.State.Sides[side].IsAi || Engine.State.ChoiceLocked[side]) continue;
                var decision = _ai.Choose(Engine, side);
                if (decision.Choices.Count == 0) continue;
                await BroadcastAsync("aiDecision", new { side, choices = decision.Choices, candidates = decision.Candidates.Take(12) }, cancellationToken);
                _logger.LogDebug("Battle {BattleId} AI side {Side} selected {ChoiceCount} choices on turn {Turn}", Id, side, decision.Choices.Count, Engine.State.Turn);
                var result = Engine.SubmitChoices(side, decision.Choices);
                if (result.Accepted) await BroadcastEventsAsync(result.Events, cancellationToken);
                progressed = result.Accepted;
            }
            if (Engine.State.Turn != turnBefore || Engine.State.Phase == BattlePhase.Ended)
                await BroadcastSnapshotAsync(cancellationToken);
            if (!progressed || Engine.State.Sides.Any(x => !x.IsAi && !Engine.State.ChoiceLocked[x.Side])) break;
        }
    }

    private async Task AdvanceDungeonAsync(CancellationToken cancellationToken)
    {
        while (_dungeon is not null && Engine.State.Phase == BattlePhase.Ended && !Engine.State.DungeonCompleted)
        {
            CommandResult result;
            if (Engine.State.WinnerSide != 0)
            {
                result = Engine.FailDungeon();
                if (result.Accepted) await BroadcastEventsAsync(result.Events, cancellationToken);
                return;
            }

            var nextWave = _dungeon.Waves.FirstOrDefault(x => x.Wave == Engine.State.DungeonWave + 1);
            if (nextWave is null)
            {
                result = Engine.CompleteDungeon(_dungeon);
                if (result.Accepted) await BroadcastEventsAsync(result.Events, cancellationToken);
                return;
            }

            result = Engine.StartDungeonWave(nextWave);
            if (!result.Accepted) return;
            await BroadcastEventsAsync(result.Events, cancellationToken);
            if (Engine.State.Phase == BattlePhase.AwaitingChoices) await AdvanceAiAsync(cancellationToken);
        }
    }

    private void EnsureChoiceDeadlineLocked()
    {
        var ready = Engine.State.Mode != BattleMode.Pvp || OpponentClaimed;
        var awaitingHuman = ready && Engine.State.Phase == BattlePhase.AwaitingChoices &&
            Engine.State.Sides.Any(side => !side.IsAi && !Engine.State.ChoiceLocked[side.Side]);
        if (!awaitingHuman)
        {
            CancelChoiceDeadlineLocked();
            return;
        }

        if (_choiceDeadlineCancellation is not null && _choiceDeadlineTurn == Engine.State.Turn) return;
        CancelChoiceDeadlineLocked();
        _choiceDeadlineTurn = Engine.State.Turn;
        _choiceDeadlineUtc = DateTimeOffset.UtcNow.Add(_choiceTimeout);
        var cancellation = new CancellationTokenSource();
        _choiceDeadlineCancellation = cancellation;
        _ = RunChoiceDeadlineAsync(_choiceDeadlineTurn, cancellation);
    }

    private void CancelChoiceDeadlineLocked()
    {
        _choiceDeadlineCancellation?.Cancel();
        _choiceDeadlineCancellation = null;
        _choiceDeadlineUtc = null;
    }

    private async Task RunChoiceDeadlineAsync(int turn, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_choiceTimeout, cancellation.Token);
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (!ReferenceEquals(_choiceDeadlineCancellation, cancellation) || Engine.State.Phase != BattlePhase.AwaitingChoices || Engine.State.Turn != turn)
                    return;

                _choiceDeadlineCancellation = null;
                _choiceDeadlineUtc = null;
                for (var side = 0; side < 2 && Engine.State.Phase == BattlePhase.AwaitingChoices; side++)
                {
                    if (Engine.State.Sides[side].IsAi || Engine.State.ChoiceLocked[side]) continue;
                    var decision = _ai.Choose(Engine, side);
                    if (decision.Choices.Count == 0) continue;
                    _logger.LogInformation("Battle {BattleId} auto-selected side {Side} choices after {Seconds}s on turn {Turn}", Id, side, _choiceTimeout.TotalSeconds, turn);
                    await BroadcastAsync("choiceTimedOut", new { side, turn, choices = decision.Choices }, CancellationToken.None);
                    var result = Engine.SubmitChoices(side, decision.Choices);
                    if (result.Accepted) await BroadcastEventsAsync(result.Events, CancellationToken.None);
                }

                await AdvanceAiAsync(CancellationToken.None);
                await AdvanceDungeonAsync(CancellationToken.None);
                EnsureChoiceDeadlineLocked();
                await BroadcastSnapshotAsync(CancellationToken.None);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Battle {BattleId} choice deadline failed on turn {Turn}", Id, turn);
        }
        finally { cancellation.Dispose(); }
    }

    private async Task BroadcastEventsAsync(IReadOnlyList<BattleEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count > 0) await BroadcastAsync("battleEvents", new { events }, cancellationToken);
    }

    private async Task BroadcastSnapshotAsync(CancellationToken cancellationToken)
    {
        foreach (var session in SessionSnapshot())
            if (session.IsOpen) await SendSnapshotAsync(session, cancellationToken);
    }

    private async Task BroadcastAsync(string type, object payload, CancellationToken cancellationToken)
    {
        foreach (var session in SessionSnapshot())
            if (session.IsOpen) await session.SendAsync(type, payload, Guid.NewGuid().ToString("N"), cancellationToken);
    }

    public long NextServerSequence() => Interlocked.Increment(ref _serverSequence);

    private WebSocketSession[] SessionSnapshot()
    {
        lock (_sessions) return _sessions.ToArray();
    }

    public void Dispose()
    {
        _choiceDeadlineCancellation?.Cancel();
        _choiceDeadlineCancellation = null;
        _choiceDeadlineUtc = null;
    }
}

public sealed class BattleRoomManager
{
    private readonly ConcurrentDictionary<string, BattleRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly BattleConfig _config;
    private readonly JsonSerializerOptions _json;
    private readonly ILoggerFactory _loggerFactory;

    public BattleRoomManager(BattleConfig config, JsonSerializerOptions json, ILoggerFactory loggerFactory)
    {
        _config = config;
        _json = json;
        _loggerFactory = loggerFactory;
    }

    public BattleRoom Create(CreateBattlePayload payload)
    {
        var format = Enum.Parse<BattleFormat>(payload.Format, true);
        var mode = Enum.TryParse<BattleMode>(payload.Mode, true, out var parsedMode) ? parsedMode : BattleMode.Practice;
        if (mode == BattleMode.Dungeon) throw new ArgumentException("Use createDungeon for PVE dungeons.");
        var playerAi = mode == BattleMode.Pvp ? false : payload.PlayerAi;
        var opponentAi = mode == BattleMode.Pvp ? false : payload.OpponentAi;
        var state = BattleFactory.Create(_config, payload.RulesetId, format, payload.Team1Id, payload.Team2Id, payload.Seed, playerAi, opponentAi, mode,
            randomizeTeamOrder: true);
        var room = new BattleRoom(_config, state, _json, _loggerFactory.CreateLogger($"Battle.{state.BattleId}"));
        if (!_rooms.TryAdd(room.Id, room)) throw new InvalidOperationException("Could not register battle room.");
        return room;
    }

    public BattleRoom CreateDungeon(CreateDungeonPayload payload)
    {
        if (!_config.DungeonById.TryGetValue(payload.DungeonId, out var dungeon)) throw new ArgumentException($"Unknown PVE dungeon '{payload.DungeonId}'.");
        var firstWave = dungeon.Waves.OrderBy(x => x.Wave).First();
        var playerTeamId = string.IsNullOrWhiteSpace(payload.PlayerTeamId) ? dungeon.DefaultPlayerTeamId : payload.PlayerTeamId;
        var state = BattleFactory.Create(_config, dungeon.RulesetId, dungeon.Format, playerTeamId, firstWave.EnemyTeamId, payload.Seed, false, true,
            BattleMode.Dungeon, dungeon.Id, firstWave.Wave, dungeon.Waves.Count, firstWave.EnemyPartySize);
        var room = new BattleRoom(_config, state, _json, _loggerFactory.CreateLogger($"Dungeon.{state.BattleId}"), dungeon);
        if (!_rooms.TryAdd(room.Id, room)) throw new InvalidOperationException("Could not register dungeon room.");
        return room;
    }

    public bool TryGet(string id, out BattleRoom room) => _rooms.TryGetValue(id, out room!);
    public IEnumerable<object> Summaries() => _rooms.Values.Select(x => new { battleId = x.Id, mode = x.Engine.State.Mode.ToString(), x.Engine.State.RulesetId, format = x.Engine.State.Format.ToString(), x.Engine.State.Turn, phase = x.Engine.State.Phase.ToString(), x.OpponentClaimed });
}
