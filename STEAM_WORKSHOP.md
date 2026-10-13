[h1]战斗重掷与历史节点重打，一键再来[/h1]

Retry the Spire 可以在战斗中重掷遭遇或牌序，也可以从历史记录中任意已到访节点重新开始。支持单人和多人模式。

[b]注意：请勿与原版 Retry 模组同时启用。[/b]

[h1]核心功能[/h1]

[h2]战斗中即时重掷[/h2]

[img]https://images.steamusercontent.com/ugc/9612398864964810110/E3AEC82A58F822362A6680A56ADF21044F6517B9/[/img]

[h3]遭遇重掷[/h3]

随机替换为当前章节、相同战斗层级的另一场完整遭遇，然后从战斗入口重新开始。弱怪、普通怪、精英和 Boss 不会跨层级混合。

[img]https://images.steamusercontent.com/ugc/14491581482156062642/594F3A010997C5DB795B992D1850D86C4ADC26B5/[/img]

[h3]牌序重掷[/h3]

只推进洗牌随机数，改变初始牌序及后续洗牌顺序；怪物 AI、随机目标和奖励随机数保持不变。

[img]https://images.steamusercontent.com/ugc/13332759611398916762/6EB95CE854F3BC8282D50357364514C782FB39C5/[/img]

两个入口只在战斗中显示。多人模式下仅房主可以发起，所有客户端会加载同一份变换后的存档。

[h2]从历史记录选择节点重打[/h2]

[b]1.[/b] 在“对局历史”中打开一局记录。
[b]2.[/b] 点击 [b]View Acts（查看章节）[/b]。

[img]https://images.steamusercontent.com/ugc/14548824996800969737/76DA4D5A52920437406C1A030DA47045A8C89DA3/[/img]

[b]3.[/b] 切换章节，选择已经到访的地图节点并确认。
[b]4.[/b] 使用记录中的状态从该节点重新开始。

[img]https://images.steamusercontent.com/ugc/16099416557635511572/878FA39C82FD7045AE4B42EFED0EEB695E209055/[/img]

恢复多人历史对局时，仅房主需要安装 Retry the Spire。客户端会通过游戏原生的读取对局大厅接收存档。

[h2]普通房间重打[/h2]

暂停游戏后点击“重打”，读取进入当前房间时生成的原生存档。

[img]https://images.steamusercontent.com/ugc/13765178304800972703/DE37A2FA444E366FBFB735B07761E46617D98201/[/img]

[h3]单人模式[/h3]

立即从房间入口重新开始。

[img]https://images.steamusercontent.com/ugc/16583176595871336671/790008526F6F6CDB5D3484D50CA8B7E1D464797B/[/img]

[h3]多人：全员安装模组[/h3]

当所有在线玩家均使用 v0.5.0 或更高版本时，全员保留当前连接并直接重载。

[img]https://images.steamusercontent.com/ugc/18351087105714867667/CBF7C67E42B967B77CC76A3497CA376B4C2ACA9F/[/img]

[h3]多人：存在未安装玩家[/h3]

房主自动创建游戏原生的替换大厅。新版玩家会自动找到大厅并准备，未安装模组的玩家可以手动重新加入，无需安装额外依赖。

[img]https://images.steamusercontent.com/ugc/9821421455064797358/EF60696D25BE48D3CD2A6CCEB1564EC11893ECAD/[/img]

[h1]可以恢复什么[/h1]

[list]
[*]种子、角色与进阶等级
[*]牌组、遗物与药水
[*]生命值、金币与游戏时间
[*]已访问路线、当前章节和节点
[*]事件历史与房间队列
[*]可用的房间入口玩家状态、地图和随机数快照
[*]原多人对局参与者及其 Steam ID
[/list]

房间快照保存在当前档案的存档目录中，可通过 Steam 云同步到其他设备。

[h1]安装与使用[/h1]

[olist]
[*]订阅本模组。
[*]启动《杀戮尖塔 2》，在模组界面中启用 [b]Retry the Spire[/b]。
[*]如果已经安装原版 [b]Retry[/b]，请将其禁用或移除。
[/olist]

[h1]多人兼容性[/h1]

[list]
[*]只有需要自动原房间直载的玩家才需要安装本模组。
[*]未安装模组的玩家仍可正常加入，并在重打后通过原生替换大厅手动重进。
[*]玩法或内容模组若注册了游戏模型，所有多人客户端仍需保持一致。
[*]外观类或不影响玩法的模组可以不同。
[*]v0.4.x 使用不兼容的多人协议；加入当前版本房间前请升级到 v0.5.0 或更高版本，或禁用旧版。
[/list]

[h1]开源与反馈[/h1]

源码、完整文档与问题反馈：
[url=https://github.com/666666lyc/retry-the-spire]github.com/666666lyc/retry-the-spire[/url]

本项目派生自 Austin/sts2mods 创建的 Retry，并沿用 MIT 许可证。
