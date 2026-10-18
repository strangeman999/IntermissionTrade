using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace IntermissionTrade.IntermissionTradeCode;

/// <summary>
/// 协调幕间交换界面、主持端校验、卡牌移动和原游戏切幕的运行时服务。
/// 目前作为接入层实现的第一版；游戏内运行和双人联机验证仍须在游戏中完成。
/// </summary>
internal static class TradePhaseRuntime
{
    /// <summary>每个客户端各自维护一份相同的流程状态；状态变更由主持端消息驱动。</summary>
    private static readonly TradePhaseCoordinator Coordinator = new();

    /// <summary>当前暂停在幕末的游戏同步器，交换结束后通过原方法继续切幕。</summary>
    private static ActChangeSynchronizer? _heldActChangeSynchronizer;

    /// <summary>从游戏同步器反射字段取得的本局状态；RunManager 将对应属性声明为 private。</summary>
    private static RunState? _runState;

    /// <summary>标记这次反射调用是继续原切幕，而不是再次开启交换阶段。</summary>
    private static bool _allowNextActTransition;

    /// <summary>当前显示的交换面板。</summary>
    private static TradeOverlayScreen? _screen;

    /// <summary>收到阶段结束消息但本地卡牌移动尚未完成时，暂存结束信号。</summary>
    private static bool _phaseEndReceived;

    /// <summary>只订阅一次 BaseLib 消息路由，避免重复初始化产生重复处理。</summary>
    private static bool _initialized;

    /// <summary>绑定消息接收事件。应在 mod 初始化时调用一次。</summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        TradeNetworkRouter.IntentReceived += OnIntentReceived;
        TradeNetworkRouter.StateReceived += OnStateReceived;
        _initialized = true;
    }

    /// <summary>
    /// 被 Harmony 切幕前缀调用。若此刻是第一幕或第二幕，先暂停原切幕，等待交换流程完成。
    /// 第三幕结束或单人游戏时返回 true，让原游戏直接继续。
    /// </summary>
    public static bool TryPauseBeforeNextAct(ActChangeSynchronizer synchronizer)
    {
        if (_allowNextActTransition)
        {
            _allowNextActTransition = false;
            return true;
        }

        // 游戏的同步器持有本局 RunState；通过已核对过的私有字段读取，避免猜测不存在的公开属性。
        _runState = AccessTools.Field(typeof(ActChangeSynchronizer), "_runState")?.GetValue(synchronizer) as RunState;
        RunState? runState = _runState;
        if (runState is null || runState.Players.Count <= 1)
        {
            return true;
        }

        // CurrentActIndex 为从 0 开始的幕索引：0、1 对应用户要求的第一幕和第二幕。
        int actNumber = runState.CurrentActIndex + 1;
        if (actNumber is not (1 or 2))
        {
            return true;
        }

        _heldActChangeSynchronizer = synchronizer;
        _phaseEndReceived = false;

        // 只有主持端创建并广播阶段开始消息。客户端先暂停原流程，等待该消息到达。
        if (RunManager.Instance.NetService.Type == NetGameType.Host)
        {
            BeginPhaseOnHost(actNumber);
        }

        return false;
    }

    /// <summary>主持端开始阶段并把幕编号和共同的开始时间广播给所有客户端。</summary>
    private static void BeginPhaseOnHost(int actNumber)
    {
        RunState? runState = _runState;
        if (runState is null || !Coordinator.BeginPhase(actNumber, runState.Players.Select(player => PlayerKey(player.NetId))))
        {
            MainFile.Logger.Error("无法开始幕间交换：当前牌局或玩家列表无效。");
            return;
        }

        var message = new TradeStateMessage(
            TradeStateKind.PhaseStarted,
            actNumber,
            timestampUtcTicks: DateTimeOffset.UtcNow.UtcTicks);

        // Coordinator 已由上方 BeginPhase 初始化；这里只打开面板，不能再重复初始化并清空阶段状态。
        OpenAndBindScreen();
        TradeNetworkRouter.BroadcastState(message);

        // 若当前游戏场景无法创建覆盖层，自动结束本阶段，避免把所有玩家永久卡在幕末。
        if (_screen is null)
        {
            MainFile.Logger.Error("幕间交换界面无法打开；本阶段将自动跳过并继续切幕。");
            foreach (Player player in runState.Players)
            {
                Coordinator.SkipInitiating(PlayerKey(player.NetId));
            }

            TryFinishPhaseOnHost();
        }
    }

    /// <summary>网络回调中只做主线程派发，Godot 节点和游戏模型均在主线程访问。</summary>
    private static void OnIntentReceived(TradeIntentMessage message, ulong senderId)
    {
        Callable.From(() => ProcessHostIntent(message, senderId)).CallDeferred();
    }

    /// <summary>校验并处理来自客户端的操作意图；只有主持端可以改动权威流程状态。</summary>
    private static void ProcessHostIntent(TradeIntentMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host ||
            senderId != message.ActorNetId ||
            !Coordinator.ContainsPlayer(PlayerKey(senderId)))
        {
            MainFile.Logger.Warn($"拒绝无效的交换操作消息，发送者：{senderId}。");
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        switch (message.Kind)
        {
            case TradeIntentKind.Request:
                HandleRequestIntent(message, now);
                break;
            case TradeIntentKind.Accept:
                HandleResponseIntent(message, now, accepted: true);
                break;
            case TradeIntentKind.Decline:
                HandleResponseIntent(message, now, accepted: false);
                break;
            case TradeIntentKind.SelectCard:
                HandleCardSelectionIntent(message);
                break;
            case TradeIntentKind.Skip:
                HandleSkipIntent(message);
                break;
            default:
                MainFile.Logger.Warn($"拒绝未知交换操作类型：{message.Kind}。");
                break;
        }
    }

    /// <summary>检查玩家选择并广播新请求；失败的请求不会更改机会状态。</summary>
    private static void HandleRequestIntent(TradeIntentMessage intent, DateTimeOffset now)
    {
        string actorId = PlayerKey(intent.ActorNetId);
        string targetId = PlayerKey(intent.TargetNetId);
        TradeRequestSnapshot? request = Coordinator.TryCreateRequest(actorId, targetId, now);
        if (request is null)
        {
            RefreshScreen();
            return;
        }

        var state = new TradeStateMessage(
            TradeStateKind.RequestCreated,
            Coordinator.ActNumber,
            intent.ActorNetId,
            intent.TargetNetId,
            timestampUtcTicks: now.UtcTicks);

        // 主持端刚刚直接更新过协调器；只刷新界面，再广播给客户端应用一次。
        RefreshScreen();
        TradeNetworkRouter.BroadcastState(state);
        StartHostRequestTimeout(request);
    }

    /// <summary>只允许指定接收者回应仍有效的请求；拒绝后不会消耗发起者的机会。</summary>
    private static void HandleResponseIntent(TradeIntentMessage intent, DateTimeOffset now, bool accepted)
    {
        bool changed = accepted
            ? Coordinator.AcceptRequest(PlayerKey(intent.ActorNetId), now)
            : Coordinator.DeclineRequest(PlayerKey(intent.ActorNetId), now);

        if (!changed)
        {
            RefreshScreen();
            return;
        }

        var state = new TradeStateMessage(
            accepted ? TradeStateKind.RequestAccepted : TradeStateKind.RequestDeclined,
            Coordinator.ActNumber,
            intent.ActorNetId,
            timestampUtcTicks: now.UtcTicks);

        RefreshScreen();
        TradeNetworkRouter.BroadcastState(state);
    }

    /// <summary>验证卡牌索引、所属玩家和诅咒类型后，再登记该玩家的确认选择。</summary>
    private static void HandleCardSelectionIntent(TradeIntentMessage intent)
    {
        TradeRequestSnapshot? pending = Coordinator.PendingRequest;
        if (pending is null ||
            !TryGetPlayer(intent.ActorNetId, out Player player) ||
            intent.DeckCardIndex < 0 ||
            intent.DeckCardIndex >= player.Deck.Cards.Count ||
            player.Deck.Cards[intent.DeckCardIndex].Type == CardType.Curse)
        {
            RefreshScreen();
            return;
        }

        // 牌组索引仅在当前单笔交易中使用；唯一交易锁保证本次交换完成前不会发生另一笔牌组修改。
        string cardKey = intent.DeckCardIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool bothPlayersConfirmed = Coordinator.ConfirmCard(PlayerKey(intent.ActorNetId), cardKey);
        var state = new TradeStateMessage(
            TradeStateKind.CardSelected,
            Coordinator.ActNumber,
            intent.ActorNetId,
            intent.TargetNetId,
            intent.DeckCardIndex);

        RefreshScreen();
        TradeNetworkRouter.BroadcastState(state);

        // 只有主持端在收到双方确认后执行卡牌迁移；其他客户端等它广播迁移结果。
        if (bothPlayersConfirmed)
        {
            TaskHelper.RunSafely(CommitTradeOnHostAsync());
        }
    }

    /// <summary>玩家跳过只结束自己的主动发起机会，仍保留接收其他玩家请求的资格。</summary>
    private static void HandleSkipIntent(TradeIntentMessage intent)
    {
        if (!Coordinator.SkipInitiating(PlayerKey(intent.ActorNetId)))
        {
            RefreshScreen();
            return;
        }

        var state = new TradeStateMessage(TradeStateKind.PlayerSkipped, Coordinator.ActNumber, intent.ActorNetId);
        RefreshScreen();
        TradeNetworkRouter.BroadcastState(state);
        TryFinishPhaseOnHost();
    }

    /// <summary>
    /// 重新定位当前选中的两张卡并通过游戏自己的卡牌命令交换所有者。
    /// 现有 CardModel 被移动而不是复制，因此升级等模型状态随原卡保留。
    /// </summary>
    private static async Task CommitTradeOnHostAsync()
    {
        TradeRequestSnapshot? pending = Coordinator.PendingRequest;
        if (pending is null ||
            !int.TryParse(pending.InitiatorCardId, out int initiatorIndex) ||
            !int.TryParse(pending.RecipientCardId, out int recipientIndex) ||
            !TryGetPlayer(ParsePlayerKey(pending.InitiatorId), out Player initiator) ||
            !TryGetPlayer(ParsePlayerKey(pending.RecipientId), out Player recipient) ||
            initiatorIndex < 0 || initiatorIndex >= initiator.Deck.Cards.Count ||
            recipientIndex < 0 || recipientIndex >= recipient.Deck.Cards.Count)
        {
            BroadcastTradeFailure(pending);
            return;
        }

        var initiatorCard = initiator.Deck.Cards[initiatorIndex];
        var recipientCard = recipient.Deck.Cards[recipientIndex];
        if (initiatorCard.Type == CardType.Curse || recipientCard.Type == CardType.Curse)
        {
            BroadcastTradeFailure(pending);
            return;
        }

        bool firstMoveCompleted = false;
        try
        {
            // 移动同一张卡牌模型到对方的牌组，保留升级和其他卡牌属性。
            await CardPileCmd.GiveToAnotherPlayer(initiatorCard, recipient, PileType.Deck);
            firstMoveCompleted = true;
            await CardPileCmd.GiveToAnotherPlayer(recipientCard, initiator, PileType.Deck);

            if (!Coordinator.CompleteConfirmedTrade())
            {
                throw new InvalidOperationException("交易状态未处于可提交状态。");
            }

            var committed = new TradeStateMessage(
                TradeStateKind.TradeCommitted,
                Coordinator.ActNumber,
                ParsePlayerKey(pending.InitiatorId),
                ParsePlayerKey(pending.RecipientId),
                initiatorIndex,
                recipientIndex);
            TradeNetworkRouter.BroadcastState(committed);
            RefreshScreen();
            TryFinishPhaseOnHost();
        }
        catch (Exception exception)
        {
            MainFile.Logger.Error($"卡牌交换失败：{exception}");

            // 第二张卡迁移失败时尝试将第一张卡放回原主人，避免留下单边转移。
            // 极少数异常可能在命令抛错前已改变第二张卡的归属；先尝试把它还给原接收者。
            if (recipientCard.Owner.NetId != ParsePlayerKey(pending.RecipientId))
            {
                try
                {
                    await CardPileCmd.GiveToAnotherPlayer(recipientCard, recipient, PileType.Deck);
                }
                catch (Exception rollbackException)
                {
                    MainFile.Logger.Error($"回滚第二张卡牌失败：{rollbackException}");
                }
            }

            if (firstMoveCompleted && initiatorCard.Owner.NetId != ParsePlayerKey(pending.InitiatorId))
            {
                try
                {
                    await CardPileCmd.GiveToAnotherPlayer(initiatorCard, initiator, PileType.Deck);
                }
                catch (Exception rollbackException)
                {
                    MainFile.Logger.Error($"回滚卡牌交换失败：{rollbackException}");
                }
            }

            BroadcastTradeFailure(pending);
        }
    }

    /// <summary>卡牌迁移失败时广播撤销，发起者保留本幕的主动机会。</summary>
    private static void BroadcastTradeFailure(TradeRequestSnapshot? pending)
    {
        if (pending is null)
        {
            return;
        }

        Coordinator.FailConfirmedTrade();
        var state = new TradeStateMessage(
            TradeStateKind.TradeFailed,
            Coordinator.ActNumber,
            ParsePlayerKey(pending.InitiatorId),
            ParsePlayerKey(pending.RecipientId));
        RefreshScreen();
        TradeNetworkRouter.BroadcastState(state);
    }

    /// <summary>客戶端按主持端提交消息执行同一对卡牌迁移。</summary>
    private static async Task CompleteRemoteSideTransferAsync(TradeStateMessage message)
    {
        if (!TryGetPlayer(message.ActorNetId, out Player initiator) ||
            !TryGetPlayer(message.TargetNetId, out Player recipient) ||
            message.ActorDeckCardIndex < 0 || message.ActorDeckCardIndex >= initiator.Deck.Cards.Count ||
            message.TargetDeckCardIndex < 0 || message.TargetDeckCardIndex >= recipient.Deck.Cards.Count)
        {
            MainFile.Logger.Error("收到卡牌交换结果，但本地牌组与主持端不一致。");
            return;
        }

        var initiatorCard = initiator.Deck.Cards[message.ActorDeckCardIndex];
        var recipientCard = recipient.Deck.Cards[message.TargetDeckCardIndex];
        if (initiatorCard.Type == CardType.Curse || recipientCard.Type == CardType.Curse)
        {
            MainFile.Logger.Error("收到的交换结果包含诅咒牌，已拒绝应用。");
            return;
        }

        await CardPileCmd.GiveToAnotherPlayer(initiatorCard, recipient, PileType.Deck);
        await CardPileCmd.GiveToAnotherPlayer(recipientCard, initiator, PileType.Deck);

        // 主持端已先完成其本地迁移和机会记账，客户端也在本地提交相同结果。
        Coordinator.CompleteConfirmedTrade();
        RefreshScreen();
        TryResumeAfterPhaseEnd();
    }

    /// <summary>切回当前玩家的 Godot 界面并把按钮点击变成待审核意图。</summary>
    private static void OpenAndBindScreen()
    {
        _screen = TradeOverlayScreen.Open();
        if (_screen is null)
        {
            MainFile.Logger.Error("当前场景没有可用的 NOverlayStack，无法显示交换界面。");
            return;
        }

        _screen.PlayerRequestSelected += targetId => SubmitIntent(new TradeIntentMessage(
            TradeIntentKind.Request,
            LocalContext.NetId ?? 0,
            ParsePlayerKey(targetId)));
        _screen.RequestAccepted += () => SubmitIntent(new TradeIntentMessage(
            TradeIntentKind.Accept,
            LocalContext.NetId ?? 0));
        _screen.RequestDeclined += () => SubmitIntent(new TradeIntentMessage(
            TradeIntentKind.Decline,
            LocalContext.NetId ?? 0));
        _screen.SkipSelected += () => SubmitIntent(new TradeIntentMessage(
            TradeIntentKind.Skip,
            LocalContext.NetId ?? 0));
        _screen.CardSelected += cardId =>
        {
            if (int.TryParse(cardId, out int cardIndex))
            {
                SubmitIntent(new TradeIntentMessage(
                    TradeIntentKind.SelectCard,
                    LocalContext.NetId ?? 0,
                    deckCardIndex: cardIndex));
            }
        };

        RefreshScreen();
    }

    /// <summary>本机玩家提交意图；主持端直接处理，客户端通过 BaseLib 消息发给主持端。</summary>
    private static void SubmitIntent(TradeIntentMessage message)
    {
        if (RunManager.Instance.NetService.Type == NetGameType.Host)
        {
            ProcessHostIntent(message, RunManager.Instance.NetService.NetId);
        }
        else
        {
            TradeNetworkRouter.SendIntent(message);
        }
    }

    /// <summary>接收权威状态广播；客户端只接受当前主持端发出的状态消息。</summary>
    private static void OnStateReceived(TradeStateMessage message, ulong senderId)
    {
        Callable.From(() =>
        {
            if (RunManager.Instance.NetService.Type != NetGameType.Client ||
                RunManager.Instance.NetService is not NetClientGameService clientService ||
                senderId != clientService.HostNetId)
            {
                return;
            }

            ApplyState(message);
        }).CallDeferred();
    }

    /// <summary>将主持端广播的流程状态同步到本地协调器和本地界面。</summary>
    private static void ApplyState(TradeStateMessage message)
    {
        switch (message.Kind)
        {
            case TradeStateKind.PhaseStarted:
                ApplyPhaseStarted(message);
                break;
            case TradeStateKind.RequestCreated:
                Coordinator.TryCreateRequest(
                    PlayerKey(message.ActorNetId),
                    PlayerKey(message.TargetNetId),
                    new DateTimeOffset(message.TimestampUtcTicks, TimeSpan.Zero));
                StartHostRequestTimeout(Coordinator.PendingRequest);
                RefreshScreen();
                break;
            case TradeStateKind.RequestAccepted:
                Coordinator.AcceptRequest(
                    PlayerKey(message.ActorNetId),
                    new DateTimeOffset(message.TimestampUtcTicks, TimeSpan.Zero));
                RefreshScreen();
                break;
            case TradeStateKind.RequestDeclined:
                Coordinator.DeclineRequest(
                    PlayerKey(message.ActorNetId),
                    new DateTimeOffset(message.TimestampUtcTicks, TimeSpan.Zero));
                RefreshScreen();
                break;
            case TradeStateKind.RequestTimedOut:
                Coordinator.ExpireRequestIfNeeded(new DateTimeOffset(message.TimestampUtcTicks, TimeSpan.Zero));
                RefreshScreen();
                break;
            case TradeStateKind.CardSelected:
                Coordinator.ConfirmCard(PlayerKey(message.ActorNetId),
                    message.ActorDeckCardIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                RefreshScreen();
                break;
            case TradeStateKind.TradeCommitted:
                TaskHelper.RunSafely(CompleteRemoteSideTransferAsync(message));
                break;
            case TradeStateKind.TradeFailed:
                Coordinator.FailConfirmedTrade();
                RefreshScreen();
                break;
            case TradeStateKind.PlayerSkipped:
                Coordinator.SkipInitiating(PlayerKey(message.ActorNetId));
                RefreshScreen();
                break;
            case TradeStateKind.PhaseEnded:
                _phaseEndReceived = true;
                TryResumeAfterPhaseEnd();
                break;
        }
    }

    /// <summary>在每个客户端初始化相同玩家列表并打开本地界面。</summary>
    private static void ApplyPhaseStarted(TradeStateMessage message)
    {
        RunState? runState = _runState;
        if (runState is null || !Coordinator.BeginPhase(
                message.ActNumber,
                runState.Players.Select(player => PlayerKey(player.NetId))))
        {
            return;
        }

        OpenAndBindScreen();
    }

    /// <summary>仅主持端启动五秒计时，到期后广播超时状态。</summary>
    private static void StartHostRequestTimeout(TradeRequestSnapshot? request)
    {
        if (request is null || RunManager.Instance.NetService.Type != NetGameType.Host || NRun.Instance is null)
        {
            return;
        }

        string initiatorId = request.InitiatorId;
        string recipientId = request.RecipientId;
        long deadlineTicks = request.ResponseDeadline.UtcTicks;
        double secondsRemaining = Math.Max(0, (request.ResponseDeadline - DateTimeOffset.UtcNow).TotalSeconds);
        SceneTreeTimer timer = NRun.Instance.GetTree().CreateTimer(secondsRemaining);
        timer.Timeout += () =>
        {
            TradeRequestSnapshot? pending = Coordinator.PendingRequest;
            if (pending is null ||
                pending.InitiatorId != initiatorId ||
                pending.RecipientId != recipientId ||
                pending.ResponseDeadline.UtcTicks != deadlineTicks ||
                !Coordinator.ExpireRequestIfNeeded(DateTimeOffset.UtcNow))
            {
                return;
            }

            var state = new TradeStateMessage(
                TradeStateKind.RequestTimedOut,
                Coordinator.ActNumber,
                ParsePlayerKey(initiatorId),
                ParsePlayerKey(recipientId),
                timestampUtcTicks: deadlineTicks);
            TradeNetworkRouter.BroadcastState(state);
            RefreshScreen();
        };
    }

    /// <summary>根据当前状态刷新本机面板内容，卡牌列表始终来自本机玩家自己的整副牌组。</summary>
    private static void RefreshScreen()
    {
        if (_screen is null || !GodotObject.IsInstanceValid(_screen) || _runState is null)
        {
            return;
        }

        ulong localNetId = LocalContext.NetId ?? RunManager.Instance.NetService.NetId;
        string localKey = PlayerKey(localNetId);
        TradeRequestSnapshot? pending = Coordinator.PendingRequest;

        if (pending is null)
        {
            var players = _runState.Players
                .Where(player => player.NetId != localNetId)
                .Select(player => (
                    PlayerId: PlayerKey(player.NetId),
                    DisplayName: PlatformUtil.GetPlayerNameRaw(RunManager.Instance.NetService.Platform, player.NetId),
                    CanRequest: Coordinator.CanInitiate(localKey) && !Coordinator.HasPendingRequest));

            _screen.ShowPlayers(players);
            if (!Coordinator.CanInitiate(localKey))
            {
                _screen.SetStatus("你已完成本幕的主动操作；仍可接收其他玩家的请求。");
            }

            return;
        }

        if (pending.Status == TradeRequestStatus.AwaitingResponse && pending.RecipientId == localKey)
        {
            int seconds = (int)Math.Ceiling(Math.Max(0, (pending.ResponseDeadline - DateTimeOffset.UtcNow).TotalSeconds));
            string initiatorName = PlatformUtil.GetPlayerNameRaw(
                RunManager.Instance.NetService.Platform,
                ParsePlayerKey(pending.InitiatorId));
            _screen.ShowIncomingRequest(initiatorName, seconds);
            return;
        }

        if (pending.Status == TradeRequestStatus.SelectingCards &&
            (pending.InitiatorId == localKey || pending.RecipientId == localKey))
        {
            if (!TryGetPlayer(localNetId, out Player localPlayer))
            {
                return;
            }

            _screen.ShowCards(localPlayer.Deck.Cards
                .Select((card, index) => (Card: card, Index: index))
                .Where(entry => entry.Card.Type != CardType.Curse)
                .Select(entry => (
                    CardId: entry.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    DisplayName: $"{entry.Card.Id.Entry}{(entry.Card.IsUpgraded ? " +" : string.Empty)}")));

            bool alreadySelected = pending.InitiatorId == localKey
                ? pending.InitiatorCardId is not null
                : pending.RecipientCardId is not null;
            if (alreadySelected)
            {
                _screen.SetStatus("已提交选择，等待另一位玩家确认卡牌。");
            }

            return;
        }

        _screen.ShowPlayers(Array.Empty<(string PlayerId, string DisplayName, bool CanRequest)>());
        _screen.SetStatus("正在等待其他玩家完成这笔交易……");
    }

    /// <summary>在主持端检查交换阶段是否已满足结束条件。</summary>
    private static void TryFinishPhaseOnHost()
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host ||
            Coordinator.Status != TradePhaseStatus.Completed)
        {
            return;
        }

        var state = new TradeStateMessage(TradeStateKind.PhaseEnded, Coordinator.ActNumber);
        // 主持端本地阶段已经完成，只需记下结束信号；客户端会在收到广播后自行应用。
        _phaseEndReceived = true;
        TradeNetworkRouter.BroadcastState(state);
        TryResumeAfterPhaseEnd();
    }

    /// <summary>切幕结束信号若早于本地卡牌移动完成则暂缓，避免下一幕先开始。</summary>
    private static void TryResumeAfterPhaseEnd()
    {
        if (!_phaseEndReceived || Coordinator.PendingRequest is not null || _heldActChangeSynchronizer is null)
        {
            return;
        }

        _screen?.Close();
        _screen = null;

        ActChangeSynchronizer synchronizer = _heldActChangeSynchronizer;
        _heldActChangeSynchronizer = null;
        _allowNextActTransition = true;

        // MoveToNextAct 是游戏自己的私有方法；通过 Harmony 的原方法重新进入，保留它的幕进度与淡出逻辑。
        AccessTools.Method(typeof(ActChangeSynchronizer), "MoveToNextAct")?.Invoke(synchronizer, null);
    }

    /// <summary>从当前游戏运行状态按网络玩家标识获取玩家模型。</summary>
    private static bool TryGetPlayer(ulong netId, out Player player)
    {
        Player? foundPlayer = _runState?.GetPlayer(netId);
        if (foundPlayer is null)
        {
            player = null!;
            return false;
        }

        player = foundPlayer;
        return true;
    }

    /// <summary>把 NetId 转为协调器使用的稳定字符串键，避免依赖可能重复的昵称。</summary>
    private static string PlayerKey(ulong netId) => netId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>把协调器保存的 NetId 字符串键还原成游戏网络玩家标识。</summary>
    private static ulong ParsePlayerKey(string playerKey) =>
        ulong.TryParse(playerKey, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out ulong netId) ? netId : 0;
}

/// <summary>
/// 在游戏所有玩家都准备进入下一幕后拦截原切幕函数。
/// 交换完成后，运行时服务通过同一个 Harmony 入口继续执行游戏原本的幕切换。
/// </summary>
[HarmonyPatch(typeof(ActChangeSynchronizer), "MoveToNextAct")]
internal static class IntermissionTradeActTransitionPatch
{
    /// <summary>返回 false 暂停原函数；阶段结束后通过运行时的继续标记放行。</summary>
    private static bool Prefix(ActChangeSynchronizer __instance) =>
        TradePhaseRuntime.TryPauseBeforeNextAct(__instance);
}
