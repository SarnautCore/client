using SarnautCore.Shell;
using Xunit;

namespace SarnautCore.Shell.Tests;

public sealed class ContentPackIdentityTests
{
    private const string PackId = "ff5b2261170eecaa85903fec12c41c7c335c832d857e563255d9d126f5c00ffc";

    [Fact]
    public void ReadsThePackIdOutOfAPackDirectory()
    {
        using var pack = new TemporaryPack($$"""{"schema_version": 1, "pack_id": "{{PackId}}"}""");

        Assert.Equal(PackId, ContentPackIdentity.Resolve(null, pack.Directory));
    }

    // Someone who set the id outright meant it, whatever a pack directory beside
    // it happens to hold.
    [Fact]
    public void PrefersAStatedIdOverAPackDirectory()
    {
        using var pack = new TemporaryPack($$"""{"pack_id": "{{PackId}}"}""");

        Assert.Equal("stated", ContentPackIdentity.Resolve("  stated  ", pack.Directory));
    }

    // An empty id is what a client with no content has. It is legal on the wire
    // and the shard decides whether to admit it, so none of these throw.
    [Fact]
    public void HasNoIdWhenThereIsNothingToReadItFrom()
    {
        Assert.Equal(string.Empty, ContentPackIdentity.Resolve(null, null));
        Assert.Equal(string.Empty, ContentPackIdentity.Resolve("   ", "   "));
        Assert.Equal(string.Empty, ContentPackIdentity.Resolve(null, Path.Combine(Path.GetTempPath(), "no-such-pack-dir")));
    }

    [Theory]
    [InlineData("{\"pack_id\": \"\"}")]
    [InlineData("{\"schema_version\": 1}")]
    [InlineData("not json at all")]
    public void HasNoIdWhenTheManifestDoesNotCarryOne(string manifest)
    {
        using var pack = new TemporaryPack(manifest);

        Assert.Equal(string.Empty, ContentPackIdentity.Resolve(null, pack.Directory));
    }

    private sealed class TemporaryPack : IDisposable
    {
        internal TemporaryPack(string manifest)
        {
            Directory = Path.Combine(Path.GetTempPath(), $"sarnaut-pack-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, ContentPackIdentity.ManifestFileName), manifest);
        }

        internal string Directory { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
