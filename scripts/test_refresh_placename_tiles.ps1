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

function Fixture([string]$Name, [string]$Relative = '7\109\44.png', [switch]$NoCache, [switch]$NoTile) {
    $game = Join-Path $output $Name
    foreach ($set in @('Synoptic', 'Cities')) {
        $path = Join-Path $game "WW\PlacenameSets\$set\0000_0000.wwp"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        $writer = [IO.BinaryWriter]::new([IO.File]::Create($path), [Text.Encoding]::UTF8)
        try {
            if ($set -eq 'Synoptic') { $writer.Write([int]0) }
            else {
                $writer.Write([int]1); $writer.Write('Fixture')
                $writer.Write([float]47.721432); $writer.Write([float]128.875290); $writer.Write([uint32]1)
            }
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

# Exercise the actual Save counter even when the renderer's internal catch swallows a locked-file error.
$core = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($CounterLibrary)).GetType('CMOLocalizerInstaller.InstallerCore', $true)
$method = $core.GetMethod('LocalizeMapLabelTiles', [Reflection.BindingFlags]'Static,NonPublic')
$counted = $method.Invoke($null, @([string]$normal.Game))
Check ($counted -is [int] -and $counted -eq 1) 'renderer returns actual successful Save count'
New-Item -ItemType Directory -Force -Path ($locked.Cache + '.orig') | Out-Null
$handle = [IO.File]::Open($locked.Tile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $counted = $method.Invoke($null, @([string]$locked.Game)) }
finally { $handle.Dispose() }
Check ($counted -is [int] -and $counted -eq 0) 'failed Save returns zero instead of false success'

$summary = "Placename tile checks=$script:checks failures=$script:failures"
$script:log.Add($summary)
[IO.File]::WriteAllText((Join-Path $output 'refresh_tiles.log'), ($script:log -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Output $summary
Write-Output "Fixture output: $output"
if ($script:failures) { exit 1 }
