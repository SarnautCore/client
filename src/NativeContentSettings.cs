using Godot;

namespace SarnautCore;

/// <summary>
/// Project settings for native terrain and content loading.
/// Follows the pattern of <see cref="UpscaledTextures"/> for setting registration.
/// </summary>
public static class NativeContentSettings
{
    /// <summary>Root directory where native content is mounted (e.g., "res://content/league-slice").</summary>
    public const string NativeRootSetting = "sarnaut/content/native_root";

    private const string DefaultNativeRoot = "res://content/league-slice";

    private static string _nativeRoot = string.Empty;

    /// <summary>
    /// Gets the native content root directory. Returns the project setting value,
    /// or the default if not configured.
    /// </summary>
    public static string NativeRoot
    {
        get
        {
            if (_nativeRoot.Length == 0)
            {
                _nativeRoot = ProjectSettings.GetSetting(NativeRootSetting, DefaultNativeRoot)
                    .AsString()
                    .TrimEnd('/');
                if (_nativeRoot.Length == 0)
                {
                    _nativeRoot = DefaultNativeRoot;
                }
            }

            return _nativeRoot;
        }
    }

    /// <summary>Test seam: resets the cached root so the setting is re-read.</summary>
    public static void Reset()
    {
        _nativeRoot = string.Empty;
    }
}
