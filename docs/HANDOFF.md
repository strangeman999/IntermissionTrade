# 开发交接记录

## 目标

制作一个《杀戮尖塔2》联机 mod，在第一幕和第二幕结束后让玩家交换卡牌；第三幕结束不触发交换。详细玩法和仍待定的少量细节见 `docs/GAMEPLAY_SPEC.md`。

## 新收到的玩法需求

- 第一、二幕结束时，等所有玩家选完卡牌奖励后进入交换阶段；第三幕结束不交换。
- 按当前理解，每个交换阶段每位玩家有一次成功发起机会；成功后消耗，拒绝或 5 秒无响应超时后可重试。每幕分别重置。
- 机会只限制发起者，接收方可多次接收。用户示例允许依次完成 A→B、B→A、C→A。
- 同意后双方从各自整副牌组中选一张非诅咒牌并确认；确认后互换，保留卡牌原有状态。
- 界面建议为独立面板，以玩家昵称作为请求按钮，并提供同意、拒绝和跳过。
- 跳过作用范围、并发请求策略、阶段结束条件及选牌阶段断线/超时处理仍需在实现前定案；见玩法文档。

## 用户明确的编码规则

- 新增或修改的代码必须附有详细中文注释，解释代码用途和关键逻辑。

## 当前进度

- 已用 `Alchyr.Sts2.Templates` 的空白 mod 模板生成 `IntermissionTrade` 项目。
- 已创建并打开 `IntermissionTrade.sln`。
- 用户确认此前本机生成成功。构建输出显示 DLL、PDB、JSON 已复制到游戏 mods 目录。
- 尚未实现可在游戏内运行的交换功能，也没有确认 mod 在游戏中实际加载或进行联机测试。
- 已新增 `IntermissionTradeCode/TradePhaseCoordinator.cs`：实现纯 C# 交换阶段状态协调器，覆盖仅第一/二幕、每个阶段主动成功机会重置、拒绝/5 秒未回应可重试、双方选牌确认、单笔请求串行化和跳过语义。跳过暂定为放弃主动发起但仍可接收请求。
- 该协调器不持有游戏 `Player`/`CardModel`，也未连接 UI、游戏幕间钩子、网络消息或实际卡牌转移；提交前仍须由游戏侧确认卡牌归属并排除诅咒牌。
- 添加协调器后已运行 `dotnet build IntermissionTrade.sln --no-restore`，构建成功，0 警告、0 错误，模板将产物复制到 mods 目录。尚未进行游戏内或联机验证。
- 游戏安装目录包含 `sts2.xml`，BaseLib NuGet 包版本为 3.4.7；后续应继续用本机文档/程序集核对奖励完成时机、联机安全消息和牌组迁移 API，再实现接入。
- 用户刚刚报告 Visual Studio 已更新到 **17.14**。更新后的重新构建尚未确认。
- 当前项目目录尚未初始化为 Git 仓库，也未配置远程仓库或 CI。

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
- 曾看到的编译 DLL 路径为 `.godot\mono\temp\bin\Debug\IntermissionTrade.dll`。
- C# 代码改动用 **生成 → 生成解决方案**；非代码资源改动需要走模板 Publish 流程。
- 用户机器上 `Ctrl+Shift+B` 曾弹出 Windows 表情面板；给用户指路时使用 VS 菜单操作。
- 游戏日志：`%APPDATA%\SlayTheSpire2\logs\godot.log`。

## 建议的下一步

1. 在 VS 17.14 中重新生成解决方案，确认更新后的工具链仍可构建。
2. 启动游戏，在模组设置中确认 `IntermissionTrade` 和 BaseLib 均可加载；遇到问题查看 `godot.log`。
3. 继续梳理“奖励全部选完”的游戏生命周期入口，以及联机玩家如何安全地提交/同步卡牌选择。
4. 接入 UI、游戏钩子、联机流程和卡牌转移；跳过语义等未确认处按 `docs/GAMEPLAY_SPEC.md` 中的暂定方案实现，并明确告知用户。
5. 建立 Git 本地仓库并提交当前可构建基线；之后再评估 CI。项目构建依赖本机游戏程序集，因此公共 CI runner 可能无法直接构建。不要将游戏文件提交到仓库。

