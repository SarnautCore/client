using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;

namespace SarnautCore;

/// <summary>
/// Converts online world values at the boundary between the server's canonical
/// X/Y ground plane and Godot's X/Z ground plane.
/// </summary>
public static class OnlineCoordinateFrame
{
    /// <summary>Maps a wire position into the Godot world used by converted content.</summary>
    public static Vector3 ToGodot(Vec3 server) =>
        ToGodot(server.X, server.Y, server.Z);

    /// <summary>Maps an interpolated server sample into the Godot world.</summary>
    public static Vector3 ToGodot(SampledEntity server) =>
        ToGodot(server.X, server.Y, server.Z);

    /// <summary>Maps a Godot ground direction into the server's X/Y ground plane.</summary>
    public static Vector2 ToServerGround(Vector3 godot) => new(godot.X, -godot.Z);

    private static Vector3 ToGodot(float serverX, float serverY, float serverZ) =>
        new(serverX, serverZ, -serverY);
}
