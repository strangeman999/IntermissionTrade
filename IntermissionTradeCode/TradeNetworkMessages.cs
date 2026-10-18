using System;
using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace IntermissionTrade.IntermissionTradeCode;

/// <summary>
/// 描述玩家发给主持端的交换操作意图。
/// 这是“请求”而不是权威结果：远端玩家不能凭消息直接改动其他玩家的牌组。
/// </summary>
public enum TradeIntentKind : byte
{
    /// <summary>玩家请求与另一名玩家交换。</summary>
    Request = 0,

    /// <summary>被邀请玩家同意请求。</summary>
    Accept = 1,

    /// <summary>被邀请玩家拒绝请求。</summary>
    Decline = 2,

    /// <summary>玩家提交自己的卡牌选择。</summary>
    SelectCard = 3,

    /// <summary>玩家跳过自己的主动发起机会。</summary>
    Skip = 4
}

/// <summary>
/// 描述主持端审核后广播给所有玩家的权威交换状态变化。
/// 客户端只应按照此消息更新本地 UI/协调器，不应自行猜测操作是否成功。
/// </summary>
public enum TradeStateKind : byte
{
    /// <summary>主持端开启了新的幕间交换阶段。</summary>
    PhaseStarted = 0,

    /// <summary>主持端接受并广播了一笔交换请求。</summary>
    RequestCreated = 1,

    /// <summary>接收玩家已同意该请求。</summary>
    RequestAccepted = 2,

    /// <summary>接收玩家已拒绝该请求。</summary>
    RequestDeclined = 3,

    /// <summary>旧版协议保留的状态编号；当前版本不再发送请求超时消息。</summary>
    RequestTimedOut = 4,

    /// <summary>一名玩家提交了自己的卡牌选择。</summary>
    CardSelected = 5,

    /// <summary>主持端完成卡牌校验并广播交换提交结果。</summary>
    TradeCommitted = 6,

    /// <summary>玩家跳过了自己的主动操作。</summary>
    PlayerSkipped = 7,

    /// <summary>所有玩家均完成或跳过，交换阶段结束。</summary>
    PhaseEnded = 8,

    /// <summary>游戏侧卡牌迁移失败，本笔交易撤销且不扣除发起机会。</summary>
    TradeFailed = 9
}

/// <summary>
/// 发给主持端的玩家操作消息。
/// ShouldBroadcast 为 false，意味着这条消息只用于请求主持端审核；
/// 审核通过后主持端另发 TradeStateMessage 给全体玩家。
/// </summary>
public sealed class TradeIntentMessage : ICustomMessage
{
    /// <summary>操作类别，以一个字节传输。</summary>
    public TradeIntentKind Kind;

    /// <summary>客户端声称的发起玩家 NetId；主持端必须与网络层 senderId 比对。</summary>
    public ulong ActorNetId;

    /// <summary>操作相关的另一名玩家 NetId，例如请求接收者。</summary>
    public ulong TargetNetId;

    /// <summary>选择卡牌时使用的牌组位置；其他操作忽略此字段。</summary>
    public int DeckCardIndex;

    /// <summary>无参构造函数供 BaseLib 收到消息后反序列化使用。</summary>
    public TradeIntentMessage() { }

    /// <summary>创建一条玩家意图；字段会在 Serialize 中按固定顺序写入网络包。</summary>
    public TradeIntentMessage(TradeIntentKind kind, ulong actorNetId, ulong targetNetId = 0, int deckCardIndex = -1)
    {
        Kind = kind;
        ActorNetId = actorNetId;
        TargetNetId = targetNetId;
        DeckCardIndex = deckCardIndex;
    }

    /// <summary>玩家意图先交由主持端校验，不允许直接广播成已生效的结果。</summary>
    public bool ShouldBroadcast => false;

    /// <summary>交互消息必须可靠送达；丢失一次同意/拒绝会令两端流程永久分叉。</summary>
    public NetTransferMode Mode => NetTransferMode.Reliable;

    /// <summary>收消息时交由后续的流程控制器审核。</summary>
    public void HandleMessage(ulong senderId) => TradeNetworkRouter.DispatchIntent(this, senderId);

    /// <summary>按固定字段顺序序列化，接收端必须使用相同顺序反序列化。</summary>
    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte((byte)Kind);
        writer.WriteULong(ActorNetId);
        writer.WriteULong(TargetNetId);
        writer.WriteInt(DeckCardIndex);
    }

    /// <summary>恢复玩家操作字段；枚举值由主持端继续校验，不能信任陌生或过期值。</summary>
    public void Deserialize(PacketReader reader)
    {
        Kind = (TradeIntentKind)reader.ReadByte();
        ActorNetId = reader.ReadULong();
        TargetNetId = reader.ReadULong();
        DeckCardIndex = reader.ReadInt();
    }
}

/// <summary>
/// 主持端审核后的权威状态广播。
/// 所有客户端收到后按相同顺序应用状态，从而避免各自本地按钮点击直接修改游戏状态。
/// </summary>
public sealed class TradeStateMessage : ICustomMessage
{
    /// <summary>状态变化的类型。</summary>
    public TradeStateKind Kind;

    /// <summary>相关操作的发起者或已提交选择的玩家。</summary>
    public ulong ActorNetId;

    /// <summary>相关的另一名玩家；不涉及玩家配对时为 0。</summary>
    public ulong TargetNetId;

    /// <summary>卡牌选择或提交时的发起者牌组位置。</summary>
    public int ActorDeckCardIndex;

    /// <summary>提交交易时的接收者牌组位置。</summary>
    public int TargetDeckCardIndex;

    /// <summary>所在幕编号，避免旧阶段延迟消息误作用于新的一幕。</summary>
    public int ActNumber;

    /// <summary>主持端记录的 UTC 时间戳，用于让所有客户端采用同一请求期限。</summary>
    public long TimestampUtcTicks;

    /// <summary>无参构造函数供 BaseLib 消息反序列化创建对象使用。</summary>
    public TradeStateMessage() { }

    /// <summary>创建一条主持端审核通过的广播消息。</summary>
    public TradeStateMessage(
        TradeStateKind kind,
        int actNumber,
        ulong actorNetId = 0,
        ulong targetNetId = 0,
        int actorDeckCardIndex = -1,
        int targetDeckCardIndex = -1,
        long timestampUtcTicks = 0)
    {
        Kind = kind;
        ActNumber = actNumber;
        ActorNetId = actorNetId;
        TargetNetId = targetNetId;
        ActorDeckCardIndex = actorDeckCardIndex;
        TargetDeckCardIndex = targetDeckCardIndex;
        TimestampUtcTicks = timestampUtcTicks;
    }

    /// <summary>该状态已经由主持端验证，需转发给所有联机玩家。</summary>
    public bool ShouldBroadcast => true;

    /// <summary>交易状态必须可靠传输，否则客户端可能错过阶段开始或卡牌交换结果。</summary>
    public NetTransferMode Mode => NetTransferMode.Reliable;

    /// <summary>收消息后交给后续状态控制器；实际处理需切回 Godot 主线程。</summary>
    public void HandleMessage(ulong senderId) => TradeNetworkRouter.DispatchState(this, senderId);

    /// <summary>按固定顺序写入状态类别、玩家、卡牌索引、幕编号和主持端时间。</summary>
    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte((byte)Kind);
        writer.WriteULong(ActorNetId);
        writer.WriteULong(TargetNetId);
        writer.WriteInt(ActorDeckCardIndex);
        writer.WriteInt(TargetDeckCardIndex);
        writer.WriteInt(ActNumber);
        writer.WriteLong(TimestampUtcTicks);
    }

    /// <summary>按与 Serialize 相同的顺序读取字段。</summary>
    public void Deserialize(PacketReader reader)
    {
        Kind = (TradeStateKind)reader.ReadByte();
        ActorNetId = reader.ReadULong();
        TargetNetId = reader.ReadULong();
        ActorDeckCardIndex = reader.ReadInt();
        TargetDeckCardIndex = reader.ReadInt();
        ActNumber = reader.ReadInt();
        TimestampUtcTicks = reader.ReadLong();
    }
}

/// <summary>
/// BaseLib 消息封装的统一入口。
/// 控制器稍后订阅这两个事件；事件回调必须自行确保在 Godot 主线程执行 UI 或游戏模型操作。
/// </summary>
public static class TradeNetworkRouter
{
    /// <summary>主持端收到玩家意图时触发，并附带真实网络发送者 NetId。</summary>
    public static event Action<TradeIntentMessage, ulong>? IntentReceived;

    /// <summary>所有客户端收到主持端状态广播时触发。</summary>
    public static event Action<TradeStateMessage, ulong>? StateReceived;

    /// <summary>由 BaseLib 消息接收器调用，再把意图交给订阅中的主持端控制器。</summary>
    internal static void DispatchIntent(TradeIntentMessage message, ulong senderId)
    {
        IntentReceived?.Invoke(message, senderId);
    }

    /// <summary>由 BaseLib 消息接收器调用，再把状态广播交给本地控制器。</summary>
    internal static void DispatchState(TradeStateMessage message, ulong senderId)
    {
        StateReceived?.Invoke(message, senderId);
    }

    /// <summary>将玩家操作发给主持端审核。</summary>
    public static void SendIntent(TradeIntentMessage message)
    {
        CustomMessageWrapper.Send(message);
    }

    /// <summary>广播主持端审核后的状态变更；调用方需先在主持端应用该状态。</summary>
    public static void BroadcastState(TradeStateMessage message)
    {
        CustomMessageWrapper.Send(message);
    }
}
