namespace SarnautCore.NativeHud;

public enum HudMessageBoxPurpose
{
    QuestAbandon,
    QuestShareInvitation,
    ItemConfirmation,
    TradeInvitation,
}

public enum HudMessageBoxButtons
{
    AcceptDecline,
    Confirm,
}

public enum HudMessageBoxDecision
{
    Accept,
    Decline,
}

/// <summary>
/// One typed request for the shared retail ContextUniMessageBox surface. The authoritative
/// remaining lifetime may close a prompt before its local modal timeout.
/// </summary>
public readonly record struct HudMessageBoxRequest(
    HudId RequestId,
    HudMessageBoxPurpose Purpose,
    HudId HeaderTextId,
    HudId BodyTextId,
    HudId RelatedId,
    HudId SecondaryId,
    HudMessageBoxButtons Buttons,
    HudMessageBoxDecision DefaultDecision,
    int TimeoutMilliseconds,
    int AuthoritativeRemainingMilliseconds,
    HudStamp ExpectedRevision)
{
    public bool IsValid => !RequestId.IsEmpty && !HeaderTextId.IsEmpty && !BodyTextId.IsEmpty &&
        (uint)Purpose <= (uint)HudMessageBoxPurpose.TradeInvitation &&
        (uint)Buttons <= (uint)HudMessageBoxButtons.Confirm &&
        (uint)DefaultDecision <= (uint)HudMessageBoxDecision.Decline &&
        TimeoutMilliseconds is > 0 and <= 30_000 &&
        AuthoritativeRemainingMilliseconds >= 0;

    public int EffectiveLifetimeMilliseconds => AuthoritativeRemainingMilliseconds == 0
        ? TimeoutMilliseconds
        : Math.Min(TimeoutMilliseconds, AuthoritativeRemainingMilliseconds);
}

public readonly record struct HudMessageBoxView(
    HudId Element,
    int Slot,
    HudMessageBoxRequest Request,
    int RemainingMilliseconds,
    bool Active,
    bool Visible);

internal struct HudMessageBoxState
{
    public bool Occupied;
    public HudMessageBoxRequest Request;
    public HudStamp Stamp;
    public long ExpiresAt;
    public long Order;
}

public sealed class HudMessageBoxReadModel
{
    private readonly HudMessageBoxView[] _entries;

    internal HudMessageBoxReadModel(HudMessageBoxView[] entries) => _entries = entries;

    public ReadOnlySpan<HudMessageBoxView> Entries => _entries;
    public int Count { get; internal set; }
    public HudId ActiveRequestId { get; internal set; }
}

public sealed class HudMessageBoxProduct
{
    public const int Capacity = 2;

    public HudMessageBoxProduct(HudId root)
    {
        if (root.IsEmpty)
        {
            throw new ArgumentException("The shared ContextUniMessageBox root is required.", nameof(root));
        }

        Root = root;
    }

    public HudId Root { get; }
}
