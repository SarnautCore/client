namespace SarnautCore.Content;

/// <summary>The native Godot resource categories that the developer asset viewer can load.</summary>
public enum NativeAssetKind
{
    Scene,
    Resource,
}

/// <summary>
/// Validates one Godot scene or resource beneath the configured native content root.
/// </summary>
public sealed record NativeAssetReference(string Path, NativeAssetKind Kind)
{
    private const string ResourceScheme = "res://";

    public static bool TryCreate(
        string? nativeRoot,
        string? path,
        out NativeAssetReference reference,
        out string error)
    {
        reference = null!;
        if (!TryValidateRoot(nativeRoot, out string root, out error))
        {
            return false;
        }

        if (string.IsNullOrEmpty(path) || !string.Equals(path, path.Trim(), StringComparison.Ordinal))
        {
            error = "The asset path is empty or has surrounding whitespace.";
            return false;
        }

        string prefix = root + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            error = $"The asset path is outside the native content root '{root}'.";
            return false;
        }

        string relativePath = path[prefix.Length..];
        if (!HasSafeSegments(relativePath))
        {
            error = "The asset path has an unsafe segment.";
            return false;
        }

        NativeAssetKind? kind = ExtensionKind(System.IO.Path.GetExtension(relativePath));
        if (kind is null)
        {
            error = "The asset is not a supported Godot scene or resource.";
            return false;
        }

        reference = new NativeAssetReference(path, kind.Value);
        error = string.Empty;
        return true;
    }

    public static NativeAssetKind? ExtensionKind(string? extension) =>
        extension?.ToLowerInvariant() switch
        {
            ".scn" or ".tscn" => NativeAssetKind.Scene,
            ".res" or ".tres" => NativeAssetKind.Resource,
            _ => null,
        };

    public static bool IsSupportedFile(string fileName) =>
        ExtensionKind(System.IO.Path.GetExtension(fileName)) is not null;

    public static bool TryValidateRoot(string? nativeRoot, out string root, out string error)
    {
        root = nativeRoot?.TrimEnd('/') ?? string.Empty;
        if (!root.StartsWith(ResourceScheme, StringComparison.Ordinal)
            || root.Length == ResourceScheme.Length
            || !HasSafeSegments(root[ResourceScheme.Length..]))
        {
            error = "The native content root is not a confined res:// directory.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool HasSafeSegments(string value)
    {
        if (value.Contains('\\'))
        {
            return false;
        }

        string[] segments = value.Split('/');
        return segments.All(segment => segment.Length > 0
            && segment is not "." and not ".."
            && segment.IndexOfAny([':', '*', '?', '#']) < 0);
    }
}
