# WebSocket 协议

端点：`ws://localhost:5080/ws`。所有消息均为 UTF-8 JSON。

## 信封

```json
{
  "protocolVersion": 1,
  "messageId": "client-generated-id",
  "battleId": "optional-battle-id",
  "clientSequence": 1,
  "type": "createBattle",
  "payload": {}
}
```

服务端信封额外包含单调递增的 `serverSequence`。未知版本、重复/过期序列、非法选择和无效房间不会改变战斗状态。

## 客户端消息

- `createBattle`：`mode`（`Practice`/`Pvp`）、`rulesetId`、`format`、`team1Id`、`team2Id`、`seed`、`playerAi`、`opponentAi`。PVP 模式强制双方为真人控制。
- `joinBattle`：第二名玩家提交 `battleId` 与 8 位 `joinCode`，成功后获得只控制 P2 的重连令牌。
- `createDungeon`：提交 `dungeonId`、`playerTeamId` 与基础 `seed`，按配置创建 PVE 波次。
- `choose`：`side` 与每个活动位置的 `choices`；技能选择包含 `moveId`、目标方和目标位置，可带 `specialAction`。
- `setAi`：在 AI 练习或 PVE 副本中，将当前控制方切换为服务端 AI 托管；PVP 不允许托管。
- `requestSnapshot`：请求当前完整状态。
- `requestDebug`：按 `after`、`level`、`category`、`limit` 增量请求战斗诊断。
- `reconnect`：提交 `battleId`、`playerToken`、`afterServerSequence`。
- `forfeit`：指定一方投降。

## 服务端消息

- `battleCreated`：战斗 ID、P1 令牌、模式、配置哈希；PVP 创建者额外获得加入码。
- `battleJoined`：P2 成功认领后的战斗 ID、P2 令牌和控制阵营。
- `dungeonCreated`：副本战斗 ID、P1 令牌和配置哈希。
- `battleSnapshot`：完整权威状态、仅属于当前控制方的合法选择（技能中包含中英文名、当前/最大 PP、目标与可用强化）以及房间/PVE 进度。
- `roomState`：PVP 就绪与在线阵营、15 秒行动时限及 UTC 截止时间，或当前副本波次、完成状态和奖励元数据。
- `choiceAccepted`：只确认本方已经锁定，不泄露另一方选择。
- `choiceTimedOut`：一方超过 15 秒仍未锁定时，服务端为其自动选择全部合法行动。
- `battleEvents`：按序输出本次状态转换的领域事件。
- `aiDecision`：AI 候选评分、最终选择和说明。
- `debugLog`：服务端顺序化诊断，包含等级、分类、回合、消息和结构化数据。
- `battleEnded`：胜方和原因。
- `error`：稳定错误码、可读消息和对应消息 ID。

## 事件类型

`BattleStarted`、`TurnStarted`、`MoveUsed`、`MoveMissed`、`CriticalHit`、`DamageApplied`、`HealApplied`、`StatusApplied`、`StatStageChanged`、`AbilityActivated`、`WeatherChanged`、`TerrainChanged`、`PokemonSwitched`、`PokemonFainted`、`SpecialActivated`、`TurnEnded`、`BattleEnded`、`DungeonStarted`、`DungeonWaveStarted`、`DungeonWaveCompleted`、`DungeonCompleted`、`DungeonFailed`。

`BattleEnded.data.reason` 可为 `all_fainted`、`draw`、`forfeit`、`turn_limit` 或 `stalemate`。`stalemate` 表示连续 8 回合双方队伍总 HP 未实际下降，并附带 `turnsWithoutHpLoss`。

涉及具体精灵的事件同时携带稳定实例 ID，例如 `pokemonInstanceId`、`targetInstanceId`、`fromInstanceId` 和 `toInstanceId`。显示名仅用于展示；动画、回放与状态关联应优先使用实例 ID，以正确区分队内同名精灵。

## HTTP 调试与回放

- `GET /api/battles/{id}/debug`：支持 `after`、`level`、`category` 和 `limit` 查询参数。
- `GET /api/battles/{id}/replay`：返回版本化回放文件，包括配置哈希、规则集、双方队伍、种子、初始/最终快照、已接受命令、事件、事件 SHA-256 与完整性结果。

回放版本与协议版本独立。当前 `replayVersion` 为 1；客户端导入未知版本时必须拒绝播放。
