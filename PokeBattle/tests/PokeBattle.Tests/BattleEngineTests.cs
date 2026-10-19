using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PokeBattle.Domain;
using PokeBattle.Server;
using Xunit;

namespace PokeBattle.Tests;

public sealed class BattleEngineTests
{
    private static readonly BattleConfig Config = LoadConfig();

    [Fact]
    public void Configuration_HasNineRulesetsAndPlayableRepresentativeData()
    {
        Assert.Equal(9, Config.RuleSets.Count);
        Assert.True(Config.Species.Count >= 12);
        Assert.True(Config.Moves.Count >= 30);
        Assert.All(Config.PresetTeams, team => Assert.Equal(6, team.Members.Count));
        Assert.All(Config.PresetTeams, team =>
            Assert.Equal(team.Members.Count, team.Members.Select(member => member.SpeciesId).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
        Assert.All(Config.PresetTeams.SelectMany(team => team.Members), member =>
        {
            Assert.False(string.IsNullOrWhiteSpace(member.AbilityId));
            Assert.InRange(member.Moves.Count, 2, 4);
            Assert.Equal(member.Moves.Count, member.Moves.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
        var dungeon = Assert.Single(Config.PveDungeons);
        Assert.Equal("test-training-ground", dungeon.Id);
        Assert.Equal(2, dungeon.Waves.Count);
    }

    [Fact]
    public void TypeChart_UsesGenerationSpecificGhostPsychicBehavior()
    {
        var gen1 = new ConfiguredRuleSet(Config, Config.RuleSetById["gen1-rby"]);
        var gen2 = new ConfiguredRuleSet(Config, Config.RuleSetById["gen2-gsc"]);
        Assert.Equal(0, gen1.TypeEffectiveness("Ghost", ["Psychic"]));
        Assert.Equal(2, gen2.TypeEffectiveness("Ghost", ["Psychic"]));
    }

    [Fact]
    public void SpecialActions_AreGatedByGeneration()
    {
        var gen6 = new ConfiguredRuleSet(Config, Config.RuleSetById["gen6-xy"]);
        var gen7 = new ConfiguredRuleSet(Config, Config.RuleSetById["gen7-sm"]);
        var gen8 = new ConfiguredRuleSet(Config, Config.RuleSetById["gen8-swsh"]);
        var gen9 = new ConfiguredRuleSet(Config, Config.RuleSetById["gen9-sv"]);
        Assert.True(gen6.AllowsSpecialAction(SpecialAction.Mega));
        Assert.True(gen7.AllowsSpecialAction(SpecialAction.ZMove));
        Assert.True(gen8.AllowsSpecialAction(SpecialAction.Dynamax));
        Assert.True(gen9.AllowsSpecialAction(SpecialAction.Terastallize));
        Assert.False(gen9.AllowsSpecialAction(SpecialAction.Mega));
    }

    [Fact]
    public void EveryCombatant_KeepsTwoToFourMovesAndAnAbilityAcrossLabRulesets()
    {
        var state = BattleFactory.Create(Config, "gen1-rby", BattleFormat.Singles, "modern-violet", "modern-scarlet", 9, false, false);

        Assert.All(state.Sides.SelectMany(side => side.Team), pokemon =>
        {
            Assert.InRange(pokemon.Moves.Count, 2, 4);
            Assert.False(string.IsNullOrWhiteSpace(pokemon.AbilityId));
        });
    }

    [Fact]
    public void BattleFactory_AcceptsAConfiguredTwoMoveCombatant()
    {
        var config = LoadConfig();
        config.TeamById["classic-red"].Members[0].Moves.RemoveRange(2, 2);

        var state = BattleFactory.Create(config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 10, false, false);

        Assert.Equal(2, state.Sides[0].Team[0].Moves.Count);
    }

    [Fact]
    public void SameSeedAndCommands_ProduceIdenticalEventLog()
    {
        var left = NewEngine(424242);
        var right = NewEngine(424242);
        PlayOneTurn(left);
        PlayOneTurn(right);
        Assert.Equal(JsonSerializer.Serialize(left.State.EventLog), JsonSerializer.Serialize(right.State.EventLog));
        Assert.Equal(left.State.RandomState, right.State.RandomState);
    }

    [Fact]
    public void RandomizedTeamOrder_IsDeterministicAndKeepsSpeciesUnique()
    {
        var first = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 20260810, false, true,
            randomizeTeamOrder: true);
        var replay = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 20260810, false, true,
            randomizeTeamOrder: true);
        var alternate = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 20260811, false, true,
            randomizeTeamOrder: true);

        Assert.Equal(first.Sides[0].Team.Select(x => x.SpeciesId), replay.Sides[0].Team.Select(x => x.SpeciesId));
        Assert.NotEqual(first.Sides[0].Team.Select(x => x.SpeciesId), alternate.Sides[0].Team.Select(x => x.SpeciesId));
        Assert.All(first.Sides, side =>
            Assert.Equal(side.Team.Count, side.Team.Select(x => x.SpeciesId).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }

    [Fact]
    public void MoveUsed_IdentifiesTheExactCastingPokemon()
    {
        var engine = NewEngine(424242);
        var actors = engine.State.Sides.ToDictionary(
            side => side.Side,
            side => side.Team[side.Active[0]].InstanceId);

        PlayOneTurn(engine);

        var moveEvents = engine.State.EventLog.Where(x => x.Type == "MoveUsed").ToList();
        Assert.NotEmpty(moveEvents);
        Assert.All(moveEvents, battleEvent =>
        {
            var side = Convert.ToInt32(battleEvent.Data["side"]);
            Assert.Equal(actors[side], battleEvent.Data["pokemonInstanceId"]);
        });
    }

    [Fact]
    public void DamageEvents_IdentifyTheExactTargetPokemon()
    {
        var engine = NewEngine(424242);

        PlayOneTurn(engine);

        var instanceIds = engine.State.Sides.SelectMany(side => side.Team).Select(x => x.InstanceId).ToHashSet(StringComparer.Ordinal);
        var damageEvents = engine.State.EventLog.Where(x => x.Type == "DamageApplied").ToList();
        Assert.NotEmpty(damageEvents);
        Assert.All(damageEvents, battleEvent =>
        {
            var targetInstanceId = Assert.IsType<string>(battleEvent.Data["targetInstanceId"]);
            Assert.Contains(targetInstanceId, instanceIds);
        });
    }

    [Fact]
    public void BasicAi_OnlyReturnsLegalChoices()
    {
        var engine = NewEngine(100);
        var legal = engine.GetLegalChoices(0);
        var decision = new BasicBattleAi().Choose(engine, 0);
        Assert.Equal(legal.Count, decision.Choices.Count);
        foreach (var choice in decision.Choices)
            Assert.Contains(legal.Single(x => x.Slot == choice.Slot).Moves, x => x.MoveId == choice.MoveId);
    }

    [Fact]
    public void BasicAi_ChangesMoveAfterItsBestAttackMadeNoProgress()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 101, true, true);
        state.Sides[1].Active[0] = 1; // Venusaur makes Flamethrower the otherwise highest-scoring attack.
        var engine = new BattleEngine(Config, state);

        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "flamethrower", 1, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "protect", 1, 0)]).Accepted);
        Assert.Contains(state.EventLog, x => x.Type == "MoveBlocked" && x.Data.GetValueOrDefault("reason")?.ToString() == "protect");

        var decision = new BasicBattleAi().Choose(engine, 0);

        Assert.Equal("dragon-claw", Assert.Single(decision.Choices).MoveId);
        var penalized = Assert.Single(decision.Candidates, x => x.Choice.MoveId == "flamethrower");
        Assert.Contains("未造成对手伤害", penalized.Reason);
    }

    [Fact]
    public void BasicAi_RecognizesFixedDamageMovesAsBattleProgress()
    {
        var state = BattleFactory.Create(Config, "gen7-sm", BattleFormat.Singles, "classic-red", "classic-blue", 102, true, true);
        var active = state.Sides[0].Team[state.Sides[0].Active[0]];
        active.Moves.Clear();
        active.Moves.Add(new MoveSlotState { MoveId = "dragon-rage", Pp = 10, MaxPp = 10 });
        var engine = new BattleEngine(Config, state);

        var decision = new BasicBattleAi().Choose(engine, 0);

        var choice = Assert.Single(decision.Choices);
        Assert.Equal(ChoiceKind.Move, choice.Kind);
        Assert.Equal("dragon-rage", choice.MoveId);
        Assert.True(decision.Candidates.Single(x => x.Choice.MoveId == "dragon-rage").Score > 6);
    }

    [Fact]
    public void BasicAi_DoesNotUseProtectOnConsecutiveTurns()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 103, true, true);
        var engine = new BattleEngine(Config, state);

        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "protect", 0, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "protect", 1, 0)]).Accepted);

        var decision = new BasicBattleAi().Choose(engine, 0);

        Assert.NotEqual("protect", Assert.Single(decision.Choices).MoveId);
        var protect = Assert.Single(decision.Candidates, x => x.Choice.MoveId == "protect");
        Assert.Contains("避免连续使用保护类招式", protect.Reason);
    }

    [Fact]
    public void BasicAi_SwitchesAfterDifferentActionsRepeatedlyFailToReduceOpponentHp()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "modern-violet", "modern-scarlet", 104, true, true);
        var engine = new BattleEngine(Config, state);

        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "giga-drain", 1, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "protect", 1, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "bullet-seed", 1, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "protect", 1, 0)]).Accepted);

        var decision = new BasicBattleAi().Choose(engine, 0);

        Assert.Equal(ChoiceKind.Switch, Assert.Single(decision.Choices).Kind);
        Assert.Contains(decision.Candidates, candidate => candidate.Choice.Kind == ChoiceKind.Switch && candidate.Score >= 80);
    }

    [Fact]
    public void RepeatedTurnsWithoutHpLoss_EndAsAStalemate()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 105, false, false);
        var engine = new BattleEngine(Config, state);

        for (var turn = 0; turn < BattleEngine.MaxConsecutiveTurnsWithoutHpLoss; turn++)
        {
            Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "protect", 0, 0)]).Accepted);
            Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "protect", 1, 0)]).Accepted);
        }

        Assert.Equal(BattlePhase.Ended, state.Phase);
        Assert.Null(state.WinnerSide);
        Assert.Contains(state.EventLog, battleEvent => battleEvent.Type == "BattleEnded" &&
            battleEvent.Data.GetValueOrDefault("reason")?.ToString() == "stalemate");
    }

    [Fact]
    public void Doubles_ProvidesTwoActiveChoiceSetsAndSpreadTargets()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Doubles, "modern-violet", "modern-scarlet", 2026, false, false);
        var engine = new BattleEngine(Config, state);
        var legal = engine.GetLegalChoices(0);
        Assert.Equal(2, legal.Count);
        var surf = legal.SelectMany(x => x.Moves).FirstOrDefault(x => x.MoveId == "surf");
        if (surf is not null) Assert.Equal(2, surf.Targets.Count);
    }

    [Fact]
    public void InvalidOrRepeatedChoiceDoesNotAdvanceTurn()
    {
        var engine = NewEngine(7);
        var result = engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "not-a-move", 1, 0)]);
        Assert.False(result.Accepted);
        Assert.Equal(1, engine.State.Turn);
        Assert.False(engine.State.ChoiceLocked[0]);
    }

    [Fact]
    public async Task ChoiceDeadline_AutoSelectsForAnUnlockedHumanSide()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "modern-violet", "modern-scarlet", 15, false, true);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        using var room = new BattleRoom(Config, state, json, NullLogger.Instance, choiceTimeout: TimeSpan.FromMilliseconds(30));

        await room.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (state.Turn == 1) await Task.Delay(10, timeout.Token);

        Assert.Equal(TimeSpan.FromSeconds(15), BattleRoom.DefaultChoiceTimeout);
        Assert.Contains(state.CommandLog, command => command.Turn == 1 && command.Side == 0);
        Assert.Contains(state.EventLog, battleEvent => battleEvent.Turn == 1 && battleEvent.Type == "MoveUsed");
    }

    [Fact]
    public void FaintedQueuedActor_DoesNotUseItsMove()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "modern-violet", "modern-scarlet", 17, false, false);
        state.Sides[1].Team[0].CurrentHp = 1;
        state.Sides[1].Team[1].Moves.Clear();
        state.Sides[1].Team[1].Moves.Add(new MoveSlotState { MoveId = "shadow-ball", Pp = 15, MaxPp = 15 });
        var engine = new BattleEngine(Config, state);

        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "giga-drain", 1, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "shadow-ball", 0, 0)]).Accepted);

        Assert.True(state.Sides[1].Team[0].Fainted);
        Assert.DoesNotContain(state.EventLog, x => x.Type == "MoveUsed" && Convert.ToInt32(x.Data.GetValueOrDefault("side")) == 1);
        Assert.Contains(state.DebugLog, x => x.Message == "queued_action_skipped");
    }

    [Fact]
    public void Ai_SwitchesInsteadOfRepeatingAnIneffectiveMove()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 18, true, false);
        state.Sides[0].Active[0] = 3; // Snorlax: Normal move is type-immune, Ground move is blocked by Levitate.
        state.Sides[1].Active[0] = 3; // Gengar with Levitate.
        var engine = new BattleEngine(Config, state);

        var decision = new BasicBattleAi().Choose(engine, 0);

        Assert.Equal(ChoiceKind.Switch, Assert.Single(decision.Choices).Kind);
    }

    [Fact]
    public void TurnNinetyNine_DefenderSurvivalEndsBattleWithAttackerFailure()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 19, false, false);
        state.Turn = BattleEngine.MaxTurns;
        var engine = new BattleEngine(Config, state);

        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "protect", 0, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "protect", 1, 0)]).Accepted);

        Assert.Equal(BattlePhase.Ended, state.Phase);
        Assert.Equal(1, state.WinnerSide);
        Assert.Equal(BattleEngine.MaxTurns, state.Turn);
        Assert.Contains(state.EventLog, x => x.Type == "BattleEnded" && x.Data.GetValueOrDefault("reason")?.ToString() == "turn_limit");
    }

    [Fact]
    public void ExhaustedMoves_FallBackToStruggleSoBattleCanStillAdvance()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 20, false, false);
        state.Sides.SelectMany(side => side.Team).SelectMany(pokemon => pokemon.Moves).ToList().ForEach(move => move.Pp = 0);
        var engine = new BattleEngine(Config, state);

        var left = Assert.Single(engine.GetLegalChoices(0));
        var right = Assert.Single(engine.GetLegalChoices(1));
        Assert.Equal("struggle", Assert.Single(left.Moves).MoveId);
        Assert.Equal("struggle", Assert.Single(right.Moves).MoveId);

        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "struggle", 1, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "struggle", 0, 0)]).Accepted);
        Assert.Equal(2, state.Turn);
        Assert.Contains(state.EventLog, battleEvent => battleEvent.Type == "DamageApplied");
    }

    [Fact]
    public void DamagingMove_CannotRemoveMoreThanConfiguredMaxHpFraction()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 23, false, false);
        state.Sides[1].Active[0] = 1; // Venusaur is weak to Flamethrower.
        var target = state.Sides[1].Team[1];
        var engine = new BattleEngine(Config, state);

        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "flamethrower", 1, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "growl", 0, 0)]).Accepted);

        var damage = DamageTo(engine, target.Name);
        var ceiling = (int)Math.Floor(target.MaxHp * Config.ConstantsByGeneration[9].MaxDamageFractionPerMove);
        Assert.InRange(damage, 1, ceiling);
        Assert.False(target.Fainted);
    }

    [Theory]
    [InlineData(BattleFormat.Singles)]
    [InlineData(BattleFormat.Doubles)]
    public void AiSoak_MultipleSeedsKeepBattleStateLegal(BattleFormat format)
    {
        var ai = new BasicBattleAi();
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var state = BattleFactory.Create(Config, "gen9-sv", format, "modern-violet", "modern-scarlet", seed, true, true);
            var engine = new BattleEngine(Config, state);

            for (var turn = 0; turn < 20 && engine.State.Phase != BattlePhase.Ended; turn++)
            {
                var left = ai.Choose(engine, 0);
                Assert.Equal(engine.GetLegalChoices(0).Count, left.Choices.Count);
                Assert.True(engine.SubmitChoices(0, left.Choices).Accepted);

                var right = ai.Choose(engine, 1);
                Assert.Equal(engine.GetLegalChoices(1).Count, right.Choices.Count);
                Assert.True(engine.SubmitChoices(1, right.Choices).Accepted);

                Assert.All(engine.State.Sides.SelectMany(x => x.Team), pokemon =>
                {
                    Assert.InRange(pokemon.CurrentHp, 0, pokemon.MaxHp);
                    Assert.All(pokemon.Moves, move => Assert.InRange(move.Pp, 0, move.MaxPp));
                    Assert.All(pokemon.StatStages.Values, stage => Assert.InRange(stage, -6, 6));
                });
            }
        }
    }

    [Fact]
    public void SandStream_ActivatesOnInitialDoublesEntry()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Doubles, "modern-violet", "modern-scarlet", 99, false, false);
        var engine = new BattleEngine(Config, state);

        Assert.Equal("sandstorm", engine.State.Field.Weather);
        Assert.Contains(engine.State.EventLog, x => x.Type == "AbilityActivated" && x.Data.GetValueOrDefault("abilityId")?.ToString() == "sand-stream");
        Assert.Contains(engine.State.DebugLog, x => x.Category == "event" && x.Message == "AbilityActivated");
    }

    [Fact]
    public void Levitate_BlocksGroundDamageAndEmitsExplanation()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", 101, false, false);
        state.Sides[0].Active[0] = 3; // Snorlax with Earthquake
        state.Sides[1].Active[0] = 3; // Gengar with Levitate
        var engine = new BattleEngine(Config, state);

        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "earthquake", 1, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, "shadow-ball", 0, 0)]).Accepted);

        Assert.Contains(engine.State.EventLog, x => x.Type == "MoveImmune" && x.Data.GetValueOrDefault("abilityId")?.ToString() == "levitate");
        Assert.DoesNotContain(engine.State.EventLog, x => x.Type == "DamageApplied" && x.Data.GetValueOrDefault("target")?.ToString() == "耿鬼");
    }

    [Fact]
    public void Blaze_IncreasesDamageAtOneThirdHp()
    {
        var boosted = NewEngine(303);
        var baseline = NewEngine(303);
        boosted.State.Sides[0].Team[0].CurrentHp = boosted.State.Sides[0].Team[0].MaxHp / 3;
        baseline.State.Sides[0].Team[0].CurrentHp = baseline.State.Sides[0].Team[0].MaxHp / 3;
        baseline.State.Sides[0].Team[0].AbilityId = string.Empty;

        PlaySpecificTurn(boosted, "flamethrower", "ice-beam");
        PlaySpecificTurn(baseline, "flamethrower", "ice-beam");

        var boostedDamage = DamageTo(boosted, "水箭龟");
        var baselineDamage = DamageTo(baseline, "水箭龟");
        Assert.True(boostedDamage > baselineDamage, $"Expected Blaze damage {boostedDamage} to exceed baseline {baselineDamage}.");
        Assert.Contains(boosted.State.EventLog, x => x.Type == "AbilityActivated" && x.Data.GetValueOrDefault("abilityId")?.ToString() == "blaze");
    }

    [Fact]
    public void Protean_ChangesTypeAndGenNineOnlyActivatesOncePerSwitchIn()
    {
        var state = BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "modern-violet", "modern-scarlet", 404, false, false);
        var engine = new BattleEngine(Config, state);
        PlaySpecificTurn(engine, "tera-blast", "shadow-ball");

        var user = engine.State.Sides[0].Team[0];
        Assert.Equal(["Normal"], user.CurrentTypes);
        Assert.True(user.AbilityActivatedThisSwitch);
        Assert.Single(engine.State.EventLog, x => x.Type == "AbilityActivated" && x.Data.GetValueOrDefault("abilityId")?.ToString() == "protean");
    }

    [Fact]
    public void CommandsAndRejectedChoices_AreCapturedForReplayAndDiagnostics()
    {
        var engine = NewEngine(505);
        Assert.False(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, "missing", 1, 0)]).Accepted);
        PlayOneTurn(engine);

        Assert.Equal(2, engine.State.CommandLog.Count);
        Assert.Equal([0, 1], engine.State.CommandLog.Select(x => x.Side).ToArray());
        Assert.Contains(engine.State.DebugLog, x => x.Level == "Warning" && x.Message == "choice_rejected");
        Assert.Contains(engine.State.DebugLog, x => x.Category == "turn" && x.Message == "action_order_resolved");
    }

    [Fact]
    public void Dungeon_StartsNextConfiguredWaveAndRestoresPlayerTeam()
    {
        var dungeon = Config.DungeonById["test-training-ground"];
        var first = dungeon.Waves[0];
        var state = BattleFactory.Create(Config, dungeon.RulesetId, dungeon.Format, dungeon.DefaultPlayerTeamId, first.EnemyTeamId, 606, false, true,
            BattleMode.Dungeon, dungeon.Id, 1, dungeon.Waves.Count, first.EnemyPartySize);
        var engine = new BattleEngine(Config, state);
        state.Sides[0].Team[0].CurrentHp = 1;
        state.Sides[1].Team.ForEach(x => x.CurrentHp = 0);
        state.WinnerSide = 0;
        state.Phase = BattlePhase.Ended;

        var result = engine.StartDungeonWave(dungeon.Waves[1]);

        Assert.True(result.Accepted);
        Assert.Equal(2, state.DungeonWave);
        Assert.Equal("pve-test-boss", state.Team2Id);
        Assert.Single(state.Sides[1].Team);
        Assert.Equal(state.Sides[0].Team[0].MaxHp, state.Sides[0].Team[0].CurrentHp);
        Assert.Equal(BattlePhase.AwaitingChoices, state.Phase);
        Assert.Contains(result.Events, x => x.Type == "DungeonWaveCompleted");
        Assert.Contains(result.Events, x => x.Type == "DungeonWaveStarted" && Convert.ToInt32(x.Data["wave"]) == 2);
    }

    [Fact]
    public void Dungeon_CompletionEmitsConfiguredRewardMetadata()
    {
        var dungeon = Config.DungeonById["test-training-ground"];
        var last = dungeon.Waves[^1];
        var state = BattleFactory.Create(Config, dungeon.RulesetId, dungeon.Format, dungeon.DefaultPlayerTeamId, last.EnemyTeamId, 707, false, true,
            BattleMode.Dungeon, dungeon.Id, last.Wave, dungeon.Waves.Count, last.EnemyPartySize);
        var engine = new BattleEngine(Config, state);
        state.WinnerSide = 0;
        state.Phase = BattlePhase.Ended;

        var result = engine.CompleteDungeon(dungeon);

        Assert.True(result.Accepted);
        Assert.True(state.DungeonCompleted);
        var completed = Assert.Single(result.Events, x => x.Type == "DungeonCompleted");
        Assert.Equal("TestToken", completed.Data["rewardType"]);
        Assert.Equal(100, Convert.ToInt32(completed.Data["rewardAmount"]));
    }

    private static BattleEngine NewEngine(ulong seed) => new(Config, BattleFactory.Create(Config, "gen9-sv", BattleFormat.Singles, "classic-red", "classic-blue", seed, false, false));

    private static void PlayOneTurn(BattleEngine engine)
    {
        var first = engine.GetLegalChoices(0).Single();
        var second = engine.GetLegalChoices(1).Single();
        var firstMove = first.Moves.First();
        var secondMove = second.Moves.First();
        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, firstMove.MoveId, firstMove.Targets[0].Side, firstMove.Targets[0].Slot)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, secondMove.MoveId, secondMove.Targets[0].Side, secondMove.Targets[0].Slot)]).Accepted);
    }

    private static void PlaySpecificTurn(BattleEngine engine, string firstMoveId, string secondMoveId)
    {
        Assert.True(engine.SubmitChoices(0, [new BattleChoice(0, ChoiceKind.Move, firstMoveId, 1, 0)]).Accepted);
        Assert.True(engine.SubmitChoices(1, [new BattleChoice(0, ChoiceKind.Move, secondMoveId, 0, 0)]).Accepted);
    }

    private static int DamageTo(BattleEngine engine, string target) => engine.State.EventLog
        .Where(x => x.Type == "DamageApplied" && x.Data.GetValueOrDefault("target")?.ToString() == target)
        .Select(x => Convert.ToInt32(x.Data["amount"]))
        .First();

    private static BattleConfig LoadConfig()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "data", "runtime", "battle-config.json");
            if (File.Exists(candidate))
            {
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
                return JsonSerializer.Deserialize<BattleConfig>(File.ReadAllText(candidate), options)!;
            }
            current = current.Parent;
        }
        throw new FileNotFoundException("data/runtime/battle-config.json was not found from the test output path.");
    }
}
