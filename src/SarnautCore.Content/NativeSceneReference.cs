namespace SarnautCore.Content;

/// <summary>Selects and validates one manifest-relative native Godot scene.</summary>
public static class NativeSceneReference
{
    public static string Select(
        string? scene,
        string? runtimeScene,
        bool allowParentSegments = false)
    {
        bool hasRuntimeField = runtimeScene is not null;
        string selected = (hasRuntimeField ? runtimeScene : scene)?.Trim() ?? string.Empty;
        if (selected.Length == 0)
        {
            throw new InvalidDataException(
                hasRuntimeField
                    ? "runtime_scene is present but empty."
                    : "scene is missing or empty.");
        }

        if (selected.Contains('\\')
            || selected.StartsWith('/')
            || selected.Contains("://", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Scene path '{selected}' is not manifest-relative.");
        }

        string[] parts = selected.Split('/');
        if (parts.Any(part => part.Length == 0
            || part == "."
            || (!allowParentSegments && part == "..")
            || part.IndexOfAny([':', '*', '?']) >= 0))
        {
            throw new InvalidDataException($"Scene path '{selected}' has an unsafe segment.");
        }

        if (hasRuntimeField)
        {
            if (!selected.EndsWith(".scn", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"runtime_scene must name a compiled .scn resource, got '{selected}'.");
            }
        }
        else if (!selected.EndsWith(".scn", StringComparison.OrdinalIgnoreCase)
            && !selected.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"scene must name a .scn or .tscn resource, got '{selected}'.");
        }

        return selected;
    }

    public static string Extension(string selected) =>
        selected.EndsWith(".scn", StringComparison.OrdinalIgnoreCase) ? ".scn" : ".tscn";
}
