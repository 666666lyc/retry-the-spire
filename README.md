# Retry the Spire

[English](#english) | [简体中文](#简体中文)

## English

Retry the Spire resumes a *Slay the Spire 2* run-history entry from a visited
map node with the recorded seed, deck, relics, potions, HP, gold, and map
progress restored.

> [!IMPORTANT]
> Retry the Spire is a modified fork of
> [sts2mods/Retry](https://github.com/sts2mods/Retry), originally created by
> Austin/sts2mods. Do not enable Retry and Retry the Spire at the same time;
> both patch the same game screens and run-loading paths.

## Features

- Adds **View Acts** to run history and game-over screens.
- Previews every visited act and node using the original map path.
- Restores cards, relics, potions, health, gold, event history, room queues,
  and available RNG snapshots.
- Keeps single-player retries compatible with the upstream custom-run flow.
- Rebuilds multiplayer history as a native `SerializableRun` and opens the
  game's standard load-run lobby.
- Restores every original multiplayer participant under their recorded Steam
  ID. Only those players can join the restored lobby.
- Requires Retry the Spire only on the host. Clients receive the native save
  through the game's normal `LoadRunLobby` flow.
- Keeps multiplayer history lobbies in `GameMode.Standard`, without custom
  modifiers, so the game applies its normal multiplayer ascension rules.
- Keeps the game's independent single-player and multiplayer save slots
  separate.
- Adds **重打** to the in-run pause menu. Single-player reloads the
  native room-entry save immediately; multiplayer hosts reopen that save in
  the native load-run lobby so unmodded friends can rejoin.
- During an in-run multiplayer restart, teammates running v0.4.3 or newer are
  verified over a dedicated reliable control channel before the old lobby is
  closed. They automatically find the replacement lobby and ready up. If a
  compatible teammate does not acknowledge within three seconds, the live run
  stays connected and the host chooses whether to cancel or continue with
  manual rejoining.

## Limitations

- Historical runs created before installing the mod may lack per-floor RNG
  snapshots, so combat-internal RNG can diverge.
- Gameplay/content mods that register models must still match on every
  multiplayer client. Cosmetic or non-gameplay mods may differ.
- The original Retry and Retry the Spire are mutually exclusive at runtime.
- The game disables achievements while mods are loaded.

## Installation

Download `RetryTheSpire-v0.4.3.zip` from the GitHub Releases page and extract
it into the game's `mods` directory. The final layout must be:

```text
mods/
  RetryTheSpire/
    RetryTheSpire.dll
    mod_manifest.json
    LICENSE
    INSTALL.md
```

Disable or remove the original Retry mod before enabling Retry the Spire.

## Building

The project requires the .NET 9 SDK and a local copy of *Slay the Spire 2*.

```bash
./build.sh Release
```

The project references game assemblies from the local installation but does
not redistribute them.

## Attribution and license

Retry the Spire is maintained by **666666lyc** and is derived
from [Retry](https://github.com/sts2mods/Retry), originally created by
Austin/sts2mods.

The original MIT license and copyright notice are preserved unchanged in
[`LICENSE`](LICENSE). Additional attribution for this fork is recorded in
[`NOTICE`](NOTICE). The modifications are distributed under the same MIT
license.

---

## 简体中文

Retry the Spire 可以从已到访的地图节点重新开始《杀戮尖塔 2》的历史对局，并恢复该对局记录中的种子、牌组、遗物、药水、生命值、金币以及地图进度。

> [!IMPORTANT]
> Retry the Spire 是 [sts2mods/Retry](https://github.com/sts2mods/Retry) 的修改分支，原项目由 Austin/sts2mods 创建。请勿同时启用 Retry 和 Retry the Spire；两者会修改相同的游戏界面与对局加载流程。

### 功能

- 在对局历史和游戏结束界面中加入 **View Acts（查看章节）** 按钮。
- 按原始地图路线预览所有已到访的章节和节点。
- 恢复卡牌、遗物、药水、生命值、金币、事件历史、房间队列以及可用的随机数快照。
- 单人模式重打兼容上游项目的自定义对局流程。
- 将多人历史对局重建为游戏原生的 `SerializableRun`，并打开游戏的标准读取对局大厅。
- 使用记录中的 Steam ID 恢复原多人对局的所有参与者；只有这些玩家能够加入恢复后的大厅。
- 仅多人模式房主需要安装 Retry the Spire。客户端通过游戏正常的 `LoadRunLobby` 流程接收原生存档。
- 多人历史对局大厅保持为 `GameMode.Standard`，不附加自定义修改项，因此游戏会应用正常的多人进阶规则。
- 保持游戏的单人和多人存档槽相互独立。
- 在对局中的暂停菜单加入 **重打**。单人模式会立即读取进入当前房间时的原生存档；多人模式房主则会在原生读取对局大厅中重新打开该存档，让未安装此模组的好友也能重新加入。
- 在多人对局中重打时，v0.4.3 或更高版本会先通过专用的可靠控制通道确认队友兼容，再关闭旧大厅。兼容的队友会自动找到新大厅并准备。如果兼容队友未在三秒内响应，当前对局将保持连接，由房主选择取消重打，或继续并让队友手动重新加入。

### 已知限制

- 安装此模组之前生成的历史对局可能缺少每层的随机数快照，因此战斗内部的随机结果可能与原对局不同。
- 注册游戏模型的玩法或内容模组必须在所有多人客户端上保持一致；外观类或不影响玩法的模组可以不同。
- 原版 Retry 与 Retry the Spire 无法同时运行。
- 游戏会在加载模组时禁用成就。

### 安装

从 GitHub Releases 页面下载 `RetryTheSpire-v0.4.3.zip`，并解压到游戏的 `mods` 目录中。最终目录结构必须如下：

```text
mods/
  RetryTheSpire/
    RetryTheSpire.dll
    mod_manifest.json
    LICENSE
    INSTALL.md
```

启用 Retry the Spire 前，请先禁用或移除原版 Retry 模组。

### 构建

本项目需要 .NET 9 SDK，以及本地安装的《杀戮尖塔 2》。

```bash
./build.sh Release
```

本项目会引用本地游戏安装目录中的程序集，但不会重新分发这些文件。

### 署名与许可证

Retry the Spire 由 **666666lyc** 维护，派生自 Austin/sts2mods 最初创建的 [Retry](https://github.com/sts2mods/Retry)。

原 MIT 许可证与版权声明完整保留在 [`LICENSE`](LICENSE) 中。此分支的额外署名记录在 [`NOTICE`](NOTICE) 中。所有修改均使用相同的 MIT 许可证发布。
