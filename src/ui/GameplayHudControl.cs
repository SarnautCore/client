using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SarnautCore.Gameplay;

namespace SarnautCore;

/// <summary>Thin Godot adapter over the complete plain-C# gameplay HUD model.</summary>
public partial class GameplayHudControl : Control
{
    private GameplayHudViewModel _model = null!;
    private ZoneNetworkLoop _network = null!;
    private InventoryControl _inventory = null!;
    private QuestLogControl _questLog = null!;
    private LootWindowControl _loot = null!;
    private QuestDialogueControl _dialogue = null!;
    private DamageNumbersControl _damageNumbers = null!;

    public void Initialize(GameplayHudViewModel model, ZoneNetworkLoop network)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        Name = "GameplayHud";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ConvertedUiTranslations.Mount();

        _damageNumbers = new DamageNumbersControl();
        _damageNumbers.Bind(model.DamageNumbers, ProjectEntity);
        AddChild(_damageNumbers);

        var target = new TargetFrameControl();
        target.Bind(model.Target);
        AddChild(target);

        var abilities = new AbilityBarControl();
        abilities.Bind(model.Abilities, model.Target);
        AddChild(abilities);

        var death = new DeathFeedbackControl();
        death.Bind(model.DeathFeedback);
        AddChild(death);

        var tracker = new QuestTrackerControl();
        tracker.Bind(model.QuestTracker);
        AddChild(tracker);

        _loot = new LootWindowControl();
        _loot.Bind(model.Loot);
        _loot.CloseRequested += () => model.Focus.Close(GameplayWindow.Loot);
        AddChild(_loot);

        _inventory = new InventoryControl();
        _inventory.Bind(model.Inventory);
        _inventory.CloseRequested += () => model.Focus.Close(GameplayWindow.Inventory);
        AddChild(_inventory);

        _questLog = new QuestLogControl();
        _questLog.Bind(model.QuestLog);
        _questLog.CloseRequested += () => model.Focus.Close(GameplayWindow.QuestLog);
        AddChild(_questLog);

        _dialogue = new QuestDialogueControl();
        _dialogue.Bind(model.Dialogue);
        _dialogue.CloseRequested += () => model.Focus.Close(GameplayWindow.Dialogue);
        AddChild(_dialogue);

        model.Abilities.AbilityRequested += OnAbilityRequested;
        model.Loot.TakeRequested += OnLootTakeRequested;
        model.Loot.Changed += SyncLootFocus;
        model.Dialogue.AcceptRequested += OnQuestAcceptRequested;
        model.Dialogue.TurnInRequested += OnQuestTurnInRequested;
        model.Dialogue.Changed += SyncDialogueFocus;
        model.QuestLog.AbandonRequested += OnQuestAbandonRequested;
        model.Focus.Changed += SyncFocusedWindows;
        SyncLootFocus();
        SyncDialogueFocus();
        SyncFocusedWindows();
    }

    public override void _Process(double delta)
    {
        if (_model is null)
        {
            return;
        }

        _model.Advance(delta);
        _damageNumbers.RefreshPositions();
    }

    public override void _ExitTree()
    {
        if (_model is null)
        {
            return;
        }

        _model.Abilities.AbilityRequested -= OnAbilityRequested;
        _model.Loot.TakeRequested -= OnLootTakeRequested;
        _model.Loot.Changed -= SyncLootFocus;
        _model.Dialogue.AcceptRequested -= OnQuestAcceptRequested;
        _model.Dialogue.TurnInRequested -= OnQuestTurnInRequested;
        _model.Dialogue.Changed -= SyncDialogueFocus;
        _model.QuestLog.AbandonRequested -= OnQuestAbandonRequested;
        _model.Focus.Changed -= SyncFocusedWindows;
    }

    public void ToggleInventory()
    {
        Toggle(GameplayWindow.Inventory);
    }

    public void ToggleQuestLog()
    {
        Toggle(GameplayWindow.QuestLog);
    }

    private void Toggle(GameplayWindow window)
    {
        if (_model.Focus.IsOpen(window))
        {
            _model.Focus.Close(window);
        }
        else
        {
            _model.Focus.Open(window);
        }
    }

    private Vector2? ProjectEntity(ulong entityId)
    {
        return _network.TryGetEntityScreenPoint(entityId, out Vector2 point) ? point : null;
    }

    private void SyncLootFocus()
    {
        if (_model.Loot.IsOpen)
        {
            if (!_model.Focus.IsOpen(GameplayWindow.Loot))
            {
                _model.Focus.Open(GameplayWindow.Loot);
            }
        }
        else
        {
            _model.Focus.Close(GameplayWindow.Loot);
        }
    }

    private void SyncDialogueFocus()
    {
        if (_model.Dialogue.IsOpen)
        {
            if (!_model.Focus.IsOpen(GameplayWindow.Dialogue))
            {
                _model.Focus.Open(GameplayWindow.Dialogue);
            }
        }
        else
        {
            _model.Focus.Close(GameplayWindow.Dialogue);
        }
    }

    private void SyncFocusedWindows()
    {
        _inventory.SetOpen(_model.Focus.IsOpen(GameplayWindow.Inventory));
        _questLog.SetOpen(_model.Focus.IsOpen(GameplayWindow.QuestLog));
        _loot.SetOpen(_model.Focus.IsOpen(GameplayWindow.Loot));
        _dialogue.SetOpen(_model.Focus.IsOpen(GameplayWindow.Dialogue));

        if (!_model.Focus.IsOpen(GameplayWindow.Loot) && _model.Loot.IsOpen)
        {
            _model.Loot.Close();
        }

        if (!_model.Focus.IsOpen(GameplayWindow.Dialogue) && _model.Dialogue.IsOpen)
        {
            _model.Dialogue.Close();
        }
    }

    private void OnAbilityRequested(AbilityUseRequest request) => _network.RequestAbilityUse(request);

    private void OnLootTakeRequested(ulong corpseEntityId) => _network.RequestLootTake(corpseEntityId);

    private void OnQuestAcceptRequested(QuestCommandRequest request) => _network.RequestQuestAccept(request);

    private void OnQuestTurnInRequested(QuestCommandRequest request) => _network.RequestQuestTurnIn(request);

    private void OnQuestAbandonRequested(string questId) => _network.RequestQuestAbandon(questId);
}

public partial class TargetFrameControl : Control
{
    private TargetViewModel _model = null!;
    private Label _title = null!;
    private Label _healthText = null!;
    private ProgressBar _health = null!;

    public void Bind(TargetViewModel model)
    {
        _model = model;
        HudLayout.Place(this, 0.5f, 0, -190, 88, 190, 174);
        ClipContents = true;
        bool converted = ConvertedHudChrome.Mount(this, ConvertedHudChrome.TargetSelection);
        VBoxContainer body = HudLayout.Panel(this, "Target", converted);
        _title = HudLayout.Label(body, string.Empty, 18);
        _health = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(330, 14) };
        body.AddChild(_health);
        _healthText = HudLayout.Label(body, string.Empty, 13);
        model.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_model != null)
        {
            _model.Changed -= Refresh;
        }
    }

    private void Refresh()
    {
        Visible = _model.HasTarget;
        if (!Visible)
        {
            return;
        }

        string name = LocalizedText.Resolve(_model.NameKey);
        if (name.Length == 0)
        {
            name = CanonicalText.Fallback(_model.ContentId);
        }

        _title.Text = _model.Level > 0 ? $"{name}  ·  {_model.Level}" : name;
        _health.Value = _model.HealthFraction * 100;
        _healthText.Text = _model.Alive ? $"{_model.Health:N0} / {_model.MaxHealth:N0}" : "Defeated";
    }
}

public partial class AbilityBarControl : Control
{
    private AbilityBarViewModel _model = null!;
    private TargetViewModel _target = null!;
    private readonly List<Button> _buttons = [];

    public void Bind(AbilityBarViewModel model, TargetViewModel target)
    {
        _model = model;
        _target = target;
        HudLayout.Place(this, 0.5f, 1, -250, -108, 250, -22);
        ClipContents = true;
        bool converted = ConvertedHudChrome.Mount(this, ConvertedHudChrome.Character);
        VBoxContainer body = HudLayout.Panel(this, "Abilities", converted);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        body.AddChild(row);
        for (int index = 0; index < model.Abilities.Count; index++)
        {
            int slot = index;
            AbilityDefinition ability = model.Abilities[index];
            var button = new Button
            {
                CustomMinimumSize = new Vector2(104, 48),
                TooltipText = LocalizedText.Resolve(ability.NameKey),
                FocusMode = FocusModeEnum.All,
            };
            button.Pressed += () => model.TryRequestUse(slot, target.EntityId);
            row.AddChild(button);
            _buttons.Add(button);
        }

        model.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_model != null)
        {
            _model.Changed -= Refresh;
        }
    }

    private void Refresh()
    {
        for (int index = 0; index < _buttons.Count; index++)
        {
            string name = LocalizedText.Resolve(_model.Abilities[index].NameKey);
            _buttons[index].Text = _model.IsOnGlobalCooldown
                ? $"{index + 1}  {name}\n{_model.GlobalCooldownRemainingSeconds:0.0}s"
                : $"{index + 1}  {name}";
            _buttons[index].Disabled = _model.IsOnGlobalCooldown;
        }
    }
}

public partial class DamageNumbersControl : Control
{
    private DamageNumberPoolViewModel _model = null!;
    private Func<ulong, Vector2?> _project = null!;
    private Label[] _labels = [];

    public void Bind(DamageNumberPoolViewModel model, Func<ulong, Vector2?> project)
    {
        _model = model;
        _project = project;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ConvertedHudChrome.Mount(this, ConvertedHudChrome.DamageVisualization);
        _labels = model.Slots.Select(_ => new Label
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            ThemeTypeVariation = "HeaderSmall",
        }).ToArray();
        foreach (Label label in _labels)
        {
            AddChild(label);
        }

        model.Spawned += Refresh;
        model.Expired += Refresh;
    }

    public override void _ExitTree()
    {
        if (_model != null)
        {
            _model.Spawned -= Refresh;
            _model.Expired -= Refresh;
        }
    }

    public void RefreshPositions()
    {
        if (_model == null)
        {
            return;
        }

        for (int index = 0; index < _model.Slots.Count; index++)
        {
            DamageNumberViewModel number = _model.Slots[index];
            Label label = _labels[index];
            label.Visible = number.Active;
            if (!number.Active)
            {
                continue;
            }

            Vector2 point = _project(number.EntityId) ?? GetViewportRect().Size * 0.5f;
            double elapsed = 1.2 - number.RemainingSeconds;
            label.Position = point + new Vector2(-24, -48 - (float)(elapsed * 34));
        }
    }

    private void Refresh(DamageNumberViewModel number)
    {
        Label label = _labels[number.PoolIndex];
        label.Text = number.Critical ? $"{number.Amount:N0}!" : number.Amount.ToString("N0");
        label.AddThemeColorOverride("font_color", number.Critical ? new Color("ffd166") : new Color("fff0d0"));
        label.Visible = number.Active;
    }
}

public partial class DeathFeedbackControl : Control
{
    private DeathFeedbackViewModel _model = null!;
    private Label _label = null!;

    public void Bind(DeathFeedbackViewModel model)
    {
        _model = model;
        HudLayout.Place(this, 0.5f, 0.5f, -180, -148, 180, -96);
        MouseFilter = MouseFilterEnum.Ignore;
        VBoxContainer body = HudLayout.Panel(this, string.Empty);
        _label = HudLayout.Label(body, string.Empty, 22);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        model.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_model != null)
        {
            _model.Changed -= Refresh;
        }
    }

    private void Refresh()
    {
        Visible = _model.Visible;
        _label.Text = _model.Kind switch
        {
            DeathFeedbackKind.TargetDefeated => "Target defeated",
            DeathFeedbackKind.PlayerDied => "You have fallen",
            DeathFeedbackKind.Respawned => "Respawned",
            _ => string.Empty,
        };
    }
}

public partial class LootWindowControl : Control
{
    private LootWindowViewModel _model = null!;
    private VBoxContainer _items = null!;
    private Label _money = null!;

    public event Action? CloseRequested;

    public void Bind(LootWindowViewModel model)
    {
        _model = model;
        HudLayout.Place(this, 0.5f, 0.5f, 110, -205, 475, 165);
        bool converted = ConvertedHudChrome.Mount(this, ConvertedHudChrome.LootBag);
        VBoxContainer body = HudLayout.Panel(this, "Loot", converted);
        _items = new VBoxContainer();
        body.AddChild(_items);
        _money = HudLayout.Label(body, string.Empty, 14);
        var actions = new HBoxContainer();
        body.AddChild(actions);
        Button take = HudLayout.Button(actions, "Take all");
        take.Pressed += () => model.RequestTake();
        Button close = HudLayout.Button(actions, "Close");
        close.Pressed += () => CloseRequested?.Invoke();
        model.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_model != null)
        {
            _model.Changed -= Refresh;
        }
    }

    public void SetOpen(bool open)
    {
        Visible = open && _model.IsOpen;
    }

    private void Refresh()
    {
        HudLayout.Clear(_items);
        foreach (LootEntryViewModel item in _model.Items)
        {
            HudLayout.Label(_items, $"{LocalizedText.Resolve(item.ItemId)}  ×{item.Count:N0}", 15);
        }

        if (_model.Items.Count == 0)
        {
            HudLayout.Label(_items, "Nothing to take", 14);
        }

        _money.Text = _model.Money > 0 ? $"Money  {_model.Money:N0}" : string.Empty;
        Visible = _model.IsOpen;
    }
}

public partial class InventoryControl : Control
{
    private InventoryViewModel _model = null!;
    private GridContainer _grid = null!;
    private Label _money = null!;
    private Button[] _slots = [];
    private int _selectedSlot = -1;

    public event Action? CloseRequested;

    public void Bind(InventoryViewModel model)
    {
        _model = model;
        HudLayout.Place(this, 1, 0.5f, -430, -288, -22, 288);
        bool converted = ConvertedHudChrome.Mount(this, ConvertedHudChrome.Multibag);
        VBoxContainer body = HudLayout.Panel(this, "Bags", converted);
        _grid = new GridContainer { Columns = 4 };
        body.AddChild(_grid);
        _slots = new Button[model.Capacity];
        for (int index = 0; index < model.Capacity; index++)
        {
            int slot = index;
            var button = new Button { CustomMinimumSize = new Vector2(88, 72), ClipText = true };
            button.Pressed += () => ClickSlot(slot);
            _grid.AddChild(button);
            _slots[index] = button;
        }

        _money = HudLayout.Label(body, string.Empty, 14);
        Button close = HudLayout.Button(body, "Close");
        close.Pressed += () => CloseRequested?.Invoke();
        model.Changed += Refresh;
        SetOpen(false);
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_model != null)
        {
            _model.Changed -= Refresh;
        }
    }

    public void SetOpen(bool open)
    {
        Visible = open;
        if (!open)
        {
            _selectedSlot = -1;
        }
    }

    private void ClickSlot(int slot)
    {
        if (_selectedSlot < 0)
        {
            if (_model.Slots[slot] is not null)
            {
                _selectedSlot = slot;
                Refresh();
            }

            return;
        }

        _model.TryMove(_selectedSlot, slot);
        _selectedSlot = -1;
        Refresh();
    }

    private void Refresh()
    {
        for (int index = 0; index < _slots.Length; index++)
        {
            InventoryStackViewModel? stack = _model.Slots[index];
            _slots[index].Text = stack is null
                ? string.Empty
                : $"{LocalizedText.Resolve(stack.ItemId)}\n×{stack.Count:N0}";
            _slots[index].ButtonPressed = index == _selectedSlot;
        }

        _money.Text = $"Money  {_model.Currency:N0}";
    }
}

public partial class QuestLogControl : Control
{
    private QuestLogViewModel _model = null!;
    private ItemList _list = null!;
    private Label _title = null!;
    private Label _description = null!;
    private VBoxContainer _objectives = null!;
    private Button _abandon = null!;

    public event Action? CloseRequested;

    public void Bind(QuestLogViewModel model)
    {
        _model = model;
        HudLayout.Place(this, 0, 0.5f, 22, -290, 610, 290);
        bool converted = ConvertedHudChrome.Mount(this, ConvertedHudChrome.QuestLog);
        VBoxContainer body = HudLayout.Panel(this, "Quest log", converted);
        var columns = new HBoxContainer();
        body.AddChild(columns);
        _list = new ItemList { CustomMinimumSize = new Vector2(220, 430) };
        _list.ItemSelected += index => Select((int)index);
        columns.AddChild(_list);
        var details = new VBoxContainer { CustomMinimumSize = new Vector2(310, 430) };
        columns.AddChild(details);
        _title = HudLayout.Label(details, string.Empty, 20);
        _description = HudLayout.Label(details, string.Empty, 15);
        _description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _objectives = new VBoxContainer();
        details.AddChild(_objectives);
        var actions = new HBoxContainer();
        body.AddChild(actions);
        _abandon = HudLayout.Button(actions, "Abandon");
        _abandon.Pressed += () => model.RequestAbandonSelected();
        Button close = HudLayout.Button(actions, "Close");
        close.Pressed += () => CloseRequested?.Invoke();
        model.Changed += Refresh;
        SetOpen(false);
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_model != null)
        {
            _model.Changed -= Refresh;
        }
    }

    public void SetOpen(bool open) => Visible = open;

    private void Select(int index)
    {
        if (index >= 0 && index < _model.Quests.Count)
        {
            _model.Select(_model.Quests[index].QuestId);
        }
    }

    private void Refresh()
    {
        _list.Clear();
        foreach (QuestLogEntry quest in _model.Quests)
        {
            _list.AddItem(LocalizedText.Resolve(quest.TitleKey));
        }

        QuestLogEntry? selected = _model.SelectedQuest;
        _title.Text = selected is null ? "No quest selected" : LocalizedText.Resolve(selected.TitleKey);
        _description.Text = selected is null ? string.Empty : LocalizedText.Resolve(selected.DescriptionKey);
        _abandon.Disabled = selected is not { CanAbandon: true };
        HudLayout.Clear(_objectives);
        if (selected is null)
        {
            return;
        }

        foreach (QuestObjectiveUpdate objective in selected.Objectives)
        {
            string count = objective.ShowCount ? $"  {objective.Current:N0} / {objective.Limit:N0}" : string.Empty;
            HudLayout.Label(_objectives, $"{LocalizedText.Resolve(objective.TextKey)}{count}", 14);
        }
    }
}

public partial class QuestTrackerControl : Control
{
    private QuestTrackerViewModel _model = null!;
    private VBoxContainer _quests = null!;

    public void Bind(QuestTrackerViewModel model)
    {
        _model = model;
        HudLayout.Place(this, 1, 0, -340, 118, -22, 410);
        bool converted = ConvertedHudChrome.Mount(this, ConvertedHudChrome.QuestTracker);
        VBoxContainer body = HudLayout.Panel(this, "Objectives", converted);
        _quests = new VBoxContainer();
        body.AddChild(_quests);
        model.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_model != null)
        {
            _model.Changed -= Refresh;
        }
    }

    private void Refresh()
    {
        HudLayout.Clear(_quests);
        Visible = _model.Quests.Count > 0;
        foreach (QuestTrackerEntry quest in _model.Quests)
        {
            Label title = HudLayout.Label(_quests, LocalizedText.Resolve(quest.TitleKey), 16);
            title.AddThemeColorOverride("font_color", quest.Complete ? new Color("8ee6a2") : new Color("e6edf6"));
            foreach (QuestObjectiveUpdate objective in quest.Objectives)
            {
                string count = objective.ShowCount ? $"  {objective.Current:N0} / {objective.Limit:N0}" : string.Empty;
                HudLayout.Label(_quests, $"{LocalizedText.Resolve(objective.TextKey)}{count}", 13);
            }
        }
    }
}

public partial class QuestDialogueControl : Control
{
    private QuestDialogueViewModel _model = null!;
    private Label _title = null!;
    private Label _description = null!;
    private VBoxContainer _objectives = null!;
    private Button _primary = null!;

    public event Action? CloseRequested;

    public void Bind(QuestDialogueViewModel model)
    {
        _model = model;
        HudLayout.Place(this, 0.5f, 0.5f, -320, -250, 320, 250);
        bool converted = ConvertedHudChrome.Mount(this, ConvertedHudChrome.QuestInfo);
        VBoxContainer body = HudLayout.Panel(this, "Quest", converted);
        _title = HudLayout.Label(body, string.Empty, 22);
        _description = HudLayout.Label(body, string.Empty, 15);
        _description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _description.CustomMinimumSize = new Vector2(570, 120);
        _objectives = new VBoxContainer();
        body.AddChild(_objectives);
        var actions = new HBoxContainer();
        body.AddChild(actions);
        _primary = HudLayout.Button(actions, "Accept");
        _primary.Pressed += Primary;
        Button close = HudLayout.Button(actions, "Close");
        close.Pressed += () => CloseRequested?.Invoke();
        model.Changed += Refresh;
        SetOpen(false);
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_model != null)
        {
            _model.Changed -= Refresh;
        }
    }

    public void SetOpen(bool open) => Visible = open && _model.IsOpen;

    private void Primary()
    {
        if (_model.Mode == QuestDialogueMode.Offer)
        {
            _model.RequestAccept();
        }
        else if (_model.Mode == QuestDialogueMode.TurnIn)
        {
            _model.RequestTurnIn();
        }
    }

    private void Refresh()
    {
        Visible = _model.IsOpen;
        QuestUpdate? quest = _model.Quest;
        _title.Text = quest is null ? string.Empty : LocalizedText.Resolve(quest.TitleKey);
        _description.Text = quest is null ? string.Empty : LocalizedText.Resolve(quest.DescriptionKey);
        _primary.Text = _model.Mode == QuestDialogueMode.TurnIn ? "Complete" : "Accept";
        HudLayout.Clear(_objectives);
        if (quest is null)
        {
            return;
        }

        foreach (QuestObjectiveUpdate objective in quest.Objectives.Where(objective => !objective.Internal))
        {
            string count = objective.ShowCount ? $"  {objective.Current:N0} / {objective.Limit:N0}" : string.Empty;
            HudLayout.Label(_objectives, $"{LocalizedText.Resolve(objective.TextKey)}{count}", 14);
        }
    }
}

internal static class HudLayout
{
    public static void Place(Control control, float anchorX, float anchorY, float left, float top, float right, float bottom)
    {
        control.AnchorLeft = anchorX;
        control.AnchorRight = anchorX;
        control.AnchorTop = anchorY;
        control.AnchorBottom = anchorY;
        control.OffsetLeft = left;
        control.OffsetTop = top;
        control.OffsetRight = right;
        control.OffsetBottom = bottom;
    }

    public static VBoxContainer Panel(Control host, string title, bool convertedChrome = false)
    {
        var panel = new PanelContainer();
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.MouseFilter = Control.MouseFilterEnum.Stop;
        if (convertedChrome)
        {
            // The converted form supplies the frame. Keep the working labels,
            // lists, and buttons above it without painting a second opaque
            // fallback frame over the converted one.
            panel.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        }

        host.AddChild(panel);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        panel.AddChild(margin);
        var body = new VBoxContainer();
        margin.AddChild(body);
        if (!string.IsNullOrEmpty(title))
        {
            Label(body, title, 18);
        }

        return body;
    }

    public static Label Label(Node parent, string text, int fontSize)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        parent.AddChild(label);
        return label;
    }

    public static Button Button(Node parent, string text)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(112, 38) };
        parent.AddChild(button);
        return button;
    }

    public static void Clear(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }
}
