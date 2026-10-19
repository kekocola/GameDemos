using System.Text.Json;
using System.Text.Json.Serialization;
using PokeBattle.Domain;
using PokeBattle.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5080");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = false,
    Converters = { new JsonStringEnumConverter() }
};
var configPath = ResolveConfigPath();
var battleConfig = JsonSerializer.Deserialize<BattleConfig>(await File.ReadAllTextAsync(configPath), json)
    ?? throw new InvalidDataException("Battle configuration could not be deserialized.");

builder.Services.AddSingleton(json);
builder.Services.AddSingleton(battleConfig);
builder.Services.AddSingleton<BattleRoomManager>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(origin => new Uri(origin).IsLoopback)));

var app = builder.Build();
app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30), AllowedOrigins = { "http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:5080" } });
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", configVersion = battleConfig.Version, configHash = battleConfig.Hash }));
app.MapGet("/api/config", () => Results.Ok(new
{
    battleConfig.Version,
    battleConfig.Hash,
    ruleSets = battleConfig.RuleSets,
    teams = battleConfig.PresetTeams.Select(x => new { x.Id, x.NameZh }),
    dungeons = battleConfig.PveDungeons.Select(x => new
    {
        x.Id,
        x.NameZh,
        x.Description,
        x.RulesetId,
        format = x.Format.ToString(),
        x.DefaultPlayerTeamId,
        x.RecommendedLevel,
        x.RewardType,
        x.RewardAmount,
        waves = x.Waves.Select(w => new { w.Wave, w.NameZh, w.Boss })
    }),
    moves = battleConfig.Moves.Select(x => new { x.Id, x.NameZh, x.Type, category = x.Category.ToString(), x.Power, x.Accuracy, x.Priority })
}));
app.MapGet("/api/dungeons", () => Results.Ok(battleConfig.PveDungeons));
app.MapGet("/api/battles", (BattleRoomManager rooms) => Results.Ok(rooms.Summaries()));
app.MapGet("/api/battles/{id}/replay", async (string id, BattleRoomManager rooms, CancellationToken cancellationToken) =>
    rooms.TryGet(id, out var room) ? Results.Ok(await room.CreateReplayAsync(cancellationToken)) : Results.NotFound());
app.MapGet("/api/battles/{id}/debug", async (string id, long? after, string? level, string? category, int? limit, BattleRoomManager rooms, CancellationToken cancellationToken) =>
    rooms.TryGet(id, out var room)
        ? Results.Ok(new { entries = await room.GetDebugAsync(after ?? 0, level, category, limit ?? 200, cancellationToken) })
        : Results.NotFound());

app.Map("/ws", async (HttpContext context, BattleRoomManager rooms, JsonSerializerOptions serializerOptions) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await new WebSocketSession(socket, rooms, serializerOptions).RunAsync(context.RequestAborted);
});

app.MapFallbackToFile("index.html");
app.Run();

static string ResolveConfigPath()
{
    var candidates = new[]
    {
        Environment.GetEnvironmentVariable("POKEBATTLE_CONFIG"),
        Path.Combine(AppContext.BaseDirectory, "data", "runtime", "battle-config.json"),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data", "runtime", "battle-config.json"))
    };
    return candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x))
        ?? throw new FileNotFoundException("Run PokeBattle.ConfigCompiler before starting the server.");
}

public partial class Program;
