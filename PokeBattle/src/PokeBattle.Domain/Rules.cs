namespace PokeBattle.Domain;

public interface IRuleSet
{
    RuleSetDefinition Definition { get; }
    GenerationConstants Constants { get; }
    MoveCategory ResolveCategory(MoveDefinition move);
    double TypeEffectiveness(string attackType, IEnumerable<string> defenseTypes);
    int EffectiveStat(int rawStat, int stage);
    bool AllowsSpecialAction(SpecialAction action);
}

public sealed class ConfiguredRuleSet : IRuleSet
{
    private static readonly HashSet<string> EarlyPhysicalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Normal", "Fighting", "Flying", "Poison", "Ground", "Rock", "Bug", "Ghost", "Steel"
    };

    private readonly BattleConfig _config;

    public ConfiguredRuleSet(BattleConfig config, RuleSetDefinition definition)
    {
        _config = config;
        Definition = definition;
        Constants = config.ConstantsByGeneration[definition.Generation];
    }

    public RuleSetDefinition Definition { get; }
    public GenerationConstants Constants { get; }

    public MoveCategory ResolveCategory(MoveDefinition move)
    {
        if (move.Category == MoveCategory.Status || !Constants.CategoryByType) return move.Category;
        return EarlyPhysicalTypes.Contains(move.Type) ? MoveCategory.Physical : MoveCategory.Special;
    }

    public double TypeEffectiveness(string attackType, IEnumerable<string> defenseTypes)
    {
        var result = 1d;
        foreach (var defenseType in defenseTypes.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var match = _config.TypeChart.LastOrDefault(x =>
                x.AttackType.Equals(attackType, StringComparison.OrdinalIgnoreCase) &&
                x.DefenseType.Equals(defenseType, StringComparison.OrdinalIgnoreCase) &&
                Definition.Generation >= x.FromGeneration && Definition.Generation <= x.ToGeneration);
            result *= match?.Multiplier ?? 1d;
        }
        return result;
    }

    public int EffectiveStat(int rawStat, int stage)
    {
        stage = Math.Clamp(stage, -6, 6);
        if (stage >= 0) return Math.Max(1, rawStat * (2 + stage) / 2);
        return Math.Max(1, rawStat * 2 / (2 - stage));
    }

    public bool AllowsSpecialAction(SpecialAction action) => action switch
    {
        SpecialAction.None => true,
        SpecialAction.Mega => Definition.Generation is 6 or 7,
        SpecialAction.ZMove => Definition.Generation == 7,
        SpecialAction.Dynamax => Definition.Generation == 8,
        SpecialAction.Terastallize => Definition.Generation == 9,
        _ => false
    };
}

public sealed record DamageBreakdown(
    int BaseDamage,
    int RandomRoll,
    double Spread,
    double Weather,
    double Critical,
    double Stab,
    double TypeEffectiveness,
    double Burn,
    double Special,
    double AbilityPower,
    double AbilityAttack,
    double AbilityDefense,
    int FinalDamage);

public static class DamageCalculator
{
    public static DamageBreakdown Calculate(
        IRuleSet rules,
        PokemonState attacker,
        PokemonState defender,
        MoveDefinition move,
        IRandomSource random,
        BattleFieldState field,
        bool critical,
        bool spread,
        SpecialAction specialAction,
        AbilityDamageModifiers abilityModifiers)
    {
        var category = rules.ResolveCategory(move);
        if (category == MoveCategory.Status || move.Power <= 0)
            return new(0, 100, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0);

        var attackStage = category == MoveCategory.Physical ? attacker.Stage("Attack") : attacker.Stage("SpecialAttack");
        var defenseStage = category == MoveCategory.Physical ? defender.Stage("Defense") : defender.Stage("SpecialDefense");
        if (critical)
        {
            attackStage = Math.Max(0, attackStage);
            defenseStage = Math.Min(0, defenseStage);
        }

        var attack = rules.EffectiveStat(category == MoveCategory.Physical ? attacker.Attack : attacker.SpecialAttack, attackStage);
        attack = Math.Max(1, (int)Math.Floor(attack * abilityModifiers.Attack));
        var defense = rules.EffectiveStat(category == MoveCategory.Physical ? defender.Defense : defender.SpecialDefense, defenseStage);
        if (rules.Definition.Generation == 9 && field.Weather == "snow" && defender.CurrentTypes.Contains("Ice", StringComparer.OrdinalIgnoreCase) && category == MoveCategory.Physical)
            defense = Math.Max(1, (int)Math.Floor(defense * 1.5));

        var power = move.Power;
        var special = specialAction switch
        {
            SpecialAction.ZMove => 1.5,
            SpecialAction.Dynamax => 1.5,
            _ => 1d
        };
        power = Math.Max(1, (int)Math.Floor(power * special * abilityModifiers.Power));

        var levelTerm = (2 * attacker.Level / 5) + 2;
        var baseDamage = Math.Floor(Math.Floor(Math.Floor(levelTerm * power * (double)attack / defense) / 50) + 2);
        var spreadModifier = spread && rules.Definition.Generation >= 3 ? 0.75 : 1d;
        var weatherModifier = WeatherModifier(field.Weather, move.Type);
        var criticalModifier = critical ? rules.Constants.CriticalMultiplier : 1d;
        var randomRoll = random.NextInt(rules.Constants.RandomMin, rules.Constants.RandomMax);
        var randomModifier = randomRoll / (double)rules.Constants.RandomMax;
        var stabModifier = Stab(attacker, move.Type);
        var typeModifier = rules.TypeEffectiveness(move.Type, defender.CurrentTypes);
        var burnModifier = attacker.Status == MajorStatus.Burn && category == MoveCategory.Physical && !move.Id.Equals("facade", StringComparison.OrdinalIgnoreCase) ? 0.5 : 1d;

        var damage = (int)baseDamage;
        foreach (var modifier in new[] { spreadModifier, weatherModifier, criticalModifier, randomModifier, stabModifier, typeModifier, burnModifier, abilityModifiers.Defense })
            damage = (int)Math.Floor(damage * modifier);
        if (typeModifier > 0 && damage < 1) damage = 1;

        return new((int)baseDamage, randomRoll, spreadModifier, weatherModifier, criticalModifier, stabModifier, typeModifier, burnModifier, special,
            abilityModifiers.Power, abilityModifiers.Attack, abilityModifiers.Defense, damage);
    }

    private static double WeatherModifier(string weather, string moveType) => (weather, moveType) switch
    {
        ("sun", "Fire") => 1.5,
        ("sun", "Water") => 0.5,
        ("rain", "Water") => 1.5,
        ("rain", "Fire") => 0.5,
        _ => 1d
    };

    private static double Stab(PokemonState attacker, string moveType)
    {
        var original = attacker.OriginalTypes.Contains(moveType, StringComparer.OrdinalIgnoreCase);
        if (!attacker.IsTerastallized) return original ? 1.5 : 1d;
        var tera = attacker.TeraType.Equals(moveType, StringComparison.OrdinalIgnoreCase);
        return (original, tera) switch { (true, true) => 2d, (_, true) => 1.5, (true, false) => 1.5, _ => 1d };
    }
}
