# PokeBattle 战斗测试平台

一个面向规则研发和回归验证的宝可梦式回合战斗测试平台。服务端使用 C#/.NET 10，H5 客户端使用 TypeScript，所有战斗状态由服务端权威计算。

## 已实现的首版闭环

- 第一至第九世代规则档案，以及单打/双打入口。
- 固定随机种子、顺序事件日志、快照和回放导出。
- 物理/特殊/变化招式、属性克制、STAB、暴击、命中、优先度、速度、天气、场地、异常状态、能力等级、回复、反伤、吸血、守住、多段与固定伤害等代表机制。
- Mega、Z 招式、极巨化和太晶化的代表性流程与世代限制。
- 代表性特性钩子：增伤/减伤、属性免疫、接触反应、登场天气/降级、复制、变幻自如、速度与保命。
- 本地内存房间、WebSocket、AI 对手、重连令牌和调试事件。
- 可筛选的服务端结构化战斗日志，以及带完整性哈希、导入/下载和逐事件播放的版本化回放。
- 双人实时 PVP：创建者分享战斗 ID 与一次性加入码，双方获得独立阵营令牌并同时锁定选择。
- 配置驱动 PVE 副本：连续波次、敌方 AI、队伍恢复、Boss 标记、奖励元数据与完整跨波次回放。
- 内置两波 `test-training-ground` 测试副本，用于快速验证副本全流程。
- CSV 源配置、编译后的 JSON，以及便于审阅的 `BattleConfig.xlsx`。

这不是对全部官方内容的完成声明。未纳入的独特招式、特性、道具和版本边界会在 `data/csv/MechanicsCoverage.csv` 中保持显式缺口，禁止静默视为已支持。

## 快速启动

```powershell
.\scripts\check-env.ps1
.\scripts\run.ps1
```

浏览器打开 `http://localhost:5080`。开发模式也可在 `web` 中运行 `npm run dev`，Vite 会把 `/api` 和 `/ws` 代理到服务端。

## 构建与运行脚本

脚本均可从任意当前目录调用，并会自行定位仓库根目录。默认使用锁文件恢复依赖，任何步骤失败都会立即终止。

```powershell
# 检查 .NET、Node.js 与 npm 版本
.\scripts\check-env.ps1

# 恢复 NuGet 与 npm 锁定依赖
.\scripts\restore.ps1

# Release 全量构建：配置校验、H5 构建、.NET 构建与自动化测试
.\scripts\build.ps1

# 单独执行配置、客户端和服务端验证
.\scripts\test.ps1

# 构建并启动生产合并站点，访问 http://localhost:5080
.\scripts\run.ps1

# 开发模式需要两个终端，分别启动热更新服务端和客户端
.\scripts\dev-server.ps1
.\scripts\dev-client.ps1
```

常用选项：

```powershell
.\scripts\build.ps1 -Configuration Debug -SkipRestore
.\scripts\build.ps1 -SkipClient -SkipTests
.\scripts\test.ps1 -SkipRestore
.\scripts\run.ps1 -NoBuild -Url http://localhost:5090
.\scripts\dev-server.ps1 -SkipRestore
.\scripts\dev-client.ps1 -SkipInstall
```

`run.ps1` 使用服务器内置的生产 H5 文件。开发模式不创建隐藏后台进程：`dev-server.ps1` 在当前终端运行 ASP.NET Core `dotnet watch`，`dev-client.ps1` 在另一个终端运行 Vite；客户端地址为 `http://127.0.0.1:5173`，后端地址为 `http://localhost:5080`。

## 目录

- `src/PokeBattle.Domain`：确定性战斗内核、规则档案和 AI。
- `src/PokeBattle.Server`：房间、WebSocket、快照和静态客户端宿主。
- `tools/PokeBattle.ConfigCompiler`：CSV 校验与 JSON 编译。
- `web`：TypeScript H5 测试台。
- `data/csv`：可审计源配置；`data/runtime`：运行时快照。
- `docs`：规则、协议、配置和覆盖说明。

## 素材说明

客户端只在本地测试时远程请求 PokéAPI 图片，失败时显示本地 CSS 占位。第三方图片不会保存到仓库；发布或分发前应替换为已获授权的素材。
