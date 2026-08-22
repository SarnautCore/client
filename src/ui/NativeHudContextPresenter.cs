using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;
using SarnautCore.NativeHud;

namespace SarnautCore;

/// <summary>Projects stable HUD context views onto roles owned by the compiled product scene.</summary>
internal sealed class NativeHudContextPresenter(
    NativeHudContent content,
    IHudItemPresentationCatalog items)
{
    private readonly HashSet<HudId> _missingItems = [];

    public void PresentInventory(HudInventoryReadModel model)
    {
        HudMultiBagSystem system = content.Manifest.Systems.MultiBag;
        SetVisible("multibag-window", model.HasAuthority && model.Open);
        foreach (HudInventoryLayoutBinding layout in system.Layouts)
        {
            bool active = model.HasAuthority && model.Open
                && StringComparer.Ordinal.Equals(layout.Id, model.LayoutElement.Value);
            SetVisible(layout.Id, active);
            foreach (HudInventoryPartitionBinding partition in layout.Partitions)
            {
                SetVisible(partition.Id, active && PartitionVisible(model, partition.Id));
                foreach (HudInventorySlotBinding binding in partition.Slots)
                {
                    HudInventorySlotView slot = FindInventorySlot(model, binding.Id);
                    PresentItemSlot(
                        binding.Icon.Id,
                        binding.Count.Id,
                        binding.Prepared.Id,
                        binding.Cooldown.Id,
                        slot.ItemId,
                        slot.DisplayCount,
                        slot.PreparedVisible,
                        slot.CooldownRemainingMilliseconds,
                        slot.CooldownDurationMilliseconds,
                        active && slot.Visible && slot.Occupied);
                }
            }
        }

        SetText("multibag-money", model.Currency.ToString(CultureInfo.InvariantCulture));
        SetContentId("multibag-name", model.EquippedBag.ItemId);
    }

    public void PresentLoot(HudLootReadModel model)
    {
        HudLootBagSystem system = content.Manifest.Systems.LootBag;
        SetVisible("loot-window", model.HasAuthority && model.Open);
        for (int index = 0; index < system.Items.Length; index++)
        {
            HudLootItemBinding binding = system.Items[index];
            HudLootSlotView slot = model.PageSlots[index];
            bool occupied = model.HasAuthority && model.Open && slot.Occupied;
            SetVisible(binding.Id, occupied);
            PresentItemIcon(binding.Icon.Id, slot.ItemId, occupied, out HudItemPresentation presentation);
            SetText(binding.Count.Id, occupied && slot.Count > 1
                ? slot.Count.ToString(CultureInfo.InvariantCulture)
                : string.Empty);
            SetText(binding.Name.Id, occupied ? Resolve(presentation.NameTextId) : string.Empty);
            SetContentId(binding.Slot.Id, slot.ItemId);
        }

        SetContentId("loot-label", new HudId($"loot-page-{model.Page + 1}-of-{Math.Max(model.PageCount, 1)}"));
        SetEnabled("loot-previous", model.Page > 0);
        SetEnabled("loot-next", model.Page + 1 < model.PageCount);
    }

    public void PresentQuestLog(HudQuestLogReadModel model)
    {
        HudQuestLogSystem system = content.Manifest.Systems.QuestLog;
        SetVisible("quest-log-window", model.HasAuthority && model.Open);
        for (int index = 0; index < system.Rows.Length; index++)
        {
            HudQuestLogRowBinding binding = system.Rows[index];
            HudQuestLogEntryView entry = model.Entries[index];
            bool occupied = model.HasAuthority && model.Open && entry.Occupied;
            SetVisible(binding.Id, occupied);
            SetContentId(binding.Id, entry.QuestId);
            SetState(binding.Id, "selected", entry.Selected);
            SetRoleText(binding.Roles, "-name", entry.TitleId, occupied);
            SetRoleText(binding.Roles, "-brief", entry.DescriptionId, occupied);
            SetRoleLiteral(binding.Roles, "-progress", Progress(entry.Document), occupied);
        }

        SetState("quest-log-bookmark-zones", "selected", model.ActiveBookmark == HudQuestLogBookmark.Zones);
        SetState("quest-log-bookmark-completed", "selected", model.ActiveBookmark == HudQuestLogBookmark.Completed);
        SetState("quest-log-bookmark-world-secrets", "selected", model.ActiveBookmark == HudQuestLogBookmark.WorldSecrets);
        SetEnabled("quest-log-abandon", !model.SelectedQuestId.IsEmpty);
        SetEnabled("quest-log-share", !model.SelectedQuestId.IsEmpty);
        SetContentId("quest-log-window", model.SelectedQuestId);
        SetCountdown(
            "quest-log-window",
            "abandon_confirmation_expires_at_ms",
            model.AbandonConfirmationExpiresAtMilliseconds);
        SetCountdown(
            "quest-log-window",
            "share_invitation_expires_at_ms",
            model.ShareInvitationExpiresAtMilliseconds);
    }

    public void PresentQuestInfo(in HudQuestInfoView model)
    {
        HudNpcTalkSystem system = content.Manifest.Systems.NpcTalk;
        bool open = model.HasAuthority && model.Open && model.Mode != HudQuestInfoMode.None;
        SetVisible("npc-talk-window", open);
        SetContentId("npc-talk-window", model.QuestId);
        SetContentId("npc-talk-title", model.Quest?.TitleId ?? HudId.Empty);
        SetExactRoleText(system.Roles, "npc-talk-quest-name", model.Quest?.TitleId ?? HudId.Empty, open);
        SetExactRoleText(system.Roles, "npc-talk-quest-brief", model.Quest?.DescriptionId ?? HudId.Empty, open);
        for (int index = 0; index < system.Options.Roles.Length; index++)
        {
            HudSemanticRole role = system.Options.Roles[index];
            // The core view intentionally remains the authority for option occupancy and text.
            // Until it projects the snapshot's talk-option rows, fail closed instead of exposing
            // authored placeholders as selectable choices.
            SetVisible(role.Id, false);
            SetState(role.Id, "selected", false);
        }

        PresentRewardPool(system.AlternativeIcons.Roles, model.Reward, alternatives: true, model.SelectedRewardIndex);
        PresentRewardPool(system.MandatoryIcons.Roles, model.Reward, alternatives: false, selected: -1);
        SetVisible("npc-talk-accept", open && model.Mode == HudQuestInfoMode.Offer);
        SetVisible("npc-talk-complete", open && model.Mode == HudQuestInfoMode.TurnIn);
        SetEnabled("npc-talk-complete", model.Reward is not null);
    }

    public void PresentCharacter(HudCharacterReadModel model)
    {
        SetVisible("character-window", model.HasAuthority && model.Open);
        SetContentId("character-window", model.NameId);
        SetValue("character-window", "level", model.Level);
        foreach (HudCharacterEquipmentView equipment in model.Equipment)
        {
            PresentItemIcon(equipment.Element.Value, equipment.ItemId, equipment.Occupied, out _);
            SetContentId(equipment.Element.Value, equipment.ItemId);
            SetValue(equipment.Element.Value, "instance_id", equipment.InstanceId);
            SetValue(equipment.Element.Value, "count", equipment.Count);
            SetState(equipment.Element.Value, "bound", equipment.Bound);
            SetState(equipment.Element.Value, "cursed", equipment.Cursed);
        }

        foreach (HudCharacterStatView stat in model.Stats)
        {
            SetContentId(stat.Element.Value, stat.StatId);
            SetValue(stat.Element.Value, "base", stat.BaseValue);
            SetValue(stat.Element.Value, "effective", stat.EffectiveValue);
            SetValue(stat.Element.Value, "long_term", stat.LongTermValue);
            SetVisible(stat.Element.Value, model.HasAuthority && stat.HasAuthority);
        }
    }

    public void PresentMessageBoxes(HudMessageBoxReadModel model)
    {
        HudMessageBoxSystem system = content.Manifest.Systems.MessageBox;
        ReadOnlySpan<HudMessageBoxView> entries = model.Entries;
        for (int index = 0; index < system.Instances.Length; index++)
        {
            HudMessageBoxInstance binding = system.Instances[index];
            HudMessageBoxView view = index < entries.Length ? entries[index] : default;
            bool visible = view.Visible && view.Slot == index;
            HudMessageBoxRequest request = view.Request;
            SetVisible(binding.Id, visible);
            SetContentId(binding.Id, visible ? request.RequestId : HudId.Empty);
            SetValue(binding.Id, "purpose", visible ? (int)request.Purpose : -1);
            SetValue(binding.Id, "expected_revision", visible ? request.ExpectedRevision.Revision : 0UL);

            SetContentId(binding.Title.Id, visible ? request.HeaderTextId : HudId.Empty);
            SetText(binding.Title.Id, string.Empty);
            SetContentId(binding.Body.Id, visible ? request.BodyTextId : HudId.Empty);
            SetText(binding.Body.Id, string.Empty);
            SetContentId(binding.Icon.Id, visible ? request.RelatedId : HudId.Empty);
            SetVisible(binding.Icon.Id, visible && !request.RelatedId.IsEmpty);

            int lifetime = visible ? request.EffectiveLifetimeMilliseconds : 0;
            float remaining = lifetime > 0
                ? Math.Clamp((float)view.RemainingMilliseconds / lifetime, 0.0f, 1.0f)
                : 0.0f;
            SetVisible(binding.Progress.Id, visible && lifetime > 0);
            SetValue(binding.Progress.Id, "remaining_fraction", remaining);
            SetText(
                binding.TimerLabel.Id,
                visible && lifetime > 0
                    ? Math.Max(0, (view.RemainingMilliseconds + 999) / 1000)
                        .ToString(CultureInfo.InvariantCulture)
                    : string.Empty);
            SetVisible(binding.TimerLabel.Id, visible && lifetime > 0);
            SetVisible(binding.ButtonTab.Id, visible);
            SetVisible(binding.ButtonContainer.Id, visible);

            bool acceptDecline = visible && request.Buttons == HudMessageBoxButtons.AcceptDecline;
            bool confirm = visible && request.Buttons == HudMessageBoxButtons.Confirm;
            PresentMessageBoxButton(binding.Accept.Role.Id, acceptDecline, view.Active);
            PresentMessageBoxButton(binding.Decline.Role.Id, acceptDecline, view.Active);
            PresentMessageBoxButton(binding.Confirm.Role.Id, confirm, view.Active);
        }
    }

    private void PresentItemSlot(
        string iconRole,
        string countRole,
        string preparedRole,
        string cooldownRole,
        HudId itemId,
        int count,
        bool prepared,
        int remaining,
        int duration,
        bool occupied)
    {
        PresentItemIcon(iconRole, itemId, occupied, out _);
        SetText(countRole, occupied && count > 1 ? count.ToString(CultureInfo.InvariantCulture) : string.Empty);
        SetTexture(preparedRole, items.SlotPresentation.Prepared);
        SetVisible(preparedRole, occupied && prepared);
        SetTexture(cooldownRole, items.SlotPresentation.Cooldown);
        float covered = occupied && duration > 0 && remaining > 0
            ? Math.Clamp((float)remaining / duration, 0.0f, 1.0f)
            : 0.0f;
        SetValue(cooldownRole, "covered_fraction", covered);
        SetVisible(cooldownRole, covered > 0.0f);
    }

    private void PresentItemIcon(
        string role,
        HudId itemId,
        bool occupied,
        out HudItemPresentation presentation)
    {
        presentation = default;
        if (!occupied)
        {
            SetTexture(role, null);
            SetVisible(role, false);
            SetContentId(role, HudId.Empty);
            return;
        }

        if (items.TryGet(itemId, out presentation))
        {
            SetTexture(role, presentation.Icon);
            SetContentId(role, presentation.IconTextureId);
        }
        else
        {
            SetTexture(role, items.SlotPresentation.UnknownIcon);
            SetContentId(role, items.SlotPresentation.UnknownIconTextureId);
            if (_missingItems.Add(itemId))
            {
                GD.PushWarning("Native HUD item presentation catalog miss");
            }
        }

        SetVisible(role, true);
    }

    private void PresentRewardPool(
        HudSemanticRole[] roles,
        HudQuestRewardSnapshot? reward,
        bool alternatives,
        int selected)
    {
        ReadOnlySpan<HudRewardItem> values = reward is null
            ? ReadOnlySpan<HudRewardItem>.Empty
            : alternatives ? reward.AlternativeItems : reward.MandatoryItems;
        for (int index = 0; index < roles.Length; index++)
        {
            bool occupied = index < values.Length;
            PresentItemIcon(roles[index].Id, occupied ? values[index].ItemId : HudId.Empty, occupied, out _);
            SetState(roles[index].Id, "selected", occupied && index == selected);
        }
    }

    private void PresentMessageBoxButton(string role, bool visible, bool active)
    {
        SetVisible(role, visible);
        SetEnabled(role, visible && active);
    }

    private static string Progress(HudQuestDocument? document)
    {
        if (document is null || document.Objectives.Length == 0)
        {
            return string.Empty;
        }

        HudQuestObjective objective = document.Objectives[0];
        return objective.ShowCount
            ? $"{objective.Current.ToString(CultureInfo.InvariantCulture)}/{objective.Required.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
    }

    private string Resolve(HudId textId) =>
        items.TryResolveText(textId, out string text) ? text : string.Empty;

    private static bool PartitionVisible(HudInventoryReadModel model, string id)
    {
        foreach (HudInventoryPartitionView partition in model.Partitions)
        {
            if (StringComparer.Ordinal.Equals(partition.Element.Value, id))
            {
                return partition.Visible;
            }
        }

        return false;
    }

    private static HudInventorySlotView FindInventorySlot(HudInventoryReadModel model, string id)
    {
        foreach (HudInventorySlotView slot in model.Slots)
        {
            if (StringComparer.Ordinal.Equals(slot.Element.Value, id))
            {
                return slot;
            }
        }

        return default;
    }

    private void SetRoleText(HudSemanticRole[] roles, string suffix, HudId textId, bool visible)
    {
        foreach (HudSemanticRole role in roles)
        {
            if (role.Id.EndsWith(suffix, StringComparison.Ordinal))
            {
                SetContentId(role.Id, textId);
                SetText(role.Id, string.Empty);
                SetVisible(role.Id, visible);
                return;
            }
        }
    }

    private void SetRoleLiteral(HudSemanticRole[] roles, string suffix, string text, bool visible)
    {
        foreach (HudSemanticRole role in roles)
        {
            if (role.Id.EndsWith(suffix, StringComparison.Ordinal))
            {
                SetContentId(role.Id, HudId.Empty);
                SetText(role.Id, text);
                SetVisible(role.Id, visible);
                return;
            }
        }
    }

    private void SetExactRoleText(HudSemanticRole[] roles, string id, HudId textId, bool visible)
    {
        foreach (HudSemanticRole role in roles)
        {
            if (StringComparer.Ordinal.Equals(role.Id, id))
            {
                SetContentId(role.Id, textId);
                SetText(role.Id, string.Empty);
                SetVisible(role.Id, visible);
                return;
            }
        }
    }

    private Node Require(string role) => content.Roles.TryGetValue(role, out Node? node)
        ? node
        : throw new InvalidDataException($"Native HUD presenter references unknown role '{role}'");

    private void SetVisible(string role, bool visible)
    {
        if (Require(role) is CanvasItem item)
        {
            item.Visible = visible;
        }
    }

    private void SetEnabled(string role, bool enabled)
    {
        Node node = Require(role);
        node.SetMeta("hud_enabled", enabled);
        if (node is BaseButton button)
        {
            button.Disabled = !enabled;
        }
    }

    private void SetText(string role, string text)
    {
        Node node = Require(role);
        node.SetMeta("hud_text", text);
        if (node is Label label)
        {
            label.Text = text;
        }
        else if (node is LineEdit edit)
        {
            edit.Text = text;
        }
    }

    private void SetTexture(string role, Texture2D? texture)
    {
        Node node = Require(role);
        if (node is TextureRect rect)
        {
            rect.Texture = texture;
        }
        else if (node is Button button)
        {
            button.Icon = texture;
        }
        node.SetMeta("hud_texture", texture is null ? default(Variant) : Variant.From(texture));
    }

    private void SetContentId(string role, HudId value) =>
        Require(role).SetMeta("hud_content_id", value.Value);

    private void SetState(string role, string state, bool value) =>
        Require(role).SetMeta($"hud_{state}", value);

    private void SetValue(string role, string key, Variant value) =>
        Require(role).SetMeta($"hud_{key}", value);

    private void SetValue(string role, string key, ulong value) =>
        Require(role).SetMeta($"hud_{key}", checked((long)value));

    private void SetValue(string role, string key, int value) =>
        Require(role).SetMeta($"hud_{key}", value);

    private void SetValue(string role, string key, float value) =>
        Require(role).SetMeta($"hud_{key}", value);

    private void SetValue(string role, string key, float? value) =>
        Require(role).SetMeta($"hud_{key}", value ?? 0.0f);

    private void SetCountdown(string role, string key, long value) =>
        Require(role).SetMeta($"hud_{key}", value);
}
