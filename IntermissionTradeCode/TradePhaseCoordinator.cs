using System;
using System.Collections.Generic;
using System.Linq;

namespace IntermissionTrade.IntermissionTradeCode;

/// <summary>
/// 描述幕间交换流程当前所处的阶段。
/// </summary>
public enum TradePhaseStatus
{
    /// <summary>尚未开始有效的幕间交换阶段。</summary>
    NotStarted,

    /// <summary>正在等待玩家发起请求、回应请求或完成选牌。</summary>
    InProgress,

    /// <summary>所有玩家都已结束主动操作，且没有尚未完成的交易。</summary>
    Completed
}

/// <summary>
/// 描述一笔交换请求从发出到完成的过程。
/// </summary>
public enum TradeRequestStatus
{
    /// <summary>请求已发出，等待接收玩家同意或拒绝。</summary>
    AwaitingResponse,

    /// <summary>接收玩家已同意，等待双方选择并确认卡牌。</summary>
    SelectingCards,

    /// <summary>双方均已确认，交易已完成。</summary>
    Completed,

    /// <summary>请求被接收玩家拒绝。</summary>
    Declined,

    /// <summary>请求超过五秒仍未得到回应，已自动撤销。</summary>
    TimedOut,

    /// <summary>实际转移卡牌失败，撤销本笔交易且不消耗发起者机会。</summary>
    Failed
}

/// <summary>
/// 单笔交易的只读状态快照，供后续 UI 显示“等待回应/选牌/等待确认”等状态。
/// 此对象只保存玩家和卡牌的稳定标识，不直接持有或修改游戏里的 Player/CardModel。
/// </summary>
public sealed class TradeRequestSnapshot
{
    /// <summary>发起交换的玩家标识。</summary>
    public string InitiatorId { get; }

    /// <summary>接收交换请求的玩家标识。</summary>
    public string RecipientId { get; }

    /// <summary>当前请求状态。</summary>
    public TradeRequestStatus Status { get; }

    /// <summary>请求发出时间；超时判断由阶段协调器统一处理。</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>接收玩家回应请求的最后期限；固定为请求发出后五秒。</summary>
    public DateTimeOffset ResponseDeadline { get; }

    /// <summary>发起者已选定并确认的卡牌标识；尚未确认时为 null。</summary>
    public string? InitiatorCardId { get; }

    /// <summary>接收者已选定并确认的卡牌标识；尚未确认时为 null。</summary>
    public string? RecipientCardId { get; }

    /// <summary>创建一份不可变快照，避免 UI 直接改写协调器内部状态。</summary>
    internal TradeRequestSnapshot(
        string initiatorId,
        string recipientId,
        TradeRequestStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset responseDeadline,
        string? initiatorCardId,
        string? recipientCardId)
    {
        InitiatorId = initiatorId;
        RecipientId = recipientId;
        Status = status;
        CreatedAt = createdAt;
        ResponseDeadline = responseDeadline;
        InitiatorCardId = initiatorCardId;
        RecipientCardId = recipientCardId;
    }
}

/// <summary>
/// 纯 C# 的幕间交换规则协调器。
///
/// 这个类只负责验证和记录玩法流程，不调用游戏对象、不显示界面，也不发送网络消息。
/// 之后接入游戏时，必须由联机权威端驱动本类，并把合法状态变化通过游戏/BaseLib 的联机机制同步；
/// 卡牌是否属于玩家、是否为诅咒牌，也必须在真正提交交换前由游戏侧再次校验。
/// </summary>
public sealed class TradePhaseCoordinator
{
    /// <summary>邀请玩家必须回应的时限。用户明确要求请求发出后五秒超时。</summary>
    public static readonly TimeSpan RequestResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>本阶段有效的幕编号；仅第一幕、第二幕允许交换。</summary>
    public int ActNumber { get; private set; }

    /// <summary>当前阶段总状态。</summary>
    public TradePhaseStatus Status { get; private set; } = TradePhaseStatus.NotStarted;

    /// <summary>本阶段参加交换的玩家总数，用于在界面上显示跳过进度。</summary>
    public int PlayerCount => _playerIds.Count;

    /// <summary>本阶段已明确点击跳过的玩家数；成功交换的玩家不计入该数字。</summary>
    public int SkippedPlayerCount => _playersWhoSkipped.Count;

    /// <summary>当前尚未终结的唯一一笔请求；阶段采用串行交易以避免同一玩家同时选多张牌。</summary>
    public TradeRequestSnapshot? PendingRequest { get; private set; }

    /// <summary>供界面和调用方快速判断当前是否有等待处理的请求或选牌交易。</summary>
    public bool HasPendingRequest => PendingRequest is not null;

    /// <summary>本幕参与交换的玩家标识，按开始阶段时的名单固定。</summary>
    private readonly HashSet<string> _playerIds = new(StringComparer.Ordinal);

    /// <summary>已成功主动发起过交换的玩家；接收交换不会写入此集合。</summary>
    private readonly HashSet<string> _initiatorsWhoSucceeded = new(StringComparer.Ordinal);

    /// <summary>已按“跳过”结束自己主动操作的玩家；仍允许他们接收他人请求。</summary>
    private readonly HashSet<string> _playersWhoSkipped = new(StringComparer.Ordinal);

    /// <summary>最近一笔交易的终态，供界面在关闭请求卡片前呈现结果。</summary>
    public TradeRequestSnapshot? LastResolvedRequest { get; private set; }

    /// <summary>
    /// 开始第一幕或第二幕结束后的交换阶段，并重置本幕所有玩家的主动发起机会。
    /// 第三幕或其他编号会被拒绝，避免误在整局结束时开启交换。
    /// </summary>
    /// <param name="actNumber">刚结束的幕编号，只接受 1 或 2。</param>
    /// <param name="playerIds">本局所有玩家的稳定唯一标识，不能使用可能重复或改变的昵称。</param>
    /// <returns>输入合法且阶段已开始时返回 true。</returns>
    public bool BeginPhase(int actNumber, IEnumerable<string> playerIds)
    {
        if (actNumber is not (1 or 2) || playerIds is null)
        {
            return false;
        }

        // 去除空标识并去重，避免 UI 昵称相同或上游数据异常导致玩家机会被混淆。
        var validPlayerIds = playerIds
            .Where(playerId => !string.IsNullOrWhiteSpace(playerId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (validPlayerIds.Length == 0)
        {
            return false;
        }

        ActNumber = actNumber;
        _playerIds.Clear();
        _playerIds.UnionWith(validPlayerIds);
        _initiatorsWhoSucceeded.Clear();
        _playersWhoSkipped.Clear();
        PendingRequest = null;
        LastResolvedRequest = null;
        Status = TradePhaseStatus.InProgress;
        return true;
    }

    /// <summary>
    /// 发起一笔交换请求。每个阶段只允许同一时间存在一笔请求或选牌流程。
    /// 只有最终完成交换后才会消耗发起者机会；拒绝、超时都允许重试。
    /// </summary>
    /// <param name="initiatorId">发起玩家的稳定标识。</param>
    /// <param name="recipientId">接收玩家的稳定标识。</param>
    /// <param name="now">由调用方传入的当前时间，便于联机权威端统一判定。</param>
    /// <returns>成功创建的请求快照；规则不允许时返回 null。</returns>
    public TradeRequestSnapshot? TryCreateRequest(
        string initiatorId,
        string recipientId,
        DateTimeOffset now)
    {
        // 每次处理新动作前先清理已过期请求，避免超时回调稍有延迟便阻塞下一次重试。
        ExpireRequestIfNeeded(now);

        if (Status != TradePhaseStatus.InProgress ||
            PendingRequest is not null ||
            string.IsNullOrWhiteSpace(initiatorId) ||
            string.IsNullOrWhiteSpace(recipientId) ||
            string.Equals(initiatorId, recipientId, StringComparison.Ordinal) ||
            !_playerIds.Contains(initiatorId) ||
            !_playerIds.Contains(recipientId) ||
            _initiatorsWhoSucceeded.Contains(initiatorId) ||
            _playersWhoSkipped.Contains(initiatorId))
        {
            return null;
        }

        PendingRequest = new TradeRequestSnapshot(
            initiatorId,
            recipientId,
            TradeRequestStatus.AwaitingResponse,
            now,
            now + RequestResponseTimeout,
            initiatorCardId: null,
            recipientCardId: null);

        return PendingRequest;
    }

    /// <summary>
    /// 接收玩家同意请求，进入双方选牌阶段。
    /// 仅目标玩家可以同意；若该请求已经超过五秒，则先按超时处理并返回 false。
    /// </summary>
    public bool AcceptRequest(string recipientId, DateTimeOffset now)
    {
        ExpireRequestIfNeeded(now);

        if (PendingRequest is null ||
            PendingRequest.Status != TradeRequestStatus.AwaitingResponse ||
            !string.Equals(PendingRequest.RecipientId, recipientId, StringComparison.Ordinal))
        {
            return false;
        }

        PendingRequest = CopyRequest(PendingRequest, TradeRequestStatus.SelectingCards);
        return true;
    }

    /// <summary>
    /// 接收玩家拒绝请求。拒绝不会消耗发起者机会，因此发起者可以重新邀请。
    /// </summary>
    public bool DeclineRequest(string recipientId, DateTimeOffset now)
    {
        ExpireRequestIfNeeded(now);

        if (PendingRequest is null ||
            PendingRequest.Status != TradeRequestStatus.AwaitingResponse ||
            !string.Equals(PendingRequest.RecipientId, recipientId, StringComparison.Ordinal))
        {
            return false;
        }

        ResolvePendingRequest(TradeRequestStatus.Declined);
        return true;
    }

    /// <summary>
    /// 任一方提交已选卡牌。每位玩家只能确认自己的选择；不同玩家牌组中相同位置代表不同卡牌，因此索引可以相同。
    /// 本类不判断卡牌是否为诅咒或仍属于该玩家；提交真实交换前必须由游戏侧校验这些条件。
    /// </summary>
    /// <param name="playerId">提交选择的玩家标识。</param>
    /// <param name="cardId">所选卡牌在本局中的稳定唯一标识。</param>
    /// <returns>双方都确认时返回 true，表示已准备好提交；仅当前一方确认时返回 false。</returns>
    public bool ConfirmCard(string playerId, string cardId)
    {
        if (Status != TradePhaseStatus.InProgress ||
            PendingRequest is null ||
            PendingRequest.Status != TradeRequestStatus.SelectingCards ||
            string.IsNullOrWhiteSpace(cardId))
        {
            return false;
        }

        var current = PendingRequest;
        string? initiatorCardId = current.InitiatorCardId;
        string? recipientCardId = current.RecipientCardId;

        if (string.Equals(playerId, current.InitiatorId, StringComparison.Ordinal))
        {
            initiatorCardId = cardId;
        }
        else if (string.Equals(playerId, current.RecipientId, StringComparison.Ordinal))
        {
            recipientCardId = cardId;
        }
        else
        {
            return false;
        }

        PendingRequest = new TradeRequestSnapshot(
            current.InitiatorId,
            current.RecipientId,
            TradeRequestStatus.SelectingCards,
            current.CreatedAt,
            current.ResponseDeadline,
            initiatorCardId,
            recipientCardId);

        if (initiatorCardId is null || recipientCardId is null)
        {
            return false;
        }

        // 两边选择均已确认，但此时还没有执行游戏内卡牌迁移。
        // 只有游戏命令真正成功后，调用方才能调用 CompleteConfirmedTrade 消耗发起者机会。
        return true;
    }

    /// <summary>
    /// 在游戏侧确认两张卡已成功互换后，将当前交易正式记为成功。
    /// 分离“双方选择完成”和“游戏模型迁移成功”，避免卡牌转移失败时仍消耗机会。
    /// </summary>
    public bool CompleteConfirmedTrade()
    {
        if (PendingRequest is null ||
            PendingRequest.Status != TradeRequestStatus.SelectingCards ||
            PendingRequest.InitiatorCardId is null ||
            PendingRequest.RecipientCardId is null)
        {
            return false;
        }

        // 只有成功发起的一方会用掉主动机会；接收者仍可以之后主动发起另一笔交换。
        _initiatorsWhoSucceeded.Add(PendingRequest.InitiatorId);
        ResolvePendingRequest(TradeRequestStatus.Completed);
        UpdateCompletionStatus();
        return true;
    }

    /// <summary>
    /// 游戏侧卡牌迁移失败时释放当前交易，但不扣除发起者机会，允许玩家重新尝试。
    /// </summary>
    public bool FailConfirmedTrade()
    {
        if (PendingRequest is null ||
            PendingRequest.Status != TradeRequestStatus.SelectingCards ||
            PendingRequest.InitiatorCardId is null ||
            PendingRequest.RecipientCardId is null)
        {
            return false;
        }

        ResolvePendingRequest(TradeRequestStatus.Failed);
        return true;
    }

    /// <summary>
    /// 玩家点击跳过后，结束自己的主动操作，但仍可接收其他玩家的请求。
    /// 这是文档中暂定的跳过语义；如之后确认跳过应完全退出阶段，可调整此方法和接收校验。
    /// </summary>
    public bool SkipInitiating(string playerId)
    {
        if (Status != TradePhaseStatus.InProgress ||
            !_playerIds.Contains(playerId) ||
            _initiatorsWhoSucceeded.Contains(playerId) ||
            _playersWhoSkipped.Contains(playerId) ||
            (PendingRequest is not null &&
             (string.Equals(PendingRequest.InitiatorId, playerId, StringComparison.Ordinal) ||
              string.Equals(PendingRequest.RecipientId, playerId, StringComparison.Ordinal))))
        {
            return false;
        }

        _playersWhoSkipped.Add(playerId);
        UpdateCompletionStatus();
        return true;
    }

    /// <summary>
    /// 根据当前时间清理超过五秒尚未回应的邀请。
    /// 后续 UI 应设置五秒计时器，在到期时调用此方法；联机时应由权威端广播超时结果。
    /// </summary>
    public bool ExpireRequestIfNeeded(DateTimeOffset now)
    {
        if (PendingRequest is null ||
            PendingRequest.Status != TradeRequestStatus.AwaitingResponse ||
            now < PendingRequest.ResponseDeadline)
        {
            return false;
        }

        ResolvePendingRequest(TradeRequestStatus.TimedOut);
        return true;
    }

    /// <summary>查询指定玩家是否仍可主动发起交换。</summary>
    public bool CanInitiate(string playerId) =>
        Status == TradePhaseStatus.InProgress &&
        _playerIds.Contains(playerId) &&
        !_initiatorsWhoSucceeded.Contains(playerId) &&
        !_playersWhoSkipped.Contains(playerId) &&
        PendingRequest is null;

    /// <summary>查询指定玩家是否仍属于当前阶段。</summary>
    public bool ContainsPlayer(string playerId) => _playerIds.Contains(playerId);

    /// <summary>将当前请求标记为终态，保留快照供 UI 展示，并释放全局待处理位置。</summary>
    private void ResolvePendingRequest(TradeRequestStatus terminalStatus)
    {
        if (PendingRequest is null)
        {
            return;
        }

        LastResolvedRequest = CopyRequest(PendingRequest, terminalStatus);
        PendingRequest = null;
        UpdateCompletionStatus();
    }

    /// <summary>复制请求字段并仅替换状态，确保对外快照不可被后续流程意外改写。</summary>
    private static TradeRequestSnapshot CopyRequest(
        TradeRequestSnapshot request,
        TradeRequestStatus status) =>
        new(
            request.InitiatorId,
            request.RecipientId,
            status,
            request.CreatedAt,
            request.ResponseDeadline,
            request.InitiatorCardId,
            request.RecipientCardId);

    /// <summary>
    /// 阶段结束需要同时满足：没有未完成请求/交易，且每位玩家都已成功发起或主动跳过。
    /// 已用完主动机会的玩家仍可以接收请求，因此只要其他玩家尚未结束操作，阶段仍保持开放。
    /// </summary>
    private void UpdateCompletionStatus()
    {
        if (Status == TradePhaseStatus.NotStarted || PendingRequest is not null)
        {
            return;
        }

        bool everyPlayerFinishedInitiating = _playerIds.All(playerId =>
            _initiatorsWhoSucceeded.Contains(playerId) || _playersWhoSkipped.Contains(playerId));

        if (everyPlayerFinishedInitiating)
        {
            Status = TradePhaseStatus.Completed;
        }
    }
}
