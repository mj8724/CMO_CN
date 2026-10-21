[CmdletBinding()]
param([string]$OutputDir)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'build\map-tile-delivery' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$hostExe = Join-Path $OutputDir 'MapTileDeliveryTests.exe'
& $compiler /nologo /utf8output /langversion:5 /target:exe /reference:System.Drawing.dll "/out:$hostExe" (Join-Path $repo 'src\tests\MapTileDeliveryTests.cs')
if ($LASTEXITCODE) { throw 'Delivery host compilation failed' }
$valid = Join-Path $OutputDir 'valid.dat'
& $hostExe --write-pack $valid
if ($LASTEXITCODE) { throw 'Fixture generation failed' }
$bad = Join-Path $OutputDir 'bad.dat'
[IO.File]::WriteAllBytes($bad, [byte[]](1,2,3))
foreach ($mode in @('none','valid','bad')) {
    $fixture = Join-Path $OutputDir ("DeliveryInstaller-$mode.dll")
    $resourceArgs = @()
    if ($mode -eq 'valid') { $resourceArgs += "/resource:$valid,MAP_TILES" }
    if ($mode -eq 'bad') { $resourceArgs += "/resource:$bad,MAP_TILES" }
    & $compiler /nologo /utf8output /codepage:65001 /langversion:5 /target:library "/out:$fixture" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll @resourceArgs (Join-Path $repo 'src\shared\CMOShared.cs') (Join-Path $repo 'src\shared\MapLabelRenderer.cs') (Join-Path $repo 'src\installer\CMO_Chinese_Setup.cs')
    if ($LASTEXITCODE) { throw 'Installer fixture compilation failed' }
    & $hostExe $fixture $mode (Join-Path $OutputDir ('fixtures-' + [Guid]::NewGuid().ToString('N')))
    if ($LASTEXITCODE) { throw "Delivery tests failed: $mode" }
}
