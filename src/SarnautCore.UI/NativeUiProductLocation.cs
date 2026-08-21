namespace SarnautCore.UI;

/// <summary>Finds a product manifest and resolves its confined native resources.</summary>
public static class NativeUiProductLocation
{
    public const string ProductDirectory = "ui";
    public const string ManifestFile = "ui-product.json";

    public static IReadOnlyList<string> ManifestCandidates(string nativeRoot)
    {
        string root = RequireRoot(nativeRoot);
        return
        [
            $"{root}/{ProductDirectory}/{ManifestFile}",
            $"{root}/{ManifestFile}",
        ];
    }

    public static string Resolve(string manifestPath, NativeContentPath relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        int separator = manifestPath.LastIndexOf('/');
        if (separator <= "res://".Length)
        {
            throw new ArgumentException("UI manifest path has no product directory", nameof(manifestPath));
        }

        return $"{manifestPath[..separator]}/{relativePath.Value}";
    }

    private static string RequireRoot(string nativeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeRoot);
        string root = nativeRoot.TrimEnd('/');
        if (!root.StartsWith("res://", StringComparison.Ordinal)
            || root.Contains('\\')
            || root["res://".Length..].Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException(
                "Native content root must be a confined res:// path",
                nameof(nativeRoot));
        }

        return root;
    }
}
