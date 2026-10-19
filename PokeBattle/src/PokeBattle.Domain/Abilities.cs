namespace PokeBattle.Domain;

public sealed record AbilityDamageModifiers(
    double Power,
    double Attack,
    double Defense,
    IReadOnlyList<string> Sources);

public static class AbilityRules
{
    public static AbilityDamageModifiers DamageModifiers(PokemonState attacker, PokemonState defender, MoveDefinition move, MoveCategory category)
    {
        var power = 1d;
        var attack = 1d;
        var defense = 1d;
        var sources = new List<string>();

        var lowHp = attacker.CurrentHp * 3 <= attacker.MaxHp;
        if (lowHp && Matches(attacker, "overgrow") && move.Type.Equals("Grass", StringComparison.OrdinalIgnoreCase))
            Add(ref power, 1.5, sources, "overgrow");
        if (lowHp && Matches(attacker, "blaze") && move.Type.Equals("Fire", StringComparison.OrdinalIgnoreCase))
            Add(ref power, 1.5, sources, "blaze");
        if (lowHp && Matches(attacker, "torrent") && move.Type.Equals("Water", StringComparison.OrdinalIgnoreCase))
            Add(ref power, 1.5, sources, "torrent");
        if (Matches(attacker, "technician") && move.Power is > 0 and <= 60)
            Add(ref power, 1.5, sources, "technician");
        if (Matches(attacker, "flash-fire") && attacker.FlashFireBoost && move.Type.Equals("Fire", StringComparison.OrdinalIgnoreCase))
            Add(ref power, 1.5, sources, "flash-fire");
        if (Matches(attacker, "huge-power") && category == MoveCategory.Physical)
            Add(ref attack, 2, sources, "huge-power");

        if (Matches(defender, "thick-fat") && move.Type is "Fire" or "Ice")
            Add(ref defense, 0.5, sources, "thick-fat");
        if (Matches(defender, "multiscale") && defender.CurrentHp == defender.MaxHp)
            Add(ref defense, 0.5, sources, "multiscale");

        return new(power, attack, defense, sources);
    }

    public static string? ImmunityAbility(PokemonState defender, MoveDefinition move)
    {
        if (Matches(defender, "levitate") && move.Type.Equals("Ground", StringComparison.OrdinalIgnoreCase)) return "levitate";
        if (Matches(defender, "water-absorb") && move.Type.Equals("Water", StringComparison.OrdinalIgnoreCase)) return "water-absorb";
        if (Matches(defender, "flash-fire") && move.Type.Equals("Fire", StringComparison.OrdinalIgnoreCase)) return "flash-fire";
        return null;
    }

    public static double SpeedModifier(PokemonState pokemon, BattleFieldState field) =>
        Matches(pokemon, "swift-swim") && field.Weather.Equals("rain", StringComparison.OrdinalIgnoreCase) ? 2d : 1d;

    public static bool Matches(PokemonState pokemon, string abilityId) =>
        pokemon.AbilityId.Equals(abilityId, StringComparison.OrdinalIgnoreCase);

    private static void Add(ref double current, double modifier, ICollection<string> sources, string source)
    {
        current *= modifier;
        sources.Add(source);
    }
}
