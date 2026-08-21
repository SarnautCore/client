namespace SarnautCore.UI;

public static class OptionsProductLocation
{
    public const string ProductKey = "options";
    public const string ProductDirectory = "ui/options";
    public const string ManifestFile = "options-product.json";
    public const string PlainRootScene = "screens/options.tscn";
    public const string CompiledRootScene = "screens/options.scn";

    public static string ManifestPath(string nativeContentRoot)
    {
        string root = ConfinedResourceRoot(nativeContentRoot);
        return $"{root}/{ProductDirectory}/{ManifestFile}";
    }

    public static string Resolve(string manifestPath, NativeContentPath relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);
        string suffix = $"/{ProductDirectory}/{ManifestFile}";
        string resourcePath = manifestPath.StartsWith("res://", StringComparison.Ordinal)
            ? manifestPath["res://".Length..]
            : string.Empty;
        if (!manifestPath.StartsWith("res://content/", StringComparison.Ordinal)
            || !manifestPath.EndsWith(suffix, StringComparison.Ordinal)
            || manifestPath.Contains('\\')
            || resourcePath.Contains(':')
            || resourcePath.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException(
                "Manifest path must identify a confined native options product",
                nameof(manifestPath));
        }

        return $"{manifestPath[..^ManifestFile.Length]}{relativePath.Value}";
    }

    private static string ConfinedResourceRoot(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        string normalized = value.TrimEnd('/');
        string resourcePath = normalized.StartsWith("res://", StringComparison.Ordinal)
            ? normalized["res://".Length..]
            : string.Empty;
        if (!normalized.StartsWith("res://content", StringComparison.Ordinal)
            || normalized.Length > "res://content".Length
                && normalized["res://content".Length] != '/'
            || normalized.Contains('\\')
            || resourcePath.Contains(':')
            || resourcePath.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException(
                "Native content root must be a confined res://content/ path",
                nameof(value));
        }

        return normalized;
    }
}
