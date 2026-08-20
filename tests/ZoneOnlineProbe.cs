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
        string zoneId = Setting("SARNAUT_PROBE_ZONE", "InstLeague1");
        string mapName = Setting("SARNAUT_PROBE_MAP", ZoneLoader.DefaultMapName);
        double settleSeconds = double.Parse(Setting("SARNAUT_PROBE_SECONDS", "8"), System.Globalization.CultureInfo.InvariantCulture);

        SessionHost session = SessionHost.Of(this);
        GD.Print(
            $"ZONE_ONLINE_PROBE address={session.ServerAddress} zone={zoneId} "
            + $"pack={session.ContentPackId} pack_path={Setting(ContentPackIdentity.PackPathVariable, "<unset>")}");
        ShardTicket ticket = await AdmitAsync(session, CancellationToken.None);
        session.Zone = new ZoneRequest(mapName, zoneId, session.ServerAddress, Online: true, ticket.Token);

        Node zone = ResourceLoader.Load<PackedScene>(ZoneScene).Instantiate();
        AddChild(zone);

        var loop = zone.GetNodeOrNull<ZoneNetworkLoop>("NetworkLoop");
        Expect(loop != null, "the zone scene started its network loop");
        if (loop == null)
        {
            Finish(null, string.Empty, 0, 0);
            return;
        }

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

        var walker = zone.GetNode<WalkaboutController>("Walker");
        SampledEntity local = registry.LocalSample;
        Expect(
            walker.Position.DistanceTo(new Vector3(local.X, local.Z, local.Y)) < 0.5f,
            $"the controller stands where the shard says: {walker.Position} against {local.X},{local.Z},{local.Y}");

        string plate = string.Empty;
        int models = 0;
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
            }

            string text = visual.GetNode<Label3D>("Overhead/Nameplate").Text;
            if (text.Length > 0)
            {
                nameplated++;
                plate = plate.Length == 0 ? text : plate;
            }

            if (visual.GetNode<MeshInstance3D>("Overhead/HealthBar").Visible)
            {
                healthBars++;
            }
        }

        Expect(nameplated == registry.Count, $"every entity has a nameplate, {nameplated} of {registry.Count} do");
        Expect(healthBars > 0, "living entities show a health bar");
        Expect(!plate.Contains(".txt", StringComparison.OrdinalIgnoreCase), $"a nameplate is not a raw key: '{plate}'");

        Expect(loop.TryCycleTarget(out ulong tabbed), "Tab picks a target");
        Expect(registry.Contains(tabbed), $"the Tab target {tabbed} is a replicated entity");

        ulong clicked = await ClickPickAsync(loop, registry, walker, tabbed);
        Expect(clicked == tabbed, $"clicking the Tab target picks it back: got {clicked}, wanted {tabbed}");

        Finish(loop, plate, models, healthBars);
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

    private async Task<ShardTicket> AdmitAsync(SessionHost session, CancellationToken cancellationToken)
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
        return await roster.EnterWorldAsync(option, cancellationToken)
            ?? throw new AuthException(roster.LastFailure ?? AuthFailure.ServiceError, roster.Message);
    }

    private void Finish(ZoneNetworkLoop? loop, string plate, int models, int healthBars)
    {
        bool passed = _failures.Count == 0;
        int entities = loop?.Entities.Count ?? 0;
        GD.Print(
            $"ZONE_ONLINE_PROBE entities={entities} models={models} capsules={entities - models} "
            + $"health_bars={healthBars} own={loop?.OwnEntityId ?? 0} target={loop?.TargetEntityId ?? 0} "
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
}
