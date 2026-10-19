using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PokeBattle.Server;

public sealed class WebSocketSession(WebSocket socket, BattleRoomManager rooms, JsonSerializerOptions json)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long _lastClientSequence;

    public BattleRoom? Room { get; set; }
    public int? Side { get; set; }
    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    stream.Write(buffer, 0, result.Count);
                    if (stream.Length > 64 * 1024) throw new InvalidDataException("WebSocket message exceeds 64 KiB.");
                } while (!result.EndOfMessage);

                var envelope = JsonSerializer.Deserialize<ClientEnvelope>(stream.ToArray(), json);
                if (envelope is null) continue;
                await HandleAsync(envelope, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (socket.State == WebSocketState.Open) await SendErrorAsync("session_error", ex.Message, string.Empty, CancellationToken.None);
        }
        finally
        {
            Room?.Detach(this);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None);
        }
    }

    private async Task HandleAsync(ClientEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.ProtocolVersion != 1)
        {
            await SendErrorAsync("protocol_version", "只支持协议版本 1。", envelope.MessageId, cancellationToken);
            return;
        }
        if (envelope.ClientSequence <= _lastClientSequence)
        {
            await SendErrorAsync("stale_sequence", "clientSequence 必须严格递增。", envelope.MessageId, cancellationToken);
            return;
        }
        _lastClientSequence = envelope.ClientSequence;

        switch (envelope.Type)
        {
            case "createBattle":
            {
                var payload = envelope.Payload.Deserialize<CreateBattlePayload>(json) ?? new();
                var room = rooms.Create(payload);
                Room?.Detach(this);
                await room.AttachAsync(this, 0, cancellationToken);
                await SendAsync("battleCreated", new { battleId = room.Id, playerToken = room.PlayerTokens[0], side = 0, joinCode = room.JoinCode, mode = room.Engine.State.Mode.ToString(), configHash = room.Engine.State.ConfigHash }, envelope.MessageId, cancellationToken);
                await room.StartAsync(cancellationToken);
                break;
            }
            case "joinBattle":
            {
                var payload = envelope.Payload.Deserialize<JoinBattlePayload>(json) ?? new();
                if (!rooms.TryGet(payload.BattleId, out var room))
                {
                    await SendErrorAsync("battle_not_found", "找不到战斗。", envelope.MessageId, cancellationToken);
                    break;
                }
                var previousRoom = Room;
                var joined = await room.JoinOpponentAsync(this, payload.JoinCode, cancellationToken);
                if (!joined.Accepted)
                {
                    await SendErrorAsync(joined.ErrorCode!, joined.ErrorMessage!, envelope.MessageId, cancellationToken);
                    break;
                }
                previousRoom?.Detach(this);
                await SendAsync("battleJoined", new { battleId = room.Id, playerToken = joined.Token, side = 1, mode = "Pvp", configHash = room.Engine.State.ConfigHash }, envelope.MessageId, cancellationToken);
                await room.BroadcastRoomStateAsync(cancellationToken);
                await room.BroadcastSnapshotsAsync(cancellationToken);
                break;
            }
            case "createDungeon":
            {
                var payload = envelope.Payload.Deserialize<CreateDungeonPayload>(json) ?? new();
                var room = rooms.CreateDungeon(payload);
                Room?.Detach(this);
                await room.AttachAsync(this, 0, cancellationToken);
                await SendAsync("dungeonCreated", new { battleId = room.Id, playerToken = room.PlayerTokens[0], side = 0, mode = "Dungeon", configHash = room.Engine.State.ConfigHash }, envelope.MessageId, cancellationToken);
                await room.StartAsync(cancellationToken);
                break;
            }
            case "reconnect":
            {
                var payload = envelope.Payload.Deserialize<ReconnectPayload>(json) ?? new();
                if (!rooms.TryGet(payload.BattleId, out var room))
                {
                    await SendErrorAsync("battle_not_found", "找不到战斗。", envelope.MessageId, cancellationToken);
                    break;
                }
                var side = room.ResolveSide(payload.PlayerToken);
                if (side is null)
                {
                    await SendErrorAsync("invalid_token", "重连令牌无效。", envelope.MessageId, cancellationToken);
                    break;
                }
                Room?.Detach(this);
                await room.AttachAsync(this, side, cancellationToken);
                await room.SendSnapshotAsync(this, cancellationToken);
                break;
            }
            case "choose" when Room is not null:
                await Room.SubmitAsync(this, envelope.Payload.Deserialize<ChoosePayload>(json) ?? new(), envelope.MessageId, cancellationToken);
                break;
            case "setAi" when Room is not null:
            {
                var side = envelope.Payload.GetProperty("side").GetInt32();
                var enabled = envelope.Payload.GetProperty("enabled").GetBoolean();
                await Room.SetAiAsync(this, side, enabled, cancellationToken);
                break;
            }
            case "forfeit" when Room is not null:
                await Room.ForfeitAsync(Side ?? 0, cancellationToken);
                break;
            case "requestSnapshot" when Room is not null:
                await Room.SendSnapshotAsync(this, cancellationToken);
                break;
            case "requestDebug" when Room is not null:
            {
                var after = envelope.Payload.TryGetProperty("after", out var afterElement) ? afterElement.GetInt64() : 0;
                var level = envelope.Payload.TryGetProperty("level", out var levelElement) ? levelElement.GetString() : null;
                var category = envelope.Payload.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() : null;
                var limit = envelope.Payload.TryGetProperty("limit", out var limitElement) ? limitElement.GetInt32() : 200;
                await Room.SendDebugAsync(this, after, level, category, limit, cancellationToken);
                break;
            }
            default:
                await SendErrorAsync("unknown_message", $"无法处理消息类型 '{envelope.Type}'。", envelope.MessageId, cancellationToken);
                break;
        }
    }

    public Task SendErrorAsync(string code, string message, string messageId, CancellationToken cancellationToken) =>
        SendAsync("error", new { code, message, relatedMessageId = messageId }, messageId, cancellationToken);

    public async Task SendAsync(string type, object payload, string messageId, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;
        var envelope = new ServerEnvelope(1, messageId, Room?.Id, Room?.NextServerSequence() ?? 0, type, payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, json);
        await _sendGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { _sendGate.Release(); }
    }
}
