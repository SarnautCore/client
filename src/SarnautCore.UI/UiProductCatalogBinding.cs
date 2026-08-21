namespace SarnautCore.UI;

public static class UiProductCatalogBinding
{
    public static void Validate<TTexture, TSound>(
        UiProductManifest manifest,
        UiCursorCatalog<TTexture> cursors,
        UiSoundCatalog<TSound> sounds)
        where TTexture : class
        where TSound : class
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(sounds);

        foreach (UiScreenDefinition screen in manifest.Screens)
        {
            ValidateCues(screen.Cues, sounds);
            foreach (UiRoleDefinition role in screen.Roles)
            {
                if (role.Cursor is { } cursor)
                {
                    cursors.GetRequired(cursor);
                }

                ValidateCues(role.Cues, sounds);
            }

            foreach (UiButtonVariant variant in screen.Buttons.SelectMany(button => button.Variants))
            {
                ValidateCues(variant.Cues, sounds);
            }
        }
    }

    private static void ValidateCues<TSound>(UiCueSet cues, UiSoundCatalog<TSound> sounds)
        where TSound : class
    {
        foreach (string cue in new[] { cues.Show, cues.Hide, cues.Hover, cues.Press }.OfType<string>())
        {
            sounds.GetRequired(cue);
        }
    }
}
