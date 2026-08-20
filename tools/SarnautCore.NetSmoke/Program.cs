using Sarnaut.Protocol.V1;
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
