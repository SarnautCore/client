using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SarnautCore.Networking;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>
/// Enters a live shard, loads the real zone scene, and reports what the client
/// ended up drawing.
/// </summary>
/// <remarks>
/// <para>
/// The synthetic smoke proves the binding; this proves it against a shard that
/// is actually simulating. It signs in through the same view models the screens
/// use, presents the ticket through the same <see cref="ZoneRequest"/> the
/// character select writes, and then asks the zone's own registry what it has:
/// one visual per replicated entity, a nameplate on each, a Tab target and a
/// target picked by unprojecting an entity's own position back onto the screen.
/// </para>
/// <para>
/// It needs a shard, an account service and the infrastructure both need, so it
/// is driven by <c>scripts/m2-session-smoke.ps1 -EntityProbe</c> rather than by
/// CI.
/// </para>
/// </remarks>
public partial class ZoneOnlineProbe : Node
{
    private const string ZoneScene = "res://scenes/zone_walkabout.tscn";

    private readonly List<string> _failures = [];
    private Sarnaut.Protocol.V1.Vec3? _wireSpawn;
    private Vector3 _godotSpawn;
    private float _groundGap = float.NaN;
    private string _groundCollider = string.Empty;

    public override async void _Ready()
    {
        try
        {
            await RunAsync();
        }
        catch (Exception exception)
        {
            GD.PrintErr($"ZONE_ONLINE_PROBE result=FAIL error={exception.Message}");
            GetTree().Quit(1);
        }
    }

    private async Task RunAsync()
    {
        string expectedZoneId = Setting("SARNAUT_PROBE_ZONE", string.Empty);
        string mapName = Setting("SARNAUT_PROBE_MAP", ZoneLoader.DefaultMapName);
        double settleSeconds = double.Parse(Setting("SARNAUT_PROBE_SECONDS", "8"), System.Globalization.CultureInfo.InvariantCulture);

        SessionHost session = SessionHost.Of(this);
        GD.Print(
            $"ZONE_ONLINE_PROBE address={session.ServerAddress} expected_zone={expectedZoneId} "
            + $"pack={session.ContentPackId} pack_path={Setting(ContentPackIdentity.PackPathVariable, "<unset>")}");
        ProbeAdmission admission = await AdmitAsync(session, CancellationToken.None);
        if (expectedZoneId.Length > 0)
        {
            Expect(
                admission.Option.SpawnZoneId == expectedZoneId,
                $"auth supplied the expected session zone: {admission.Option.SpawnZoneId} against {expectedZoneId}");
        }

        session.Zone = new ZoneRequest(
            mapName,
            admission.Option.SpawnZoneId,
            session.ServerAddress,
            Online: true,
            admission.Ticket.Token);

        Node zone = ResourceLoader.Load<PackedScene>(ZoneScene).Instantiate();
        AddChild(zone);
        var walker = zone.GetNode<WalkaboutController>("Walker");
        // A windowed proof must not turn ambient keyboard/controller state into
        // movement while it waits for the first authoritative snapshot.
        walker.InputEnabled = false;
        walker.LookInputEnabled = false;

        var loop = zone.GetNodeOrNull<ZoneNetworkLoop>("NetworkLoop");
        Expect(loop != null, "the zone scene started its network loop");
        if (loop == null)
        {
            Finish(null, null, string.Empty, 0, 0, 0);
            return;
        }

        var loader = zone.GetNode<ZoneLoader>("ZoneLoader");
        int expectedTerrain = int.Parse(Setting("SARNAUT_PROBE_TERRAIN_TILES", "4"));
        int expectedVisuals = int.Parse(Setting("SARNAUT_PROBE_VISUAL_OBJECTS", "36"));
        Expect(
            loader.TerrainTileCount == expectedTerrain,
            $"the full map terrain loaded: {loader.TerrainTileCount} of {expectedTerrain} tiles");
        Expect(
            loader.VisualObjectCount == expectedVisuals,
            $"all non-NPC map visuals loaded: {loader.VisualObjectCount} of {expectedVisuals}");
        Expect(!loader.UsedFlatTerrainFallback, "native terrain loaded without a flat fallback");
        int layeredTerrain = CountLayeredTerrainTiles(loader);
        Expect(
            layeredTerrain == expectedTerrain,
            $"every terrain tile uses separate layered up/down materials: {layeredTerrain} of {expectedTerrain}");

        var worldEnvironment = zone.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        var sun = zone.GetNodeOrNull<DirectionalLight3D>("Sun");
        Expect(worldEnvironment?.Environment != null, "the zone has an active world environment");
        Expect(
            worldEnvironment?.Environment?.AmbientLightSource == Godot.Environment.AmbientSource.Color,
            "the zone ambient color is active without requiring a missing sky resource");
        Expect(sun?.ShadowEnabled == true, "the zone has a shadow-casting directional sun");

        // The shard has to be given time to subscribe this client and send a
        // few ticks; one frame proves nothing.
        await ToSignal(GetTree().CreateTimer(settleSeconds), SceneTreeTimer.SignalName.Timeout);

        EntityRegistry registry = loop.Entities;
        var entityRoot = zone.GetNodeOrNull<Node3D>("NetworkEntities");
        int visuals = entityRoot?.GetChildCount() ?? 0;
        Expect(registry.Count > 0, $"the shard replicated entities, saw {registry.Count}");
        Expect(
            visuals == registry.Count,
            $"exactly one visual per entity: {visuals} visuals for {registry.Count} entities");
        Expect(loop.OwnEntityId != 0, "the shard named this client's own entity");
        Expect(registry.HasLocalSample, "the client's own entity arrives in the snapshot");
        Expect(!registry.Contains(loop.OwnEntityId), "the local player has no second visual of its own");

        SampledEntity local = registry.LocalSample;
        Vector3 authoritativePosition = OnlineCoordinateFrame.ToGodot(local);
        _wireSpawn = loop.EnteredSpawnPosition;
        Expect(_wireSpawn != null, "the client retained the actual EnterZoneResponse spawn");
        if (_wireSpawn != null)
        {
            _godotSpawn = OnlineCoordinateFrame.ToGodot(_wireSpawn);
            Expect(
                authoritativePosition.DistanceTo(_godotSpawn) < 0.001f,
                $"the first local snapshot matches EnterZoneResponse: {authoritativePosition} against {_godotSpawn}");
            Expect(
                walker.AuthoritativeNetworkPosition.DistanceTo(_godotSpawn) < 0.001f,
                $"the controller maps the actual EnterZoneResponse: {walker.AuthoritativeNetworkPosition} against {_godotSpawn}");
        }

        Expect(
            walker.Position.DistanceTo(authoritativePosition) < 0.5f,
            $"the controller stands where the shard says: {walker.Position} against {authoritativePosition}");
        Expect(
            walker.AuthoritativeNetworkPosition.DistanceTo(authoritativePosition) < 0.001f,
            $"the controller preserves the unmodified shard position: {walker.AuthoritativeNetworkPosition}");
        float horizontalError = new Vector2(
            walker.Position.X - authoritativePosition.X,
            walker.Position.Z - authoritativePosition.Z).Length();
        Expect(
            horizontalError < 0.001f,
            $"grounding preserves the shard's horizontal position: error {horizontalError:F4}");
        Expect(
            Math.Abs(walker.NetworkGroundingAdjustment) <= walker.NetworkGroundingDistance,
            $"grounding adjustment stays local: {walker.NetworkGroundingAdjustment:F3}");
        await ExpectGroundedAsync(walker);

        string plate = string.Empty;
        int models = 0;
        int animatedModels = 0;
        int nameplated = 0;
        int healthBars = 0;
        foreach (ulong entityId in registry.Ids)
        {
            if (!registry.TryGet(entityId, out TrackedEntity? tracked)
                || tracked.Visual is not NetworkEntityVisual visual)
            {
                continue;
            }

            if (visual.HasModel)
            {
                models++;
                if (visual.IsAnimationPlaying && visual.ActiveClip.Length > 0)
                {
                    animatedModels++;
                }
            }

            string text = visual.GetNode<Label3D>("Overhead/Nameplate").Text;
            if (text.Length > 0)
            {
                nameplated++;
                if (plate.Length == 0)
                {
                    plate = text;
                }

                // Check every nameplate to ensure no path separators or path-like patterns leak.
                ValidateNameplateText(text);
            }

            if (visual.GetNode<MeshInstance3D>("Overhead/HealthBar").Visible)
            {
                healthBars++;
            }
        }

        Expect(nameplated == registry.Count, $"every entity has a nameplate, {nameplated} of {registry.Count} do");
        Expect(models == registry.Count,
            $"every replicated tutorial entity has a converted model, {models} of {registry.Count} do");
        Expect(healthBars > 0, "living entities show a health bar");

        ConvertedCharacter localCharacter = zone.GetNode<ConvertedCharacter>("Walker/Character");
        Expect(localCharacter.HasConvertedModel, "the local player uses a converted character model");
        bool localAnimated = localCharacter.HasConvertedModel
            && localCharacter.IsAnimationPlaying
            && localCharacter.ActiveClip.Length > 0;
        Expect(
            localAnimated || animatedModels > 0,
            $"at least one converted actor is actively animated: local={localAnimated}, remote={animatedModels}/{models}");

        Expect(loop.TryCycleTarget(out ulong tabbed), "Tab picks a target");
        Expect(registry.Contains(tabbed), $"the Tab target {tabbed} is a replicated entity");

        ulong clicked = await ClickPickAsync(loop, registry, walker, tabbed);
        Expect(clicked == tabbed, $"clicking the Tab target picks it back: got {clicked}, wanted {tabbed}");

        await CaptureScreenshotAsync();

        Finish(loop, loader, plate, models, animatedModels, healthBars);
    }

    /// <summary>
    /// Validates that a nameplate text does not contain path separators or
    /// appear to be a raw path. Fails expectations if the text looks like a
    /// content-id path.
    /// </summary>
    private void ValidateNameplateText(string text)
    {
        Expect(!text.Contains('/', StringComparison.Ordinal), $"nameplate does not contain '/' path separator: '{text}'");
        Expect(!text.Contains('\\', StringComparison.Ordinal), $"nameplate does not contain '\\' path separator: '{text}'");
        Expect(!text.Contains(".txt", StringComparison.OrdinalIgnoreCase), $"nameplate is not a raw key: '{text}'");
    }

    private static int CountLayeredTerrainTiles(ZoneLoader loader)
    {
        int count = 0;
        Node terrain = loader.GetNode("Terrain");
        foreach (Node tile in terrain.GetChildren())
        {
            var up = tile.GetNodeOrNull<MeshInstance3D>("Up");
            var down = tile.GetNodeOrNull<MeshInstance3D>("Down");
            if (up?.MaterialOverride is ShaderMaterial && down?.MaterialOverride is ShaderMaterial)
            {
                count++;
            }
        }

        return count;
    }

    private async Task ExpectGroundedAsync(WalkaboutController walker)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var collision = walker.GetNode<CollisionShape3D>("CollisionShape3D");
        if (collision.Shape is not CapsuleShape3D capsule)
        {
            Expect(false, "the local player's grounding shape is a capsule");
            return;
        }

        float feetY = collision.GlobalPosition.Y - (capsule.Height * 0.5f * collision.GlobalBasis.Scale.Y);
        Vector3 start = new(walker.GlobalPosition.X, feetY + 0.15f, walker.GlobalPosition.Z);
        Vector3 end = new(walker.GlobalPosition.X, feetY - 0.5f, walker.GlobalPosition.Z);
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(start, end, collisionMask: 1);
        query.Exclude = [walker.GetRid()];
        Godot.Collections.Dictionary hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        Expect(hit.Count > 0, $"authored support collision exists immediately below the player's feet at {walker.GlobalPosition}");
        if (hit.Count == 0 || !hit.TryGetValue("position", out Variant positionValue))
        {
            return;
        }

        Vector3 ground = positionValue.AsVector3();
        float gap = feetY - ground.Y;
        _groundGap = gap;
        _groundCollider = hit.TryGetValue("collider", out Variant colliderValue)
            && colliderValue.AsGodotObject() is Node collider
                ? collider.GetPath().ToString()
                : string.Empty;
        Expect(Math.Abs(gap) <= 0.08f, $"the player's feet contact support: vertical gap {gap:F3} at {walker.GlobalPosition}");
    }

    /// <summary>
    /// Turns the player towards an entity, projects that entity's own position
    /// back onto the screen, and asks the zone what is under that point.
    /// </summary>
    private async Task<ulong> ClickPickAsync(
        ZoneNetworkLoop loop,
        EntityRegistry registry,
        WalkaboutController walker,
        ulong entityId)
    {
        if (!registry.TryGet(entityId, out TrackedEntity? tracked)
            || tracked.Visual is not NetworkEntityVisual visual)
        {
            return 0;
        }

        Vector3 towards = visual.GlobalPosition - walker.GlobalPosition;
        walker.Rotation = new Vector3(0, Mathf.Atan2(-towards.X, -towards.Z), 0);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        Camera3D? camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            return 0;
        }

        Vector3 aim = visual.GlobalPosition + (Vector3.Up * visual.PickCentreHeight);
        if (camera.IsPositionBehind(aim))
        {
            return 0;
        }

        return loop.TryTargetAtScreenPoint(camera.UnprojectPosition(aim), out ulong picked) ? picked : 0;
    }

    private async Task<ProbeAdmission> AdmitAsync(SessionHost session, CancellationToken cancellationToken)
    {
        string email = Setting("SARNAUT_PROBE_EMAIL", string.Empty);
        var password = new Secret(Setting("SARNAUT_PROBE_PASSWORD", string.Empty));
        string characterName = Setting("SARNAUT_PROBE_CHARACTER", string.Empty);
        if (email.Length == 0 || characterName.Length == 0)
        {
            throw new InvalidOperationException(
                "SARNAUT_PROBE_EMAIL, SARNAUT_PROBE_PASSWORD and SARNAUT_PROBE_CHARACTER are required.");
        }

        AuthClient auth = session.Auth;
        PlayerSession player = session.Player;
        ScreenFlow flow = session.Flow;

        flow.BeginSignIn();
        var login = new LoginViewModel(auth, player) { Email = email, Password = password };
        if (!await login.SignInAsync(cancellationToken))
        {
            login.Password = password;
            if (!await login.RegisterAsync(cancellationToken))
            {
                throw new AuthException(login.LastFailure ?? AuthFailure.ServiceError, login.Message);
            }
        }

        flow.SignedIn();
        var roster = new CharacterSelectViewModel(auth, player);
        if (!await roster.RefreshAsync(cancellationToken))
        {
            throw new AuthException(roster.LastFailure ?? AuthFailure.ServiceError, roster.Message);
        }

        var create = new CharacterCreateViewModel(auth, player);
        if (!await create.LoadOptionsAsync(cancellationToken))
        {
            throw new AuthException(create.LastFailure ?? AuthFailure.UnknownOption, create.Message);
        }

        ChargenOption option = create.Selected!;
        CharacterSummary? existing = roster.Characters.FirstOrDefault(character => character.Name == characterName);
        if (existing is null)
        {
            flow.CreateCharacter();
            create.Name = characterName;
            CharacterSummary created = await create.SubmitAsync(cancellationToken)
                ?? throw new AuthException(create.LastFailure ?? AuthFailure.NameInvalid, create.Message);
            flow.LeaveCreateCharacter();
            await roster.RefreshAsync(cancellationToken);
            roster.SelectById(created.CharacterId);
        }
        else
        {
            roster.SelectById(existing.CharacterId);
        }

        flow.EnterWorld();
        ShardTicket ticket = await roster.EnterWorldAsync(option, cancellationToken)
            ?? throw new AuthException(roster.LastFailure ?? AuthFailure.ServiceError, roster.Message);
        return new ProbeAdmission(ticket, option);
    }

    private async Task CaptureScreenshotAsync()
    {
        string path = Setting("SARNAUT_PROBE_SCREENSHOT", string.Empty);
        if (path.Length == 0)
        {
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        Error result = GetViewport().GetTexture().GetImage().SavePng(path);
        Expect(result == Error.Ok, $"the viewport screenshot saved to {path}: {result}");
    }

    private void Finish(
        ZoneNetworkLoop? loop,
        ZoneLoader? loader,
        string plate,
        int models,
        int animatedModels,
        int healthBars)
    {
        bool passed = _failures.Count == 0;
        int entities = loop?.Entities.Count ?? 0;
        GD.Print(
            $"ZONE_ONLINE_PROBE terrain={loader?.TerrainTileCount ?? 0} visuals={loader?.VisualObjectCount ?? 0} "
            + $"entities={entities} models={models} animated_models={animatedModels} capsules={entities - models} "
            + $"health_bars={healthBars} own={loop?.OwnEntityId ?? 0} target={loop?.TargetEntityId ?? 0} "
            + $"wire_spawn={Format(_wireSpawn)} godot_spawn={_godotSpawn} "
            + $"ground_gap={_groundGap:F4} ground_collider=\"{_groundCollider}\" "
            + $"nameplate=\"{plate}\" result={(passed ? "PASS" : "FAIL")}");
        foreach (string failure in _failures)
        {
            GD.PushError($"ZONE_ONLINE_PROBE {failure}");
        }

        GetTree().Quit(passed ? 0 : 1);
    }

    private void Expect(bool condition, string what)
    {
        if (!condition)
        {
            _failures.Add(what);
        }
    }

    private static string Setting(string variable, string fallback)
    {
        string? value = System.Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string Format(Sarnaut.Protocol.V1.Vec3? value) => value == null
        ? "<missing>"
        : $"({value.X:F4},{value.Y:F4},{value.Z:F4})";

    private sealed record ProbeAdmission(ShardTicket Ticket, ChargenOption Option);
}
