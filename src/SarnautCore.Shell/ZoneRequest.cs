namespace SarnautCore.Shell;

/// <summary>A spawn the server named, in the server's axis order.</summary>
public sealed record ZoneSpawn(float X, float Y, float Z);

/// <summary>
/// Everything the zone scene needs to know before it loads, carried across the
/// scene change on the session autoload.
/// </summary>
/// <remarks>
/// This replaces <c>ZoneWalkabout.RequestedMapName</c>,
/// <c>RequestedOnlineMode</c> and <c>RequestedServerAddress</c>. Those were
/// class statics: writable from anywhere, impossible to reset between runs, and
/// invisible in the zone scene's own signature. A record on the session says who
/// owns the parameters and when they were set.
///
/// <see cref="Ticket"/> is the opaque single-use shard ticket. It is presented
/// once in <c>EnterZoneRequest</c> and the shard burns it (ADR 0030 section 2),
/// so nothing keeps a copy and nothing prints one.
/// </remarks>
public sealed record ZoneRequest(
    string MapName,
    string ZoneId,
    string ServerAddress,
    bool Online,
    Secret Ticket,
    ZoneSpawn? Spawn = null)
{
    /// <summary>An offline walkabout of a converted map, with no shard involved.</summary>
    public static ZoneRequest Offline(string mapName, string zoneId) =>
        new(mapName, zoneId, string.Empty, Online: false, Secret.None);

    public override string ToString() =>
        $"ZoneRequest(map={MapName}, zone={ZoneId}, online={Online}, address={ServerAddress})";
}
