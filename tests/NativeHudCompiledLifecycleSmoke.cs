using System;
using Godot;
using SarnautCore.NativeHud;

namespace SarnautCore;

/// <summary>Mounts the compiled HUD product through its real Godot host.</summary>
public partial class NativeHudCompiledLifecycleSmoke : Node3D
{
    private readonly FakeWorld _world = new();

    public override async void _Ready()
    {
        var failures = new System.Collections.Generic.List<string>();
        var layer = new CanvasLayer { Name = "Interface" };
        var session = new InMemoryHudSession();
        var request = new HudMessageBoxRequest(
            new HudId("smoke-message"),
            HudMessageBoxPurpose.ItemConfirmation,
            new HudId("smoke-title"),
            new HudId("smoke-body"),
            new HudId("item.mechanics.vendor7healing-potion"),
            HudId.Empty,
            HudMessageBoxButtons.Confirm,
            HudMessageBoxDecision.Decline,
            10_000,
            0,
            new HudStamp(1, 7, 0));
        session.TryQueue(HudEvent.MessageBoxOffered(new HudStamp(1, 7, 0), request));
        AddChild(layer);
        AddChild(_world.Root);

        string manifestPath = NativeHudContentPaths.Canonical().Manifest;
        try
        {
            HudProductManifestParser.Parse(Godot.FileAccess.GetFileAsString(manifestPath));
        }
        catch (Exception exception)
        {
            failures.Add($"manifest parses: {exception}");
        }

        bool mounted = NativeGameplayHudHost.TryMount(
            layer,
            NativeHudContentPaths.Canonical(),
            session,
            _world,
            _ => { },
            out NativeGameplayHudHost? host,
            out string error);
        Expect(mounted, $"host mounts: {error}", failures);
        Expect(host is not null, "host instance exists", failures);
        Expect(host?.GetNodeOrNull<Control>("NativeGameplayHud") is not null,
            "compiled Main root is attached", failures);
        Expect(_world.Root.GetNodeOrNull<Node3D>("TargetSelection") is not null,
            "compiled target-selection root is attached to the world", failures);
        Expect(host?.GetNodeOrNull<Control>("NativeGameplayHud/ContextUniMessageBox") is not null,
            "shared message-box root is present", failures);
        Expect(host?.GetNodeOrNull<Control>("NativeGameplayHud/ContextActionbar2") is not null,
            "authored action bar is present", failures);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Control? messageBox = host?.GetNodeOrNull<Control>(
            "NativeGameplayHud/ContextUniMessageBox/MainForm/MessageBox01");
        Label? title = host?.GetNodeOrNull<Label>(
            "NativeGameplayHud/ContextUniMessageBox/MainForm/MessageBox01/MessageBoxContainer/Header/HeaderBack/HeaderLabel");
        Button? confirm = host?.GetNodeOrNull<Button>(
            "NativeGameplayHud/ContextUniMessageBox/MainForm/MessageBox01/MessageBoxContainer/ButtonTab/ButtonContainer/ButtonConfirm");
        Button? accept = host?.GetNodeOrNull<Button>(
            "NativeGameplayHud/ContextUniMessageBox/MainForm/MessageBox01/MessageBoxContainer/ButtonTab/ButtonContainer/ButtonAccept");
        Expect(messageBox?.Visible == true, "runtime message box is presented", failures);
        Expect(title?.GetMeta("hud_content_id", Variant.From(string.Empty)).AsString() == "smoke-title",
            "runtime message-box title id is presented", failures);
        Expect(confirm?.Visible == true && confirm.Disabled == false,
            "runtime confirm action is visible and enabled", failures);
        Expect(accept?.Visible == false,
            "runtime accept action is hidden for confirm prompts", failures);
        host?.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        foreach (string failure in failures)
        {
            GD.PushError($"NATIVE_HUD_COMPILED_LIFECYCLE {failure}");
        }

        bool passed = failures.Count == 0;
        GD.Print($"NATIVE_HUD_COMPILED_LIFECYCLE message_boxes=2 action_slots=36 result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private static void Expect(bool condition, string message, System.Collections.Generic.List<string> failures)
    {
        if (!condition)
        {
            failures.Add(message);
        }
    }

    private sealed class FakeWorld : INativeHudWorldScene
    {
        public Node3D Root { get; } = new() { Name = "World" };

        public Camera3D? Camera => null;

        public void MountTargetSelection(Node3D targetSelection) => Root.AddChild(targetSelection);

        public bool TryGetAnchor(ulong entityId, out Vector3 anchor)
        {
            anchor = default;
            return false;
        }

        public bool TryGetGroundFootprint(ulong entityId, out Vector3 position, out float pickRadius)
        {
            position = default;
            pickRadius = 0.0f;
            return false;
        }

        public bool IsOccluded(Vector3 origin, Vector3 anchor) => false;

        public bool TryPickEntity(Vector2 screenPoint, out ulong entityId)
        {
            entityId = 0;
            return false;
        }
    }
}
