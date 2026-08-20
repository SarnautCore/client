using Sarnaut.Protocol.V1;

namespace SarnautCore.Networking;

/// <summary>
/// Paces client move intents at a fixed send rate and stamps each one with the
/// span of time it actually covers.
/// </summary>
/// <remarks>
/// <para>
/// <c>dt_seconds</c> is how much simulated time the shard advances the sender
/// by, so it has to be the real interval between intents. The zone loop used to
/// send twice the interval — 0.1s at a 20Hz cadence — which told the shard the
/// client had been moving for twice as long as it had. Nothing caught it while
/// the shard only echoed positions back, and it becomes a divergence the moment
/// ability range and facing are checked server-side.
/// </para>
/// <para>
/// The accumulator drains in whole intervals rather than being reset, so a frame
/// long enough to span two intervals sends two intents and no elapsed time is
/// invented or dropped.
/// </para>
/// </remarks>
public sealed class MoveIntentCadence
{
    private double _accumulator;

    public MoveIntentCadence(double sendIntervalSeconds)
    {
        if (!double.IsFinite(sendIntervalSeconds) || sendIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sendIntervalSeconds));
        }

        SendIntervalSeconds = sendIntervalSeconds;
    }

    public double SendIntervalSeconds { get; }

    /// <summary>The elapsed time every intent claims: the send interval itself.</summary>
    public float DtSeconds => (float)SendIntervalSeconds;

    /// <summary>The sequence number of the last intent handed out.</summary>
    public ulong Sequence { get; private set; }

    public void Accumulate(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
        {
            return;
        }

        _accumulator += deltaSeconds;
    }

    /// <summary>
    /// Hands out the next due intent, or returns false when the accumulator has
    /// not yet reached a whole send interval.
    /// </summary>
    /// <param name="inputX">Movement input on the shard's X axis.</param>
    /// <param name="inputY">Movement input on the shard's Y axis, its ground plane's second axis.</param>
    /// <param name="heading">Facing, in radians.</param>
    public bool TryDequeue(float inputX, float inputY, float heading, out ClientMoveIntent intent)
    {
        if (_accumulator < SendIntervalSeconds)
        {
            intent = null!;
            return false;
        }

        _accumulator -= SendIntervalSeconds;
        intent = new ClientMoveIntent
        {
            Seq = ++Sequence,
            Input = new Vec3 { X = inputX, Y = inputY },
            Heading = heading,
            DtSeconds = DtSeconds,
        };
        return true;
    }
}
