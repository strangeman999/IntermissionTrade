using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace IntermissionTrade.IntermissionTradeCode;

/// <summary>
/// 幕间卡牌交换面板的第一版界面骨架。
///
/// 该面板使用游戏现有的 NOverlayStack 管理，因而能作为模态覆盖层显示在奖励/地图界面之上。
/// 本类只负责显示状态和把玩家点击转成 C# 事件；它不会自行发送网络消息，也不会修改卡牌。
/// 之后由交换流程服务订阅这些事件，再通过 BaseLib 消息接口进行联机验证和同步。
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

    /// <summary>玩家选中卡牌并按下确认按钮后触发；参数使用牌组位置，而不是可能重复的显示名称。</summary>
    public event Action<string>? CardSelected;

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

    /// <summary>显示玩家按钮或卡牌按钮的列表容器。</summary>
    private VBoxContainer? _itemsContainer;

    /// <summary>当前请求的同意按钮；无待处理请求时隐藏。</summary>
    private Button? _acceptButton;

    /// <summary>当前请求的拒绝按钮；无待处理请求时隐藏。</summary>
    private Button? _declineButton;

    /// <summary>结束自己的主动操作的跳过按钮。</summary>
    private Button? _skipButton;

    /// <summary>卡牌选择步骤中用于确认已选卡牌的按钮。</summary>
    private Button? _confirmCardButton;

    /// <summary>本地暂存当前高亮的卡牌标识；只有点击确认后才发送给主持端。</summary>
    private string? _selectedCardId;

    /// <summary>允许该节点挂入游戏覆盖层栈时安全创建界面控件。</summary>
    public override void _Ready()
    {
        BuildInterface();
    }

    /// <summary>
    /// 创建由 Godot 原生控件组成的交换面板。
    /// 先用清晰、简单的文字按钮验证交互流程，卡牌图像和美术排版可在功能稳定后再完善。
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

        // 玩家先点卡牌进行本地选择，再点“确认卡牌”提交；避免误触直接完成确认。
        _confirmCardButton = new Button { Text = "确认卡牌", Visible = false, Disabled = true };
        _confirmCardButton.Pressed += () =>
        {
            if (_selectedCardId is not null)
            {
                CardSelected?.Invoke(_selectedCardId);
                _confirmCardButton.Disabled = true;
            }
        };
        responseButtons.AddChild(_confirmCardButton);

        var bottomButtons = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.End
        };
        layout.AddChild(bottomButtons);

        _skipButton = new Button { Text = "跳过" };
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
        _selectedCardId = null;
        SetHeader("选择交换对象", "点击玩家昵称发送交换请求；请求五秒内未获回应会自动撤销。");
        SetResponseButtonsVisible(false);
        SetCardConfirmationVisible(false);

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
    public void ShowIncomingRequest(string initiatorName, int secondsRemaining)
    {
        ClearItems();
        _selectedCardId = null;
        SetHeader("收到交换请求", $"{initiatorName} 想与你交换一张卡牌。请在 {Math.Max(0, secondsRemaining)} 秒内回应。");
        SetResponseButtonsVisible(true);
        SetCardConfirmationVisible(false);
    }

    /// <summary>显示当前可选择的卡牌列表；诅咒牌应由调用方提前过滤，不在界面中展示。</summary>
    public void ShowCards(IEnumerable<(string CardId, string DisplayName)> cards)
    {
        ClearItems();
        _selectedCardId = null;
        SetHeader("选择一张卡牌", "从自己的整副牌组中选择一张非诅咒牌，然后确认。");
        SetResponseButtonsVisible(false);
        SetCardConfirmationVisible(true);

        foreach ((string cardId, string displayName) in cards)
        {
            var button = new Button
            {
                Text = displayName,
                CustomMinimumSize = new Vector2(0, 48)
            };
            button.Pressed += () =>
            {
                _selectedCardId = cardId;
                if (_confirmCardButton is not null)
                {
                    _confirmCardButton.Disabled = false;
                }

                SetStatus($"已选择 {displayName}，点击“确认卡牌”提交给对方。");
            };
            _itemsContainer?.AddChild(button);
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

    /// <summary>只在选牌步骤显示确认按钮，并在没有选中卡牌时禁用它。</summary>
    private void SetCardConfirmationVisible(bool visible)
    {
        if (_confirmCardButton is not null)
        {
            _confirmCardButton.Visible = visible;
            _confirmCardButton.Disabled = !visible || _selectedCardId is null;
        }
    }

    /// <summary>重画玩家列表或卡牌列表前移除上一阶段留下的按钮。</summary>
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
