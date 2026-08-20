using System.Text.Json;
using System.Text.Json.Serialization;

namespace SarnautCore.Shell;

/// <summary>
/// The id of the runtime pack this client claims to be running, for the
/// <c>ClientHello</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>pack_id</c> is a gate, not a version banner: a shard with a pack refuses a
/// client that names a different one, and refuses a client that names none
/// unless it was started with <c>content.allow_unverified_pack</c> (ADR 0029,
/// session spec rule 5.1). The Godot client sent an empty one, so against any
/// shard configured the way production is it never got past the handshake — the
/// console smoke passed a <c>--pack</c> and the game did not.
/// </para>
/// <para>
/// It is read the same way the shard reads it: <c>pack_id</c> out of the
/// <c>manifest.json</c> of the directory <c>SARNAUT_CONTENT_PACK</c> names.
/// <c>SARNAUT_CONTENT_PACK_ID</c> states it outright for a client that has no
/// pack directory to read. Neither being set is a legal, and normal, state: an
/// empty id is what a client with no content has, and saying so is honest.
/// </para>
/// </remarks>
public static class ContentPackIdentity
{
    public const string PackPathVariable = "SARNAUT_CONTENT_PACK";
    public const string PackIdVariable = "SARNAUT_CONTENT_PACK_ID";
    public const string ManifestFileName = "manifest.json";

    /// <summary>Reads the id from the environment, or returns empty.</summary>
    public static string FromEnvironment() => Resolve(
        Environment.GetEnvironmentVariable(PackIdVariable),
        Environment.GetEnvironmentVariable(PackPathVariable));

    /// <summary>
    /// Resolves the id a client should announce. A stated id wins over a pack
    /// directory, because someone who set it meant it.
    /// </summary>
    public static string Resolve(string? statedPackId, string? packDirectory)
    {
        string stated = statedPackId?.Trim() ?? string.Empty;
        if (stated.Length > 0)
        {
            return stated;
        }

        if (string.IsNullOrWhiteSpace(packDirectory))
        {
            return string.Empty;
        }

        return ReadManifest(Path.Combine(packDirectory.Trim(), ManifestFileName));
    }

    /// <summary>
    /// Reads <c>pack_id</c> out of one manifest, or returns empty when there is
    /// no readable manifest there.
    /// </summary>
    /// <remarks>
    /// A missing or malformed manifest is not worth failing the client's start
    /// over: an empty id means the shard decides whether an unverified client is
    /// welcome, which is where that decision belongs.
    /// </remarks>
    public static string ReadManifest(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return string.Empty;
            }

            using FileStream stream = File.OpenRead(manifestPath);
            Manifest? manifest = JsonSerializer.Deserialize<Manifest>(stream);
            return manifest?.PackId?.Trim() ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private sealed class Manifest
    {
        [JsonPropertyName("pack_id")] public string? PackId { get; set; }
    }
}
