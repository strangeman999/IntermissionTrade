using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace IntermissionTrade.IntermissionTradeCode;

/// <summary>
/// 幕间卡牌交换的请求与阶段状态界面。
///
/// 该面板使用游戏现有的 NOverlayStack 管理，因而能作为模态覆盖层显示在奖励/地图界面之上。
/// 本类只负责显示玩家、请求操作和等待状态；卡牌图形网格由游戏自己的牌组选牌屏幕负责显示。
/// 本类不会自行发送网络消息，也不会修改卡牌，所有交互都交给交换流程服务审核。
/// </summary>
public partial class TradeOverlayScreen : Control, IOverlayScreen
{
    /// <summary>供手柄导航使用的默认焦点控件；界面创建完成前允许为空。</summary>
    public Control? DefaultFocusedControl => _skipButton;

    /// <summary>
    /// 当前版本没有专属的联网界面类型，因此使用 None。
    /// 后续若需要在远端玩家状态栏展示专属交换图标，再评估使用或扩展对应同步方案。
    /// </summary>
    public NetScreenType ScreenType => NetScreenType.None;

    /// <summary>请求界面使用独立的暗色遮罩，避免和游戏其他弹层共用淡入背景。</summary>
    public bool UseSharedBackstop => false;

    /// <summary>玩家在列表中点击另一名玩家后触发，由后续服务发送请求。</summary>
    public event Action<string>? PlayerRequestSelected;

    /// <summary>收到请求时点击同意后触发。</summary>
    public event Action? RequestAccepted;

    /// <summary>收到请求时点击拒绝后触发。</summary>
    public event Action? RequestDeclined;

    /// <summary>点击跳过后触发；主动机会如何标记由交换协调器决定。</summary>
    public event Action? SkipSelected;

    /// <summary>
    /// 将交换界面放到游戏自己的覆盖层栈顶部。
    /// 使用游戏提供的 NOverlayStack.Push 而不是直接挂到场景根节点，
    /// 这样游戏会正确处理模态遮罩、鼠标输入和当前活动屏幕上下文。
    /// </summary>
    public static TradeOverlayScreen? Open()
    {
        NOverlayStack? overlayStack = NOverlayStack.Instance;
        if (overlayStack is null)
        {
            // 例如游戏尚未进入实际跑图场景时，覆盖层栈还不存在；此时不能创建孤立界面。
            return null;
        }

        if (overlayStack.Peek() is TradeOverlayScreen alreadyOpen)
        {
            // 避免多个网络回调重复打开同一个流程的面板。
            return alreadyOpen;
        }

        var screen = new TradeOverlayScreen();
        overlayStack.Push(screen);
        return screen;
    }

    /// <summary>标题控件，用于在不同交换步骤间提示当前操作。</summary>
    private Label? _titleLabel;

    /// <summary>状态控件，用于显示请求倒计时、等待对方等信息。</summary>
    private Label? _statusLabel;

    /// <summary>显示请求剩余时间的文字，帮助玩家准确判断回应时限。</summary>
    private Label? _countdownLabel;

    /// <summary>按请求剩余时间逐渐耗尽的可视化进度条。</summary>
    private ProgressBar? _requestProgressBar;

    /// <summary>请求倒计时尚余秒数；由 Godot 每帧传入的 delta 递减。</summary>
    private double _requestSecondsRemaining;

    /// <summary>当前倒计时显示的提示文字，例如“回应剩余”或“等待回应”。</summary>
    private string _requestCountdownDescription = string.Empty;

    /// <summary>只有正在等待回应的请求才启用本地动画倒计时。</summary>
    private bool _isRequestCountdownActive;

    /// <summary>显示玩家按钮或卡牌按钮的列表容器。</summary>
    private VBoxContainer? _itemsContainer;

    /// <summary>当前请求的同意按钮；无待处理请求时隐藏。</summary>
    private Button? _acceptButton;

    /// <summary>当前请求的拒绝按钮；无待处理请求时隐藏。</summary>
    private Button? _declineButton;

    /// <summary>结束自己的主动操作的跳过按钮。</summary>
    private Button? _skipButton;

    /// <summary>允许该节点挂入游戏覆盖层栈时安全创建界面控件。</summary>
    public override void _Ready()
    {
        BuildInterface();
    }

    /// <summary>
    /// 创建由 Godot 原生控件组成的交换面板。
    /// 玩家请求对象使用按钮列表；卡牌选取会切换到游戏自带的卡图网格界面。
    /// </summary>
    private void BuildInterface()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Enabled;

        // 半透明全屏底色既提示这是模态流程，也阻止点击穿透到下面的奖励/地图界面。
        var backdrop = new ColorRect
        {
            Color = new Color(0.02f, 0.02f, 0.04f, 0.78f),
            MouseFilter = MouseFilterEnum.Stop
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        var center = new CenterContainer
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(760, 500),
            MouseFilter = MouseFilterEnum.Stop
        };
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(margin);

        var layout = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        layout.AddThemeConstantOverride("separation", 14);
        margin.AddChild(layout);

        _titleLabel = new Label
        {
            Text = "幕间卡牌交换",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 30);
        layout.AddChild(_titleLabel);

        _statusLabel = new Label
        {
            Text = "选择一位玩家发起交换，或跳过本阶段。",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 18);
        layout.AddChild(_statusLabel);

        // 将文字倒计时和进度条放在玩家列表之前，使请求时限在弹窗中醒目可见。
        _countdownLabel = new Label
        {
            Text = string.Empty,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false
        };
        _countdownLabel.AddThemeFontSizeOverride("font_size", 16);
        layout.AddChild(_countdownLabel);

        // 进度条初始为满格；请求经过五秒后逐渐归零，与主持端超时判定同步。
        _requestProgressBar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 100,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 14),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Visible = false
        };
        layout.AddChild(_requestProgressBar);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        layout.AddChild(scroll);

        _itemsContainer = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _itemsContainer.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_itemsContainer);

        var responseButtons = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        responseButtons.AddThemeConstantOverride("separation", 12);
        layout.AddChild(responseButtons);

        _acceptButton = new Button { Text = "同意", Visible = false };
        _acceptButton.Pressed += () => RequestAccepted?.Invoke();
        responseButtons.AddChild(_acceptButton);

        _declineButton = new Button { Text = "拒绝", Visible = false };
        _declineButton.Pressed += () => RequestDeclined?.Invoke();
        responseButtons.AddChild(_declineButton);

        var bottomButtons = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.End
        };
        layout.AddChild(bottomButtons);

        _skipButton = new Button { Text = "跳过 0/0" };
        _skipButton.Pressed += () => SkipSelected?.Invoke();
        bottomButtons.AddChild(_skipButton);
    }

    /// <summary>
    /// 显示可以邀请的玩家列表。
    /// 玩家标识与昵称分开传入：按钮文字使用昵称，点击事件则携带稳定标识进行联机校验。
    /// </summary>
    public void ShowPlayers(IEnumerable<(string PlayerId, string DisplayName, bool CanRequest)> players)
    {
        ClearItems();
        StopRequestCountdown();
        SetHeader("选择交换对象", "点击玩家昵称发送交换请求；请求五秒内未获回应会自动撤销。");
        SetResponseButtonsVisible(false);

        foreach ((string playerId, string displayName, bool canRequest) in players)
        {
            var button = new Button
            {
                Text = canRequest ? displayName : $"{displayName}（暂不可邀请）",
                Disabled = !canRequest,
                CustomMinimumSize = new Vector2(0, 48)
            };
            button.Pressed += () => PlayerRequestSelected?.Invoke(playerId);
            _itemsContainer?.AddChild(button);
        }
    }

    /// <summary>显示一条发给当前玩家的请求，并展示同意/拒绝按钮。</summary>
    public void ShowIncomingRequest(string initiatorName, double secondsRemaining)
    {
        ClearItems();
        SetHeader("收到交换请求", $"{initiatorName} 想与你交换一张卡牌，请在倒计时结束前回应。");
        StartRequestCountdown(secondsRemaining, "回应剩余");
        SetResponseButtonsVisible(true);
    }

    /// <summary>让发起者也能看到对方回应请求的剩余时间和进度。</summary>
    public void ShowOutgoingRequest(string recipientName, double secondsRemaining)
    {
        ClearItems();
        SetHeader("等待玩家回应", $"已向 {recipientName} 发送交换请求；对方未及时回应时请求会自动撤销。");
        StartRequestCountdown(secondsRemaining, "等待回应");
        SetResponseButtonsVisible(false);
    }

    /// <summary>提示玩家游戏原生的图形卡牌选择界面将打开。</summary>
    public void ShowCardSelectionWaiting(string message)
    {
        ClearItems();
        StopRequestCountdown();
        SetHeader("选择一张卡牌", message);
        SetResponseButtonsVisible(false);
    }

    /// <summary>刷新跳过按钮上的全局计数，显示已跳过人数与本局总人数。</summary>
    public void SetSkipCount(int skippedPlayers, int totalPlayers)
    {
        if (_skipButton is null)
        {
            return;
        }

        // 防止异常网络状态产生负数或“已跳过人数大于总人数”的显示。
        int safeTotal = Math.Max(0, totalPlayers);
        int safeSkipped = Math.Clamp(skippedPlayers, 0, safeTotal);
        _skipButton.Text = $"跳过 {safeSkipped}/{safeTotal}";
    }

    /// <summary>每帧更新本地显示；真正的超时裁决仍由主持端统一完成并广播。</summary>
    public override void _Process(double delta)
    {
        if (!_isRequestCountdownActive || _countdownLabel is null || _requestProgressBar is null)
        {
            return;
        }

        // delta 是 Godot 当前帧经过的秒数；用它而非帧数递减，保证不同帧率下倒计时速度一致。
        _requestSecondsRemaining = Math.Max(0, _requestSecondsRemaining - delta);
        _requestProgressBar.Value = _requestSecondsRemaining / TradePhaseCoordinator.RequestResponseTimeout.TotalSeconds * 100.0;
        _countdownLabel.Text = _requestSecondsRemaining > 0
            ? $"{_requestCountdownDescription}：{_requestSecondsRemaining:0.0} 秒"
            : "请求已到期，正在同步撤销……";

        // 本地动画到零后停止更新，但保留零格进度和到期提示，直到主持端状态消息到达。
        if (_requestSecondsRemaining <= 0)
        {
            _isRequestCountdownActive = false;
        }
    }

    /// <summary>显示等待状态，例如等待另一名玩家回应或确认卡牌。</summary>
    public void SetStatus(string message)
    {
        if (_statusLabel is not null)
        {
            _statusLabel.Text = message;
        }
    }

    /// <summary>由交换流程服务在整个幕间阶段完成后关闭面板。</summary>
    public void Close()
    {
        NOverlayStack.Instance?.Remove(this);
        QueueFree();
    }

    /// <summary>设置页面标题和操作说明，减少不同流程界面各自维护控件的重复代码。</summary>
    private void SetHeader(string title, string status)
    {
        if (_titleLabel is not null)
        {
            _titleLabel.Text = title;
        }

        SetStatus(status);
    }

    /// <summary>统一控制收到请求时才出现的同意/拒绝按钮。</summary>
    private void SetResponseButtonsVisible(bool visible)
    {
        if (_acceptButton is not null)
        {
            _acceptButton.Visible = visible;
        }

        if (_declineButton is not null)
        {
            _declineButton.Visible = visible;
        }
    }

    /// <summary>启动请求倒计时显示，并按剩余时间初始化进度条。</summary>
    private void StartRequestCountdown(double secondsRemaining, string description)
    {
        _requestSecondsRemaining = Math.Clamp(
            secondsRemaining,
            0,
            TradePhaseCoordinator.RequestResponseTimeout.TotalSeconds);
        _requestCountdownDescription = description;
        _isRequestCountdownActive = _requestSecondsRemaining > 0;

        if (_countdownLabel is not null)
        {
            _countdownLabel.Visible = true;
            _countdownLabel.Text = $"{_requestCountdownDescription}：{_requestSecondsRemaining:0.0} 秒";
        }

        if (_requestProgressBar is not null)
        {
            _requestProgressBar.Visible = true;
            _requestProgressBar.Value = _requestSecondsRemaining /
                TradePhaseCoordinator.RequestResponseTimeout.TotalSeconds * 100.0;
        }
    }

    /// <summary>离开请求等待界面时隐藏并停止倒计时，避免旧请求时间残留到其他步骤。</summary>
    private void StopRequestCountdown()
    {
        _isRequestCountdownActive = false;
        if (_countdownLabel is not null)
        {
            _countdownLabel.Visible = false;
        }

        if (_requestProgressBar is not null)
        {
            _requestProgressBar.Visible = false;
        }
    }

    /// <summary>重画可交换玩家列表前移除上一阶段留下的按钮。</summary>
    private void ClearItems()
    {
        if (_itemsContainer is null)
        {
            return;
        }

        foreach (Node child in _itemsContainer.GetChildren())
        {
            _itemsContainer.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>交换面板进入游戏覆盖层栈时暂无额外动画逻辑。</summary>
    public void AfterOverlayOpened() { }

    /// <summary>交换面板从游戏覆盖层栈移除时暂无额外状态逻辑。</summary>
    public void AfterOverlayClosed() { }

    /// <summary>交换面板重新成为最上层界面时暂无额外显示逻辑。</summary>
    public void AfterOverlayShown() { }

    /// <summary>交换面板被其他覆盖层挡住时暂无额外隐藏逻辑。</summary>
    public void AfterOverlayHidden() { }
}
