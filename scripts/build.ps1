# Engine-only build: no game assets, installer, or installed-game writes.
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$Compiler = (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build' }
$engineDir = Join-Path $repoRoot 'src\engine'
$scriptResource = Join-Path $engineDir 'WebViewTranslation.js'
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
    throw "C# compiler not found: $Compiler"
}
if (-not (Test-Path -LiteralPath $scriptResource -PathType Leaf)) {
    throw "Required WebView translation resource is missing: $scriptResource"
}
$sources = @(Get-ChildItem -LiteralPath $engineDir -Filter '*.cs' | Sort-Object Name)
if ($sources.Count -eq 0) { throw "No engine sources in $engineDir" }
$frameworkDir = Split-Path -Parent $Compiler
$references = @('System.dll', 'System.Core.dll', 'System.Data.dll',
    'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Xml.dll', 'System.Xml.Linq.dll',
    (Join-Path $frameworkDir 'WPF\WindowsBase.dll'),
    (Join-Path $frameworkDir 'WPF\PresentationCore.dll'),
    (Join-Path $frameworkDir 'WPF\PresentationFramework.dll'),
    (Join-Path $frameworkDir 'System.Xaml.dll'))
$resolvedOutput = [IO.Path]::GetFullPath($OutputDir)
$assemblyPath = Join-Path $resolvedOutput 'CMOLocalizer.dll'
$compilerArgs = @('-nologo', '-utf8output', '-target:library', '-platform:anycpu', '-optimize+',
    "-out:$assemblyPath", "-resource:$scriptResource,CMOLocalizer.WebViewTranslation.js")
$compilerArgs += @($references | ForEach-Object { "-reference:$_" })
$compilerArgs += @($sources | ForEach-Object { $_.FullName })
if ($CheckOnly) {
    Write-Output "Engine inputs valid: $($sources.Count) C# sources and required WebView JavaScript."
    return
}
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
& $Compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Engine compilation failed (exit $LASTEXITCODE)." }
Write-Output "Engine built: $assemblyPath"
