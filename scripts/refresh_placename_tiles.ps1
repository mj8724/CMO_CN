# Redraw existing label PNGs using the installer's renderer. Never touches terrain tiles.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$Compiler
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$repoRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..'))
if (-not [IO.Path]::IsPathRooted($GameDir)) { throw 'GameDir must be an absolute directory' }
$game = [IO.Path]::GetFullPath($GameDir)
if (-not [IO.Directory]::Exists($game)) { throw 'GameDir does not exist' }
$cache = Join-Path $game 'WW\Cache\Stamen Terrain - Labels'
if (-not [IO.Directory]::Exists($cache)) {
    Write-Output 'label cache: absent, nothing to refresh'
    return
}

function Assert-PlainPath([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        if (([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed below GameDir: $Path"
        }
    }
}

foreach ($relative in @('WW', 'WW\Cache', 'WW\Cache\Stamen Terrain - Labels',
                        'WW\Cache\Stamen Terrain - Labels.orig', 'WW\PlacenameSets',
                        'WW\PlacenameSets\Synoptic', 'WW\PlacenameSets\Cities')) {
    Assert-PlainPath (Join-Path $game $relative)
}
$pngs = New-Object 'Collections.Generic.List[string]'
$pending = New-Object 'Collections.Generic.Stack[string]'
$pending.Push($cache)
while ($pending.Count) {
    foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
        Assert-PlainPath $item.FullName
        if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        elseif ($item.Extension -ieq '.png') { $pngs.Add($item.FullName) }
    }
}
if (-not $pngs.Count) {
    Write-Output 'label cache: empty, nothing to refresh'
    return
}
foreach ($png in $pngs) {
    $relative = $png.Substring($cache.TrimEnd('\', '/').Length).TrimStart('\', '/')
    if ($relative -notmatch '^([0-9]+)[\\/]([0-9]+)[\\/]([0-9]+)\.png$') {
        throw "Invalid label tile path: $relative"
    }
    $z = 0; $x = 0; $y = 0
    if (-not [int]::TryParse($Matches[1], [ref]$z) -or $z -lt 0 -or $z -gt 30 -or
        -not [int]::TryParse($Matches[2], [ref]$x) -or
        -not [int]::TryParse($Matches[3], [ref]$y) -or
        $x -lt 0 -or $y -lt 0 -or $x -ge [Math]::Pow(2, $z) -or $y -ge [Math]::Pow(2, $z)) {
        throw "Label tile coordinates out of range: $relative"
    }
    # Fail before backup/rendering if a PNG is read-only or held by another process.
    $probe = [IO.File]::Open($png, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $probe.Dispose()
}

$sources = New-Object 'Collections.Generic.List[IDisposable]'
try {
    $places = 0
    foreach ($set in @('Synoptic', 'Cities')) {
        $path = Join-Path $game "WW\PlacenameSets\$set\0000_0000.wwp"
        Assert-PlainPath $path
        # Keep a read lock through rendering so validated sources cannot be replaced or truncated.
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $sources.Add($stream)
        $reader = [IO.BinaryReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true)
        try {
            $count = $reader.ReadInt32()
            if ($count -lt 0 -or $count -gt ($stream.Length - 4) / 13) { throw "Invalid WWP count: $set" }
            for ($i = 0; $i -lt $count; $i++) {
                $name = $reader.ReadString()
                $lat = $reader.ReadSingle()
                $lon = $reader.ReadSingle()
                $null = $reader.ReadUInt32()
                if (-not $name -or [float]::IsNaN($lat) -or [float]::IsInfinity($lat) -or
                    [float]::IsNaN($lon) -or [float]::IsInfinity($lon) -or
                    $lat -lt -90 -or $lat -gt 90 -or $lon -lt -180 -or $lon -gt 180) {
                    throw "Invalid WWP record: $set/$i"
                }
            }
            if ($stream.Position -ne $stream.Length) { throw "Trailing WWP data: $set" }
            $places += $count
        }
        finally { $reader.Dispose() }
    }
    if (-not $places) { throw 'Both placename sets are empty; refusing to erase labels' }
    if (-not $Compiler) {
        $Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
        if (-not [IO.File]::Exists($Compiler)) {
            $Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
        }
    }
    $output = Join-Path $repoRoot ('build\placename-tiles\' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $library = Join-Path $output 'PlacenameTileRenderer.dll'
    & $Compiler '-nologo' '-utf8output' '-codepage:65001' '-target:library' "-out:$library" `
        '-reference:System.dll' '-reference:System.Core.dll' '-reference:System.Drawing.dll' `
        '-reference:System.Windows.Forms.dll' '-reference:System.Xml.dll' `
        '-reference:System.IO.Compression.dll' '-reference:System.IO.Compression.FileSystem.dll' `
        (Join-Path $repoRoot 'src\shared\CMOShared.cs') (Join-Path $repoRoot 'src\installer\CMO_Chinese_Setup.cs')
    if ($LASTEXITCODE) { throw 'Placename tile renderer compilation failed' }
    $core = [Reflection.Assembly]::LoadFrom($library).GetType('CMOLocalizerInstaller.InstallerCore', $true)
    $method = $core.GetMethod('LocalizeMapLabelTiles', [Reflection.BindingFlags]'Static,NonPublic')
    if (-not $method -or $method.ReturnType -ne [int]) { throw 'Missing counted label renderer' }
    $saved = $method.Invoke($null, @($game))
    if ($saved -ne $pngs.Count) { throw "Label render incomplete: saved $saved of $($pngs.Count) PNGs" }
    foreach ($png in $pngs) {
        if (-not [IO.File]::Exists($png)) { throw "Rendered PNG is missing: $png" }
    }
    Write-Output "label cache: refreshed $saved PNG(s)"
}
finally { foreach ($source in $sources) { $source.Dispose() } }
