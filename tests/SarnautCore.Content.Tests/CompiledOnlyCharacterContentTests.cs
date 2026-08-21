using Xunit;

namespace SarnautCore.Content.Tests;

public sealed class CompiledOnlyCharacterContentTests
{
    [Fact]
    public void Manifest_resolves_when_only_compiled_scene_exists()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "sarnaut-compiled-only-character-" + Guid.NewGuid().ToString("N"));
        try
        {
            string fixture = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "fixtures",
                "character-manifest.json"));
            string compiledManifest = fixture.Replace(
                "\"scene\": \"Rat1_1/Rat1_1.tscn\"",
                "\"runtime_scene\": \"Rat1_1/Rat1_1.scn\"",
                StringComparison.Ordinal);
            string charactersRoot = Path.Combine(root, "characters");
            string compiledScene = Path.Combine(charactersRoot, "Rat1_1", "Rat1_1.scn");
            Directory.CreateDirectory(Path.GetDirectoryName(compiledScene)!);
            File.WriteAllBytes(compiledScene, "RSCC"u8.ToArray());

            NativeCharacterManifest manifest = NativeCharacterManifest.Parse(compiledManifest);
            Assert.True(manifest.TryResolve("50032", out NativeCharacterModel rat));

            string selectedPath = Path.Combine(
                charactersRoot,
                rat.ScenePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(compiledScene, selectedPath);
            Assert.True(File.Exists(selectedPath));
            Assert.Empty(Directory.GetFiles(root, "*.tscn", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
