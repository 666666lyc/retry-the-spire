# 开发指南

本次重构保留 v0.7.0 的功能、`Retry` 命名空间、公开接口和 Harmony 补丁目标。
主项目只编译 `src/**/*.cs`；诊断工具仍随 Mod 编译，配置默认值和触发条件保持不变。

## 模块职责

| 目录 | 职责和入口 |
| --- | --- |
| `src/Bootstrap` | Mod 初始化和版本，入口 `RetryMod`。 |
| `src/History` | 历史目标、玩家状态推算、路径导航和恢复补丁，入口 `RetryRunner`。卡牌、遗物、药水规则各自封装。 |
| `src/Browser` | `NActMapBrowser` 提供兼容入口；`BrowserSession` 持有预览状态，`BrowserMaps`、`BrowserPreview`、`BrowserView`、`BrowserCommit` 共用该会话。 |
| `src/Restart` | `QuickRoomRestart` 准备房间入口存档；`RoomSaveTransform` 处理重掷，`RoomReload` 执行加载；暂停菜单和直载补丁独立存放。 |
| `src/Multiplayer` | 历史存档构建与大厅启动分离；重试会话协调网络连接、准备确认、客户端重连和大厅自动准备。网络封装、原生载荷、发送和主线程分发各自封装。 |
| `src/Snapshots` | `RngSnapshotStore` 提供采集和查询；编码、v4 迁移、原生序列化、索引和裁剪、配置档发现、文件轮换各自封装。 |
| `src/UI` | 共享按钮和弹窗。确认按钮统一通过 `GameConfirmButton` 提取，浏览器保留详细诊断输出。 |
| `src/Diagnostics` | 自动测试配置、执行与报告，以及浏览器诊断与状态转储。 |
| `src/Infrastructure` | 补丁注册、反射状态访问和主题辅助方法。 |

生产代码仍只有一个程序集，不增加运行时包或依赖注入容器。新组件使用 `internal`。
现有入口负责转发；组件通过参数或所属会话访问状态。

## 状态与兼容约束

- 浏览器的临时对局、菜单、节点选择、缓存与玩家预览属于 `BrowserSession`；清理顺序沿用原实现。
  `NetServiceNetIdOverride` 继续保留为公开静态字段，兼容已有消费者。
- 联机状态分为 `RestartConnectionState`（能力与网络绑定）、`RestartAttemptState`（代次、nonce、确认集合和直载准备）、
  `RestartRejoinState` 与 `AutoRejoinSession`（客户端身份和重连进度）。原有重置方法仍只清理原先负责的字段。
- `RetryContext` 仍承担 Harmony 补丁的一次性开关。单人历史恢复、多人原生存档和当前房间重试保持独立管线。
- 单人导航和多人原生存档的历史切片都保留目标条目；房间模拟与历史预填只处理目标之前的条目。
  各流程沿用自己的截断、边界检查与玩家处理，不合并成通用加载管线。
- 快照文件名、v5 二进制布局、v4 迁移、压缩、备份、大小上限与裁剪顺序保持不变。
- 联机消息编号、magic、协议版本、字段布局、校验顺序和超时参数保持不变。
- `PatchRegistration` 显式保留原补丁注册顺序，避免目录迁移改变同优先级 Harmony 补丁的先后关系。
- 快照原生序列化与网络载荷各自保留原先的位对齐和长度计算；不为消除表面重复而合并。

## 构建

需要 .NET 9 SDK 和本机游戏程序集。`global.json` 从 9.0.100 起选择可用的稳定 9.0 feature band。
构建工具按命令参数、`STS2_GAME_DIR`、平台默认 Steam 目录的顺序定位游戏。非默认 Steam 库请显式传路径。

Windows：

```powershell
.\build.ps1 -GameDir 'C:\path\to\Slay the Spire 2'
.\build.ps1 -Configuration Debug -GameDir 'C:\path\to\Slay the Spire 2'
# 显式安装：
.\build.ps1 -GameDir 'C:\path\to\Slay the Spire 2' -Install
```

macOS / Linux / Git Bash：

```bash
./build.sh Release --game-dir '/path/to/Slay the Spire 2' --no-install
# 省略 --no-install 时保持原有的构建并安装行为。
```

也可在工程根目录直接构建：

```powershell
dotnet build RetryTheSpire.csproj -c Release '-p:STS2GameDir=C:\path\to\Slay the Spire 2'
```

构建结果放在 `out/RetryTheSpire/`（脚本）或 `bin/`（直接构建）。脚本不删除整个输出目录。
安装脚本延续复制 DLL 和清单的逻辑；正式发布包仍按[发布流程](RELEASING.md)只包含四个产品文件。
不将游戏程序集、诊断报告、测试程序或编译辅助文件加入发布包。

回归方法见 [TESTING.md](TESTING.md)，已知保留问题见 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)。
创意工坊文案为 [中文](STEAM_WORKSHOP.md)和[英文](STEAM_WORKSHOP_EN.md)。
