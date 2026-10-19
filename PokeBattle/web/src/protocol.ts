export type BattleFormat = 'Singles' | 'Doubles';
export type BattleMode = 'Practice' | 'Pvp' | 'Dungeon';
export type SpecialAction = 'None' | 'Mega' | 'ZMove' | 'Dynamax' | 'Terastallize';

export interface ClientEnvelope {
  protocolVersion: 1;
  messageId: string;
  battleId?: string;
  clientSequence: number;
  type: string;
  payload: unknown;
}

export interface ServerEnvelope<T = unknown> {
  protocolVersion: number;
  messageId: string;
  battleId?: string;
  serverSequence: number;
  type: string;
  payload: T;
}

export interface RuleSet { id: string; generation: number; nameZh: string; versionGroup: string; specialMechanic: string }
export interface TeamSummary { id: string; nameZh: string }
export interface MoveSummary { id: string; nameZh: string; type: string; category: string; power: number; accuracy: number; priority: number }
export interface DungeonSummary {
  id: string; nameZh: string; description: string; rulesetId: string; format: BattleFormat; defaultPlayerTeamId: string;
  recommendedLevel: number; rewardType: string; rewardAmount: number; waves: { wave: number; nameZh: string; boss: boolean }[];
}
export interface ConfigSummary { version: string; hash: string; ruleSets: RuleSet[]; teams: TeamSummary[]; dungeons: DungeonSummary[]; moves: MoveSummary[] }

export interface MoveSlot { moveId: string; pp: number; maxPp: number }
export interface PokemonState {
  instanceId: string; speciesId: string; pokedexNumber: number; name: string; spriteUrl: string;
  level: number; originalTypes: string[]; currentTypes: string[]; teraType: string;
  maxHp: number; currentHp: number; status: string; statStages: Record<string, number>; moves: MoveSlot[];
  abilityId: string;
  isMega: boolean; isDynamaxed: boolean; isTerastallized: boolean; fainted: boolean;
}
export interface BattleSide { side: number; name: string; isAi: boolean; team: PokemonState[]; active: number[] }
export interface BattleState {
  battleId: string; configHash: string; rulesetId: string; team1Id: string; team2Id: string; format: BattleFormat; mode: BattleMode; seed: number;
  dungeonId: string; dungeonWave: number; dungeonTotalWaves: number; dungeonCompleted: boolean;
  turn: number; phase: string; field: { weather: string; weatherTurns: number; terrain: string; terrainTurns: number };
  sides: BattleSide[]; choiceLocked: boolean[]; winnerSide?: number; eventSequence: number;
}
export interface TargetRef { side: number; slot: number; instanceId: string; name: string }
export interface LegalMove { moveId: string; nameZh: string; nameEn: string; pp: number; maxPp: number; targets: TargetRef[]; specialActions: SpecialAction[] }
export interface LegalChoiceSet { side: number; slot: number; moves: LegalMove[]; switches: number[] }
export interface BattleEvent { sequence: number; turn: number; type: string; data: Record<string, unknown> }
export interface BattleDebugEntry { sequence: number; turn: number; level: string; category: string; message: string; data: Record<string, unknown> }
export interface ReplayCommand { sequence: number; turn: number; type: string; side: number; choices: BattleChoice[]; randomStateBefore: number }
export interface BattleReplay {
  replayVersion: number; protocolVersion: number; battleId: string; configHash: string; rulesetId: string; format: BattleFormat; mode: BattleMode; dungeonId: string;
  seed: number; team1Id: string; team2Id: string; initialState: BattleState; finalState: BattleState;
  commands: ReplayCommand[]; events: BattleEvent[]; eventHash: string; verified: boolean; verificationErrors: string[];
}
export interface RoomState {
  mode: BattleMode; ready: boolean; opponentClaimed: boolean; connectedSides: number[];
  choiceDeadlineUtc?: string; choiceTimeoutSeconds: number;
  dungeon?: { id: string; nameZh: string; wave: number; totalWaves: number; completed: boolean; rewardType: string; rewardAmount: number };
}
export interface SnapshotPayload { state: BattleState; legalChoices: LegalChoiceSet[]; controllingSide?: number; room: RoomState }

export interface BattleChoice {
  slot: number; kind: 'Move' | 'Switch'; moveId?: string; targetSide?: number; targetSlot?: number;
  switchIndex?: number; specialAction?: SpecialAction;
}
