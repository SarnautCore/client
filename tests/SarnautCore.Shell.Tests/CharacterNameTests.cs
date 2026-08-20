using Xunit;

namespace SarnautCore.Shell.Tests;

/// <summary>
/// The client's pre-submit name check, against ADR 0032 section 3's own
/// accept/reject table. The client mirror has to agree with the server's rule or
/// it either blocks a legal name or promises an illegal one.
/// </summary>
public sealed class CharacterNameTests
{
    [Theory]
    [InlineData("Abc")]
    [InlineData("Anne")]
    [InlineData("O'brien")]
    [InlineData("Jean-luc")]
    public void Accepts_the_names_the_server_accepts(string name)
    {
        Assert.True(CharacterName.IsValid(name));
        Assert.Null(CharacterName.Explain(name));
    }

    [Theory]
    [InlineData("Ab")]                      // too short: passes the pattern, fails the length
    [InlineData("A'")]                      // trailing punctuation
    [InlineData("Ann'")]                    // trailing punctuation
    [InlineData("Ann--e")]                  // adjacent punctuation
    [InlineData("-anne")]                   // leading punctuation
    [InlineData("ANNE")]                    // interior uppercase
    [InlineData("Ann3")]                    // digit
    [InlineData("Ann e")]                   // space
    [InlineData("Averyveryverylongname")]   // too long
    [InlineData("Аnne")]                    // Cyrillic homoglyph
    [InlineData("")]
    public void Rejects_the_names_the_server_rejects(string name)
    {
        Assert.False(CharacterName.IsValid(name));
        Assert.NotNull(CharacterName.Explain(name));
    }

    [Theory]
    [InlineData("O'brien", "obrien")]
    [InlineData("Obrien", "obrien")]
    [InlineData("Ob-rien", "obrien")]
    [InlineData("Jean-luc", "jeanluc")]
    public void Normalizes_the_way_the_unique_index_does(string typed, string normalized)
    {
        Assert.Equal(normalized, CharacterName.Normalize(typed));
    }
}
