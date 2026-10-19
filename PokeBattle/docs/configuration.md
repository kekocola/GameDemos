# 配置说明

## 数据流

`data/csv/*.csv` 是版本化源数据。配置编译器完成 UTF-8 CSV 解析、主键/引用/枚举/数值范围/处理器检查，生成 `data/runtime/battle-config.json`。服务器只加载编译结果，不在战斗期间访问外部网站。

`BattleConfig.xlsx` 汇总相同 CSV，用于筛选、批注、审阅和覆盖统计；它不是运行时数据源。

## 精灵固定配置

`PresetTeams.csv` 的每一名成员必须配置一个有效特性和二至四个互不重复的技能；同一预设队伍的六名成员不能使用重复物种，否则配置编译失败。普通练习和 PVP 房间会按战斗种子分别打乱双方的队内顺序；同一种子仍会产生相同顺序，保证命令与回放稳定。成员的技能、特性和装备不会被随机替换；Mega、Z 招式、极巨化和太晶化仍由所选世代规则决定是否可用。

## 战斗节奏常量

`GenerationConstants.csv` 中的 `maxDamageFractionPerMove` 是测试场专用的伤害节奏上限。当前值为 `0.45`，表示同一招式（包括多段与固定伤害）对单个目标最多扣除其最大 HP 的 45%，避免满血被一次攻击直接击倒，并让高伤害对局通常需要约三次命中。属性、暴击、天气、特性等仍先按世代规则计算，只有超过上限的最终伤害会被截断。

## 效果步骤

`EffectSteps` 按 `effectId + order` 执行。代表性操作：

- `damage`、`fixedDamage`
- `status`、`statStage`
- `healPercent`、`drainPercent`、`recoilPercent`
- `weather`、`terrain`
- `protect`、`multiHit`
- `forceSwitch`、`clearStages`

需要访问复杂上下文的内容在 `EffectDefinitions.handlerKey` 指向白名单 C# 处理器。未知处理器是编译错误。

## 版本与来源

所有机制内容必须提供 `fromGeneration`、`toGeneration`、`implementationStatus` 和 `sourceId`。`MechanicsCoverage` 的允许状态为 `VerifiedRepresentative`、`ImplementedUnverified`、`Planned`、`OutOfScope`；只有前两者可以进入可玩预设。

## 特性配置

`Abilities.csv` 的 `effectKey` 对应 C# 特性钩子族。配置编译器校验特性 ID 唯一性以及预设队引用，未知特性会阻止 JSON 快照生成。

特性并非仅凭 `implementationStatus` 自动生效：运行时必须存在相应的出招前、伤害、免疫、接触、登场、速度或换下清理逻辑，并配套 `AbilityActivated` 事件与测试。无基础机制承载的特性保持 `Planned`。

## PVE 副本配置

`PveDungeons.csv` 定义副本入口与奖励元数据，`PveDungeonWaves.csv` 定义连续波次并引用敌方预设队。编译器校验副本 ID、规则集、玩家/敌方队伍、波次连续性和实际敌方人数。完整字段与测试副本见 `docs/pve-dungeons.zh-CN.md`。
