using System.Text.Json;
using System.Text.Json.Serialization;

namespace SarnautCore.NativeTrade;

public sealed record TradePlacement(int Priority, int X, int HighY, int Width, int Height);

public sealed record TradePanelPlacement(int X, int Y, int Width, int Height);

public sealed record TradeResourceReference(string Path);

public sealed record TradeArtPolicy(string Authority, bool UpscaleRequired);

public sealed record TradeSlotRoles(
    string Container,
    string ItemIcon,
    string Icon,
    string Count,
    string Cooldown,
    string ItemName);

public sealed record TradeSideRoles(
    string Frame,
    string Name,
    IReadOnlyList<TradeSlotRoles> Slots,
    string Gold,
    string Silver,
    string Copper,
    string PrimaryButton,
    string AcceptMark);

public sealed record TradeConfirmationRoles(
    string SafeToggle,
    string Panel,
    string FinalButton,
    string PendingText,
    string AcceptedText);

public sealed record TradeRoles(
    string Root,
    string Close,
    TradeSideRoles Own,
    TradeSideRoles Partner,
    TradeConfirmationRoles Confirmation);

public sealed record TradePolicy(
    int SlotCount,
    int GoldDigits,
    int SilverDigits,
    int CopperDigits,
    int InvitationTimeoutMilliseconds,
    string InvitationMessageBoxId,
    bool InvitationDefaultAccept,
    bool InviteByName,
    bool InviteSelectedVisiblePlayerNonNpc,
    bool InvalidTargetIsWrongFormat,
    int EditCursorBlinkMilliseconds,
    int MaximumDistanceMeters,
    bool SafeConfirmationDefault,
    bool SafeConfirmationNetworked);

public sealed record TradeOrdinal(string Id, int Value);

public sealed record TradeSemantics(
    IReadOnlyList<TradeOrdinal> Phases,
    IReadOnlyList<TradeOrdinal> Errors,
    IReadOnlyList<TradeOrdinal> StartResults,
    IReadOnlyList<string> Actions,
    bool OfferMutationResetsConfirmations,
    bool BagRetargetPreservesConfirmations,
    bool WholeStacksOnly,
    bool BoundItemsAllowed,
    bool PrimaryMarkAdditive,
    bool BothPrimarySafeOffAutoFinal,
    bool BothPrimarySafeOnShowsOverlay,
    bool OwnFinalShowsWaitingText,
    bool FinalRevokeClearsPrimary,
    bool StackCountPrecedesCounterCount,
    int StackCountDisplayThreshold,
    int CounterCountDisplayThreshold,
    bool HideCountBelowThreshold,
    bool BothFinalCompletes,
    IReadOnlyList<string> CancellationCauses,
    IReadOnlyList<string> AuthoredTimelines);

public sealed class TradeProduct
{
    public const string SchemaId = "sarnaut.trade-product/v1";
    public const int AuthoredSlotCount = 5;

    [JsonConstructor]
    public TradeProduct(
        string schemaIdValue,
        string scene,
        IReadOnlyList<TradeResourceReference> resources,
        string itemPresentationCatalog,
        TradeArtPolicy art,
        TradePlacement placement,
        TradePanelPlacement ownPanel,
        TradePanelPlacement partnerPanel,
        TradePanelPlacement confirmationPanel,
        IReadOnlyList<string> authoredRootChildOrder,
        IReadOnlyList<string> nativeRootChildOrder,
        TradeRoles roles,
        TradePolicy policy,
        TradeSemantics semantics)
    {
        SchemaIdValue = schemaIdValue;
        Scene = scene;
        Resources = resources;
        ItemPresentationCatalog = itemPresentationCatalog;
        Art = art;
        Placement = placement;
        OwnPanel = ownPanel;
        PartnerPanel = partnerPanel;
        ConfirmationPanel = confirmationPanel;
        AuthoredRootChildOrder = authoredRootChildOrder;
        NativeRootChildOrder = nativeRootChildOrder;
        Roles = roles;
        Policy = policy;
        Semantics = semantics;
        Validate();
    }

    [JsonPropertyName("schema_id")]
    public string SchemaIdValue { get; }

    public string Scene { get; }

    public IReadOnlyList<TradeResourceReference> Resources { get; }

    [JsonPropertyName("item_presentation_catalog")]
    public string ItemPresentationCatalog { get; }

    public TradeArtPolicy Art { get; }

    public TradePlacement Placement { get; }

    [JsonPropertyName("own_panel")]
    public TradePanelPlacement OwnPanel { get; }

    [JsonPropertyName("partner_panel")]
    public TradePanelPlacement PartnerPanel { get; }

    [JsonPropertyName("confirmation_panel")]
    public TradePanelPlacement ConfirmationPanel { get; }

    [JsonPropertyName("authored_root_child_order")]
    public IReadOnlyList<string> AuthoredRootChildOrder { get; }

    [JsonPropertyName("native_root_child_order")]
    public IReadOnlyList<string> NativeRootChildOrder { get; }

    public TradeRoles Roles { get; }

    public TradePolicy Policy { get; }

    public TradeSemantics Semantics { get; }

    public static TradeProduct Parse(ReadOnlySpan<byte> json)
    {
        TradeProduct? product = JsonSerializer.Deserialize<TradeProduct>(json, JsonOptions);
        return product ?? throw new InvalidDataException("Trade product JSON is empty.");
    }

    public void Validate()
    {
        if (SchemaIdValue != SchemaId)
        {
            throw new InvalidDataException($"Unsupported trade product schema '{SchemaIdValue}'.");
        }

        ValidateResourcePath(Scene, nameof(Scene), ".scn", ".tscn");
        ArgumentNullException.ThrowIfNull(Resources);
        string? previous = null;
        HashSet<string> resources = new(StringComparer.Ordinal);
        foreach (TradeResourceReference resource in Resources)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ValidateResourcePath(resource.Path, nameof(Resources), ".scn", ".tscn", ".res", ".tres");
            if (!resources.Add(resource.Path) ||
                previous is not null && string.CompareOrdinal(previous, resource.Path) >= 0)
            {
                throw new InvalidDataException("Trade product resources must be unique and sorted.");
            }

            previous = resource.Path;
        }

        if (resources.Contains(Scene))
        {
            throw new InvalidDataException("The primary trade scene cannot be duplicated in resources.");
        }

        if (ItemPresentationCatalog != "hud.items.inst-league1")
        {
            throw new InvalidDataException("Trade must use the shared item presentation catalog.");
        }
        if (Art != new TradeArtPolicy("classic-1.1", true))
        {
            throw new InvalidDataException("Trade art must use required upscaled classic 1.1 authority.");
        }
        if (Placement != new TradePlacement(1500, -11, 155, 475, 485))
        {
            throw new InvalidDataException("Trade root placement does not match the authored 1.1 form.");
        }

        if (OwnPanel != new TradePanelPlacement(17, 25, 221, 445) ||
            PartnerPanel != new TradePanelPlacement(241, 25, 221, 445) ||
            ConfirmationPanel != new TradePanelPlacement(11, 371, 456, 100))
        {
            throw new InvalidDataException("Trade panel placement does not match the authored 1.1 form.");
        }
        string[] authoredOrder =
        {
            "UserFrame", "CustomerFrame", "GoldenCorner", "FramePanel", "WindowHeader", "TradeConfirmation",
        };
        string[] nativeOrder =
        {
            "FramePanel", "UserFrame", "CustomerFrame", "GoldenCorner", "WindowHeader", "TradeConfirmation",
        };
        ArgumentNullException.ThrowIfNull(AuthoredRootChildOrder);
        ArgumentNullException.ThrowIfNull(NativeRootChildOrder);
        if (!AuthoredRootChildOrder.SequenceEqual(authoredOrder, StringComparer.Ordinal) ||
            !NativeRootChildOrder.SequenceEqual(nativeOrder, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Trade root child order differs from the baked product contract.");
        }

        ArgumentNullException.ThrowIfNull(Roles);
        ArgumentNullException.ThrowIfNull(Policy);
        if (Policy.SlotCount != 5 || Policy.GoldDigits != 5 || Policy.SilverDigits != 2 ||
            Policy.CopperDigits != 2 || Policy.InvitationTimeoutMilliseconds != 30_000 ||
            Policy.InvitationMessageBoxId != "trade_invitation" || Policy.InvitationDefaultAccept ||
            !Policy.InviteByName || !Policy.InviteSelectedVisiblePlayerNonNpc ||
            !Policy.InvalidTargetIsWrongFormat ||
            Policy.EditCursorBlinkMilliseconds != 500 || Policy.MaximumDistanceMeters != 5 ||
            Policy.SafeConfirmationDefault || Policy.SafeConfirmationNetworked)
        {
            throw new InvalidDataException("Trade policy differs from the closed retail contract.");
        }

        ValidateSide(Roles.Own, "own");
        ValidateSide(Roles.Partner, "partner");
        ValidateNodePath(Roles.Root, "root");
        ValidateNodePath(Roles.Close, "close");
        ValidateNodePath(Roles.Confirmation.SafeToggle, "confirmation.safe_toggle");
        ValidateNodePath(Roles.Confirmation.Panel, "confirmation.panel");
        ValidateNodePath(Roles.Confirmation.FinalButton, "confirmation.final_button");
        ValidateNodePath(Roles.Confirmation.PendingText, "confirmation.pending_text");
        ValidateNodePath(Roles.Confirmation.AcceptedText, "confirmation.accepted_text");

        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (string path in EnumerateRolePaths())
        {
            if (!paths.Add(path))
            {
                throw new InvalidDataException($"Trade role path '{path}' is duplicated.");
            }
        }

        ValidateSemantics();
    }

    private void ValidateSemantics()
    {
        ArgumentNullException.ThrowIfNull(Semantics);
        AssertOrdinals(Semantics.Phases, new[]
        {
            "invitation", "in-progress", "completed", "canceled", "failed", "no-bag-space", "lost",
        });
        AssertOrdinals(Semantics.Errors, new[]
        {
            "money-not-enough", "primary-confirmation-required", "item-not-found", "slot-is-used", "item-is-used", "item-is-bound",
        });
        AssertOrdinals(Semantics.StartResults, new[]
        {
            "success", "error", "invited-avatar-is-busy", "inviter-avatar-is-busy", "invited-avatar-not-found",
            "too-far", "invited-avatar-is-dead", "inviter-avatar-is-dead", "you-are-invisible",
        });
        string[] actions =
        {
            "invite.accept", "invite.decline", "trade.cancel", "offer.put-whole-stack",
            "offer.remove-own-slot", "offer.hover-slot", "offer.set-money",
            "confirmation.set-primary", "confirmation.toggle-safe-local", "confirmation.set-final",
        };
        if (!Semantics.Actions.SequenceEqual(actions, StringComparer.Ordinal) ||
            !Semantics.OfferMutationResetsConfirmations || !Semantics.BagRetargetPreservesConfirmations ||
            !Semantics.WholeStacksOnly || Semantics.BoundItemsAllowed || !Semantics.PrimaryMarkAdditive ||
            !Semantics.BothPrimarySafeOffAutoFinal || !Semantics.BothPrimarySafeOnShowsOverlay ||
            !Semantics.OwnFinalShowsWaitingText || !Semantics.FinalRevokeClearsPrimary ||
            !Semantics.StackCountPrecedesCounterCount || Semantics.StackCountDisplayThreshold != 2 ||
            Semantics.CounterCountDisplayThreshold != 2 || !Semantics.HideCountBelowThreshold ||
            !Semantics.BothFinalCompletes ||
            !Semantics.CancellationCauses.SequenceEqual(new[]
            {
                "client-close", "escape", "force-close", "inventory-mutation", "avatar-removal",
                "non-trade-bag-open", "distance-exceeded", "participant-death",
            }, StringComparer.Ordinal) || Semantics.AuthoredTimelines.Count != 0)
        {
            throw new InvalidDataException("Trade semantic table differs from the closed retail contract.");
        }
    }

    private static void AssertOrdinals(IReadOnlyList<TradeOrdinal> actual, IReadOnlyList<string> expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (actual.Count != expected.Count)
        {
            throw new InvalidDataException("Trade ordinal table has the wrong length.");
        }

        for (int index = 0; index < expected.Count; index++)
        {
            if (actual[index] != new TradeOrdinal(expected[index], index))
            {
                throw new InvalidDataException("Trade ordinal table differs from retail.");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static void ValidateSide(TradeSideRoles side, string name)
    {
        ArgumentNullException.ThrowIfNull(side);
        if (side.Slots.Count != AuthoredSlotCount)
        {
            throw new InvalidDataException($"Trade {name} side must bind exactly five authored slots.");
        }

        ValidateNodePath(side.Frame, $"{name}.frame");
        ValidateNodePath(side.Name, $"{name}.name");
        ValidateNodePath(side.Gold, $"{name}.gold");
        ValidateNodePath(side.Silver, $"{name}.silver");
        ValidateNodePath(side.Copper, $"{name}.copper");
        ValidateNodePath(side.PrimaryButton, $"{name}.primary_button");
        ValidateNodePath(side.AcceptMark, $"{name}.accept_mark");
        for (int index = 0; index < side.Slots.Count; index++)
        {
            TradeSlotRoles slot = side.Slots[index];
            ValidateNodePath(slot.Container, $"{name}.slots[{index}].container");
            ValidateNodePath(slot.ItemIcon, $"{name}.slots[{index}].item_icon");
            ValidateNodePath(slot.Icon, $"{name}.slots[{index}].icon");
            ValidateNodePath(slot.Count, $"{name}.slots[{index}].count");
            ValidateNodePath(slot.Cooldown, $"{name}.slots[{index}].cooldown");
            ValidateNodePath(slot.ItemName, $"{name}.slots[{index}].item_name");
        }
    }

    private IEnumerable<string> EnumerateRolePaths()
    {
        yield return Roles.Root;
        yield return Roles.Close;
        foreach (TradeSideRoles side in new[] { Roles.Own, Roles.Partner })
        {
            yield return side.Frame;
            yield return side.Name;
            yield return side.Gold;
            yield return side.Silver;
            yield return side.Copper;
            yield return side.PrimaryButton;
            yield return side.AcceptMark;
            foreach (TradeSlotRoles slot in side.Slots)
            {
                yield return slot.Container;
                yield return slot.ItemIcon;
                yield return slot.Icon;
                yield return slot.Count;
                yield return slot.Cooldown;
                yield return slot.ItemName;
            }
        }

        yield return Roles.Confirmation.SafeToggle;
        yield return Roles.Confirmation.Panel;
        yield return Roles.Confirmation.FinalButton;
        yield return Roles.Confirmation.PendingText;
        yield return Roles.Confirmation.AcceptedText;
    }

    private static void ValidateNodePath(string path, string role)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith("..", StringComparison.Ordinal) ||
            path.Contains(':') || path.Contains('\\') || path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Trade role '{role}' has an unsafe node path.");
        }
    }

    private static void ValidateResourcePath(string path, string field, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            !extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Trade product field '{field}' has an unsafe resource path.");
        }
    }
}
