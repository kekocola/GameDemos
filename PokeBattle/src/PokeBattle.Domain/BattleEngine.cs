namespace PokeBattle.Domain;

public sealed class BattleEngine
{
    public const int MaxTurns = 99;
    public const int MaxConsecutiveTurnsWithoutHpLoss = 8;

    private readonly IRuleSet _rules;
    private int _consecutiveTurnsWithoutHpLoss;

    public BattleEngine(BattleConfig config, BattleState state)
    {
        Config = config;
        State = state;
        if (!config.RuleSetById.TryGetValue(state.RulesetId, out var definition))
            throw new InvalidOperationException($"Ruleset '{state.RulesetId}' does not exist.");
        _rules = new ConfiguredRuleSet(config, definition);
        Random = new XorShiftRandom(state.RandomState);
        if (state.EventLog.Count == 0)
        {
            Emit("BattleStarted", ("rulesetId", state.RulesetId), ("format", state.Format.ToString()), ("seed", state.Seed), ("mode", state.Mode.ToString()));
            if (state.Mode == BattleMode.Dungeon && config.DungeonById.TryGetValue(state.DungeonId, out var dungeon))
            {
                var wave = dungeon.Waves.FirstOrDefault(x => x.Wave == state.DungeonWave);
                Emit("DungeonStarted", ("dungeonId", dungeon.Id), ("name", dungeon.NameZh), ("totalWaves", dungeon.Waves.Count));
                Emit("DungeonWaveStarted", ("dungeonId", dungeon.Id), ("wave", state.DungeonWave), ("totalWaves", state.DungeonTotalWaves), ("name", wave?.NameZh), ("boss", wave?.Boss ?? false));
            }
            TriggerInitialAbilities();
            Emit("TurnStarted", ("turn", state.Turn));
            state.RandomState = Random.State;
        }
    }

    public BattleConfig Config { get; }
    public BattleState State { get; }
    public IRandomSource Random { get; private set; }

    public IReadOnlyList<LegalChoiceSet> GetLegalChoices(int sideIndex)
    {
        if (State.Phase != BattlePhase.AwaitingChoices || sideIndex is < 0 or > 1) return [];
        var side = State.Sides[sideIndex];
        var results = new List<LegalChoiceSet>();
        for (var slot = 0; slot < side.Active.Count; slot++)
        {
            var pokemon = side.Team[side.Active[slot]];
            if (pokemon.Fainted) continue;
            var moves = new List<LegalMoveChoice>();
            foreach (var moveSlot in pokemon.Moves.Where(x => x.Pp > 0))
            {
                if (!Config.MoveById.TryGetValue(moveSlot.MoveId, out var move)) continue;
                var targets = ResolveLegalTargets(sideIndex, slot, move);
                var specials = new List<SpecialAction> { SpecialAction.None };
                foreach (var special in Enum.GetValues<SpecialAction>().Where(x => x != SpecialAction.None))
                    if (CanUseSpecial(side, pokemon, move, special)) specials.Add(special);
                moves.Add(new(move.Id, move.NameZh, move.NameEn, moveSlot.Pp, moveSlot.MaxPp, targets, specials));
            }
            if (moves.Count == 0 && Config.MoveById.TryGetValue("struggle", out var struggle))
                moves.Add(new(struggle.Id, struggle.NameZh, struggle.NameEn, 1, 1,
                    ResolveLegalTargets(sideIndex, slot, struggle), [SpecialAction.None]));
            var switches = Enumerable.Range(0, side.Team.Count)
                .Where(index => !side.Team[index].Fainted && !side.Active.Contains(index))
                .ToList();
            results.Add(new(sideIndex, slot, moves, switches));
        }
        return results;
    }

    public double EstimateMoveEffectiveness(string moveId, int targetSide, int targetSlot)
    {
        var target = ActivePokemon(targetSide, targetSlot);
        if (target is null || !Config.MoveById.TryGetValue(moveId, out var move)) return 0;
        return AbilityRules.ImmunityAbility(target, move) is null ? _rules.TypeEffectiveness(move.Type, target.CurrentTypes) : 0;
    }

    public CommandResult SubmitChoices(int sideIndex, IReadOnlyList<BattleChoice> choices)
    {
        if (State.Phase == BattlePhase.Ended) return Reject("battle_ended", "战斗已经结束。", sideIndex);
        if (State.Phase != BattlePhase.AwaitingChoices) return Reject("invalid_phase", "当前不接受行动选择。", sideIndex);
        if (sideIndex is < 0 or > 1) return Reject("invalid_side", "无效的阵营。", sideIndex);
        if (State.PendingChoices.ContainsKey(sideIndex)) return Reject("choice_locked", "本回合选择已经锁定。", sideIndex);

        var legal = GetLegalChoices(sideIndex);
        if (choices.Count != legal.Count) return Reject("incomplete_choice", $"需要为 {legal.Count} 个活动位置提交选择。", sideIndex);
        foreach (var choice in choices)
        {
            var set = legal.FirstOrDefault(x => x.Slot == choice.Slot);
            if (set is null || !IsLegalChoice(choice, set)) return Reject("illegal_choice", $"位置 {choice.Slot} 的选择不合法。", sideIndex);
        }

        var before = State.EventLog.Count;
        State.CommandLog.Add(new(++State.CommandSequence, State.Turn, "choose", sideIndex, choices.ToList(), Random.State));
        Trace("Info", "command", "choice_accepted", ("side", sideIndex), ("choices", choices));
        State.PendingChoices[sideIndex] = choices.ToList();
        State.ChoiceLocked[sideIndex] = true;
        Emit("ChoiceLocked", ("side", sideIndex));

        if (State.PendingChoices.Count == 2) ResolveTurn();
        State.RandomState = Random.State;
        return new(true, null, null, State.EventLog.Skip(before).ToList());
    }

    public CommandResult Forfeit(int sideIndex)
    {
        if (State.Phase == BattlePhase.Ended) return CommandResult.Error("battle_ended", "战斗已经结束。");
        var before = State.EventLog.Count;
        State.CommandLog.Add(new(++State.CommandSequence, State.Turn, "forfeit", sideIndex, [], Random.State));
        Trace("Info", "command", "forfeit_accepted", ("side", sideIndex));
        State.WinnerSide = 1 - sideIndex;
        State.Phase = BattlePhase.Ended;
        Emit("BattleEnded", ("winnerSide", State.WinnerSide), ("reason", "forfeit"));
        return new(true, null, null, State.EventLog.Skip(before).ToList());
    }

    public CommandResult StartDungeonWave(PveDungeonWaveDefinition wave)
    {
        if (State.Mode != BattleMode.Dungeon) return CommandResult.Error("not_dungeon", "当前战斗不是副本。");
        if (State.Phase != BattlePhase.Ended || State.WinnerSide != 0) return CommandResult.Error("wave_not_won", "只有玩家获胜后才能进入下一波。");
        if (!Config.TeamById.TryGetValue(wave.EnemyTeamId, out var enemyTeam)) return CommandResult.Error("enemy_team_missing", $"副本敌方队伍 '{wave.EnemyTeamId}' 不存在。");

        var before = State.EventLog.Count;
        Emit("DungeonWaveCompleted", ("dungeonId", State.DungeonId), ("wave", State.DungeonWave));
        PrepareSideForWave(State.Sides[0], wave.RestorePlayerTeam);
        var enemySide = BattleFactory.CreateSide(Config, 1, "副本敌人", enemyTeam, true, _rules.Definition.Generation, wave.EnemyPartySize, $"w{wave.Wave}-p1");
        var enemyActiveCount = State.Format == BattleFormat.Doubles ? 2 : 1;
        enemySide.Active.AddRange(Enumerable.Range(0, Math.Min(enemyActiveCount, enemySide.Team.Count)));
        State.Sides[1] = enemySide;
        State.Team2Id = enemyTeam.Id;
        State.DungeonWave = wave.Wave;
        State.RandomState = State.Seed + (ulong)Math.Max(0, wave.SeedOffset);
        Random = new XorShiftRandom(State.RandomState);
        State.Field.Weather = string.Empty;
        State.Field.WeatherTurns = 0;
        State.Field.Terrain = string.Empty;
        State.Field.TerrainTurns = 0;
        _consecutiveTurnsWithoutHpLoss = 0;
        State.PendingChoices.Clear();
        State.ChoiceLocked[0] = false;
        State.ChoiceLocked[1] = false;
        State.WinnerSide = null;
        State.Phase = BattlePhase.AwaitingChoices;
        State.Turn++;
        Emit("DungeonWaveStarted", ("dungeonId", State.DungeonId), ("wave", wave.Wave), ("totalWaves", State.DungeonTotalWaves), ("name", wave.NameZh), ("boss", wave.Boss));
        TriggerInitialAbilities();
        Emit("TurnStarted", ("turn", State.Turn));
        State.RandomState = Random.State;
        return new(true, null, null, State.EventLog.Skip(before).ToList());
    }

    public CommandResult CompleteDungeon(PveDungeonDefinition dungeon)
    {
        if (State.Mode != BattleMode.Dungeon || State.DungeonCompleted) return CommandResult.Error("dungeon_not_completable", "副本不能重复结算。");
        if (State.Phase != BattlePhase.Ended || State.WinnerSide != 0) return CommandResult.Error("dungeon_not_won", "副本尚未胜利。");
        var before = State.EventLog.Count;
        Emit("DungeonWaveCompleted", ("dungeonId", dungeon.Id), ("wave", State.DungeonWave));
        State.DungeonCompleted = true;
        Emit("DungeonCompleted", ("dungeonId", dungeon.Id), ("name", dungeon.NameZh), ("rewardType", dungeon.RewardType), ("rewardAmount", dungeon.RewardAmount));
        return new(true, null, null, State.EventLog.Skip(before).ToList());
    }

    public CommandResult FailDungeon()
    {
        if (State.Mode != BattleMode.Dungeon || State.EventLog.Any(x => x.Type == "DungeonFailed"))
            return CommandResult.Error("dungeon_not_failable", "副本失败已经结算。");
        var before = State.EventLog.Count;
        Emit("DungeonFailed", ("dungeonId", State.DungeonId), ("wave", State.DungeonWave));
        return new(true, null, null, State.EventLog.Skip(before).ToList());
    }

    private void ResolveTurn()
    {
        var totalHpBeforeTurn = State.Sides.SelectMany(side => side.Team).Sum(pokemon => pokemon.CurrentHp);
        State.Phase = BattlePhase.Resolving;
        var actions = new List<QueuedAction>();
        foreach (var pair in State.PendingChoices)
        foreach (var choice in pair.Value)
        {
            var actor = ActivePokemon(pair.Key, choice.Slot);
            if (actor is null) continue;
            var priority = choice.Kind == ChoiceKind.Switch ? 6 : Config.MoveById.GetValueOrDefault(choice.MoveId)?.Priority ?? 0;
            var speed = EffectiveSpeed(actor);
            actions.Add(new(pair.Key, actor.InstanceId, choice, priority, speed, Random.NextInt(0, int.MaxValue)));
        }

        var orderedActions = actions.OrderByDescending(x => x.Priority).ThenByDescending(x => x.Speed).ThenByDescending(x => x.TieBreaker).ToList();
        Trace("Debug", "turn", "action_order_resolved", ("actions", orderedActions.Select(x => new { x.Side, x.Choice.Slot, kind = x.Choice.Kind.ToString(), x.Priority, x.Speed, x.TieBreaker }).ToList()));
        foreach (var action in orderedActions)
        {
            if (State.Phase == BattlePhase.Ended) break;
            var actor = ActivePokemon(action.Side, action.Choice.Slot);
            if (actor is null || actor.Fainted || !actor.InstanceId.Equals(action.ActorInstanceId, StringComparison.Ordinal))
            {
                Trace("Debug", "turn", "queued_action_skipped", ("side", action.Side), ("slot", action.Choice.Slot),
                    ("actorInstanceId", action.ActorInstanceId), ("activeInstanceId", actor?.InstanceId), ("reason", actor?.Fainted == true ? "fainted" : "actor_changed"));
                continue;
            }
            if (action.Choice.Kind == ChoiceKind.Switch) ExecuteSwitch(action.Side, action.Choice.Slot, action.Choice.SwitchIndex, "manual");
            else if (action.Choice.Kind == ChoiceKind.Move) ExecuteMove(action.Side, action.Choice);
            CheckFaintsAndAutoSwitch();
            CheckBattleEnd();
        }

        if (State.Phase != BattlePhase.Ended) EndTurn();
        State.PendingChoices.Clear();
        State.ChoiceLocked[0] = false;
        State.ChoiceLocked[1] = false;
        if (State.Phase != BattlePhase.Ended)
        {
            Emit("TurnEnded", ("turn", State.Turn));
            var totalHpAfterTurn = State.Sides.SelectMany(side => side.Team).Sum(pokemon => pokemon.CurrentHp);
            _consecutiveTurnsWithoutHpLoss = totalHpAfterTurn < totalHpBeforeTurn ? 0 : _consecutiveTurnsWithoutHpLoss + 1;
            if (_consecutiveTurnsWithoutHpLoss >= MaxConsecutiveTurnsWithoutHpLoss)
            {
                State.WinnerSide = DetermineLeaderByRemainingTeam();
                State.Phase = BattlePhase.Ended;
                Emit("BattleEnded", ("winnerSide", State.WinnerSide), ("reason", "stalemate"),
                    ("turnsWithoutHpLoss", _consecutiveTurnsWithoutHpLoss));
            }
            else if (State.Turn >= MaxTurns)
            {
                State.WinnerSide = State.Sides[1].Team.Any(x => !x.Fainted) ? 1 : 0;
                State.Phase = BattlePhase.Ended;
                Emit("BattleEnded", ("winnerSide", State.WinnerSide), ("reason", "turn_limit"), ("maxTurns", MaxTurns));
            }
            else
            {
                State.Turn++;
                State.Phase = BattlePhase.AwaitingChoices;
                Emit("TurnStarted", ("turn", State.Turn));
            }
        }
    }

    private void PrepareSideForWave(BattleSideState side, bool restore)
    {
        foreach (var pokemon in side.Team)
        {
            if (restore)
            {
                pokemon.CurrentHp = pokemon.MaxHp;
                pokemon.Status = MajorStatus.None;
                pokemon.StatusCounter = 0;
                foreach (var move in pokemon.Moves) move.Pp = move.MaxPp;
            }
            pokemon.StatStages.Clear();
            pokemon.CurrentTypes = [.. pokemon.OriginalTypes];
            pokemon.AbilityId = pokemon.OriginalAbilityId;
            pokemon.AbilityActivatedThisSwitch = false;
            pokemon.FlashFireBoost = false;
            pokemon.Protecting = false;
            pokemon.IsMega = false;
            pokemon.IsDynamaxed = false;
            pokemon.DynamaxTurns = 0;
            pokemon.IsTerastallized = false;
        }
        side.MegaUsed = false;
        side.ZMoveUsed = false;
        side.DynamaxUsed = false;
        side.TeraUsed = false;
        side.Active.Clear();
        var activeCount = State.Format == BattleFormat.Doubles ? 2 : 1;
        side.Active.AddRange(Enumerable.Range(0, side.Team.Count).Where(index => !side.Team[index].Fainted).Take(activeCount));
    }

    private void ExecuteMove(int sideIndex, BattleChoice choice)
    {
        var user = ActivePokemon(sideIndex, choice.Slot);
        if (user is null || user.Fainted) return;
        var moveSlot = user.Moves.FirstOrDefault(x => x.MoveId.Equals(choice.MoveId, StringComparison.OrdinalIgnoreCase));
        var isStruggle = moveSlot is null && choice.MoveId.Equals("struggle", StringComparison.OrdinalIgnoreCase) && user.Moves.All(x => x.Pp <= 0);
        if ((!isStruggle && (moveSlot is null || moveSlot.Pp <= 0)) || !Config.MoveById.TryGetValue(choice.MoveId, out var move)) return;

        if (!CanAct(user)) return;
        if (moveSlot is not null) moveSlot.Pp--;
        if (choice.SpecialAction != SpecialAction.None && !ActivateSpecial(sideIndex, user, move, choice.SpecialAction)) return;
        ActivateProtean(user, move);
        Emit("MoveUsed", ("side", sideIndex), ("slot", choice.Slot), ("pokemon", user.Name),
            ("pokemonInstanceId", user.InstanceId), ("moveId", move.Id), ("move", move.NameZh));

        var targets = ResolveExecutionTargets(sideIndex, choice.Slot, choice, move);
        if (targets.Count == 0 && move.Target != MoveTarget.Self) return;
        if (!Config.EffectById.TryGetValue(move.EffectId, out var effect)) effect = Config.EffectById["damage"];
        var hitCountStep = effect.Steps.FirstOrDefault(x => x.Operation == EffectOperation.MultiHit);
        var hitCount = hitCountStep is null ? 1 : Random.NextInt(2, Math.Max(2, (int)hitCountStep.Value));

        foreach (var targetRef in targets)
        {
            var target = ActivePokemon(targetRef.Side, targetRef.Slot);
            if (target is null || target.Fainted) continue;
            if (target.Protecting && move.Target != MoveTarget.Self)
            {
                Emit("MoveBlocked", ("target", target.Name), ("targetInstanceId", target.InstanceId), ("reason", "protect"));
                continue;
            }
            if (move.Accuracy > 0 && Random.NextInt(1, 100) > EffectiveAccuracy(user, target, move.Accuracy))
            {
                Emit("MoveMissed", ("user", user.Name), ("userInstanceId", user.InstanceId),
                    ("target", target.Name), ("targetInstanceId", target.InstanceId), ("moveId", move.Id));
                continue;
            }
            if (TryApplyAbilityImmunity(target, move)) continue;

            var totalDamage = 0;
            var damageBudget = Math.Max(1, (int)Math.Floor(target.MaxHp * _rules.Constants.MaxDamageFractionPerMove));
            for (var hit = 1; hit <= hitCount && !target.Fainted && totalDamage < damageBudget; hit++)
            {
                if (move.Category != MoveCategory.Status && move.Power > 0)
                {
                    var critical = Random.NextInt(1, _rules.Definition.Generation >= 7 ? 24 : 16) == 1;
                    var category = _rules.ResolveCategory(move);
                    var abilityModifiers = AbilityRules.DamageModifiers(user, target, move, category);
                    foreach (var abilityId in abilityModifiers.Sources)
                        EmitAbilityActivation(abilityId, AbilityRules.Matches(user, abilityId) ? user : target, "damage_modifier",
                            ("moveId", move.Id), ("power", abilityModifiers.Power), ("attack", abilityModifiers.Attack), ("defense", abilityModifiers.Defense));
                    var breakdown = DamageCalculator.Calculate(_rules, user, target, move, Random, State.Field, critical, targets.Count > 1, choice.SpecialAction, abilityModifiers);
                    if (breakdown.TypeEffectiveness <= 0)
                    {
                        Emit("MoveImmune", ("target", target.Name), ("targetInstanceId", target.InstanceId), ("moveType", move.Type));
                        break;
                    }
                    if (critical) Emit("CriticalHit", ("target", target.Name), ("targetInstanceId", target.InstanceId));
                    var remainingBudget = damageBudget - totalDamage;
                    var damage = Math.Min(target.CurrentHp, Math.Min(breakdown.FinalDamage, remainingBudget));
                    if (AbilityRules.Matches(target, "sturdy") && target.CurrentHp == target.MaxHp && damage >= target.CurrentHp)
                    {
                        damage = Math.Max(0, target.CurrentHp - 1);
                        EmitAbilityActivation("sturdy", target, "survive", ("moveId", move.Id));
                    }
                    target.CurrentHp -= damage;
                    totalDamage += damage;
                    Emit("DamageApplied", ("source", user.Name), ("sourceInstanceId", user.InstanceId),
                        ("target", target.Name), ("targetInstanceId", target.InstanceId), ("amount", damage),
                        ("remainingHp", target.CurrentHp), ("hit", hit), ("breakdown", breakdown));
                    if (damage > 0 && move.MakesContact) ApplyContactAbilities(user, target, move);
                }
                totalDamage += ApplyEffectSteps(effect, user, target, totalDamage, damageBudget - totalDamage, sideIndex, targetRef.Side);
            }
        }
    }

    private int ApplyEffectSteps(EffectDefinition effect, PokemonState user, PokemonState target, int totalDamage, int remainingDamageBudget, int userSide, int targetSide)
    {
        var additionalDamage = 0;
        foreach (var step in effect.Steps.OrderBy(x => x.Order))
        {
            if (step.Operation is EffectOperation.Damage or EffectOperation.MultiHit) continue;
            if (step.Chance < 100 && Random.NextInt(1, 100) > step.Chance) continue;
            var affected = step.Target == EffectTarget.User ? user : target;
            switch (step.Operation)
            {
                case EffectOperation.FixedDamage:
                {
                    var damage = Math.Min(target.CurrentHp, Math.Min(Math.Max(0, remainingDamageBudget - additionalDamage), Math.Max(1, (int)step.Value)));
                    if (damage <= 0) break;
                    target.CurrentHp -= damage;
                    additionalDamage += damage;
                    Emit("DamageApplied", ("source", user.Name), ("sourceInstanceId", user.InstanceId),
                        ("target", target.Name), ("targetInstanceId", target.InstanceId), ("amount", damage),
                        ("remainingHp", target.CurrentHp), ("fixed", true));
                    break;
                }
                case EffectOperation.Status when affected.Status == MajorStatus.None && Enum.TryParse<MajorStatus>(step.Parameter, true, out var status):
                    affected.Status = status;
                    affected.StatusCounter = status == MajorStatus.Sleep ? Random.NextInt(1, 3) : 0;
                    Emit("StatusApplied", ("target", affected.Name), ("targetInstanceId", affected.InstanceId), ("status", status.ToString()));
                    break;
                case EffectOperation.StatStage:
                {
                    var old = affected.Stage(step.Parameter);
                    var value = Math.Clamp(old + (int)step.Value, -6, 6);
                    affected.StatStages[step.Parameter] = value;
                    Emit("StatStageChanged", ("target", affected.Name), ("targetInstanceId", affected.InstanceId),
                        ("stat", step.Parameter), ("from", old), ("to", value));
                    break;
                }
                case EffectOperation.HealPercent:
                    Heal(affected, (int)Math.Floor(affected.MaxHp * step.Value), "move");
                    break;
                case EffectOperation.DrainPercent:
                    Heal(user, (int)Math.Floor(totalDamage * step.Value), "drain");
                    break;
                case EffectOperation.RecoilPercent:
                {
                    var recoil = Math.Min(user.CurrentHp, Math.Max(1, (int)Math.Floor(totalDamage * step.Value)));
                    user.CurrentHp -= recoil;
                    Emit("DamageApplied", ("source", user.Name), ("sourceInstanceId", user.InstanceId),
                        ("target", user.Name), ("targetInstanceId", user.InstanceId), ("amount", recoil),
                        ("remainingHp", user.CurrentHp), ("reason", "recoil"));
                    break;
                }
                case EffectOperation.Weather:
                    State.Field.Weather = step.Parameter;
                    State.Field.WeatherTurns = (int)Math.Max(1, step.Value);
                    Emit("WeatherChanged", ("weather", step.Parameter), ("turns", State.Field.WeatherTurns));
                    break;
                case EffectOperation.Terrain:
                    State.Field.Terrain = step.Parameter;
                    State.Field.TerrainTurns = (int)Math.Max(1, step.Value);
                    Emit("TerrainChanged", ("terrain", step.Parameter), ("turns", State.Field.TerrainTurns));
                    break;
                case EffectOperation.Protect:
                    user.Protecting = true;
                    Emit("ProtectApplied", ("target", user.Name));
                    break;
                case EffectOperation.ForceSwitch:
                    ForceSwitch(targetSide);
                    break;
                case EffectOperation.ClearStages:
                    affected.StatStages.Clear();
                    Emit("StatStagesCleared", ("target", affected.Name));
                    break;
            }
        }
        return additionalDamage;
    }

    private bool CanAct(PokemonState pokemon)
    {
        if (pokemon.Status == MajorStatus.Sleep)
        {
            pokemon.StatusCounter--;
            if (pokemon.StatusCounter <= 0)
            {
                pokemon.Status = MajorStatus.None;
                Emit("StatusCured", ("target", pokemon.Name), ("status", "Sleep"));
            }
            else
            {
                Emit("ActionSkipped", ("target", pokemon.Name), ("reason", "sleep"));
                return false;
            }
        }
        if (pokemon.Status == MajorStatus.Freeze)
        {
            if (Random.NextInt(1, 100) <= 20)
            {
                pokemon.Status = MajorStatus.None;
                Emit("StatusCured", ("target", pokemon.Name), ("status", "Freeze"));
            }
            else
            {
                Emit("ActionSkipped", ("target", pokemon.Name), ("reason", "freeze"));
                return false;
            }
        }
        if (pokemon.Status == MajorStatus.Paralysis && Random.NextInt(1, 100) <= 25)
        {
            Emit("ActionSkipped", ("target", pokemon.Name), ("reason", "paralysis"));
            return false;
        }
        return true;
    }

    private bool ActivateSpecial(int sideIndex, PokemonState pokemon, MoveDefinition move, SpecialAction action)
    {
        var side = State.Sides[sideIndex];
        if (!_rules.AllowsSpecialAction(action) || !CanUseSpecial(side, pokemon, move, action)) return false;
        switch (action)
        {
            case SpecialAction.Mega:
                side.MegaUsed = true;
                pokemon.IsMega = true;
                break;
            case SpecialAction.ZMove:
                side.ZMoveUsed = true;
                break;
            case SpecialAction.Dynamax:
                side.DynamaxUsed = true;
                pokemon.IsDynamaxed = true;
                pokemon.DynamaxTurns = 3;
                pokemon.MaxHp *= 2;
                pokemon.CurrentHp *= 2;
                break;
            case SpecialAction.Terastallize:
                side.TeraUsed = true;
                pokemon.IsTerastallized = true;
                pokemon.CurrentTypes = [pokemon.TeraType];
                break;
        }
        Emit("SpecialActivated", ("side", sideIndex), ("pokemon", pokemon.Name),
            ("pokemonInstanceId", pokemon.InstanceId), ("action", action.ToString()));
        return true;
    }

    private bool CanUseSpecial(BattleSideState side, PokemonState pokemon, MoveDefinition move, SpecialAction action)
    {
        if (!_rules.AllowsSpecialAction(action)) return false;
        return action switch
        {
            SpecialAction.None => true,
            SpecialAction.Mega => !side.MegaUsed && !pokemon.IsMega,
            SpecialAction.ZMove => !side.ZMoveUsed && move.Category != MoveCategory.Status,
            SpecialAction.Dynamax => !side.DynamaxUsed && !pokemon.IsDynamaxed && move.Category != MoveCategory.Status,
            SpecialAction.Terastallize => !side.TeraUsed && !pokemon.IsTerastallized,
            _ => false
        };
    }

    private void EndTurn()
    {
        foreach (var side in State.Sides)
        foreach (var index in side.Active.ToList())
        {
            var pokemon = side.Team[index];
            if (pokemon.Fainted) continue;
            var denominator = pokemon.Status switch
            {
                MajorStatus.Burn => _rules.Constants.BurnResidualDenominator,
                MajorStatus.Poison => 8,
                MajorStatus.BadPoison => Math.Max(1, 16 / Math.Max(1, ++pokemon.StatusCounter)),
                _ => 0
            };
            if (denominator > 0) ApplyResidual(pokemon, Math.Max(1, pokemon.MaxHp / denominator), pokemon.Status.ToString());
            if (!pokemon.Fainted && State.Field.Weather is "sandstorm" or "hail" && !WeatherImmune(pokemon, State.Field.Weather))
                ApplyResidual(pokemon, Math.Max(1, pokemon.MaxHp / 16), State.Field.Weather);
            if (!pokemon.Fainted && pokemon.ItemId.Equals("leftovers", StringComparison.OrdinalIgnoreCase))
                Heal(pokemon, Math.Max(1, pokemon.MaxHp / 16), "leftovers");
            pokemon.Protecting = false;
            if (pokemon.IsDynamaxed && --pokemon.DynamaxTurns <= 0)
            {
                pokemon.IsDynamaxed = false;
                pokemon.MaxHp = Math.Max(1, pokemon.MaxHp / 2);
                pokemon.CurrentHp = Math.Min(pokemon.MaxHp, Math.Max(1, pokemon.CurrentHp / 2));
                Emit("SpecialEnded", ("pokemon", pokemon.Name), ("action", "Dynamax"));
            }
        }

        TickField("weather");
        TickField("terrain");
        CheckFaintsAndAutoSwitch();
        CheckBattleEnd();
    }

    private void TickField(string kind)
    {
        if (kind == "weather" && State.Field.WeatherTurns > 0 && --State.Field.WeatherTurns == 0)
        {
            var old = State.Field.Weather;
            State.Field.Weather = string.Empty;
            Emit("WeatherEnded", ("weather", old));
        }
        if (kind == "terrain" && State.Field.TerrainTurns > 0 && --State.Field.TerrainTurns == 0)
        {
            var old = State.Field.Terrain;
            State.Field.Terrain = string.Empty;
            Emit("TerrainEnded", ("terrain", old));
        }
    }

    private void CheckFaintsAndAutoSwitch()
    {
        foreach (var side in State.Sides)
        for (var slot = 0; slot < side.Active.Count; slot++)
        {
            var current = side.Team[side.Active[slot]];
            if (!current.Fainted) continue;
            if (!State.EventLog.Any(x => x.Type == "PokemonFainted" && x.Data.GetValueOrDefault("instanceId")?.ToString() == current.InstanceId))
                Emit("PokemonFainted", ("side", side.Side), ("slot", slot), ("instanceId", current.InstanceId), ("pokemon", current.Name));
            var replacement = Enumerable.Range(0, side.Team.Count).FirstOrDefault(index => !side.Team[index].Fainted && !side.Active.Contains(index), -1);
            if (replacement >= 0) ExecuteSwitch(side.Side, slot, replacement, "forced");
        }
    }

    private void CheckBattleEnd()
    {
        var alive = State.Sides.Select(side => side.Team.Any(x => !x.Fainted)).ToArray();
        if (alive[0] && alive[1]) return;
        State.WinnerSide = alive[0] == alive[1] ? null : alive[0] ? 0 : 1;
        State.Phase = BattlePhase.Ended;
        Emit("BattleEnded", ("winnerSide", State.WinnerSide), ("reason", alive[0] == alive[1] ? "draw" : "all_fainted"));
    }

    private int? DetermineLeaderByRemainingTeam()
    {
        var scores = State.Sides.Select(side => new
        {
            Alive = side.Team.Count(pokemon => !pokemon.Fainted),
            HpRatio = side.Team.Sum(pokemon => pokemon.CurrentHp / (double)Math.Max(1, pokemon.MaxHp))
        }).ToArray();
        if (scores[0].Alive != scores[1].Alive) return scores[0].Alive > scores[1].Alive ? 0 : 1;
        if (Math.Abs(scores[0].HpRatio - scores[1].HpRatio) < 0.000001) return null;
        return scores[0].HpRatio > scores[1].HpRatio ? 0 : 1;
    }

    private void ExecuteSwitch(int sideIndex, int slot, int teamIndex, string reason)
    {
        var side = State.Sides[sideIndex];
        if (slot < 0 || slot >= side.Active.Count || teamIndex < 0 || teamIndex >= side.Team.Count || side.Team[teamIndex].Fainted || side.Active.Contains(teamIndex)) return;
        var from = side.Team[side.Active[slot]];
        ResetOnSwitchOut(from);
        side.Active[slot] = teamIndex;
        var to = side.Team[teamIndex];
        PrepareSwitchIn(to);
        Emit("PokemonSwitched", ("side", sideIndex), ("slot", slot),
            ("from", from.Name), ("fromInstanceId", from.InstanceId),
            ("to", to.Name), ("toInstanceId", to.InstanceId), ("reason", reason));
        TriggerEntryAbility(sideIndex, slot, to);
    }

    private void ForceSwitch(int sideIndex)
    {
        var side = State.Sides[sideIndex];
        var replacement = Enumerable.Range(0, side.Team.Count).FirstOrDefault(index => !side.Team[index].Fainted && !side.Active.Contains(index), -1);
        if (replacement >= 0) ExecuteSwitch(sideIndex, 0, replacement, "move");
    }

    private int EffectiveSpeed(PokemonState pokemon)
    {
        var speed = _rules.EffectiveStat(pokemon.Speed, pokemon.Stage("Speed"));
        if (pokemon.Status == MajorStatus.Paralysis) speed = Math.Max(1, (int)Math.Floor(speed * (_rules.Definition.Generation <= 6 ? 0.25 : 0.5)));
        speed = Math.Max(1, (int)Math.Floor(speed * AbilityRules.SpeedModifier(pokemon, State.Field)));
        return speed;
    }

    private static int EffectiveAccuracy(PokemonState user, PokemonState target, int baseAccuracy)
    {
        var stage = Math.Clamp(user.Stage("Accuracy") - target.Stage("Evasion"), -6, 6);
        var modifier = stage >= 0 ? (3d + stage) / 3d : 3d / (3d - stage);
        return Math.Clamp((int)Math.Floor(baseAccuracy * modifier), 1, 100);
    }

    private IReadOnlyList<TargetRef> ResolveLegalTargets(int side, int slot, MoveDefinition move)
    {
        IEnumerable<(int Side, int Slot)> candidates = move.Target switch
        {
            MoveTarget.Self => [(side, slot)],
            MoveTarget.Ally => Enumerable.Range(0, State.Sides[side].Active.Count).Where(x => x != slot).Select(x => (side, x)),
            MoveTarget.AllActive => State.Sides.SelectMany(x => Enumerable.Range(0, x.Active.Count).Select(slotIndex => (x.Side, slotIndex))),
            _ => Enumerable.Range(0, State.Sides[1 - side].Active.Count).Select(x => (1 - side, x))
        };
        return candidates.Select(x => ActivePokemon(x.Side, x.Slot) is { } p ? new TargetRef(x.Side, x.Slot, p.InstanceId, p.Name) : null)
            .Where(x => x is not null).Cast<TargetRef>().ToList();
    }

    private List<TargetRef> ResolveExecutionTargets(int side, int slot, BattleChoice choice, MoveDefinition move)
    {
        var legal = ResolveLegalTargets(side, slot, move).ToList();
        return move.Target is MoveTarget.AllOpponents or MoveTarget.AllActive
            ? legal
            : legal.Where(x => x.Side == choice.TargetSide && x.Slot == choice.TargetSlot).DefaultIfEmpty(legal.FirstOrDefault()).Where(x => x is not null).ToList()!;
    }

    private bool IsLegalChoice(BattleChoice choice, LegalChoiceSet set)
    {
        if (choice.Kind == ChoiceKind.Switch) return set.Switches.Contains(choice.SwitchIndex);
        if (choice.Kind != ChoiceKind.Move) return false;
        var move = set.Moves.FirstOrDefault(x => x.MoveId.Equals(choice.MoveId, StringComparison.OrdinalIgnoreCase));
        return move is not null && move.SpecialActions.Contains(choice.SpecialAction) &&
               (move.Targets.Count == 0 || move.Targets.Any(x => x.Side == choice.TargetSide && x.Slot == choice.TargetSlot));
    }

    private PokemonState? ActivePokemon(int side, int slot)
    {
        if (side is < 0 or > 1) return null;
        var sideState = State.Sides[side];
        if (slot < 0 || slot >= sideState.Active.Count) return null;
        var index = sideState.Active[slot];
        return index < 0 || index >= sideState.Team.Count ? null : sideState.Team[index];
    }

    private void Heal(PokemonState target, int amount, string reason)
    {
        if (amount <= 0 || target.Fainted) return;
        var actual = Math.Min(amount, target.MaxHp - target.CurrentHp);
        if (actual <= 0) return;
        target.CurrentHp += actual;
        Emit("HealApplied", ("target", target.Name), ("targetInstanceId", target.InstanceId),
            ("amount", actual), ("remainingHp", target.CurrentHp), ("reason", reason));
    }

    private void ApplyResidual(PokemonState target, int amount, string reason)
    {
        var actual = Math.Min(target.CurrentHp, amount);
        target.CurrentHp -= actual;
        Emit("DamageApplied", ("target", target.Name), ("targetInstanceId", target.InstanceId),
            ("amount", actual), ("remainingHp", target.CurrentHp), ("reason", reason));
    }

    private static bool WeatherImmune(PokemonState pokemon, string weather) => weather switch
    {
        "sandstorm" => pokemon.CurrentTypes.Any(x => x is "Rock" or "Ground" or "Steel"),
        "hail" => pokemon.CurrentTypes.Contains("Ice", StringComparer.OrdinalIgnoreCase),
        _ => true
    };

    private BattleEvent Emit(string type, params (string Key, object? Value)[] values)
    {
        var evt = new BattleEvent(++State.EventSequence, State.Turn, type, values.ToDictionary(x => x.Key, x => x.Value));
        State.EventLog.Add(evt);
        Trace("Debug", "event", type, ("eventSequence", evt.Sequence), ("data", evt.Data));
        return evt;
    }

    private CommandResult Reject(string code, string message, int side)
    {
        Trace("Warning", "command", "choice_rejected", ("side", side), ("code", code), ("message", message));
        return CommandResult.Error(code, message);
    }

    private void Trace(string level, string category, string message, params (string Key, object? Value)[] values)
    {
        State.DebugLog.Add(new(++State.DebugSequence, State.Turn, level, category, message,
            values.ToDictionary(x => x.Key, x => x.Value)));
    }

    private void TriggerInitialAbilities()
    {
        foreach (var side in State.Sides)
        for (var slot = 0; slot < side.Active.Count; slot++)
            if (ActivePokemon(side.Side, slot) is { } pokemon) TriggerEntryAbility(side.Side, slot, pokemon);
    }

    private void TriggerEntryAbility(int sideIndex, int slot, PokemonState pokemon)
    {
        if (string.IsNullOrWhiteSpace(pokemon.AbilityId)) return;
        if (AbilityRules.Matches(pokemon, "sand-stream"))
        {
            State.Field.Weather = "sandstorm";
            State.Field.WeatherTurns = 5;
            EmitAbilityActivation("sand-stream", pokemon, "switch_in");
            Emit("WeatherChanged", ("weather", "sandstorm"), ("turns", 5), ("source", "ability"));
        }
        else if (AbilityRules.Matches(pokemon, "intimidate"))
        {
            EmitAbilityActivation("intimidate", pokemon, "switch_in");
            foreach (var targetSlot in Enumerable.Range(0, State.Sides[1 - sideIndex].Active.Count))
            {
                var target = ActivePokemon(1 - sideIndex, targetSlot);
                if (target is null || target.Fainted) continue;
                var old = target.Stage("Attack");
                target.StatStages["Attack"] = Math.Max(-6, old - 1);
                Emit("StatStageChanged", ("target", target.Name), ("targetInstanceId", target.InstanceId),
                    ("stat", "Attack"), ("from", old), ("to", target.Stage("Attack")), ("source", "intimidate"));
            }
        }
        else if (AbilityRules.Matches(pokemon, "trace"))
        {
            var copied = State.Sides[1 - sideIndex].Active
                .Select(index => State.Sides[1 - sideIndex].Team[index].AbilityId)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x.Equals("trace", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(copied))
            {
                pokemon.AbilityId = copied;
                EmitAbilityActivation("trace", pokemon, "switch_in", ("copiedAbilityId", copied), ("slot", slot));
            }
        }
    }

    private void ActivateProtean(PokemonState pokemon, MoveDefinition move)
    {
        if (!AbilityRules.Matches(pokemon, "protean") || pokemon.IsTerastallized) return;
        if (_rules.Definition.Generation >= 9 && pokemon.AbilityActivatedThisSwitch) return;
        if (pokemon.CurrentTypes.Count == 1 && pokemon.CurrentTypes[0].Equals(move.Type, StringComparison.OrdinalIgnoreCase)) return;
        pokemon.CurrentTypes = [move.Type];
        pokemon.AbilityActivatedThisSwitch = true;
        EmitAbilityActivation("protean", pokemon, "before_move", ("type", move.Type), ("moveId", move.Id));
    }

    private bool TryApplyAbilityImmunity(PokemonState target, MoveDefinition move)
    {
        var abilityId = AbilityRules.ImmunityAbility(target, move);
        if (abilityId is null) return false;
        EmitAbilityActivation(abilityId, target, "move_immunity", ("moveId", move.Id), ("moveType", move.Type));
        if (abilityId == "water-absorb") Heal(target, Math.Max(1, target.MaxHp / 4), "water-absorb");
        if (abilityId == "flash-fire") target.FlashFireBoost = true;
        Emit("MoveImmune", ("target", target.Name), ("targetInstanceId", target.InstanceId),
            ("moveType", move.Type), ("abilityId", abilityId));
        return true;
    }

    private void ApplyContactAbilities(PokemonState attacker, PokemonState defender, MoveDefinition move)
    {
        if (AbilityRules.Matches(defender, "rough-skin") && !attacker.Fainted)
        {
            EmitAbilityActivation("rough-skin", defender, "contact", ("attacker", attacker.Name), ("moveId", move.Id));
            ApplyResidual(attacker, Math.Max(1, attacker.MaxHp / 8), "rough-skin");
        }
        if (AbilityRules.Matches(defender, "static") && !attacker.Fainted && attacker.Status == MajorStatus.None &&
            !attacker.CurrentTypes.Contains("Electric", StringComparer.OrdinalIgnoreCase) && Random.NextInt(1, 100) <= 30)
        {
            EmitAbilityActivation("static", defender, "contact", ("attacker", attacker.Name), ("moveId", move.Id));
            attacker.Status = MajorStatus.Paralysis;
            Emit("StatusApplied", ("target", attacker.Name), ("targetInstanceId", attacker.InstanceId),
                ("status", MajorStatus.Paralysis.ToString()), ("source", "static"));
        }
    }

    private void EmitAbilityActivation(string abilityId, PokemonState pokemon, string trigger, params (string Key, object? Value)[] values)
    {
        var data = new List<(string Key, object? Value)>
        {
            ("abilityId", abilityId), ("pokemon", pokemon.Name), ("pokemonInstanceId", pokemon.InstanceId), ("trigger", trigger)
        };
        data.AddRange(values);
        Emit("AbilityActivated", data.ToArray());
    }

    private static void ResetOnSwitchOut(PokemonState pokemon)
    {
        pokemon.AbilityId = pokemon.OriginalAbilityId;
        pokemon.AbilityActivatedThisSwitch = false;
        pokemon.FlashFireBoost = false;
        if (!pokemon.IsTerastallized) pokemon.CurrentTypes = [.. pokemon.OriginalTypes];
    }

    private static void PrepareSwitchIn(PokemonState pokemon)
    {
        pokemon.AbilityId = pokemon.OriginalAbilityId;
        pokemon.AbilityActivatedThisSwitch = false;
        pokemon.FlashFireBoost = false;
        if (!pokemon.IsTerastallized) pokemon.CurrentTypes = [.. pokemon.OriginalTypes];
    }

    private sealed record QueuedAction(int Side, string ActorInstanceId, BattleChoice Choice, int Priority, int Speed, int TieBreaker);
}
