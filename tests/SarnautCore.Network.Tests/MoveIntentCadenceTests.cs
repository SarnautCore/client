using Sarnaut.Protocol.V1;
using SarnautCore.Networking;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class MoveIntentCadenceTests
{
    private const double TwentyHertz = 1.0 / 20.0;

    // The zone loop sent SendIntervalSeconds * 2, so at 20Hz every intent
    // claimed 0.1s of movement for 0.05s of elapsed time. The shard integrates
    // dt_seconds, so the client was asking to travel twice as far as it had
    // pressed for.
    [Fact]
    public void StampsEveryIntentWithTheRealSendInterval()
    {
        var cadence = new MoveIntentCadence(TwentyHertz);
        cadence.Accumulate(TwentyHertz);

        Assert.True(cadence.TryDequeue(1, 0, 0.25f, out ClientMoveIntent intent));
        Assert.Equal((float)TwentyHertz, intent.DtSeconds);
        Assert.Equal(0.05f, intent.DtSeconds, precision: 6);
        Assert.Equal((float)TwentyHertz, cadence.DtSeconds);
    }

    [Fact]
    public void CarriesTheInputAndHeadingItWasGiven()
    {
        var cadence = new MoveIntentCadence(TwentyHertz);
        cadence.Accumulate(TwentyHertz);

        Assert.True(cadence.TryDequeue(0.5f, -0.5f, 1.75f, out ClientMoveIntent intent));
        Assert.Equal((ulong)1, intent.Seq);
        Assert.Equal(0.5f, intent.Input.X);
        Assert.Equal(-0.5f, intent.Input.Y);
        Assert.Equal(1.75f, intent.Heading);
    }

    [Fact]
    public void SendsNothingBeforeAWholeIntervalHasPassed()
    {
        var cadence = new MoveIntentCadence(TwentyHertz);
        cadence.Accumulate(TwentyHertz / 2);

        Assert.False(cadence.TryDequeue(0, 0, 0, out _));
        Assert.Equal((ulong)0, cadence.Sequence);
    }

    // A frame long enough to span two intervals owes the shard two intents, each
    // still claiming one interval: the elapsed time is neither invented nor lost.
    [Fact]
    public void DrainsAFrameThatSpannedSeveralIntervals()
    {
        var cadence = new MoveIntentCadence(TwentyHertz);
        cadence.Accumulate(0.12);

        var sent = new List<ClientMoveIntent>();
        while (cadence.TryDequeue(1, 0, 0, out ClientMoveIntent intent))
        {
            sent.Add(intent);
        }

        Assert.Equal(2, sent.Count);
        Assert.All(sent, intent => Assert.Equal((float)TwentyHertz, intent.DtSeconds));
        Assert.Equal(new ulong[] { 1, 2 }, sent.Select(intent => intent.Seq));

        // The 0.02s remainder stays in the accumulator and is paid off later: a
        // third of an interval short of a send here is a whole one after 0.04s.
        cadence.Accumulate(0.04);
        Assert.True(cadence.TryDequeue(1, 0, 0, out ClientMoveIntent third));
        Assert.Equal((ulong)3, third.Seq);
    }

    [Fact]
    public void RefusesASendRateThatIsNotARate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MoveIntentCadence(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MoveIntentCadence(double.NaN));
    }
}
