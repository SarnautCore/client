using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SarnautCore.NativeHud;

namespace SarnautCore;

/// <summary>The one product manifest that closes over the compiled native HUD.</summary>
public sealed record NativeHudContentPaths(string Manifest)
{
    private const string ProductRelativePath = "ui/hud/hud-product.json";

    public static NativeHudContentPaths Canonical() =>
        new($"{NativeContentSettings.NativeRoot.TrimEnd('/')}/{ProductRelativePath}");

    public void Validate(string nativeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeRoot);
        RequireConfinedPath(Manifest, nativeRoot, ".json", nameof(Manifest));
    }

    public string Resolve(string relativePath, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string directory = Manifest[..(Manifest.LastIndexOf('/') + 1)];
        string resolved = directory + relativePath;
        RequireConfinedPath(resolved, NativeContentSettings.NativeRoot, extension, relativePath);
        return resolved;
    }

    private static void RequireConfinedPath(
        string path,
        string nativeRoot,
        string extension,
        string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameter);
        string prefix = nativeRoot.TrimEnd('/') + "/";
        bool unsafeSegment = path.Split('/').Any(segment => segment is "." or "..");
        if (!path.StartsWith(prefix, StringComparison.Ordinal)
            || !path.StartsWith("res://", StringComparison.Ordinal)
            || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            || unsafeSegment
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains('?', StringComparison.Ordinal)
            || path.Contains('#', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Native HUD path must be a confined '{extension}' resource under '{nativeRoot}': {path}",
                parameter);
        }
    }
}

internal sealed record NativeHudInputRole(
    string Role,
    int Priority,
    IReadOnlyDictionary<HudPhysicalInput, HudSemanticEvent> Routes);

internal sealed record NativeHudCursor(Texture2D Texture, Vector2I Hotspot);

internal sealed record NativeHudOvertipSlot(
    Control Root,
    IReadOnlyDictionary<string, CanvasItem> Roles);

internal sealed class NativeHudMaskSet : IDisposable
{
    public NativeHudPixelMask? Clip { get; init; }
    public NativeHudPixelMask? Pick { get; init; }

    public void Dispose()
    {
        Clip?.Dispose();
        if (!ReferenceEquals(Clip, Pick))
        {
            Pick?.Dispose();
        }
    }
}

/// <summary>
/// Loads and validates the complete compiled product before either its UI tree or world decal
/// becomes visible. The semantic JSON manifest remains the sole role/catalog contract.
/// </summary>
internal sealed class NativeHudContent : IDisposable
{
    private readonly Resource[] _resources;
    private bool _attached;
    private bool _disposed;

    private NativeHudContent(
        Control root,
        Control character,
        Node3D targetSelection,
        IReadOnlyDictionary<string, Node> roles,
        IReadOnlyList<NativeHudInputRole> inputRoles,
        IReadOnlyDictionary<string, HudSemanticInputBinding> inputBindings,
        IReadOnlyList<string> actionSlots,
        IReadOnlyList<string> questRows,
        IReadOnlyDictionary<string, IReadOnlyList<string>> feedbackPools,
        IReadOnlyDictionary<string, NativeHudMaskSet> masks,
        IReadOnlyDictionary<string, NativeHudCursor> cursors,
        IReadOnlyDictionary<string, AudioStream> sounds,
        IReadOnlyDictionary<(string Role, HudSoundEvent Event), string> soundBindings,
        IReadOnlyList<NativeHudOvertipSlot> overtips,
        HudProductManifest manifest,
        HudProduct product,
        Resource[] resources)
    {
        Root = root;
        Character = character;
        TargetSelection = targetSelection;
        Roles = roles;
        InputRoles = inputRoles;
        InputBindings = inputBindings;
        ActionSlots = actionSlots;
        QuestRows = questRows;
        FeedbackPools = feedbackPools;
        Masks = masks;
        Cursors = cursors;
        Sounds = sounds;
        SoundBindings = soundBindings;
        Overtips = overtips;
        Manifest = manifest;
        Product = product;
        _resources = resources;
    }

    public Control Root { get; }
    public Control Character { get; }
    public Node3D TargetSelection { get; }
    public IReadOnlyDictionary<string, Node> Roles { get; }
    public IReadOnlyList<NativeHudInputRole> InputRoles { get; }
    public IReadOnlyDictionary<string, HudSemanticInputBinding> InputBindings { get; }
    public IReadOnlyList<string> ActionSlots { get; }
    public IReadOnlyList<string> QuestRows { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FeedbackPools { get; }
    public IReadOnlyDictionary<string, NativeHudMaskSet> Masks { get; }
    public IReadOnlyDictionary<string, NativeHudCursor> Cursors { get; }
    public IReadOnlyDictionary<string, AudioStream> Sounds { get; }
    public IReadOnlyDictionary<(string Role, HudSoundEvent Event), string> SoundBindings { get; }
    public IReadOnlyList<NativeHudOvertipSlot> Overtips { get; }
    public HudProductManifest Manifest { get; }
    public HudProduct Product { get; }

    public static NativeHudContent Load(NativeHudContentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.Validate(NativeContentSettings.NativeRoot);
        if (!Godot.FileAccess.FileExists(paths.Manifest))
        {
            throw new FileNotFoundException($"Native HUD product manifest is missing: {paths.Manifest}");
        }

        HudProductManifest manifest = HudProductManifestParser.Parse(
            Godot.FileAccess.GetFileAsString(paths.Manifest));
        HudProduct product = HudProductManifestParser.BuildProduct(manifest);
        var resources = new List<Resource>();
        var ownedMasks = new List<NativeHudMaskSet>();
        Node? instance = null;
        Control? root = null;
        Control? character = null;
        Node3D? targetSelection = null;
        try
        {
            string scenePath = paths.Resolve(manifest.RuntimeScene, ".scn");
            using PackedScene scene = LoadRequired<PackedScene>(scenePath, "runtime scene");
            instance = scene.Instantiate();

            IReadOnlyDictionary<string, Node> roots = ResolveRoots(instance, manifest.Roots);
            HudRootBinding mainBinding = RequireRoot(manifest.Roots, "world-input", decalOnly: false);
            HudRootBinding decalBinding = RequireRoot(manifest.Roots, "target-selection", decalOnly: true);
            root = roots[mainBinding.Id] as Control
                ?? throw new InvalidDataException("Native HUD Main root must be Control");
            character = roots[manifest.Systems.Character.Root] as Control
                ?? throw new InvalidDataException("Native HUD ContextCharacter root must be Control");
            targetSelection = roots[decalBinding.Id] as Node3D
                ?? throw new InvalidDataException("Native HUD TargetSelection root must be Node3D");
            foreach (HudRootBinding binding in manifest.Roots.Where(binding => !binding.DecalOnly))
            {
                if (!ReferenceEquals(roots[binding.Id], root)
                    && !root.IsAncestorOf(roots[binding.Id]))
                {
                    throw new InvalidDataException(
                        $"Native HUD root '{binding.NativeRole}' must stay beneath Main");
                }
            }

            Detach(targetSelection);
            if (!ReferenceEquals(instance, root))
            {
                Detach(root);
                instance.Free();
                instance = null;
            }

            root.Name = "NativeGameplayHud";
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.Visible = false;
            character.Visible = false;
            targetSelection.Visible = false;
            IReadOnlyDictionary<string, Node> roles = ResolveSemanticRoles(roots, manifest);
            IReadOnlyList<string> actionSlots = manifest.Systems.ActionBar.Slots.Select(slot => slot.Action.Id).ToArray();
            IReadOnlyList<string> questRows = manifest.Systems.QuestTracker.Rows.Select(row => row.Id).ToArray();
            IReadOnlyDictionary<string, IReadOnlyList<string>> feedbackPools = BuildFeedbackPools(manifest);

            IReadOnlyDictionary<string, NativeHudCursor> cursors = LoadCursors(paths, manifest, resources);
            IReadOnlyDictionary<string, AudioStream> sounds = LoadSounds(paths, manifest, resources);
            IReadOnlyDictionary<string, NativeHudMaskSet> masks = LoadMasks(paths, manifest, roles, resources);
            ownedMasks.AddRange(masks.Values);
            IReadOnlyList<NativeHudInputRole> inputRoles = manifest.InputRoles
                .OrderByDescending(binding => binding.Priority)
                .Select(binding => new NativeHudInputRole(
                    binding.Role,
                    binding.Priority,
                    binding.Routes.ToDictionary(route => route.Input, route => route.Event)))
                .ToArray();
            IReadOnlyDictionary<string, HudSemanticInputBinding> inputBindings = manifest.InputBindings
                .ToDictionary(binding => binding.Input, StringComparer.Ordinal);
            IReadOnlyDictionary<(string Role, HudSoundEvent Event), string> soundBindings =
                manifest.SoundBindings.ToDictionary(
                    binding => (binding.Role, binding.Event),
                    binding => binding.Sound);
            IReadOnlyList<NativeHudOvertipSlot> overtipSlots = PreMaterializeOvertips(
                manifest,
                roles,
                product.MaxOvertips);

            var content = new NativeHudContent(
                root,
                character,
                targetSelection,
                roles,
                inputRoles,
                inputBindings,
                actionSlots,
                questRows,
                feedbackPools,
                masks,
                cursors,
                sounds,
                soundBindings,
                overtipSlots,
                manifest,
                product,
                resources.ToArray());
            root = null;
            character = null;
            targetSelection = null;
            resources.Clear();
            ownedMasks.Clear();
            return content;
        }
        catch
        {
            instance?.Free();
            root?.Free();
            targetSelection?.Free();
            foreach (Resource resource in resources)
            {
                resource.Dispose();
            }

            foreach (NativeHudMaskSet mask in ownedMasks)
            {
                mask.Dispose();
            }

            throw;
        }
    }

    public void AttachTo(Control owner, INativeHudWorldScene world)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(world);
        if (_attached || Root.GetParent() is not null || TargetSelection.GetParent() is not null)
        {
            throw new InvalidOperationException("Native HUD content is already attached");
        }

        try
        {
            world.MountTargetSelection(TargetSelection);
            owner.AddChild(Root);
            _attached = true;
            Root.Visible = true;
        }
        catch
        {
            Root.Visible = false;
            Detach(Root);
            Detach(TargetSelection);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Root.Free();
        TargetSelection.Free();
        foreach (Resource resource in _resources)
        {
            resource.Dispose();
        }

        foreach (NativeHudMaskSet mask in Masks.Values)
        {
            mask.Dispose();
        }
    }

    private static IReadOnlyDictionary<string, Node> ResolveRoots(
        Node scene,
        IReadOnlyList<HudRootBinding> bindings)
    {
        var result = new Dictionary<string, Node>(StringComparer.Ordinal);
        HudRootBinding mainBinding = bindings.Single(binding => binding.Id == "world-input");
        Node main = scene.GetNodeOrNull(mainBinding.NativeRole)
            ?? throw new InvalidDataException(
                $"Native HUD scene has no direct root named '{mainBinding.NativeRole}'");
        foreach (HudRootBinding binding in bindings)
        {
            Node? resolved = binding.Id == mainBinding.Id
                ? main
                : binding.DecalOnly
                    ? scene.GetNodeOrNull(binding.NativeRole)
                    : main.GetNodeOrNull(binding.NativeRole);
            if (resolved is null)
            {
                throw new InvalidDataException(
                    $"Native HUD scene has no authored root named '{binding.NativeRole}'");
            }
            if (binding.DecalOnly && !ReferenceEquals(resolved.GetParent(), scene)
                || !binding.DecalOnly && binding.Id != mainBinding.Id &&
                    !ReferenceEquals(resolved.GetParent(), main))
            {
                throw new InvalidDataException(
                    $"Native HUD root '{binding.NativeRole}' is outside the authored topology");
            }

            if (!result.TryAdd(binding.Id, resolved))
            {
                throw new InvalidDataException(
                    $"Native HUD semantic root '{binding.Id}' is duplicated");
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, Node> ResolveSemanticRoles(
        IReadOnlyDictionary<string, Node> roots,
        HudProductManifest manifest)
    {
        var result = new Dictionary<string, Node>(StringComparer.Ordinal);
        AddRoles(result, roots[manifest.Systems.WorldInput.Root], manifest.Systems.WorldInput.Roles);
        foreach (HudActionSlotBinding slot in manifest.Systems.ActionBar.Slots)
        {
            AddRole(result, roots[manifest.Systems.ActionBar.Root], new HudSemanticRole(slot.Id, slot.Presentation.Role));
            AddRole(result, roots[manifest.Systems.ActionBar.Root], slot.Presentation);
            AddRole(result, roots[manifest.Systems.ActionBar.Root], slot.Action);
        }
        AddRoles(result, roots[manifest.Systems.UnitPlates.Root], manifest.Systems.UnitPlates.Roles);
        AddRoles(result, roots[manifest.Systems.UnitPlates.Root], manifest.Systems.UnitPlates.Plates);
        AddRole(result, roots[manifest.Systems.Overtips.Root], manifest.Systems.Overtips.Prototype);
        AddRoles(result, roots[manifest.Systems.Overtips.Root], manifest.Systems.Overtips.Roles);
        AddRoles(result, roots[manifest.Systems.CombatFeedback.Root], manifest.Systems.CombatFeedback.Pools.Avatar.Lanes);
        AddRoles(result, roots[manifest.Systems.CombatFeedback.Root], manifest.Systems.CombatFeedback.Pools.Enemy.Lanes);
        AddRoles(result, roots[manifest.Systems.CombatFeedback.Root], manifest.Systems.CombatFeedback.Pools.Experience.Lanes);
        AddRoles(result, roots[manifest.Systems.Compass.Root], manifest.Systems.Compass.Roles);
        AddRoles(result, roots[manifest.Systems.Chat.Root], manifest.Systems.Chat.Roles);
        AddRoles(result, roots[manifest.Systems.ChatInput.Root], manifest.Systems.ChatInput.Roles);
        AddRoles(result, roots[manifest.Systems.WorldChatBubbles.Root], manifest.Systems.WorldChatBubbles.Roles);
        AddRoles(result, roots[manifest.Systems.QuestTracker.Root], manifest.Systems.QuestTracker.Roles);
        foreach (HudQuestRowBinding row in manifest.Systems.QuestTracker.Rows)
        {
            AddRole(result, roots[manifest.Systems.QuestTracker.Root], new HudSemanticRole(row.Id, row.Role));
            AddRole(result, roots[manifest.Systems.QuestTracker.Root], row.Task);
            AddRole(result, roots[manifest.Systems.QuestTracker.Root], row.Toggle);
        }

        AddRole(
            result,
            roots[manifest.Systems.TargetSelection.Root],
            manifest.Systems.TargetSelection.Decal);
        AddRoles(
            result,
            roots[manifest.Systems.Character.Root],
            manifest.Systems.Character.Equipment.Select(binding => binding.Role));
        AddRoles(
            result,
            roots[manifest.Systems.Character.Root],
            manifest.Systems.Character.StatRows);
        AddRoles(result, roots[manifest.Systems.Character.Root], manifest.Systems.Character.Roles);

        HudMultiBagSystem multibag = manifest.Systems.MultiBag;
        AddRoles(result, roots[multibag.Root], multibag.Roles);
        foreach (HudInventoryLayoutBinding layout in multibag.Layouts)
        {
            AddRole(result, roots[multibag.Root], new HudSemanticRole(layout.Id, layout.Role));
            foreach (HudInventoryPartitionBinding partition in layout.Partitions)
            {
                AddRole(result, roots[multibag.Root], new HudSemanticRole(partition.Id, partition.Role));
                foreach (HudInventorySlotBinding slot in partition.Slots)
                {
                    AddRole(result, roots[multibag.Root], new HudSemanticRole(slot.Id, slot.Role));
                    AddRole(result, roots[multibag.Root], slot.Icon);
                    AddRole(result, roots[multibag.Root], slot.Cooldown);
                    AddRole(result, roots[multibag.Root], slot.Count);
                    AddRole(result, roots[multibag.Root], slot.Prepared);
                }
            }
        }

        HudLootBagSystem loot = manifest.Systems.LootBag;
        AddRole(result, roots[loot.Root], loot.Prototype);
        AddRoles(result, roots[loot.Root], loot.Roles);
        foreach (HudLootItemBinding item in loot.Items)
        {
            AddRole(result, roots[loot.Root], new HudSemanticRole(item.Id, item.Role));
            AddRole(result, roots[loot.Root], item.Slot);
            AddRole(result, roots[loot.Root], item.Name);
            AddRole(result, roots[loot.Root], item.Icon);
            AddRole(result, roots[loot.Root], item.Count);
        }

        HudQuestLogSystem questLog = manifest.Systems.QuestLog;
        AddRole(result, roots[questLog.Root], questLog.List);
        AddRole(result, roots[questLog.Root], questLog.EntryPrototype);
        AddRoles(result, roots[questLog.Root], questLog.Bookmarks.Select(bookmark => bookmark.Role));
        AddRole(result, roots[questLog.Root], questLog.FolderToggle.Role);
        AddRoles(result, roots[questLog.Root], questLog.Roles);
        foreach (HudQuestLogRowBinding row in questLog.Rows)
        {
            AddRole(result, roots[questLog.Root], new HudSemanticRole(row.Id, row.Role));
            AddRoles(result, roots[questLog.Root], row.Roles);
        }
        AddQuestDetailPools(result, roots[questLog.Root], questLog.Detail);

        HudQuestInfoSystem questInfo = manifest.Systems.QuestInfo;
        AddRole(result, roots[questInfo.Root], questInfo.Prototype);
        AddRoles(result, roots[questInfo.Root], questInfo.Roles);

        HudNpcTalkSystem npcTalk = manifest.Systems.NpcTalk;
        AddRole(result, roots[npcTalk.Root], npcTalk.OptionPrototype);
        AddRoles(result, roots[npcTalk.Root], npcTalk.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.Options.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.Objectives.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.Reputation.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.Currencies.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.AlternativeTexts.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.MandatoryTexts.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.AlternativeIcons.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.MandatoryIcons.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.AlternativeButtons.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.MandatoryButtons.Roles);
        AddRoles(result, roots[npcTalk.Root], npcTalk.RewardGroups.Roles);

        HudMessageBoxSystem messageBox = manifest.Systems.MessageBox;
        Node messageBoxRoot = roots[messageBox.Root];
        AddRole(result, messageBoxRoot, messageBox.Prototypes.MessageBox);
        AddRole(result, messageBoxRoot, messageBox.Prototypes.Header);
        AddRole(result, messageBoxRoot, messageBox.Prototypes.Text);
        AddRole(result, messageBoxRoot, messageBox.Prototypes.Progress);
        AddRole(result, messageBoxRoot, messageBox.Prototypes.ButtonTab);
        AddRole(result, messageBoxRoot, messageBox.Prototypes.ButtonContainer);
        AddRole(result, messageBoxRoot, messageBox.Prototypes.Accept);
        AddRole(result, messageBoxRoot, messageBox.Prototypes.Decline);
        AddRole(result, messageBoxRoot, messageBox.Prototypes.Confirm);
        foreach (HudMessageBoxInstance instance in messageBox.Instances)
        {
            AddRole(result, messageBoxRoot, new HudSemanticRole(instance.Id, instance.Role));
            AddRole(result, messageBoxRoot, instance.Title);
            AddRole(result, messageBoxRoot, instance.Body);
            AddRole(result, messageBoxRoot, instance.Icon);
            AddRole(result, messageBoxRoot, instance.Progress);
            AddRole(result, messageBoxRoot, instance.TimerLabel);
            AddRole(result, messageBoxRoot, instance.ButtonTab);
            AddRole(result, messageBoxRoot, instance.ButtonContainer);
            AddRole(result, messageBoxRoot, instance.Accept.Role);
            AddRole(result, messageBoxRoot, instance.Decline.Role);
            AddRole(result, messageBoxRoot, instance.Confirm.Role);
        }

        foreach (HudInputRoleBinding inputRole in manifest.InputRoles)
        {
            if (!result.TryGetValue(inputRole.Role, out Node? inputNode)
                || inputNode is not Control)
            {
                throw new InvalidDataException(
                    $"Native HUD input role '{inputRole.Role}' has no resolved Control");
            }
        }

        return result;
    }

    private static void AddQuestDetailPools(
        IDictionary<string, Node> result,
        Node root,
        HudQuestDetailPools pools)
    {
        AddRoles(result, root, pools.Objectives.Roles);
        AddRoles(result, root, pools.ObjectiveNoNumbers.Roles);
        AddRoles(result, root, pools.Reputation.Roles);
        AddRoles(result, root, pools.Currencies.Roles);
        AddRoles(result, root, pools.AlternativeTexts.Roles);
        AddRoles(result, root, pools.MandatoryTexts.Roles);
        AddRoles(result, root, pools.AlternativeIcons.Roles);
        AddRoles(result, root, pools.MandatoryIcons.Roles);
        AddRoles(result, root, pools.Secrets.Roles);
    }

    private static void AddRoles(
        IDictionary<string, Node> result,
        Node root,
        IEnumerable<HudSemanticRole> roles)
    {
        foreach (HudSemanticRole role in roles)
        {
            AddRole(result, root, role);
        }
    }

    private static void AddRole(
        IDictionary<string, Node> result,
        Node root,
        HudSemanticRole role)
    {
        Node? node = role.Role.Contains('/', StringComparison.Ordinal)
            ? root.GetNodeOrNull(role.Role)
            : FindUniqueByName(root, role.Role);
        if (node is null && role.Role.Contains('/', StringComparison.Ordinal))
        {
            node = FindUniqueBySemanticPath(root, role.Role);
        }
        if (node is null)
        {
            throw new InvalidDataException(
                $"Native HUD role '{role.Id}' has no node at '{root.Name}/{role.Role}'");
        }

        if (!result.TryAdd(role.Id, node))
        {
            throw new InvalidDataException($"Native HUD role '{role.Id}' is duplicated");
        }
    }

    private static Node? FindUniqueBySemanticPath(Node root, string path)
    {
        string[] expected = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (expected.Length == 0)
        {
            return null;
        }

        Node? match = null;
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Node candidate = pending.Pop();
            if (candidate.Name == expected[^1] && MatchesSemanticPath(root, candidate, expected))
            {
                if (match is not null)
                {
                    return null;
                }

                match = candidate;
            }

            foreach (Node child in candidate.GetChildren())
            {
                pending.Push(child);
            }
        }

        return match;
    }

    private static bool MatchesSemanticPath(Node root, Node candidate, IReadOnlyList<string> expected)
    {
        int expectedIndex = expected.Count - 1;
        for (Node? node = candidate; node is not null && !ReferenceEquals(node, root); node = node.GetParent())
        {
            if (node.Name == expected[expectedIndex])
            {
                expectedIndex--;
                if (expectedIndex < 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<NativeHudOvertipSlot> PreMaterializeOvertips(
        HudProductManifest manifest,
        IReadOnlyDictionary<string, Node> roles,
        int capacity)
    {
        if (roles[manifest.Systems.Overtips.Prototype.Id] is not Control prototype
            || prototype.GetParent() is not Node parent)
        {
            throw new InvalidDataException("Native HUD overtip prototype must be a parented Control");
        }

        prototype.Visible = false;
        var result = new NativeHudOvertipSlot[capacity];
        string prefix = manifest.Systems.Overtips.Prototype.Role + "/";
        for (int lane = 0; lane < capacity; lane++)
        {
            if (prototype.Duplicate() is not Control clone)
            {
                throw new InvalidDataException("Native HUD overtip prototype did not duplicate as Control");
            }

            clone.Name = $"OvertipRuntime{lane:000}";
            clone.Visible = false;
            parent.AddChild(clone);
            var cloneRoles = new Dictionary<string, CanvasItem>(StringComparer.Ordinal);
            foreach (HudSemanticRole role in manifest.Systems.Overtips.Roles)
            {
                if (!role.Role.StartsWith(prefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Native HUD overtip child '{role.Id}' escapes its prototype");
                }

                string childPath = role.Role[prefix.Length..];
                CanvasItem child = clone.GetNodeOrNull<CanvasItem>(childPath)
                    ?? throw new InvalidDataException(
                        $"Native HUD overtip clone is missing '{childPath}'");
                cloneRoles.Add(role.Id, child);
            }

            result[lane] = new NativeHudOvertipSlot(clone, cloneRoles);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, NativeHudCursor> LoadCursors(
        NativeHudContentPaths paths,
        HudProductManifest manifest,
        ICollection<Resource> resources)
    {
        var result = new Dictionary<string, NativeHudCursor>(StringComparer.Ordinal);
        foreach (HudCursorResource entry in manifest.Cursors)
        {
            Texture2D texture = LoadRequired<Texture2D>(paths.Resolve(entry.Resource, ".res"), $"cursor '{entry.Id}'");
            resources.Add(texture);
            if (texture.GetWidth() != entry.Dimensions.Width || texture.GetHeight() != entry.Dimensions.Height)
            {
                throw new InvalidDataException($"Native HUD cursor '{entry.Id}' dimensions disagree with its manifest");
            }

            var hotspot = new Vector2I(entry.Hotspot.X, entry.Hotspot.Y);
            if (hotspot.X < 0 || hotspot.Y < 0 || hotspot.X >= texture.GetWidth() || hotspot.Y >= texture.GetHeight())
            {
                throw new InvalidDataException($"Native HUD cursor '{entry.Id}' hotspot is outside its texture");
            }

            result.Add(entry.Id, new NativeHudCursor(texture, hotspot));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, AudioStream> LoadSounds(
        NativeHudContentPaths paths,
        HudProductManifest manifest,
        ICollection<Resource> resources)
    {
        var result = new Dictionary<string, AudioStream>(StringComparer.Ordinal);
        foreach (HudCatalogResource entry in manifest.Sounds)
        {
            AudioStream sound = LoadRequired<AudioStream>(paths.Resolve(entry.Resource, ".res"), $"sound '{entry.Id}'");
            resources.Add(sound);
            result.Add(entry.Id, sound);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, NativeHudMaskSet> LoadMasks(
        NativeHudContentPaths paths,
        HudProductManifest manifest,
        IReadOnlyDictionary<string, Node> roles,
        ICollection<Resource> resources)
    {
        var images = new Dictionary<string, NativeHudPixelMask>(StringComparer.Ordinal);
        foreach (HudMaskResource entry in manifest.Masks)
        {
            Texture2D texture = LoadRequired<Texture2D>(paths.Resolve(entry.Resource, ".res"), $"mask '{entry.Id}'");
            resources.Add(texture);
            if (texture.GetWidth() != entry.Dimensions.Width || texture.GetHeight() != entry.Dimensions.Height)
            {
                throw new InvalidDataException($"Native HUD mask '{entry.Id}' dimensions disagree with its manifest");
            }

            using Image source = texture.GetImage();
            if (source.IsEmpty())
            {
                throw new InvalidDataException($"Native HUD mask '{entry.Id}' is empty");
            }

            images.Add(entry.Id, new NativeHudPixelMask((Image)source.Duplicate()));
        }

        var result = new Dictionary<string, NativeHudMaskSet>(StringComparer.Ordinal);
        foreach (IGrouping<string, HudMaskBinding> group in manifest.MaskBindings.GroupBy(binding => binding.Role, StringComparer.Ordinal))
        {
            if (!roles.ContainsKey(group.Key))
            {
                throw new InvalidDataException($"Native HUD mask binding references unknown role '{group.Key}'");
            }

            NativeHudPixelMask? clip = null;
            NativeHudPixelMask? pick = null;
            foreach (HudMaskBinding binding in group)
            {
                NativeHudPixelMask image = images[binding.Mask];
                if (binding.Kind == HudMaskKind.Clip)
                {
                    clip = image;
                }
                else
                {
                    pick = image;
                }
            }

            result.Add(group.Key, new NativeHudMaskSet { Clip = clip, Pick = pick });
        }

        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildFeedbackPools(HudProductManifest manifest) =>
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["avatar"] = manifest.Systems.CombatFeedback.Pools.Avatar.Lanes.Select(role => role.Id).ToArray(),
            ["enemy"] = manifest.Systems.CombatFeedback.Pools.Enemy.Lanes.Select(role => role.Id).ToArray(),
            ["experience"] = manifest.Systems.CombatFeedback.Pools.Experience.Lanes.Select(role => role.Id).ToArray(),
        };

    private static HudRootBinding RequireRoot(
        IReadOnlyList<HudRootBinding> roots,
        string id,
        bool decalOnly)
    {
        HudRootBinding binding = roots.Single(root => root.Id == id);
        if (binding.DecalOnly != decalOnly)
        {
            throw new InvalidDataException($"Native HUD root '{id}' has the wrong mount kind");
        }

        return binding;
    }

    private static Node? FindUniqueByName(Node root, string name)
    {
        Node? match = null;
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            if (node.Name == name)
            {
                if (match is not null)
                {
                    return null;
                }

                match = node;
            }

            foreach (Node child in node.GetChildren())
            {
                pending.Push(child);
            }
        }

        return match;
    }

    private static void Detach(Node node)
    {
        node.GetParent()?.RemoveChild(node);
    }

    private static T LoadRequired<T>(string path, string kind) where T : Resource
    {
        if (!Godot.FileAccess.FileExists(path))
        {
            throw new FileNotFoundException($"Native HUD {kind} is missing: {path}");
        }

        return ResourceLoader.Load<T>(path)
            ?? throw new InvalidDataException($"Native HUD {kind} is not loadable: {path}");
    }
}

internal sealed class NativeHudPixelMask(Image image) : IDisposable
{
    public float SampleAlpha(Vector2 normalized)
    {
        if (!float.IsFinite(normalized.X) || !float.IsFinite(normalized.Y)
            || normalized.X is < 0.0f or > 1.0f
            || normalized.Y is < 0.0f or > 1.0f)
        {
            return 0.0f;
        }

        int x = Math.Min((int)(normalized.X * image.GetWidth()), image.GetWidth() - 1);
        int y = Math.Min((int)(normalized.Y * image.GetHeight()), image.GetHeight() - 1);
        return image.GetPixel(x, y).A;
    }

    public void Dispose() => image.Dispose();
}
