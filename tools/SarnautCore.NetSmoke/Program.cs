using Sarnaut.Protocol.V1;
using SarnautCore.Networking;

GameEndpoint endpoint = GameEndpoint.Parse(ArgumentValue(args, "--address") ?? "127.0.0.1:4242");
string zoneId = ArgumentValue(args, "--zone") ?? "InstLeague1";
double durationSeconds = double.TryParse(ArgumentValue(args, "--duration"), out double parsedDuration)
    ? parsedDuration
    : 8;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
try
{
    await using GameSession session = await GameSession.ConnectAsync(
        endpoint,
        zoneId,
        "sar20-smoke",
        allowUntrustedDevelopmentCertificate: true,
        timeout.Token);

    ulong ownEntityId = session.EnteredZone.OwnEntityId;
    float startX = session.EnteredZone.SpawnPosition?.X ?? 0;
    var advanced = new TaskCompletionSource<(float X, ulong Tick)>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    (float advancedX, ulong serverTick) = await advanced.Task.WaitAsync(timeout.Token);
    timeout.Cancel();
    try
    {
        await receiveTask;
    }
    catch (OperationCanceledException)
    {
    }

    Console.WriteLine(
        $"SAR20_NET_SMOKE result=PASS transport=quic-stream entity={ownEntityId} " +
        $"start_x={startX:F3} advanced_x={advancedX:F3} server_tick={serverTick} intents={sequence}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"SAR20_NET_SMOKE result=FAIL error={exception.Message}");
    return 1;
}

static async Task ReceiveUntilAdvanced(
    GameSession session,
    ulong ownEntityId,
    float startX,
    TaskCompletionSource<(float X, ulong Tick)> advanced,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        SnapshotBatch snapshot = await session.ReadSnapshotAsync(cancellationToken);
        EntitySnapshot? own = snapshot.Entities.FirstOrDefault(entity => entity.EntityId == ownEntityId);
        if (own?.Position is not null && own.Position.X > startX + 0.05f)
        {
            advanced.TrySetResult((own.Position.X, snapshot.ServerTick));
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
