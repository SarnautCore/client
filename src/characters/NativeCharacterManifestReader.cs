using System;
using Godot;
using SarnautCore.Content;

namespace SarnautCore;

/// <summary>Reads the native character manifest from the mounted content root.</summary>
public sealed class NativeCharacterManifestReader
{
    public const string RelativeManifestPath = "characters/manifest.json";

    public NativeCharacterManifestReader()
        : this(NativeContentSettings.NativeRoot)
    {
    }

    public NativeCharacterManifestReader(string nativeRoot)
    {
        string root = (nativeRoot ?? string.Empty).TrimEnd('/');
        ManifestPath = $"{root}/{RelativeManifestPath}";
        CharactersRoot = $"{root}/characters";
        Load();
    }

    public string ManifestPath { get; }

    public string CharactersRoot { get; }

    public NativeCharacterManifest? Manifest { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public bool TryResolve(string key, out NativeCharacterModel model)
    {
        model = null!;
        return Manifest?.TryResolve(key, out model) == true;
    }

    public bool TryResolvePlayer(string playerKey, out NativeCharacterModel model)
    {
        model = null!;
        return Manifest?.TryResolvePlayer(playerKey, out model) == true;
    }

    public string ResolveScenePath(NativeCharacterModel model) =>
        $"{CharactersRoot}/{model.ScenePath}";

    private void Load()
    {
        if (!Godot.FileAccess.FileExists(ManifestPath))
        {
            LastError = $"No native character manifest at {ManifestPath}; characters use placeholders.";
            return;
        }

        try
        {
            Manifest = NativeCharacterManifest.Parse(Godot.FileAccess.GetFileAsString(ManifestPath));
        }
        catch (Exception exception)
        {
            LastError = $"Native character manifest {ManifestPath} could not be read: {exception.Message}";
            GD.PushWarning(LastError);
        }
    }
}
