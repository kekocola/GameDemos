namespace PokeBattle.Domain;

public sealed record AiCandidate(BattleChoice Choice, double Score, string Reason);
public sealed record AiDecision(int Side, IReadOnlyList<BattleChoice> Choices, IReadOnlyList<AiCandidate> Candidates);

public interface IBattleAi
{
    AiDecision Choose(BattleEngine engine, int side);
}

public sealed class BasicBattleAi : IBattleAi
{
    public AiDecision Choose(BattleEngine engine, int side)
    {
        var allCandidates = new List<AiCandidate>();
        var chosen = new List<BattleChoice>();
        foreach (var legal in engine.GetLegalChoices(side))
        {
            var slotCandidates = new List<AiCandidate>();
            var user = Active(engine.State, side, legal.Slot);
            var ineffectiveActionStreak = user is null ? 0 : CountRecentActionsWithoutOpponentDamage(engine.State, side, user.InstanceId);
            foreach (var move in legal.Moves)
            {
                var definition = engine.Config.MoveById[move.MoveId];
                foreach (var target in move.Targets.DefaultIfEmpty(new TargetRef(side, legal.Slot, string.Empty, "自己")))
                {
                    var type = target.InstanceId.Length == 0 ? 1d : engine.EstimateMoveEffectiveness(move.MoveId, target.Side, target.Slot);
                    var accuracy = definition.Accuracy <= 0 ? 1d : definition.Accuracy / 100d;
                    var score = definition.Category == MoveCategory.Status
                        ? ScoreStatusMove(engine, side, legal.Slot, definition, target)
                        : ScoreDamagingMove(engine, definition, type, accuracy);
                    var fixedDamage = ExpectedFixedDamage(engine.Config, definition);
                    var reason = definition.Category == MoveCategory.Status
                        ? $"变化招式，基础效用 {score:0}"
                        : fixedDamage > 0 && definition.Power <= 0
                            ? $"固定伤害 {fixedDamage:0.##} × 命中 {accuracy:P0}"
                            : $"威力 {definition.Power} × 属性 {type:0.##} × 命中 {accuracy:P0}";
                    (score, reason) = ApplyHistoryAdjustment(engine, side, legal.Slot, definition, score, reason);
                    if (ineffectiveActionStreak >= 2)
                    {
                        score = definition.Category == MoveCategory.Status ? Math.Min(score, -200) : Math.Min(score, score * 0.2);
                        reason += $"；当前宝可梦已连续 {ineffectiveActionStreak} 次行动未削减对手 HP，优先打破僵局";
                    }
                    slotCandidates.Add(new AiCandidate(new BattleChoice(legal.Slot, ChoiceKind.Move, move.MoveId, target.Side, target.Slot), score, reason));
                }
            }

            foreach (var switchIndex in legal.Switches)
            {
                var switchScore = ineffectiveActionStreak >= 2 ? 30 + ineffectiveActionStreak * 25 : 6;
                slotCandidates.Add(new AiCandidate(new BattleChoice(legal.Slot, ChoiceKind.Switch, SwitchIndex: switchIndex), switchScore,
                    ineffectiveActionStreak >= 2 ? "当前出战连续未削减对手 HP，切换后备打破僵局" : "切换健康后备，避免重复无效行动"));
            }

            var bestScore = slotCandidates.Count == 0 ? 0 : slotCandidates.Max(x => x.Score);
            var tied = slotCandidates.Where(x => Math.Abs(x.Score - bestScore) < 0.0001).ToList();
            if (tied.Count > 0) chosen.Add(tied[engine.Random.NextInt(0, tied.Count - 1)].Choice);
            allCandidates.AddRange(slotCandidates);
        }
        return new(side, chosen, allCandidates.OrderByDescending(x => x.Score).ToList());
    }

    private static double ScoreDamagingMove(BattleEngine engine, MoveDefinition move, double type, double accuracy)
    {
        if (type <= 0) return -100;

        var score = move.Power * type * accuracy;
        var fixedDamageScore = ExpectedFixedDamage(engine.Config, move);
        return Math.Max(score, fixedDamageScore * accuracy);
    }

    private static double ExpectedFixedDamage(BattleConfig config, MoveDefinition move) =>
        config.EffectById.TryGetValue(move.EffectId, out var effect)
            ? effect.Steps
                .Where(step => step.Operation == EffectOperation.FixedDamage)
                .Select(step => Math.Max(0, step.Value) * Math.Clamp(step.Chance, 0, 100) / 100d)
                .DefaultIfEmpty(0)
                .Max()
            : 0;

    private static (double Score, string Reason) ApplyHistoryAdjustment(
        BattleEngine engine,
        int side,
        int slot,
        MoveDefinition move,
        double score,
        string reason)
    {
        var user = Active(engine.State, side, slot);
        if (user is null) return (-100, $"{reason}；当前出战宝可梦不存在");

        var consecutiveUses = CountConsecutiveUses(engine.State, side, user.InstanceId, move.Id, out var noDamageUses);
        if (move.Category != MoveCategory.Status && noDamageUses > 0)
        {
            score = noDamageUses >= 2 ? -500 : Math.Min(score - 20, score * 0.35);
            reason += $"；最近连续 {noDamageUses} 次未造成对手伤害，降低优先级";
        }

        if (move.Category == MoveCategory.Status && consecutiveUses > 0 && IsProtectMove(engine.Config, move))
        {
            score = -1000;
            reason += "；避免连续使用保护类招式";
        }

        return (score, reason);
    }

    private static int CountConsecutiveUses(
        BattleState state,
        int side,
        string userInstanceId,
        string moveId,
        out int noDamageUses)
    {
        noDamageUses = 0;
        var moveUses = state.EventLog.Where(x => x.Type == "MoveUsed").ToList();
        var opponentIds = state.Sides[1 - side].Team.Select(x => x.InstanceId).ToHashSet(StringComparer.Ordinal);
        var consecutiveUses = 0;

        for (var index = moveUses.Count - 1; index >= 0; index--)
        {
            var moveUse = moveUses[index];
            if (!string.Equals(moveUse.Data.GetValueOrDefault("pokemonInstanceId")?.ToString(), userInstanceId, StringComparison.Ordinal)) continue;
            if (!string.Equals(moveUse.Data.GetValueOrDefault("moveId")?.ToString(), moveId, StringComparison.OrdinalIgnoreCase)) break;

            consecutiveUses++;
            var nextMoveSequence = index + 1 < moveUses.Count ? moveUses[index + 1].Sequence : long.MaxValue;
            var dealtDamage = state.EventLog.Any(x =>
                x.Sequence > moveUse.Sequence &&
                x.Sequence < nextMoveSequence &&
                x.Type == "DamageApplied" &&
                Convert.ToDouble(x.Data.GetValueOrDefault("amount") ?? 0) > 0 &&
                string.Equals(x.Data.GetValueOrDefault("sourceInstanceId")?.ToString(), userInstanceId, StringComparison.Ordinal) &&
                opponentIds.Contains(x.Data.GetValueOrDefault("targetInstanceId")?.ToString() ?? string.Empty));
            if (dealtDamage) break;
            noDamageUses++;
        }

        return consecutiveUses;
    }

    private static int CountRecentActionsWithoutOpponentDamage(BattleState state, int side, string userInstanceId)
    {
        var moveUses = state.EventLog.Where(battleEvent => battleEvent.Type == "MoveUsed").ToList();
        var opponentIds = state.Sides[1 - side].Team.Select(pokemon => pokemon.InstanceId).ToHashSet(StringComparer.Ordinal);
        var streak = 0;
        for (var index = moveUses.Count - 1; index >= 0; index--)
        {
            var moveUse = moveUses[index];
            if (!string.Equals(moveUse.Data.GetValueOrDefault("pokemonInstanceId")?.ToString(), userInstanceId, StringComparison.Ordinal)) continue;
            var nextMoveSequence = index + 1 < moveUses.Count ? moveUses[index + 1].Sequence : long.MaxValue;
            var dealtDamage = state.EventLog.Any(battleEvent =>
                battleEvent.Sequence > moveUse.Sequence &&
                battleEvent.Sequence < nextMoveSequence &&
                battleEvent.Type == "DamageApplied" &&
                Convert.ToDouble(battleEvent.Data.GetValueOrDefault("amount") ?? 0) > 0 &&
                string.Equals(battleEvent.Data.GetValueOrDefault("sourceInstanceId")?.ToString(), userInstanceId, StringComparison.Ordinal) &&
                opponentIds.Contains(battleEvent.Data.GetValueOrDefault("targetInstanceId")?.ToString() ?? string.Empty));
            if (dealtDamage) break;
            streak++;
        }
        return streak;
    }

    private static bool IsProtectMove(BattleConfig config, MoveDefinition move) =>
        config.EffectById.TryGetValue(move.EffectId, out var effect) &&
        effect.Steps.Any(step => step.Operation == EffectOperation.Protect);

    private static double ScoreStatusMove(BattleEngine engine, int side, int slot, MoveDefinition move, TargetRef target)
    {
        if (!engine.Config.EffectById.TryGetValue(move.EffectId, out var effect)) return -100;
        var user = Active(engine.State, side, slot);
        var targetPokemon = target.InstanceId.Length == 0 ? user : Active(engine.State, target.Side, target.Slot);
        if (user is null || targetPokemon is null) return -100;

        var scores = effect.Steps.Select(step => step.Operation switch
        {
            EffectOperation.Status => targetPokemon.Status == MajorStatus.None ? 28d : -100d,
            EffectOperation.StatStage => ScoreStatStage(step.Target == EffectTarget.User ? user : targetPokemon, step),
            EffectOperation.HealPercent => user.CurrentHp < user.MaxHp ? 12d + 28d * (user.MaxHp - user.CurrentHp) / user.MaxHp : -100d,
            EffectOperation.Weather => engine.State.Field.Weather.Equals(step.Parameter, StringComparison.OrdinalIgnoreCase) ? -100d : 10d,
            EffectOperation.Terrain => engine.State.Field.Terrain.Equals(step.Parameter, StringComparison.OrdinalIgnoreCase) ? -100d : 10d,
            EffectOperation.ForceSwitch => HasHealthyReserve(engine.State, target.Side) ? 8d : -100d,
            EffectOperation.Protect => 2d,
            EffectOperation.ClearStages => targetPokemon.StatStages.Count > 0 ? 8d : -100d,
            _ => -100d
        }).ToList();
        return scores.Count == 0 ? -100 : scores.Max();
    }

    private static double ScoreStatStage(PokemonState pokemon, EffectStepDefinition step)
    {
        var current = pokemon.Stage(step.Parameter);
        return step.Value > 0 ? current < 6 ? 14 : -100 : current > -6 ? 14 : -100;
    }

    private static bool HasHealthyReserve(BattleState state, int side) => state.Sides[side].Team
        .Where((pokemon, index) => !pokemon.Fainted && !state.Sides[side].Active.Contains(index))
        .Any();

    private static PokemonState? Active(BattleState state, int side, int slot)
    {
        if (side is < 0 or > 1 || slot < 0 || slot >= state.Sides[side].Active.Count) return null;
        var index = state.Sides[side].Active[slot];
        return index >= 0 && index < state.Sides[side].Team.Count ? state.Sides[side].Team[index] : null;
    }
}
