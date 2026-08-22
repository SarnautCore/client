using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SarnautCore.NativeHud;

namespace SarnautCore;

/// <summary>Godot adapter for the deterministic native HUD runtime.</summary>
public sealed partial class NativeGameplayHudHost : Control
{
    private const float ControllerPointerSpeed = 850.0f;
    private static WeakReference<NativeGameplayHudHost>? s_cursorOwner;

    private NativeHudContent _content = null!;
    private SarnautCore.NativeHud.NativeHud _runtime = null!;
    private HudProduct _product = null!;
    private NativeHudItemPresentationCatalog _itemCatalog = null!;
    private NativeHudContextPresenter _contextPresenter = null!;
    private INativeHudWorldScene _world = null!;
    private Vector2 _pointer;
    private string? _hoverRole;
    private string? _pressedRole;
    private Vector2 _pressedAt;
    private bool _dragging;
    private bool _controllerOwnsPointer;
    private bool _warpedPointerPending;
    private bool _initialized;
    private LineEdit? _chatInput;
    private Action<string> _openNativeProduct = null!;
    private AudioStreamPlayer? _soundPlayer;
    private int _dragInventorySlot = -1;
    private HudReadModel? _readModel;

    public static bool TryMount(
        Node owner,
        NativeHudContentPaths paths,
        IHudSession session,
        INativeHudWorldScene world,
        Action<string> openNativeProduct,
        out NativeGameplayHudHost? host,
        out string error) =>
        TryMount(owner, paths, null, session, world, openNativeProduct, out host, out error);

    public static bool TryMount(
        Node owner,
        NativeHudContentPaths paths,
        HudProduct? product,
        IHudSession session,
        INativeHudWorldScene world,
        Action<string> openNativeProduct,
        out NativeGameplayHudHost? host,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(openNativeProduct);
        var candidate = new NativeGameplayHudHost { Name = "NativeGameplayHudHost" };
        candidate.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        candidate.MouseFilter = MouseFilterEnum.Ignore;
        try
        {
            candidate.Initialize(
                paths,
                product,
                session,
                world,
                openNativeProduct,
                owner.GetViewport().GetVisibleRect());
            owner.AddChild(candidate);
            host = candidate;
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            candidate.DisposeOwnedState();
            candidate.Free();
            host = null;
            error = $"Native HUD content unavailable: {exception.Message}";
            return false;
        }
    }

    public override void _Process(double delta)
    {
        if (!_initialized)
        {
            return;
        }

        Vector2 axis = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        if (axis.LengthSquared() > 0.0f)
        {
            _controllerOwnsPointer = true;
            Rect2 viewport = GetViewport().GetVisibleRect();
            _pointer += axis * ControllerPointerSpeed * (float)delta;
            _pointer = _pointer.Clamp(viewport.Position, viewport.End);
            _warpedPointerPending = true;
            Input.WarpMouse(_pointer);
            UpdateHover(_pointer, HudPointerSource.Controller);
            DispatchPointer(HudInputKind.PointerMoved, _pointer, HudPointerSource.Controller);
        }

        Rect2 visible = GetViewport().GetVisibleRect();
        HudDiff diff = _runtime.Advance(new HudFrame(
            checked((long)Time.GetTicksMsec()),
            new HudViewport(visible.Position.X, visible.Position.Y, visible.Size.X, visible.Size.Y)));
        _readModel = diff.ReadModel;
        Present(diff);
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!_initialized)
        {
            return;
        }

        if (inputEvent is InputEventMouseMotion motion)
        {
            if (_warpedPointerPending && motion.Position.DistanceSquaredTo(_pointer) <= 1.0f)
            {
                _warpedPointerPending = false;
                return;
            }

            _warpedPointerPending = false;
            _controllerOwnsPointer = false;
            _pointer = motion.Position;
            UpdateHover(_pointer, HudPointerSource.Mouse);
            if (_pressedRole is not null
                && (motion.ButtonMask & MouseButtonMask.Left) != 0
                && !_dragging
                && _pressedAt.DistanceSquaredTo(_pointer) >= 16.0f)
            {
                _dragging = true;
                DispatchRoleEvent(
                    HudInputKind.DragStarted,
                    _pressedRole,
                    _pointer,
                    HudPointerSource.Mouse);
            }

            DispatchPointer(HudInputKind.PointerMoved, _pointer, HudPointerSource.Mouse);
            return;
        }

        if (inputEvent is InputEventMouseButton button
            && button.ButtonIndex is MouseButton.Left or MouseButton.Right)
        {
            _controllerOwnsPointer = false;
            _pointer = button.Position;
            HudInputKind kind = (button.ButtonIndex, button.Pressed, button.DoubleClick) switch
            {
                (MouseButton.Left, true, true) => HudInputKind.PointerPrimaryDoublePressed,
                (MouseButton.Left, true, false) => HudInputKind.PointerPrimaryPressed,
                (MouseButton.Left, false, _) => HudInputKind.PointerPrimaryReleased,
                (MouseButton.Right, true, true) => HudInputKind.PointerSecondaryDoublePressed,
                (MouseButton.Right, true, false) => HudInputKind.PointerSecondaryPressed,
                _ => HudInputKind.PointerSecondaryReleased,
            };
            if (button.ButtonIndex == MouseButton.Left && button.Pressed)
            {
                _pressedRole = TryHit(_pointer, HudPointerSource.Mouse, out HudInput pressed)
                    ? pressed.Target.Value
                    : null;
                _pressedAt = _pointer;
                _dragging = false;
            }
            else if (button.ButtonIndex == MouseButton.Left && !button.Pressed && _dragging && _pressedRole is not null)
            {
                DispatchRoleEvent(
                    HudInputKind.DragEnded,
                    _pressedRole,
                    _pointer,
                    HudPointerSource.Mouse);
                _pressedRole = null;
                _dragging = false;
                GetViewport().SetInputAsHandled();
                return;
            }
            else if (button.ButtonIndex == MouseButton.Left && !button.Pressed && _pressedRole is not null)
            {
                DispatchRoleEvent(
                    HudInputKind.PointerPrimaryReleased,
                    _pressedRole,
                    _pointer,
                    HudPointerSource.Mouse);
                _pressedRole = null;
                GetViewport().SetInputAsHandled();
                return;
            }

            DispatchPointerButton(kind, _pointer, HudPointerSource.Mouse);
            if (button.ButtonIndex == MouseButton.Left && !button.Pressed)
            {
                _pressedRole = null;
            }
            return;
        }

        if (_controllerOwnsPointer && inputEvent.IsActionPressed("ui_accept"))
        {
            DispatchPointerButton(
                HudInputKind.PointerPrimaryPressed,
                _pointer,
                HudPointerSource.Controller);
            return;
        }
        if (_controllerOwnsPointer && inputEvent.IsActionReleased("ui_accept"))
        {
            DispatchPointerButton(
                HudInputKind.PointerPrimaryReleased,
                _pointer,
                HudPointerSource.Controller);
            return;
        }

        if (inputEvent.IsActionPressed("ui_cancel"))
        {
            if (_runtime.Dispatch(HudInput.Cancel()).Consumed)
            {
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (inputEvent.IsActionPressed("inventory"))
        {
            if (_runtime.Dispatch(HudInput.ToggleInventory()).Consumed)
            {
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (inputEvent.IsActionPressed("journal"))
        {
            if (_runtime.Dispatch(HudInput.ToggleQuestLog()).Consumed)
            {
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        foreach (HudSemanticInputBinding binding in _content.InputBindings.Values)
        {
            string? action = GodotActionFor(binding.Input);
            if (action is not null && InputMap.HasAction(action) && inputEvent.IsActionPressed(action))
            {
                if (DispatchSemanticBinding(binding))
                {
                    GetViewport().SetInputAsHandled();
                }
                return;
            }
        }
    }

    public override void _ExitTree() => DisposeOwnedState();

    private void Initialize(
        NativeHudContentPaths paths,
        HudProduct? product,
        IHudSession session,
        INativeHudWorldScene world,
        Action<string> openNativeProduct,
        Rect2 initialViewport)
    {
        _content = NativeHudContent.Load(paths);
        _itemCatalog = NativeHudItemPresentationCatalog.Load(
            paths.Resolve(NativeHudItemPresentationCatalog.RelativePath, ".res"));
        _contextPresenter = new NativeHudContextPresenter(_content, _itemCatalog);
        _content.Root.MouseFilter = MouseFilterEnum.Ignore;
        _product = product ?? _content.Product;
        _world = world;
        _openNativeProduct = openNativeProduct;
        _soundPlayer = new AudioStreamPlayer { Name = "NativeHudSoundPlayer" };
        AddChild(_soundPlayer);
        ValidateProductMatchesContent();
        _runtime = SarnautCore.NativeHud.NativeHud.Open(_product, session, new GodotHudWorld(world));
        _content.AttachTo(this, world);
        if (_content.Roles.TryGetValue("chat-input-field", out Node? chatInput)
            && chatInput is LineEdit lineEdit)
        {
            _chatInput = lineEdit;
            _chatInput.TextSubmitted += OnChatTextSubmitted;
        }
        _pointer = initialViewport.GetCenter();
        _initialized = true;
    }

    private static string? GodotActionFor(string semanticInput) => semanticInput switch
    {
        "select-world-entity" => ZoneWalkabout.TargetClick,
        "interact-world-entity" => ZoneWalkabout.Interact,
        "open-options" => "ui_options",
        "action-01" => "ability_slot_1",
        _ => null,
    };

    private bool DispatchSemanticBinding(HudSemanticInputBinding binding)
    {
        HudInput input;
        switch (binding.Event)
        {
            case HudSemanticEvent.ActivateAction:
            {
                int slot = FindActionSlot(binding.Target);
                if (slot < 0)
                {
                    return false;
                }

                input = HudInput.ActivateAction(slot);
                break;
            }
            case HudSemanticEvent.SelectWorldEntity:
                if (!_world.TryPickEntity(_pointer, out ulong selectedEntity))
                {
                    return false;
                }

                input = HudInput.SelectWorldEntity(selectedEntity);
                break;
            case HudSemanticEvent.InteractWorldEntity:
                if (!_world.TryPickEntity(_pointer, out ulong interactedEntity))
                {
                    return false;
                }

                input = HudInput.InteractWorldEntity(interactedEntity);
                break;
            case HudSemanticEvent.RequestFocus:
                input = HudInput.RequestFocus(
                    binding.Target == "chat-input-field" ? HudFocus.Chat : HudFocus.Hud);
                break;
            case HudSemanticEvent.ReleaseFocus:
                input = HudInput.ReleaseFocus(
                    binding.Target == "chat-input-field" ? HudFocus.Chat : HudFocus.Hud);
                break;
            case HudSemanticEvent.Cancel:
                input = HudInput.Cancel();
                break;
            case HudSemanticEvent.SubmitChat:
                return SubmitChatText(_chatInput?.Text);
            case HudSemanticEvent.OpenOptions:
                _openNativeProduct("options");
                return true;
            default:
                return false;
        }

        return _runtime.Dispatch(input).Consumed;
    }

    private int FindActionSlot(string? role)
    {
        if (role is null)
        {
            return -1;
        }

        for (int index = 0; index < _content.ActionSlots.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(_content.ActionSlots[index], role))
            {
                return index;
            }
        }

        return -1;
    }

    private void OnChatTextSubmitted(string text)
    {
        if (SubmitChatText(text) && _chatInput is not null)
        {
            _chatInput.Clear();
        }
    }

    private bool SubmitChatText(string? text)
    {
        string value = text?.Trim() ?? string.Empty;
        return value.Length > 0
            && _runtime.Dispatch(HudInput.SubmitChat(new HudId(value))).Consumed;
    }

    private void ValidateProductMatchesContent()
    {
        for (int index = 0; index < HudProduct.ActionSlotCount; index++)
        {
            if (_product.ActionSlots[index].Value != _content.ActionSlots[index])
            {
                throw new InvalidDataException($"Native HUD action slot {index} disagrees with its role catalog");
            }
        }

        for (int index = 0; index < HudProduct.QuestTrackerRowCount; index++)
        {
            if (_product.QuestTrackerRows[index].Value != _content.QuestRows[index])
            {
                throw new InvalidDataException($"Native HUD quest row {index} disagrees with its role catalog");
            }
        }

        foreach (HudFeedbackPoolProduct pool in _product.FeedbackPools)
        {
            string key = pool.Kind.ToString().ToLowerInvariant();
            if (!_content.FeedbackPools.TryGetValue(key, out IReadOnlyList<string>? roles)
                || !pool.Elements.Select(element => element.Value).SequenceEqual(roles, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Native HUD {key} feedback pool disagrees with its role catalog");
            }
        }

        var masks = _product.PixelMaskedElements.Select(element => element.Value).ToHashSet(StringComparer.Ordinal);
        if (!masks.SetEquals(_content.Masks.Keys))
        {
            throw new InvalidDataException("Native HUD product and compiled mask catalog disagree");
        }

        HudId[] cursorIds =
        [
            _product.Cursors.Default,
            _product.Cursors.Hover,
            _product.Cursors.Text,
            _product.Cursors.Drag,
        ];
        foreach (HudId cursorId in cursorIds)
        {
            if (!_content.Cursors.ContainsKey(cursorId.Value))
            {
                throw new InvalidDataException(
                    $"Native HUD product cursor '{cursorId}' is absent from compiled content");
            }
        }
    }

    private void DispatchPointerButton(HudInputKind kind, Vector2 point, HudPointerSource source)
    {
        if (TryHit(point, source, out HudInput hit))
        {
            if (TryDispatchTypedRole(hit.Target.Value, kind))
            {
                GetViewport().SetInputAsHandled();
                return;
            }

            HudDispatchResult result = _runtime.Dispatch(hit with { Kind = kind });
            if (result.Consumed)
            {
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (kind is HudInputKind.PointerPrimaryPressed or HudInputKind.PointerPrimaryDoublePressed
            && _world.TryPickEntity(point, out ulong entityId))
        {
            _runtime.Dispatch(HudInput.SelectWorldEntity(entityId));
            GetViewport().SetInputAsHandled();
        }
    }

    private void DispatchPointer(HudInputKind kind, Vector2 point, HudPointerSource source)
    {
        HudInput input = TryHit(point, source, out HudInput hit)
            ? hit with { Kind = kind }
            : HudInput.PointerMoved(HudId.Empty, new HudPoint(point.X, point.Y), source);
        _runtime.Dispatch(input);
    }

    private void UpdateHover(Vector2 point, HudPointerSource source)
    {
        string? next = TryHit(point, source, out HudInput hit) ? hit.Target.Value : null;
        if (StringComparer.Ordinal.Equals(next, _hoverRole))
        {
            return;
        }

        if (_hoverRole is not null)
        {
            DispatchRoleEvent(HudInputKind.PointerExited, _hoverRole, point, source);
        }

        _hoverRole = next;
        if (_hoverRole is not null)
        {
            _runtime.Dispatch(hit with { Kind = HudInputKind.PointerEntered });
        }
    }

    private void DispatchRoleEvent(
        HudInputKind kind,
        string role,
        Vector2 point,
        HudPointerSource source)
    {
        if (TryDispatchTypedRole(role, kind))
        {
            return;
        }

        _runtime.Dispatch(HudInput.PointerEvent(
            kind,
            new HudId(role),
            new HudPoint(point.X, point.Y),
            source));
    }

    private bool TryDispatchTypedRole(string role, HudInputKind kind)
    {
        if (_readModel is not HudReadModel model)
        {
            return false;
        }

        HudInput input;
        if (TryInventorySlot(model.Inventory, role, out HudInventorySlotView inventory))
        {
            if (kind == HudInputKind.DragStarted)
            {
                _dragInventorySlot = inventory.Slot;
                return _runtime.Dispatch(HudInput.RequestFocus(HudFocus.Drag)).Consumed;
            }

            if (kind == HudInputKind.DragEnded)
            {
                int from = _dragInventorySlot;
                _dragInventorySlot = -1;
                if (from >= 0 && _hoverRole is not null
                    && TryInventorySlot(model.Inventory, _hoverRole, out HudInventorySlotView destination))
                {
                    return _runtime.Dispatch(HudInput.MoveInventoryItem(from, destination.Slot)).Consumed;
                }

                return _runtime.Dispatch(HudInput.ReleaseFocus(HudFocus.Drag)).Consumed;
            }

            if (kind == HudInputKind.PointerSecondaryPressed && inventory.Occupied
                && _itemCatalog.TryGet(inventory.ItemId, out HudItemPresentation item))
            {
                input = !item.ActionId.IsEmpty
                    ? HudInput.UseInventoryItem(inventory.Slot)
                    : !StringComparer.Ordinal.Equals(item.EquipmentSlot, "none")
                        ? HudInput.DressInventoryItem(inventory.Slot)
                        : default;
                if (input.Kind is HudInputKind.UseInventoryItem or HudInputKind.DressInventoryItem)
                {
                    PlaySound(role, HudSoundEvent.ActivateAction);
                    return _runtime.Dispatch(input).Consumed;
                }
            }

            return false;
        }

        if (TryCharacterSlot(model.Character, role, out HudCharacterEquipmentView equipment)
            && kind == HudInputKind.PointerSecondaryPressed && equipment.Occupied)
        {
            PlaySound(role, HudSoundEvent.ActivateAction);
            return _runtime.Dispatch(HudInput.UndressInventoryItem(equipment.Slot)).Consumed;
        }

        if (TryOrdinalRole(role, "loot-item-", "-slot", out int lootSlot)
            && kind == HudInputKind.PointerSecondaryPressed
            && (uint)lootSlot < (uint)model.Loot.PageSlots.Length)
        {
            HudLootSlotView loot = model.Loot.PageSlots[lootSlot];
            return loot.Occupied && _runtime.Dispatch(HudInput.TakeLootItem(loot.Entry)).Consumed;
        }

        if (kind is HudInputKind.PointerPrimaryPressed or HudInputKind.PointerPrimaryDoublePressed)
        {
            if (TryMessageBoxResolution(
                    model.MessageBoxes,
                    role,
                    out HudId requestId,
                    out HudMessageBoxDecision decision))
            {
                PlaySound(role, HudSoundEvent.ActivateAction);
                return _runtime.Dispatch(HudInput.ResolveMessageBox(requestId, decision)).Consumed;
            }

            input = role switch
            {
                "loot-previous" => HudInput.LootPreviousPage(),
                "loot-next" => HudInput.LootNextPage(),
                "loot-close" => HudInput.CloseLoot(),
                "quest-log-back" => HudInput.CloseQuestLog(),
                "quest-log-abandon" when !model.QuestLog.SelectedQuestId.IsEmpty =>
                    HudInput.AbandonQuest(model.QuestLog.SelectedQuestId),
                "quest-log-share" when !model.QuestLog.SelectedQuestId.IsEmpty =>
                    HudInput.ShareQuest(model.QuestLog.SelectedQuestId),
                "npc-talk-accept" => HudInput.AcceptQuest(),
                "npc-talk-complete" => HudInput.TurnInQuest(),
                "npc-talk-farewell" or "npc-talk-back" or "npc-talk-close" => HudInput.CloseQuestInfo(),
                _ => default,
            };
            if (input.Kind != default || role == "loot-previous")
            {
                PlaySound(role, HudSoundEvent.ActivateAction);
                return _runtime.Dispatch(input).Consumed;
            }

            if (TryOrdinalRole(role, "quest-log-row-", "-button", out int questRow)
                && (uint)questRow < (uint)model.QuestLog.Entries.Length
                && model.QuestLog.Entries[questRow].Occupied)
            {
                return _runtime.Dispatch(HudInput.SelectQuest(model.QuestLog.Entries[questRow].QuestId)).Consumed;
            }

            if (TryOrdinalRole(role, "npc-talk-option-", string.Empty, out int talkOption))
            {
                return _runtime.Dispatch(HudInput.SelectTalkOption(talkOption)).Consumed;
            }

            if (TryOrdinalRole(role, "npc-talk-alternative-button-", string.Empty, out int reward))
            {
                return _runtime.Dispatch(HudInput.SelectQuestReward(reward)).Consumed;
            }
        }

        return false;
    }

    private bool TryMessageBoxResolution(
        HudMessageBoxReadModel model,
        string role,
        out HudId requestId,
        out HudMessageBoxDecision decision)
    {
        HudMessageBoxInstance[] instances = _content.Manifest.Systems.MessageBox.Instances;
        ReadOnlySpan<HudMessageBoxView> entries = model.Entries;
        for (int index = 0; index < instances.Length && index < entries.Length; index++)
        {
            HudMessageBoxView view = entries[index];
            if (!view.Active || !view.Visible || view.Request.RequestId.IsEmpty)
            {
                continue;
            }

            HudMessageBoxInstance binding = instances[index];
            if (StringComparer.Ordinal.Equals(role, binding.Accept.Role.Id)
                || StringComparer.Ordinal.Equals(role, binding.Confirm.Role.Id))
            {
                requestId = view.Request.RequestId;
                decision = HudMessageBoxDecision.Accept;
                return true;
            }
            if (StringComparer.Ordinal.Equals(role, binding.Decline.Role.Id))
            {
                requestId = view.Request.RequestId;
                decision = HudMessageBoxDecision.Decline;
                return true;
            }
        }

        requestId = HudId.Empty;
        decision = default;
        return false;
    }

    private static bool TryInventorySlot(
        HudInventoryReadModel inventory,
        string role,
        out HudInventorySlotView slot)
    {
        foreach (HudInventorySlotView candidate in inventory.Slots)
        {
            if (StringComparer.Ordinal.Equals(candidate.Element.Value, role))
            {
                slot = candidate;
                return true;
            }
        }

        slot = default;
        return false;
    }

    private static bool TryCharacterSlot(
        HudCharacterReadModel character,
        string role,
        out HudCharacterEquipmentView slot)
    {
        foreach (HudCharacterEquipmentView candidate in character.Equipment)
        {
            if (StringComparer.Ordinal.Equals(candidate.Element.Value, role))
            {
                slot = candidate;
                return true;
            }
        }

        slot = default;
        return false;
    }

    private static bool TryOrdinalRole(string role, string prefix, string suffix, out int zeroBased)
    {
        zeroBased = -1;
        if (!role.StartsWith(prefix, StringComparison.Ordinal)
            || !role.EndsWith(suffix, StringComparison.Ordinal)
            || role.Length <= prefix.Length + suffix.Length)
        {
            return false;
        }

        ReadOnlySpan<char> number = role.AsSpan(prefix.Length, role.Length - prefix.Length - suffix.Length);
        return int.TryParse(number, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int ordinal)
            && ordinal > 0
            && (zeroBased = ordinal - 1) >= 0;
    }

    private void PlaySound(string role, HudSoundEvent eventKind)
    {
        if (_soundPlayer is null
            || !_content.SoundBindings.TryGetValue((role, eventKind), out string? soundId)
            || !_content.Sounds.TryGetValue(soundId, out AudioStream? sound))
        {
            return;
        }

        _soundPlayer.Stream = sound;
        _soundPlayer.Play();
    }

    private bool TryHit(Vector2 point, HudPointerSource source, out HudInput input)
    {
        foreach (NativeHudInputRole inputRole in _content.InputRoles)
        {
            string role = inputRole.Role;
            if (_content.Roles[role] is not Control control || !control.IsVisibleInTree())
            {
                continue;
            }

            Rect2 rect = control.GetGlobalRect();
            if (rect.Size.X <= 0 || rect.Size.Y <= 0 || !rect.HasPoint(point))
            {
                continue;
            }

            Vector2 normalized = (point - rect.Position) / rect.Size;
            bool hasMask = _content.Masks.TryGetValue(role, out NativeHudMaskSet? masks)
                && masks.Pick is not null;
            float alpha = hasMask ? masks!.Pick!.SampleAlpha(normalized) : 0.0f;
            if (hasMask && alpha < _product.PixelMaskThreshold)
            {
                continue;
            }

            input = HudInput.PointerMoved(
                new HudId(role),
                new HudPoint(point.X, point.Y),
                source,
                alpha,
                hasMask,
                new HudPoint(normalized.X, normalized.Y));
            return true;
        }

        input = default;
        return false;
    }

    private void Present(HudDiff diff)
    {
        if (diff.RequiresFullRefresh)
        {
            if ((diff.RequiredRefreshAreas & HudRefreshAreas.Inventory) != 0)
            {
                _contextPresenter.PresentInventory(diff.ReadModel.Inventory);
            }
            if ((diff.RequiredRefreshAreas & HudRefreshAreas.Loot) != 0)
            {
                _contextPresenter.PresentLoot(diff.ReadModel.Loot);
            }
            if ((diff.RequiredRefreshAreas & HudRefreshAreas.QuestLog) != 0)
            {
                _contextPresenter.PresentQuestLog(diff.ReadModel.QuestLog);
            }
            if ((diff.RequiredRefreshAreas & HudRefreshAreas.QuestInfo) != 0)
            {
                _contextPresenter.PresentQuestInfo(diff.ReadModel.QuestInfo);
            }
            if ((diff.RequiredRefreshAreas & HudRefreshAreas.Character) != 0)
            {
                _contextPresenter.PresentCharacter(diff.ReadModel.Character);
            }
            if ((diff.RequiredRefreshAreas & HudRefreshAreas.MessageBoxes) != 0)
            {
                _contextPresenter.PresentMessageBoxes(diff.ReadModel.MessageBoxes);
            }
            if ((diff.RequiredRefreshAreas & HudRefreshAreas.TargetSelection) != 0)
            {
                PresentSelectedTarget(diff.ReadModel.SelectedTarget);
            }

            if ((diff.RequiredRefreshAreas & HudRefreshAreas.ActionSlots) != 0)
            {
                foreach (HudActionSlotView slot in diff.ReadModel.ActionSlots)
                {
                    Present(new HudChange(
                        HudChangeKind.ActionSlot,
                        slot.Element,
                        0,
                        !slot.AbilityId.IsEmpty,
                        slot.CooldownMilliseconds,
                        slot.Enabled,
                        slot.AbilityId,
                        default,
                        slot.CooldownDurationMilliseconds,
                        slot.Stamp));
                }
            }

            if ((diff.RequiredRefreshAreas & HudRefreshAreas.Feedback) != 0)
            {
                foreach (HudFeedbackView feedback in diff.ReadModel.Feedback)
                {
                    Present(new HudChange(
                        HudChangeKind.Feedback,
                        feedback.Element,
                        feedback.Generation,
                        feedback.Active,
                        feedback.Amount,
                        feedback.Critical,
                        feedback.EventId,
                        feedback.Position));
                }
            }

            if ((diff.RequiredRefreshAreas & HudRefreshAreas.QuestTracker) != 0)
            {
                foreach (HudQuestView quest in diff.ReadModel.Quests)
                {
                    Present(new HudChange(
                        HudChangeKind.QuestTracker,
                        quest.Element,
                        0,
                        quest.Tracked,
                        quest.Snapshot?.Objectives.Length ?? 0,
                        quest.Completable,
                        quest.TitleId,
                        default));
                }
            }

            if ((diff.RequiredRefreshAreas & HudRefreshAreas.Cursor) != 0)
            {
                ApplyCursor(diff.ReadModel.CursorId.Value);
            }
            if ((diff.RequiredRefreshAreas & HudRefreshAreas.Focus) != 0)
            {
                SetMeta("hud_focus", (int)diff.ReadModel.Focus);
            }
            if ((diff.RequiredRefreshAreas & HudRefreshAreas.VirtualPointer) != 0)
            {
                _pointer = new Vector2((float)diff.ReadModel.Pointer.X, (float)diff.ReadModel.Pointer.Y);
                SetMeta("hud_pointer_source", (int)diff.ReadModel.PointerSource);
            }
        }
        else
        {
            foreach (HudChange change in diff.Changes)
            {
                if (change.Kind == HudChangeKind.TargetSelection)
                {
                    PresentSelectedTarget(diff.ReadModel.SelectedTarget);
                    continue;
                }

                Present(change);
            }
        }

        foreach (HudError error in diff.Errors)
        {
            GD.PushWarning($"Native HUD {error.Code} element={error.RelatedId} entity={error.EntityId}");
        }
    }

    private void Present(in HudChange change)
    {
        if (_readModel is HudReadModel model)
        {
            switch (change.Kind)
            {
                case HudChangeKind.Inventory:
                    _contextPresenter.PresentInventory(model.Inventory);
                    return;
                case HudChangeKind.Loot:
                    _contextPresenter.PresentLoot(model.Loot);
                    return;
                case HudChangeKind.QuestLog:
                    _contextPresenter.PresentQuestLog(model.QuestLog);
                    return;
                case HudChangeKind.QuestInfo:
                    _contextPresenter.PresentQuestInfo(model.QuestInfo);
                    return;
                case HudChangeKind.Character:
                    _contextPresenter.PresentCharacter(model.Character);
                    return;
                case HudChangeKind.MessageBox:
                    _contextPresenter.PresentMessageBoxes(model.MessageBoxes);
                    return;
            }
        }

        if (change.Kind == HudChangeKind.Cursor)
        {
            ApplyCursor(change.ContentId.Value);
            return;
        }

        if (change.Kind == HudChangeKind.Focus)
        {
            SetMeta("hud_focus", change.Value);
            return;
        }

        if (change.Kind == HudChangeKind.VirtualPointer)
        {
            _pointer = new Vector2((float)change.Position.X, (float)change.Position.Y);
            SetMeta("hud_pointer_source", change.Value);
            return;
        }
        if (change.Kind is HudChangeKind.Chat or HudChangeKind.WorldChatProjection)
        {
            _content.Root.SetMeta("hud_chat_event", change.Element.Value);
            _content.Root.SetMeta("hud_chat_visible", change.Visible);
            _content.Root.SetMeta("hud_chat_channel", change.ContentId.Value);
            _content.Root.SetMeta(
                "hud_chat_position",
                new Vector2((float)change.Position.X, (float)change.Position.Y));
            return;
        }

        if (!_content.Roles.TryGetValue(change.Element.Value, out Node? node))
        {
            throw new InvalidDataException($"Native HUD diff references unknown role '{change.Element}'");
        }

        if (node is not CanvasItem item)
        {
            throw new InvalidDataException($"Native HUD diff role '{change.Element}' is not a CanvasItem");
        }

        item.Visible = change.Visible;
        item.SetMeta("hud_generation", change.Generation);
        item.SetMeta("hud_value", change.Value);
        item.SetMeta("hud_flag", change.Flag);
        item.SetMeta("hud_content_id", change.ContentId.Value);
        if (change.Kind == HudChangeKind.Projection && item is Control control)
        {
            control.GlobalPosition = new Vector2((float)change.Position.X, (float)change.Position.Y);
        }

        if (change.Kind == HudChangeKind.Feedback && item is Label label)
        {
            label.Text = change.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void PresentSelectedTarget(in HudSelectedTargetView target)
    {
        HudTargetSelectionSystem selection = _content.Manifest.Systems.TargetSelection;
        if (_content.Roles[selection.Decal.Id] is not Decal decal)
        {
            throw new InvalidDataException("Native HUD target-selection role is not an authored Decal");
        }

        Vector3 ground = default;
        Vector3 anchor = default;
        float pickRadius = 0.0f;
        bool visible = target.HasAuthority
            && target.EntityId != 0
            && target.Refusal == HudTargetSelectionRefusal.None
            && _world.TryGetGroundFootprint(target.EntityId, out ground, out pickRadius)
            && _world.TryGetAnchor(target.EntityId, out anchor);
        if (!visible)
        {
            decal.Visible = false;
            _content.TargetSelection.Visible = false;
            return;
        }

        HudTargetSelectionSizing sizing = selection.Sizing;
        float radius = pickRadius * Fraction(sizing.ObjectCutAreaScale) + Fraction(sizing.ExtraRadius);
        float diameter = radius * Fraction(sizing.DiameterScale);
        float depth = Math.Max(anchor.Y - ground.Y, 0.01f);
        if (!float.IsFinite(diameter) || diameter <= 0.0f || !float.IsFinite(depth))
        {
            decal.Visible = false;
            _content.TargetSelection.Visible = false;
            return;
        }

        _content.TargetSelection.GlobalPosition = ground;
        decal.Size = new Vector3(diameter, depth, diameter);
        decal.CullMask = sizing.CullMask;
        decal.Visible = true;
        _content.TargetSelection.Visible = true;
    }

    private static float Fraction(HudFraction value) =>
        value.Denominator == 0 ? float.NaN : (float)value.Numerator / value.Denominator;

    private void ApplyCursor(string key)
    {
        if (!_content.Cursors.TryGetValue(key, out NativeHudCursor? cursor))
        {
            throw new InvalidDataException($"Native HUD cursor '{key}' is absent from compiled content");
        }

        Input.SetCustomMouseCursor(cursor.Texture, Input.CursorShape.Arrow, cursor.Hotspot);
        s_cursorOwner = new WeakReference<NativeGameplayHudHost>(this);
    }

    private void DisposeOwnedState()
    {
        if (!_initialized && _content is null)
        {
            return;
        }

        _initialized = false;
        _openNativeProduct = null!;
        _dragInventorySlot = -1;
        _readModel = null;
        _soundPlayer?.Stop();
        _soundPlayer = null;
        if (_chatInput is not null)
        {
            _chatInput.TextSubmitted -= OnChatTextSubmitted;
            _chatInput = null;
        }
        _runtime?.Dispose();
        _content?.Dispose();
        _itemCatalog?.Dispose();
        _contextPresenter = null!;
        if (s_cursorOwner?.TryGetTarget(out NativeGameplayHudHost? owner) == true
            && ReferenceEquals(owner, this))
        {
            Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
            s_cursorOwner = null;
        }
    }
}
