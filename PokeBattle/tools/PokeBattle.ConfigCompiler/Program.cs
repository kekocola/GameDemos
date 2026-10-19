using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualBasic.FileIO;
using PokeBattle.Domain;

var sourceDir = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "data/csv");
var outputPath = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "data/runtime/battle-config.json");

try
{
    var compiler = new ConfigCompiler(sourceDir);
    var config = compiler.Compile();
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var options = ConfigCompiler.JsonOptions;
    config.Hash = string.Empty;
    var canonical = JsonSerializer.Serialize(config, options);
    config.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(config, options), new UTF8Encoding(false));
    Console.WriteLine($"Compiled {config.RuleSets.Count} rulesets, {config.Species.Count} species, {config.Moves.Count} playable moves, {config.PresetTeams.Count} teams and {config.PveDungeons.Count} PVE dungeons.");
    Console.WriteLine($"Config hash: {config.Hash}");
    Console.WriteLine(outputPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration compilation failed: {ex.Message}");
    Environment.ExitCode = 1;
}

internal sealed class ConfigCompiler(string sourceDir)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public BattleConfig Compile()
    {
        var config = new BattleConfig
        {
            Version = "1.1.0",
            RuleSets = Load("RuleSets.csv").Select(r => new RuleSetDefinition(r["id"], r.Int("generation"), r["nameZh"], r["versionGroup"], r["specialMechanic"])).ToList(),
            GenerationConstants = Load("GenerationConstants.csv").Select(r => new GenerationConstants(
                r.Int("generation"), r.Double("criticalMultiplier"), r.Int("randomMin"), r.Int("randomMax"),
                r.Double("maxDamageFractionPerMove"), r.Int("burnResidualDenominator"), r.Bool("categoryByType"), r.Bool("hasAbilities"), r.Bool("hasItems"), r["iceWeather"], r["specialMechanic"])).ToList(),
            TypeChart = Load("TypeChart.csv").Select(r => new TypeChartEntry(r["attackType"], r["defenseType"], r.Double("multiplier"), r.Int("fromGeneration"), r.Int("toGeneration"))).ToList(),
            Species = Load("Species.csv").Where(Playable).Select(ParseSpecies).ToList(),
            Moves = Load("Moves.csv").Where(Playable).Select(ParseMove).ToList(),
            Abilities = Load("Abilities.csv").Select(ParseSimple).ToList(),
            Items = Load("Items.csv").Select(ParseSimple).ToList()
        };

        var steps = Load("EffectSteps.csv").GroupBy(r => r["effectId"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(ParseEffectStep).OrderBy(x => x.Order).ToList(), StringComparer.OrdinalIgnoreCase);
        config.Effects.AddRange(Load("EffectDefinitions.csv").Where(Playable).Select(r => new EffectDefinition
        {
            Id = r["id"], NameZh = r["nameZh"], HandlerKey = r["handlerKey"], Steps = steps.GetValueOrDefault(r["id"]) ?? []
        }));

        foreach (var group in Load("PresetTeams.csv").GroupBy(r => r["teamId"], StringComparer.OrdinalIgnoreCase))
        {
            config.PresetTeams.Add(new PresetTeamDefinition
            {
                Id = group.Key,
                NameZh = group.First()["nameZh"],
                Members = group.OrderBy(r => r.Int("slot")).Select(r => new TeamMemberDefinition
                {
                    SpeciesId = r["speciesId"],
                    Level = r.Int("level"),
                    AbilityId = r["abilityId"],
                    ItemId = r["itemId"],
                    TeraType = r["teraType"],
                    Moves = r["moves"].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                }).ToList()
            });
        }

        var dungeonWaves = Load("PveDungeonWaves.csv").Where(Playable)
            .GroupBy(r => r["dungeonId"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Int("wave")).Select(r => new PveDungeonWaveDefinition
            {
                Wave = r.Int("wave"),
                NameZh = r["nameZh"],
                EnemyTeamId = r["enemyTeamId"],
                EnemyPartySize = r.Int("enemyPartySize"),
                RestorePlayerTeam = r.Bool("restorePlayerTeam"),
                Boss = r.Bool("boss"),
                SeedOffset = r.Int("seedOffset"),
                Notes = r["notes"]
            }).ToList(), StringComparer.OrdinalIgnoreCase);
        config.PveDungeons.AddRange(Load("PveDungeons.csv").Where(Playable).Select(r => new PveDungeonDefinition
        {
            Id = r["id"],
            NameZh = r["nameZh"],
            Description = r["description"],
            RulesetId = r["rulesetId"],
            Format = Enum.Parse<BattleFormat>(r["format"], true),
            DefaultPlayerTeamId = r["defaultPlayerTeamId"],
            RecommendedLevel = r.Int("recommendedLevel"),
            RewardType = r["rewardType"],
            RewardAmount = r.Int("rewardAmount"),
            Waves = dungeonWaves.GetValueOrDefault(r["id"]) ?? []
        }));

        Validate(config);
        return config;
    }

    private void Validate(BattleConfig config)
    {
        RequireUnique(config.RuleSets.Select(x => x.Id), "ruleset");
        RequireUnique(config.Species.Select(x => x.Id), "species");
        RequireUnique(config.Moves.Select(x => x.Id), "move");
        RequireUnique(config.Effects.Select(x => x.Id), "effect");
        RequireUnique(config.Abilities.Select(x => x.Id), "ability");
        RequireUnique(config.PresetTeams.Select(x => x.Id), "team");
        RequireUnique(config.PveDungeons.Select(x => x.Id), "PVE dungeon");
        if (config.GenerationConstants.Select(x => x.Generation).Distinct().Count() != 9) throw new InvalidDataException("GenerationConstants must contain exactly Generations 1-9.");
        if (config.GenerationConstants.Any(x => x.MaxDamageFractionPerMove is <= 0 or >= 1))
            throw new InvalidDataException("GenerationConstants maxDamageFractionPerMove must be greater than 0 and less than 1.");

        var allowedHandlers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty, "protect-chain", "substitute", "tera-blast" };
        foreach (var effect in config.Effects)
            if (!allowedHandlers.Contains(effect.HandlerKey)) throw new InvalidDataException($"Effect '{effect.Id}' uses unknown handler '{effect.HandlerKey}'.");
        foreach (var move in config.Moves)
        {
            if (!config.EffectById.ContainsKey(move.EffectId)) throw new InvalidDataException($"Move '{move.Id}' references missing effect '{move.EffectId}'.");
            if (move.FromGeneration is < 1 or > 9 || move.ToGeneration < move.FromGeneration) throw new InvalidDataException($"Move '{move.Id}' has an invalid generation range.");
            if (move.Accuracy is < 0 or > 100 || move.Pp <= 0) throw new InvalidDataException($"Move '{move.Id}' has invalid accuracy or PP.");
        }
        foreach (var team in config.PresetTeams)
        {
            if (team.Members.Count != 6) throw new InvalidDataException($"Team '{team.Id}' must contain exactly six members.");
            if (team.Members.Select(x => x.SpeciesId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != team.Members.Count)
                throw new InvalidDataException($"Team '{team.Id}' must not contain duplicate species.");
            foreach (var member in team.Members)
            {
                if (!config.SpeciesById.ContainsKey(member.SpeciesId)) throw new InvalidDataException($"Team '{team.Id}' references missing species '{member.SpeciesId}'.");
                if (string.IsNullOrWhiteSpace(member.AbilityId))
                    throw new InvalidDataException($"Team '{team.Id}' member '{member.SpeciesId}' must have an ability.");
                if (!config.AbilityById.ContainsKey(member.AbilityId))
                    throw new InvalidDataException($"Team '{team.Id}' references missing ability '{member.AbilityId}'.");
                if (member.Moves.Count is < 2 or > 4) throw new InvalidDataException($"Team '{team.Id}' member '{member.SpeciesId}' must have between two and four moves.");
                if (member.Moves.Distinct(StringComparer.OrdinalIgnoreCase).Count() != member.Moves.Count)
                    throw new InvalidDataException($"Team '{team.Id}' member '{member.SpeciesId}' must have distinct moves.");
                foreach (var move in member.Moves)
                    if (!config.MoveById.ContainsKey(move)) throw new InvalidDataException($"Team '{team.Id}' references unavailable move '{move}'.");
            }
        }
        foreach (var dungeon in config.PveDungeons)
        {
            if (!config.RuleSetById.ContainsKey(dungeon.RulesetId)) throw new InvalidDataException($"PVE dungeon '{dungeon.Id}' references missing ruleset '{dungeon.RulesetId}'.");
            if (!config.TeamById.ContainsKey(dungeon.DefaultPlayerTeamId)) throw new InvalidDataException($"PVE dungeon '{dungeon.Id}' references missing player team '{dungeon.DefaultPlayerTeamId}'.");
            if (dungeon.Waves.Count == 0) throw new InvalidDataException($"PVE dungeon '{dungeon.Id}' must contain at least one wave.");
            if (!dungeon.Waves.Select(x => x.Wave).SequenceEqual(Enumerable.Range(1, dungeon.Waves.Count))) throw new InvalidDataException($"PVE dungeon '{dungeon.Id}' waves must be contiguous from 1.");
            foreach (var wave in dungeon.Waves)
            {
                if (!config.TeamById.ContainsKey(wave.EnemyTeamId)) throw new InvalidDataException($"PVE dungeon '{dungeon.Id}' wave {wave.Wave} references missing enemy team '{wave.EnemyTeamId}'.");
                if (wave.EnemyPartySize is < 1 or > 6) throw new InvalidDataException($"PVE dungeon '{dungeon.Id}' wave {wave.Wave} has invalid enemyPartySize.");
            }
        }
    }

    private static void RequireUnique(IEnumerable<string> ids, string kind)
    {
        var duplicate = ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate {kind} id '{duplicate.Key}'.");
    }

    private static bool Playable(CsvRow row) => !row.TryGet("implementationStatus", out var value) || value is "VerifiedRepresentative" or "ImplementedUnverified";

    private static SpeciesDefinition ParseSpecies(CsvRow r) => new()
    {
        Id = r["id"], PokedexNumber = r.Int("pokedexNumber"), NameZh = r["nameZh"], NameEn = r["nameEn"],
        Type1 = r["type1"], Type2 = NullIfEmpty(r["type2"]), Hp = r.Int("hp"), Attack = r.Int("attack"), Defense = r.Int("defense"),
        SpecialAttack = r.Int("specialAttack"), SpecialDefense = r.Int("specialDefense"), Speed = r.Int("speed"), SpriteUrl = r["spriteUrl"]
    };

    private static MoveDefinition ParseMove(CsvRow r) => new()
    {
        Id = r["id"], NameZh = r["nameZh"], NameEn = r["nameEn"], Type = r["type"],
        Category = Enum.Parse<MoveCategory>(r["category"], true), Power = r.Int("power"), Accuracy = r.Int("accuracy"), Pp = r.Int("pp"),
        Priority = r.Int("priority"), Target = Enum.Parse<MoveTarget>(r["target"], true), EffectId = r["effectId"],
        FromGeneration = r.Int("fromGeneration"), ToGeneration = r.Int("toGeneration"), MakesContact = r.Bool("makesContact"), Sound = r.Bool("sound")
    };

    private static SimpleDefinition ParseSimple(CsvRow r) => new(r["id"], r["nameZh"], r.Int("fromGeneration"), r.Int("toGeneration"), r["effectKey"]);

    private static EffectStepDefinition ParseEffectStep(CsvRow r) => new()
    {
        Order = r.Int("order"), Operation = Enum.Parse<EffectOperation>(r["operation"], true), Parameter = r["parameter"],
        Value = r.Double("value"), Chance = r.Int("chance"), Target = Enum.Parse<EffectTarget>(r["target"], true)
    };

    private List<CsvRow> Load(string name)
    {
        var path = Path.Combine(sourceDir, name);
        if (!File.Exists(path)) throw new FileNotFoundException($"Required configuration file '{name}' was not found.", path);
        using var parser = new TextFieldParser(path, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new InvalidDataException($"{name} is empty.");
        var rows = new List<CsvRow>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? [];
            if (fields.Length == 0 || fields.All(string.IsNullOrWhiteSpace)) continue;
            if (fields.Length != headers.Length) throw new InvalidDataException($"{name} line {parser.LineNumber} has {fields.Length} fields; expected {headers.Length}.");
            rows.Add(new CsvRow(headers.Zip(fields).ToDictionary(x => x.First, x => x.Second, StringComparer.OrdinalIgnoreCase)));
        }
        return rows;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed class CsvRow(Dictionary<string, string> fields)
{
    public string this[string name] => fields.TryGetValue(name, out var value) ? value.Trim() : throw new InvalidDataException($"Missing CSV column '{name}'.");
    public int Int(string name) => int.Parse(this[name], NumberStyles.Integer, CultureInfo.InvariantCulture);
    public double Double(string name) => double.Parse(this[name], NumberStyles.Float, CultureInfo.InvariantCulture);
    public bool Bool(string name) => bool.Parse(this[name]);
    public bool TryGet(string name, out string value) => fields.TryGetValue(name, out value!);
}
