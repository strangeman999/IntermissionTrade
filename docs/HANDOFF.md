# 开发交接记录

## 目标

制作一个《杀戮尖塔2》联机 mod，在第一幕和第二幕结束后让玩家交换卡牌；第三幕结束不触发交换。详细玩法和仍待定的少量细节见 `docs/GAMEPLAY_SPEC.md`。

## 新收到的玩法需求

- 第一、二幕结束时，等所有玩家选完卡牌奖励后进入交换阶段；第三幕结束不交换。
- 按当前理解，每个交换阶段每位玩家有一次成功发起机会；成功后消耗，拒绝后可重试。每幕分别重置。
- 机会只限制发起者，接收方可多次接收。用户示例允许依次完成 A→B、B→A、C→A。
- 同意后双方从各自整副牌组中选一张非诅咒牌并确认；确认后互换，保留卡牌原有状态。
- 界面建议为独立面板，以玩家昵称作为请求按钮，并提供同意、拒绝和跳过。
- 跳过作用范围、并发请求策略、阶段结束条件及选牌阶段断线/超时处理仍需在实现前定案；见玩法文档。

## 用户明确的编码规则

- 新增或修改的代码必须附有详细中文注释，解释代码用途和关键逻辑。

## 最近一次实现调整

- 已按用户要求移除请求超时撤销、倒计时与进度条；请求会持续等待，直到接收方明确同意或拒绝。
- 新增牌局放弃清理入口：关闭选牌弹窗和交换覆盖层，清除暂停的切幕同步器与阶段状态，避免 mod 遗留状态阻止游戏返回主菜单。
- 跳过按钮显示“跳过 已跳过人数/全体玩家人数”，例如“跳过 2/3”。该人数由协调器的跳过集合计算，成功发起交换的玩家不会计为跳过。

## 当前进度

- 已用 `Alchyr.Sts2.Templates` 的空白 mod 模板生成 `IntermissionTrade` 项目。
- 已创建并打开 `IntermissionTrade.sln`。
- 用户确认此前本机生成成功。构建输出显示 DLL、PDB、JSON 已复制到游戏 mods 目录。
- 交换流程接入已开始，但尚未确认 mod 在游戏中实际加载或进行联机测试；当前代码不能视作已验证可玩的成品。
- 已新增 `IntermissionTradeCode/TradePhaseCoordinator.cs`：实现纯 C# 交换阶段状态协调器，覆盖仅第一/二幕、每个阶段主动成功机会重置、拒绝后可重试、双方选牌确认、单笔请求串行化和跳过语义。跳过暂定为放弃主动发起但仍可接收请求。
- `IntermissionTradeCode/TradeOverlayScreen.cs`：通过游戏 `NOverlayStack` 打开交换阶段覆盖层，包含玩家列表、同意/拒绝和跳过。
- 选牌时由 `TradePhaseRuntime` 打开游戏原生 `NDeckCardSelectScreen`，使用牌面图像网格、滚动与原生确认操作；选择完成后将 `CardModel` 映射回本机牌组位置再交主持端验证。
- `IntermissionTradeCode/TradeNetworkMessages.cs`：定义玩家意图和主持端权威状态的可靠消息；意图只发给主持端，主持端广播审核后的状态。
- `IntermissionTradeCode/TradePhaseRuntime.cs`：已开始接入 `ActChangeSynchronizer.MoveToNextAct` 幕间拦截、主持端流程校验、界面事件、玩家昵称读取和双方牌组的 `CardPileCmd.GiveToAnotherPlayer` 转移；牌局放弃时会清理 mod 界面和暂停状态。
- `MainFile.Initialize` 会订阅消息路由并安装 Harmony 补丁。客户端会等待主持端的阶段及结果广播，再在本地应用相同卡牌迁移。
- 卡牌转移前由主持端再次确认卡牌索引有效且不是诅咒牌；移动的是原有 `CardModel`，因此模型的升级状态等数据应随卡移动。运行时断线、异常回滚和消息注册仍需实际联机验证。
- 已运行 `dotnet build IntermissionTrade.sln --no-restore`，最近一次生成成功，0 警告、0 错误，模板将产物复制到 mods 目录。尚未进行游戏内或联机验证。
- 游戏安装目录包含 `sts2.xml`，BaseLib NuGet 包版本为 3.4.7；后续应继续用本机文档/程序集核对奖励完成时机、联机安全消息和牌组迁移 API，再实现接入。
- Git 本地仓库已初始化；规则协调器基线提交为 `d11644d`，界面骨架提交为 `c5ce81f`。网络协议和运行时接入仍在工作区，未提交。
- 用户报告 Visual Studio 已更新到 **17.14**；本轮重新构建成功，0 警告、0 错误。
- 尚未配置 Git 远程仓库或 CI。

## 环境与路径

- 源码：`D:\GameMod\IntermissionTrade`
- Visual Studio：Visual Studio 2022 17.14（用户报告）
- .NET SDK：10.0.402；项目目标框架：`net9.0`
- MegaDot：`D:\GameMod\megadot-4.5.1-m.14-windows-x86_64-llvm-editor-csharp\MegaDot_v4.5.1-stable_mono_win64.exe`
- 游戏安装目录：`E:\SteamLibrary\steamapps\common\Slay the Spire 2`
- mod 部署目录：`E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\IntermissionTrade`
- 本机 Godot 路径写在 `Directory.Build.props`；该文件被 `.gitignore` 排除。构建配置会通过 `Sts2PathDiscovery.props` 自动寻找游戏数据。

## 已知构建行为

- 模板项目的 `.csproj` 在构建后将 `IntermissionTrade.dll`、`IntermissionTrade.pdb` 和 `IntermissionTrade.json` 复制到 mod 部署目录。
- 当前项目仅包含 C# 代码，没有 Godot 场景或资源包；`IntermissionTrade.json` 中必须设置 `has_pck: false`，否则游戏会因找不到 `.pck` 拒绝加载 mod。添加 Godot 资源后再生成并启用 PCK。
- 曾看到的编译 DLL 路径为 `.godot\mono\temp\bin\Debug\IntermissionTrade.dll`。
- C# 代码改动用 **生成 → 生成解决方案**；非代码资源改动需要走模板 Publish 流程。
- 用户机器上 `Ctrl+Shift+B` 曾弹出 Windows 表情面板；给用户指路时使用 VS 菜单操作。
- 游戏日志：`%APPDATA%\SlayTheSpire2\logs\godot.log`。

## 建议的下一步

1. 在 VS 17.14 中重新生成解决方案，确认更新后的工具链仍可构建。
2. 启动游戏，在模组设置中确认 `IntermissionTrade` 和 BaseLib 均可加载；遇到问题查看 `godot.log`。
3. 先核对 BaseLib 是否会自动发现并注册两种 `ICustomMessage`，并确认游戏运行时实际调用 `ActChangeSynchronizer.MoveToNextAct` 的时机确实在所有卡牌奖励选完之后。
4. 检查运行时边界情况：玩家断线、卡牌迁移命令异常、收到旧幕延迟消息、放弃牌局时清理流程和 Overlay 无法打开时的恢复策略。
5. 完成功能代码后再构建；随后在游戏内验证 mod 加载、单人/多人切幕及双人交换。双人联机测试前不要声称交换功能可用。项目构建依赖本机游戏程序集，因此公共 CI runner 可能无法直接构建。不要将游戏文件提交到仓库。

