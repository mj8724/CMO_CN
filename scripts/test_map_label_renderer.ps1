# Pure bitmap regression and an optional explicit-input sample renderer. No game process or installed files are changed.
[CmdletBinding()]
param([string]$Engine, [string]$OutputDir)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..'))
if (-not $Engine) { $Engine = Join-Path $repo 'build\CMOLocalizer.dll' }
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'build\map-label-renderer' }
$Engine = [IO.Path]::GetFullPath($Engine)
if (-not (Test-Path -LiteralPath $Engine -PathType Leaf)) { throw "Engine not found: $Engine" }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach ($fixture in @('MapLabelRendererTests', 'MapLabelSamples')) {
    $arguments = @('/nologo', '/utf8output', '/target:exe', '/reference:System.Drawing.dll',
        ('/out:' + (Join-Path $OutputDir ($fixture + '.exe'))),
        (Join-Path $repo ('src\tests\' + $fixture + '.cs')))
    & $compiler @arguments
    if ($LASTEXITCODE) { throw "$fixture compilation failed" }
}
& (Join-Path $OutputDir 'MapLabelRendererTests.exe') $Engine 2>&1 |
    Tee-Object -FilePath (Join-Path $OutputDir 'map_label_renderer.log')
if ($LASTEXITCODE) { throw 'Map label renderer regression failed' }
