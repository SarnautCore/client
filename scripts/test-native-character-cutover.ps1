$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

$runtimeFiles = @(
    "src\characters\CharacterRig.cs",
    "src\characters\NativeCharacterLodContract.cs",
    "src\characters\NativeCharacterManifestReader.cs",
    "src\characters\PlayerCharacterModel.cs",
    "src\zone\EntityModelCatalog.cs",
    "src\zone\NetworkEntityVisual.cs",
    "src\zone\ZoneEntityVisualFactory.cs",
    "src\zone\ZoneNetworkLoop.cs",
    "src\zone\ZoneWalkabout.cs",
    "src\ui\CharacterPreview.cs"
)

$source = ($runtimeFiles | ForEach-Object {
    Get-Content -LiteralPath (Join-Path $root $_) -Raw
}) -join "`n"

$zone = Get-Content -LiteralPath (Join-Path $root "src\zone\ZoneLoader.cs") -Raw
$zoneStart = $zone.IndexOf("private bool TryLoadNativeCharacterPlacements", [StringComparison]::Ordinal)
$zoneEnd = $zone.IndexOf("private static CoordinateManifestDocument? ReadCoordinateManifestDocument", $zoneStart, [StringComparison]::Ordinal)
if ($zoneStart -lt 0 -or $zoneEnd -le $zoneStart) {
    throw "native character ZoneLoader section was not found"
}
$source += "`n" + $zone.Substring($zoneStart, $zoneEnd - $zoneStart)

$forbidden = @(
    "\.skmesh",
    "ConvertedCharacter",
    "res://converted",
    "allods_",
    "visual_ref",
    "VisualRef",
    "provenance"
)
foreach ($pattern in $forbidden) {
    if ($source -match $pattern) {
        throw "native character runtime still contains forbidden pattern '$pattern'"
    }
}

foreach ($retired in @(
    "src\characters\ConvertedCharacter.cs",
    "src\characters\ConvertedCharacter.cs.uid",
    "src\characters\KaniaFemaleWarrior.cs",
    "src\characters\KaniaFemaleWarrior.cs.uid",
    "characters\kania\female-warrior.tscn"
)) {
    if (Test-Path -LiteralPath (Join-Path $root $retired)) {
        throw "retired character adapter still exists: $retired"
    }
}

foreach ($shared in @(
    "src\characters\ConvertedSceneLoader.cs",
    "src\GodotNative\ConvertedImporterMesh.cs",
    "src\GodotNative\ConvertedSkinnedMesh.cs"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $shared))) {
        throw "shared loader was removed too early: $shared"
    }
}

Write-Output "native-character-cutover: PASS"
