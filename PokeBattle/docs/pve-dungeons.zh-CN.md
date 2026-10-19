# PVE 副本配置与流程

## 配置结构

`PveDungeons.csv` 定义副本入口、规则集、单双打格式、默认玩家队伍、推荐等级和奖励元数据。`PveDungeonWaves.csv` 定义有序波次：

- `enemyTeamId`：引用 `PresetTeams.csv` 的敌方预设队。
- `enemyPartySize`：本波实际加载 1–6 名成员，便于制作首领战或快速测试波。
- `restorePlayerTeam`：进入该波前是否恢复玩家 HP、异常状态与 PP。
- `boss`：供客户端和事件展示首领标记。
- `seedOffset`：进入该波时基于副本基础种子重置确定性随机源。

编译器要求副本、规则集和队伍引用有效，波次必须从 1 连续编号，敌方人数必须在 1–6 范围内。奖励仅作为 `DungeonCompleted` 事件元数据；当前版本没有账号、背包或数据库，因此不会持久化发奖。

## 权威流程

1. 客户端发送 `createDungeon`，服务端按第一波配置创建玩家对 AI 战斗。
2. 敌方 AI 提前生成并锁定合法选择，等待玩家提交。
3. 玩家获胜后写入 `DungeonWaveCompleted`；还有下一波时替换敌方队伍、按配置恢复玩家并写入 `DungeonWaveStarted`。
4. 最后一波获胜后写入 `DungeonCompleted` 和奖励元数据；失败或投降写入 `DungeonFailed`。
5. 所有波次共用战斗 ID、事件序列、命令日志、调试日志和回放文件。

## 测试副本

`test-training-ground`（测试训练场）是两波快速回归副本：

- 第 1 波“侦察训练”：单只 Lv.5 皮卡丘。
- 第 2 波“守关测试”：单只 Lv.8 卡比兽，标记为 Boss。
- 两波之间完整恢复玩家队伍。
- 通关奖励元数据：`TestToken × 100`。

该副本仅用于验证创建、AI 锁定、波次切换、恢复、Boss、回放和结算，不代表正式数值设计。
