# Synthetic fixtures only. No real game files, installer entry point, or external dependencies.
[CmdletBinding()]
param([string]$OutputDir, [string]$CounterLibrary)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..'))
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\placename-tile-tests' }
$output = Join-Path ([IO.Path]::GetFullPath($OutputDir)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $output | Out-Null
$script:checks = 0
$script:failures = 0
$script:log = New-Object 'Collections.Generic.List[string]'
$pngBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lx8AAAAASUVORK5CYII=')
$refresh = Join-Path $repoRoot 'scripts\refresh_placename_tiles.ps1'
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Check([bool]$Condition, [string]$Name) {
    $script:checks++
    if (-not $Condition) { $script:failures++ }
    $line = $(if ($Condition) { 'PASS ' } else { 'FAIL ' }) + $Name
    $script:log.Add($line)
    Write-Output $line
}

function Fixture([string]$Name, [string]$Relative = '7\109\44.png', [switch]$NoCache, [switch]$NoTile,
                 [string]$CityName = 'CitiesFixture', [float]$CityLat = 47.721432, [float]$CityLon = 128.875290) {
    $game = Join-Path $output $Name
    foreach ($set in @('Synoptic', 'Cities')) {
        $path = Join-Path $game "WW\PlacenameSets\$set\0000_0000.wwp"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        $writer = [IO.BinaryWriter]::new([IO.File]::Create($path), [Text.Encoding]::UTF8)
        try {
            $writer.Write([int]1); $writer.Write([string]$(if ($set -eq 'Synoptic') { 'SynopticFixture' } else { $CityName }))
            $writer.Write([float]$(if ($set -eq 'Synoptic') { 0 } else { $CityLat }))
            $writer.Write([float]$(if ($set -eq 'Synoptic') { 0 } else { $CityLon }))
            $writer.Write([uint32]1)
        }
        finally { $writer.Dispose() }
    }
    $cache = Join-Path $game 'WW\Cache\Stamen Terrain - Labels'
    $tile = Join-Path $cache $Relative
    if (-not $NoCache) {
        New-Item -ItemType Directory -Force -Path $cache | Out-Null
        if (-not $NoTile) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $tile) | Out-Null
            [IO.File]::WriteAllBytes($tile, $pngBytes)
        }
    }
    $terrain = Join-Path $game 'WW\Cache\Stamen Terrain\7\109\44.png'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $terrain) | Out-Null
    [IO.File]::WriteAllText($terrain, 'terrain sentinel')
    return [PSCustomObject]@{ Game = $game; Cache = $cache; Tile = $tile; Terrain = $terrain;
        Backup = Join-Path ($cache + '.orig') $Relative }
}

function Refresh($Fixture, [switch]$MissingCompiler) {
    $ErrorActionPreference = 'Continue'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $refresh, '-GameDir', $Fixture.Game)
    if ($MissingCompiler) { $arguments += @('-Compiler', (Join-Path $output 'missing-csc.exe')) }
    $lines = @(& $powershell @arguments 2>&1)
    $code = $LASTEXITCODE
    foreach ($line in $lines) { $script:log.Add($line.ToString()) }
    return [PSCustomObject]@{ Code = $code; Text = ($lines -join "`n") }
}

$normal = Fixture 'normal'
$rendererBuild = Join-Path $repoRoot 'build\placename-tiles'
$existingLibraries = @()
if (Test-Path -LiteralPath $rendererBuild) {
    $existingLibraries = @(Get-ChildItem -LiteralPath $rendererBuild -Filter PlacenameTileRenderer.dll -Recurse | ForEach-Object FullName)
}
$result = Refresh $normal
if (-not $CounterLibrary) {
    $createdLibraries = @(Get-ChildItem -LiteralPath $rendererBuild -Filter PlacenameTileRenderer.dll -Recurse |
        Where-Object { $existingLibraries -notcontains $_.FullName })
    if ($createdLibraries.Count -ne 1) { throw 'Expected exactly one renderer library from this normal refresh' }
    $CounterLibrary = $createdLibraries[0].FullName
}
Check ($result.Code -eq 0 -and $result.Text.Contains('refreshed 1 PNG')) 'normal PNG refresh reports a counted save'
Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new($normal.Tile)
try { Check ($bitmap.Width -eq 256 -and $bitmap.Height -eq 256) 'real renderer replaced tiny PNG with 256x256 labels' }
finally { $bitmap.Dispose() }
Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($normal.Backup)) -eq [Convert]::ToBase64String($pngBytes)) 'first backup preserves original PNG'
$null = Refresh $normal
Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($normal.Backup)) -eq [Convert]::ToBase64String($pngBytes)) 'repeat refresh does not overwrite backup'
Check ([IO.File]::ReadAllText($normal.Terrain) -eq 'terrain sentinel') 'terrain sentinel remains unchanged'
$highZoom = Fixture 'high-zoom-synoptic' '8\128\128.png'
$result = Refresh $highZoom
Check ($result.Code -eq 0) 'high zoom label tile refresh succeeds'
$bitmap = [Drawing.Bitmap]::new($highZoom.Tile)
try {
    $ink = 0
    for ($y = 0; $y -lt $bitmap.Height; $y++) {
        for ($x = 0; $x -lt $bitmap.Width; $x++) {
            if ($bitmap.GetPixel($x, $y).A -gt 0) { $ink++ }
        }
    }
    Check ($ink -ge 32) 'synoptic label remains visibly rendered above zoom seven'
}
finally { $bitmap.Dispose() }
$emptyTile = Fixture 'empty-render-tile' '7\110\44.png'
$result = Refresh $emptyTile
Check ($result.Code -eq 0) 'tile without nearby labels refreshes successfully'
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead((Join-Path $repoRoot 'assets\placenames\Cities.wwp')), [Text.Encoding]::UTF8)
$longest = $null
try {
    $count = $reader.ReadInt32()
    for ($i = 0; $i -lt $count; $i++) {
        $name = $reader.ReadString(); $lat = $reader.ReadSingle(); $lon = $reader.ReadSingle()
        $null = $reader.ReadUInt32()
        if (-not $longest -or $name.Length -gt $longest.Name.Length) {
            $longest = [PSCustomObject]@{ Name = $name; Lat = $lat; Lon = $lon }
        }
    }
}
finally { $reader.Dispose() }
$longX = [int][Math]::Floor(($longest.Lon + 180.0) / 360.0 * 256)
$radians = $longest.Lat * [Math]::PI / 180.0
$longY = [int][Math]::Floor((1.0 - [Math]::Log([Math]::Tan($radians) + 1.0 / [Math]::Cos($radians)) / [Math]::PI) / 2.0 * 256)
$longTile = Fixture 'longest-real-city' "8\$longX\$longY.png" -CityName $longest.Name -CityLat $longest.Lat -CityLon $longest.Lon
$longSource = Join-Path $longTile.Game 'WW\PlacenameSets\Cities\0000_0000.wwp'
$longBefore = [Convert]::ToBase64String([IO.File]::ReadAllBytes($longSource))
$result = Refresh $longTile
Check ($result.Code -eq 0) 'longest real city refreshes at its original coordinates'
Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($longSource)) -eq $longBefore) 'fitting the longest city keeps its complete WWP name and coordinates'
$bitmap = [Drawing.Bitmap]::new($longTile.Tile)
try {
    $minX = 256; $minY = 256; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt 256; $y++) {
        for ($x = 0; $x -lt 256; $x++) {
            if ($bitmap.GetPixel($x, $y).A -gt 0) {
                $minX = [Math]::Min($minX, $x); $maxX = [Math]::Max($maxX, $x)
                $minY = [Math]::Min($minY, $y); $maxY = [Math]::Max($maxY, $y)
            }
        }
    }
    Check ($minX -gt 0 -and $minY -gt 0 -and $maxX -gt $minX -and $maxX -lt 255 -and $maxY -gt $minY -and $maxY -lt 255) 'longest real city has visible ink fully inside the tile'
}
finally { $bitmap.Dispose() }
foreach ($kind in @('missing', 'empty')) {
    $fixture = Fixture $kind -NoCache:($kind -eq 'missing') -NoTile
    $result = Refresh $fixture -MissingCompiler
    Check ($result.Code -eq 0 -and $result.Text.Contains('nothing to refresh')) "$kind cache skips compiler"
    Check (-not [IO.Directory]::Exists($fixture.Cache + '.orig')) "$kind cache creates no backup"
}
foreach ($relative in @('7\109\128.png', '7\109\bad.png')) {
    $fixture = Fixture ('invalid-' + $script:checks) $relative
    $result = Refresh $fixture
    Check ($result.Code -ne 0) "invalid tile path rejected: $relative"
    Check (-not [IO.Directory]::Exists($fixture.Cache + '.orig')) 'invalid path rejected before backup/rendering'
}
$locked = Fixture 'locked'
$handle = [IO.File]::Open($locked.Tile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $result = Refresh $locked }
finally { $handle.Dispose() }
Check ($result.Code -ne 0) 'locked PNG cannot report success'
Check (-not [IO.Directory]::Exists($locked.Cache + '.orig')) 'locked PNG rejected before backup/rendering'
Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($locked.Tile)) -eq [Convert]::ToBase64String($pngBytes)) 'locked PNG stays unchanged'
$readOnly = Fixture 'read-only'
[IO.File]::SetAttributes($readOnly.Tile, [IO.FileAttributes]::ReadOnly)
try { $result = Refresh $readOnly }
finally { [IO.File]::SetAttributes($readOnly.Tile, [IO.FileAttributes]::Normal) }
Check ($result.Code -ne 0) 'read-only PNG cannot report success'
Check (-not [IO.Directory]::Exists($readOnly.Cache + '.orig')) 'read-only PNG rejected before backup/rendering'
$missingSource = Fixture 'missing-source'
[IO.File]::Delete((Join-Path $missingSource.Game 'WW\PlacenameSets\Cities\0000_0000.wwp'))
$result = Refresh $missingSource
Check ($result.Code -ne 0) 'missing city source fails instead of rendering partial labels'
Check (-not [IO.Directory]::Exists($missingSource.Cache + '.orig')) 'missing source rejected before cache changes'
$badSource = Fixture 'bad-source'
[IO.File]::WriteAllBytes((Join-Path $badSource.Game 'WW\PlacenameSets\Cities\0000_0000.wwp'), [byte[]](1, 0, 0, 0))
$result = Refresh $badSource
Check ($result.Code -ne 0) 'truncated WWP fails before rendering'
Check (-not [IO.Directory]::Exists($badSource.Cache + '.orig')) 'truncated WWP creates no cache backup'
$emptySources = Fixture 'empty-sources'
foreach ($set in @('Synoptic', 'Cities')) {
    [IO.File]::WriteAllBytes((Join-Path $emptySources.Game "WW\PlacenameSets\$set\0000_0000.wwp"), [byte[]](0, 0, 0, 0))
}
$result = Refresh $emptySources
Check ($result.Code -ne 0) 'empty WWP sources cannot blank the label cache'
Check (-not [IO.Directory]::Exists($emptySources.Cache + '.orig')) 'empty sources rejected before cache changes'
foreach ($set in @('Synoptic', 'Cities')) {
    $emptySet = Fixture ('empty-' + $set)
    [IO.File]::WriteAllBytes((Join-Path $emptySet.Game "WW\PlacenameSets\$set\0000_0000.wwp"), [byte[]](0, 0, 0, 0))
    $result = Refresh $emptySet
    Check ($result.Code -ne 0) "empty $set rejected while other source is nonempty"
    Check (-not [IO.Directory]::Exists($emptySet.Cache + '.orig')) "empty $set rejected before backup"
    Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($emptySet.Tile)) -eq [Convert]::ToBase64String($pngBytes)) "empty $set leaves label PNG unchanged"
}

# Exercise the actual Save counter even when the renderer's internal catch swallows a locked-file error.
$core = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($CounterLibrary)).GetType('CMOLocalizerInstaller.InstallerCore', $true)
$method = $core.GetMethod('LocalizeMapLabelTiles', [Reflection.BindingFlags]'Static,NonPublic')
$placeType = $core.Assembly.GetType('CMOShared.MapLabelPlace')
$rendererType = $core.Assembly.GetType('CMOShared.MapLabelRenderer')
Check ($null -ne $placeType -and $null -ne $rendererType) 'installer contains the shared renderer dependency'
if ($placeType -and $rendererType) {
    $openList = [type]'System.Collections.Generic.List`1'
    $render = @($rendererType.GetMethods() | Where-Object { $_.Name -eq 'RenderTile' -and $_.GetParameters().Count -eq 6 })[0]
    foreach ($comparison in @(@($normal, 7, 109, 44, 1), @($highZoom, 8, 128, 128, 1), @($emptyTile, 7, 110, 44, 0), @($longTile, 8, $longX, $longY, 1))) {
        $places = [Activator]::CreateInstance($openList.MakeGenericType([Type[]]@($placeType)))
        $level = 0
        foreach ($set in @('Synoptic', 'Cities')) {
            $path = Join-Path $comparison[0].Game "WW\PlacenameSets\$set\0000_0000.wwp"
            $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($path), [Text.Encoding]::UTF8)
            try {
                $count = $reader.ReadInt32()
                for ($i = 0; $i -lt $count; $i++) {
                    $place = [Activator]::CreateInstance($placeType)
                    $place.Name = $reader.ReadString(); $place.Lat = $reader.ReadSingle(); $place.Lon = $reader.ReadSingle()
                    $null = $reader.ReadUInt32(); $place.Level = $level
                    $places.Add($place)
                }
            }
            finally { $reader.Dispose() }
            $level++
        }
        $arguments = [object[]]@($places.PSObject.BaseObject, [int]$comparison[1], [int]$comparison[2], [int]$comparison[3], 0, 0)
        $bitmap = $render.Invoke($null, $arguments)
        $stream = [IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            Check ([Convert]::ToBase64String($stream.ToArray()) -eq
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($comparison[0].Tile))) "offline/shared PNG bytes match at zoom $($comparison[1])"
            Check ($arguments[4] -eq $comparison[4] -and $arguments[5] -eq $comparison[4]) "shared renderer selects and renders the expected labels at zoom $($comparison[1])"
        }
        finally { $stream.Dispose(); $bitmap.Dispose() }
    }
}
$counted = $method.Invoke($null, @([string]$normal.Game))
Check ($counted -is [int] -and $counted -eq 1) 'renderer returns actual successful Save count'
New-Item -ItemType Directory -Force -Path ($locked.Cache + '.orig') | Out-Null
$handle = [IO.File]::Open($locked.Tile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $counted = $method.Invoke($null, @([string]$locked.Game)) }
finally { $handle.Dispose() }
Check ($counted -is [int] -and $counted -eq 0) 'failed Save returns zero instead of false success'

$copyFailure = Fixture 'backup-copy-failure'
$note = Join-Path $copyFailure.Cache 'notes.txt'
[IO.File]::WriteAllText($note, 'original label metadata')
$handle = [IO.File]::Open($note, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $result = Refresh $copyFailure }
finally { $handle.Dispose() }
Check ($result.Code -ne 0) 'locked non-PNG backup source fails refresh'
Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($copyFailure.Tile)) -eq [Convert]::ToBase64String($pngBytes)) 'backup failure leaves current PNG unchanged'
Check (-not [IO.Directory]::Exists($copyFailure.Cache + '.orig')) 'failed first backup is never published as orig'
$result = Refresh $copyFailure
Check ($result.Code -eq 0) 'retry after releasing backup source refreshes successfully'
Check ([IO.File]::Exists($copyFailure.Backup) -and
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($copyFailure.Backup)) -eq [Convert]::ToBase64String($pngBytes)) 'retry publishes original pre-refresh PNG in complete backup'
$backupNote = Join-Path ($copyFailure.Cache + '.orig') 'notes.txt'
Check ([IO.File]::Exists($backupNote) -and [IO.File]::ReadAllText($backupNote) -eq 'original label metadata') 'retry backs up non-PNG metadata too'
$handle = [IO.File]::Open($copyFailure.Tile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $counted = $method.Invoke($null, @([string]$copyFailure.Game)) }
finally { $handle.Dispose() }
Check ($counted -is [int] -and $counted -eq 0 -and [IO.File]::Exists($copyFailure.Backup) -and
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($copyFailure.Backup)) -eq [Convert]::ToBase64String($pngBytes)) 'later render failure preserves the already complete original backup'
Check ([IO.File]::ReadAllText($copyFailure.Terrain) -eq 'terrain sentinel') 'failure and retry never change terrain'

# A verified pack may add paths absent from the previous cache, including an entirely missing cache.
function Add-TilePack($Fixture) {
    $tileBytes = [IO.File]::ReadAllBytes($normal.Tile)
    $payload = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($payload)
    $writer.Write([int]1); $writer.Write([int]3); $writer.Write([int]4); $writer.Write([int]4)
    $writer.Write([int]2); $writer.Write([int]$tileBytes.Length); $writer.Write($tileBytes); $writer.Flush()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($sha.ComputeHash($payload.ToArray())).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $packWriter = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $Fixture.Game 'ChineseMapTiles.dat')))
    try { $packWriter.Write([Text.Encoding]::ASCII.GetBytes('CMOTIL01')); $packWriter.Write($fingerprint); $packWriter.Write($payload.ToArray()) }
    finally { $packWriter.Dispose(); $writer.Dispose(); $payload.Dispose() }
}
$packMissing = Fixture 'pack-missing-cache' -NoCache
Add-TilePack $packMissing
$result = Refresh $packMissing
Check ($result.Code -eq 0 -and $result.Text.Contains('refreshed 1 PNG') -and
    [IO.File]::Exists((Join-Path $packMissing.Cache '3\4\4.png'))) 'pack creates previously missing cache coverage'
$packExtra = Fixture 'pack-extra-tile'
Add-TilePack $packExtra
[IO.Directory]::CreateDirectory((Join-Path $packExtra.Cache 'empty-original-directory')) | Out-Null
$result = Refresh $packExtra
Check ($result.Code -eq 0 -and $result.Text.Contains('refreshed 2 PNG')) 'pack adds new tile and preserves counted legacy redraw'
$result = Refresh $packExtra
Check ($result.Code -eq 0 -and $result.Text.Contains('refreshed 1 PNG')) 'repeat pack refresh counts only legacy redraw'
Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($packExtra.Backup)) -eq [Convert]::ToBase64String($pngBytes)) 'pack refresh retains pre-pack original backup'
Check ([IO.Directory]::Exists((Join-Path ($packExtra.Cache + '.orig') 'empty-original-directory'))) 'pack backup includes empty original directories'
Check ([IO.File]::ReadAllText($packExtra.Terrain) -eq 'terrain sentinel') 'pack refresh never changes terrain'
$packOnly = Fixture 'pack-only' -NoCache
Add-TilePack $packOnly
foreach ($set in @('Synoptic', 'Cities')) { [IO.File]::Delete((Join-Path $packOnly.Game "WW\PlacenameSets\$set\0000_0000.wwp")) }
$result = Refresh $packOnly
Check ($result.Code -eq 0 -and $result.Text.Contains('refreshed 1 PNG')) 'pack-only refresh works without legacy WWP'
$badPack = Fixture 'corrupt-pack' -NoCache
[IO.File]::WriteAllBytes((Join-Path $badPack.Game 'ChineseMapTiles.dat'), [byte[]](1,2,3))
$result = Refresh $badPack
Check ($result.Code -ne 0 -and -not [IO.Directory]::Exists($badPack.Cache + '.orig')) 'corrupt pack fails before publishing a backup'

$summary = "Placename tile checks=$script:checks failures=$script:failures"
$script:log.Add($summary)
[IO.File]::WriteAllText((Join-Path $output 'refresh_tiles.log'), ($script:log -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Output $summary
Write-Output "Fixture output: $output"
if ($script:failures) { exit 1 }
