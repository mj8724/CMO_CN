[CmdletBinding()]
param([string]$Engine, [string]$OutputDir)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Engine) { $Engine = Join-Path $repo 'build\CMOLocalizer.dll' }
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'build\map-tile-pack' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$installer = Join-Path $OutputDir 'TilePackInstaller.dll'
& $compiler /nologo /utf8output /codepage:65001 /target:library "/out:$installer" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $repo 'src\shared\CMOShared.cs') (Join-Path $repo 'src\shared\MapLabelRenderer.cs') (Join-Path $repo 'src\installer\CMO_Chinese_Setup.cs')
if ($LASTEXITCODE) { throw 'Installer fixture compilation failed' }
$test = Join-Path $OutputDir 'MapLabelTilePackTests.exe'
& $compiler /nologo /utf8output /target:exe /reference:System.Drawing.dll "/out:$test" (Join-Path $repo 'src\tests\MapLabelTilePackTests.cs')
if ($LASTEXITCODE) { throw 'Tilepack test compilation failed' }
& $test $Engine $installer (Join-Path $OutputDir ('fixtures-' + [Guid]::NewGuid().ToString('N')))
if ($LASTEXITCODE) { throw 'Tilepack tests failed' }
