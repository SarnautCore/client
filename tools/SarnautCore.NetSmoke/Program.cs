using Sarnaut.Protocol.V1;
using SarnautCore.Gameplay;
using SarnautCore.Networking;
using SarnautCore.Shell;

GameEndpoint endpoint = GameEndpoint.Parse(ArgumentValue(args, "--address") ?? "127.0.0.1:4242");
string zoneId = ArgumentValue(args, "--zone") ?? "InstLeague1";
double durationSeconds = double.TryParse(ArgumentValue(args, "--duration"), out double parsedDuration)
    ? parsedDuration
    : 8;
string? authAddress = ArgumentValue(args, "--auth");

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
Secret ticket = new(ArgumentValue(args, "--ticket") ?? string.Empty);
string chargenSpawn = "none";
try
{
    if (authAddress is not null)
    {
        // The whole out-of-band half of the session, driven through the very
        // view models the screens bind to (session spec rule 5.3). Nothing here
        // is a test double: it is the shipped shell without a scene tree.
        (ticket, chargenSpawn) = await AdmitAsync(authAddress, args, timeout.Token);
    }

    await using GameSession session = await GameSession.ConnectAsync(
        endpoint,
        zoneId,
        "sar20-smoke",
        allowUntrustedDevelopmentCertificate: true,
        packId: ArgumentValue(args, "--pack") ?? string.Empty,
        ticket: ticket.Reveal(),
        cancellationToken: timeout.Token);

    ulong ownEntityId = session.EnteredZone.OwnEntityId;
    Vec3 spawn = session.EnteredZone.SpawnPosition ?? new Vec3();
    if (HasFlag(args, "--gameplay"))
    {
        string gameplay = await RunGameplayAsync(session, ownEntityId, timeout.Token);
        await session.SendLogoutAsync(timeout.Token);
        Console.WriteLine($"M2_GAMEPLAY_LIVE result=PASS entity={ownEntityId} {gameplay}");
        return 0;
    }

    float startX = spawn.X;
    var advanced = new TaskCompletionSource<(float X, ulong Tick, bool Alive)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    Task receiveTask = ReceiveUntilAdvanced(session, ownEntityId, startX, advanced, timeout.Token);

    using var movementTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
    ulong sequence = 0;
    while (!advanced.Task.IsCompleted && await movementTimer.WaitForNextTickAsync(timeout.Token))
    {
        await session.SendMoveIntentAsync(new ClientMoveIntent
        {
            Seq = ++sequence,
            Input = new Vec3 { X = 1 },
            Heading = 0,
            DtSeconds = 0.1f,
        }, timeout.Token);
    }

    (float advancedX, ulong serverTick, bool alive) = await advanced.Task.WaitAsync(timeout.Token);

    // A clean exit through the envelope's logout verb, before the connection is
    // torn down, so the shard's teardown runs rather than races.
    await session.SendLogoutAsync(timeout.Token);

    timeout.Cancel();
    try
    {
        await receiveTask;
    }
    catch (OperationCanceledException)
    {
    }

    Console.WriteLine(
        $"SAR20_NET_SMOKE result=PASS transport=quic-stream envelope=v1 entity={ownEntityId} " +
        $"spawn={spawn.X:F3},{spawn.Y:F3},{spawn.Z:F3} chargen_spawn={chargenSpawn} " +
        $"start_x={startX:F3} advanced_x={advancedX:F3} alive={alive} " +
        $"server_tick={serverTick} intents={sequence}");
    return 0;
}
catch (AuthException exception)
{
    Console.Error.WriteLine($"SAR20_NET_SMOKE result=FAIL failure={exception.Failure} error={exception.Message}");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"SAR20_NET_SMOKE result=FAIL error={exception.Message}");
    return 1;
}

// Register or sign in, make sure the named character exists, and mint the
// single-use shard ticket for it.
static async Task<(Secret Ticket, string ChargenSpawn)> AdmitAsync(
    string authAddress,
    string[] args,
    CancellationToken cancellationToken)
{
    string email = ArgumentValue(args, "--email")
        ?? throw new AuthException(AuthFailure.InvalidCredentials, "--email is required with --auth.");
    var password = new Secret(ArgumentValue(args, "--password")
        ?? throw new AuthException(AuthFailure.InvalidCredentials, "--password is required with --auth."));
    string characterName = ArgumentValue(args, "--character")
        ?? throw new AuthException(AuthFailure.NameInvalid, "--character is required with --auth.");

    AuthClient auth = AuthClient.Create(new Uri(authAddress));
    var player = new PlayerSession();
    var flow = new ScreenFlow();

    flow.BeginSignIn();
    var login = new LoginViewModel(auth, player) { Email = email, Password = password };
    if (!await login.SignInAsync(cancellationToken).ConfigureAwait(false))
    {
        if (login.LastFailure != AuthFailure.InvalidCredentials)
        {
            throw new AuthException(login.LastFailure ?? AuthFailure.ServiceError, login.Message);
        }

        login.Password = password;
        if (!await login.RegisterAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new AuthException(login.LastFailure ?? AuthFailure.ServiceError, login.Message);
        }
    }

    flow.SignedIn();
    var roster = new CharacterSelectViewModel(auth, player);
    if (!await roster.RefreshAsync(cancellationToken).ConfigureAwait(false))
    {
        throw new AuthException(roster.LastFailure ?? AuthFailure.ServiceError, roster.Message);
    }

    var create = new CharacterCreateViewModel(auth, player);
    if (!await create.LoadOptionsAsync(cancellationToken).ConfigureAwait(false))
    {
        throw new AuthException(create.LastFailure ?? AuthFailure.UnknownOption, create.Message);
    }

    ChargenOption option = create.Selected!;
    string chargenSpawn = $"{option.SpawnX:F3},{option.SpawnY:F3},{option.SpawnZ:F3}";
    CharacterSummary? existing = roster.Characters
        .FirstOrDefault(character => character.Name == characterName);
    if (existing is null)
    {
        flow.CreateCharacter();
        create.Name = characterName;
        CharacterSummary? created = await create.SubmitAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new AuthException(create.LastFailure ?? AuthFailure.NameInvalid, create.Message);
        flow.LeaveCreateCharacter();
        await roster.RefreshAsync(cancellationToken).ConfigureAwait(false);
        roster.SelectById(created.CharacterId);
    }
    else
    {
        roster.SelectById(existing.CharacterId);
    }

    flow.EnterWorld();
    ShardTicket? minted = await roster.EnterWorldAsync(option, cancellationToken).ConfigureAwait(false);
    if (minted is null)
    {
        flow.EnterWorldFailed();
        throw new AuthException(roster.LastFailure ?? AuthFailure.ServiceError, roster.Message);
    }

    flow.EnteredWorld();
    Console.WriteLine(
        $"SAR20_NET_SMOKE admitted account={player.Account!.AccountId} character={player.Character!.CharacterId} " +
        $"option={option.Id} flow={flow.Current}");
    return (minted.Token, chargenSpawn);
}

static async Task ReceiveUntilAdvanced(
    GameSession session,
    ulong ownEntityId,
    float startX,
    TaskCompletionSource<(float X, ulong Tick, bool Alive)> advanced,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        SnapshotBatch snapshot = await session.ReadSnapshotAsync(cancellationToken);
        EntitySnapshot? own = snapshot.Entities.FirstOrDefault(entity => entity.EntityId == ownEntityId);
        if (own?.Position is not null && own.Position.X > startX + 0.05f)
        {
            advanced.TrySetResult((own.Position.X, snapshot.ServerTick, own.Alive));
            return;
        }
    }
}

static async Task<string> RunGameplayAsync(
    GameSession session,
    ulong ownEntityId,
    CancellationToken cancellationToken)
{
    const string abilityId = "ability.melee.harbor-cleave";
    const string lootMobId = "mob.paper-harbor.copper-sparrow";
    var hud = new GameplayHudViewModel(
        ownEntityId,
        [new AbilityDefinition(abilityId, $"{abilityId}.name", string.Empty)],
        inventoryCapacity: 16,
        stackLimit: _ => 20);

    int spawnedDamageNumbers = 0;
    hud.DamageNumbers.Spawned += _ => spawnedDamageNumbers++;
    EntitySnapshot target = await ReadUntilEntityAsync(
        session,
        hud,
        entity => entity.EntityId != ownEntityId && entity.ContentId == lootMobId && entity.Alive,
        cancellationToken);
    hud.SelectTarget(ToHudSnapshot(target));

    ulong clientSequence = 0;
    int successfulCasts = 0;
    DeathEvent? death = null;
    for (int attempt = 0; attempt < 200 && death is null; attempt++)
    {
        hud.Advance(2);
        AbilityUseRequest? requested = null;
        void Capture(AbilityUseRequest request) => requested = request;
        hud.Abilities.AbilityRequested += Capture;
        bool accepted = hud.Abilities.TryRequestUse(0, target.EntityId);
        hud.Abilities.AbilityRequested -= Capture;
        if (!accepted || requested is null)
        {
            throw new InvalidOperationException("The live ability bar refused an available slot.");
        }

        await session.SendAsync(new ClientMessage
        {
            ClientSeq = ++clientSequence,
            AbilityUse = new AbilityUse
            {
                CasterId = ownEntityId,
                TargetId = requested.TargetEntityId,
                AbilityId = requested.AbilityId,
            },
        }, cancellationToken);

        while (true)
        {
            ServerMessage message = await session.ReadAsync(cancellationToken);
            hud.Route(message);
            if (message.PayloadCase == ServerMessage.PayloadOneofCase.DeathEvent
                && message.DeathEvent.VictimEntityId == target.EntityId)
            {
                death = message.DeathEvent;
                break;
            }

            if (message.PayloadCase != ServerMessage.PayloadOneofCase.CombatEvent
                || message.CombatEvent.CasterId != ownEntityId
                || message.CombatEvent.TargetId != target.EntityId)
            {
                continue;
            }

            CombatEvent combat = message.CombatEvent;
            if (combat.Rejection == AbilityRejection.OnCooldown)
            {
                await Task.Delay(100, cancellationToken);
                break;
            }

            if (combat.Rejection != AbilityRejection.None)
            {
                throw new InvalidOperationException($"Cast rejected: {combat.Rejection}.");
            }

            successfulCasts++;
            if (!combat.KillingBlow)
            {
                await Task.Delay(1050, cancellationToken);
                break;
            }
        }
    }

    if (death is null || death.KillerEntityId != ownEntityId || spawnedDamageNumbers == 0)
    {
        throw new InvalidOperationException("The live combat loop did not reach a client-observed killing blow.");
    }

    EntitySnapshot corpse = await ReadUntilEntityAsync(
        session,
        hud,
        entity => entity.EntityId != target.EntityId
            && entity.ContentId == target.ContentId
            && !entity.Alive
            && entity.MaxHealth == 0,
        cancellationToken);
    await session.SendAsync(new ClientMessage
    {
        ClientSeq = ++clientSequence,
        Interact = new Interact { TargetEntityId = corpse.EntityId },
    }, cancellationToken);

    LootOffer? offer = null;
    while (offer is null)
    {
        ServerMessage message = await session.ReadAsync(cancellationToken);
        hud.Route(message);
        if (message.PayloadCase == ServerMessage.PayloadOneofCase.LootOffer)
        {
            offer = message.LootOffer;
        }
    }

    ulong requestedCorpse = 0;
    hud.Loot.TakeRequested += entityId => requestedCorpse = entityId;
    if (!hud.Loot.RequestTake() || requestedCorpse != corpse.EntityId)
    {
        throw new InvalidOperationException("The populated live loot window did not request its corpse.");
    }

    await session.SendAsync(new ClientMessage
    {
        ClientSeq = ++clientSequence,
        LootTake = new LootTake { CorpseEntityId = requestedCorpse },
    }, cancellationToken);

    LootResult? result = null;
    InventoryUpdate? inventory = null;
    while (result is null || inventory is null)
    {
        ServerMessage message = await session.ReadAsync(cancellationToken);
        hud.Route(message);
        if (message.PayloadCase == ServerMessage.PayloadOneofCase.LootResult)
        {
            result = message.LootResult;
        }
        else if (message.PayloadCase == ServerMessage.PayloadOneofCase.InventoryUpdate)
        {
            inventory = message.InventoryUpdate;
        }
    }

    if (result.Refusal != LootRefusal.None || hud.Loot.IsOpen || hud.Inventory.OccupiedSlots == 0)
    {
        throw new InvalidOperationException(
            $"Loot did not reach the bag: refusal={result.Refusal}, slots={hud.Inventory.OccupiedSlots}.");
    }

    int units = hud.Inventory.Slots.Where(slot => slot is not null).Sum(slot => slot!.Count);
    return $"target={target.EntityId} casts={successfulCasts} damage_numbers={spawnedDamageNumbers} "
        + $"corpse={corpse.EntityId} offered_items={offer.Items.Count} bag_slots={hud.Inventory.OccupiedSlots} bag_units={units}";
}

static async Task<EntitySnapshot> ReadUntilEntityAsync(
    GameSession session,
    GameplayHudViewModel hud,
    Func<EntitySnapshot, bool> predicate,
    CancellationToken cancellationToken)
{
    while (true)
    {
        ServerMessage message = await session.ReadAsync(cancellationToken);
        hud.Route(message);
        if (message.PayloadCase != ServerMessage.PayloadOneofCase.SnapshotBatch)
        {
            continue;
        }

        EntitySnapshot? entity = message.SnapshotBatch.Entities.FirstOrDefault(predicate);
        if (entity is not null)
        {
            return entity;
        }
    }
}

static EntityHudSnapshot ToHudSnapshot(EntitySnapshot entity) => new(
    entity.EntityId,
    entity.NameKey,
    entity.ContentId,
    checked((int)entity.Level),
    entity.Health,
    entity.MaxHealth,
    entity.Alive);

static string? ArgumentValue(string[] arguments, string name)
{
    for (int index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static bool HasFlag(string[] arguments, string name) =>
    arguments.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
