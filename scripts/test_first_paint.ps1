# Isolated STA fixtures only: no game process, installer, or registry changes.
[CmdletBinding()]
param(
    [string]$Engine,
    [string]$Dictionary,
    [string]$OutputDir,
    [ValidateSet('startup', 'mutation', 'wpf', 'wpf-dynamic', 'wpf-editable', 'input', 'burst', 'benchmark')]
    [string[]]$Mode = @('startup', 'mutation', 'wpf', 'wpf-dynamic', 'wpf-editable', 'input', 'burst', 'benchmark')
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if (-not $Engine) { $Engine = Join-Path $repoRoot 'build\CMOLocalizer.dll' }
if (-not $Dictionary) { $Dictionary = Join-Path $repoRoot 'dict\CMOLocalizer.json' }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\tests' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkDir = Split-Path -Parent $compiler
$source = Join-Path $repoRoot 'src\tests\FirstPaintTests.cs'
$resolvedEngine = [IO.Path]::GetFullPath($Engine)
$resolvedDictionary = [IO.Path]::GetFullPath($Dictionary)
foreach ($required in @($compiler, $source, $resolvedEngine, $resolvedDictionary)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing fixture input: $required" }
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$hostPath = Join-Path $resolvedOutput 'FirstPaintTests.exe'
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    (Join-Path $frameworkDir 'WPF\WindowsBase.dll'),
    (Join-Path $frameworkDir 'WPF\PresentationCore.dll'),
    (Join-Path $frameworkDir 'WPF\PresentationFramework.dll'),
    (Join-Path $frameworkDir 'System.Xaml.dll'))
$compilerArgs = @('-nologo', '-utf8output', '-target:exe', '-platform:anycpu',
    "-out:$hostPath", $source)
$compilerArgs += @($references | ForEach-Object { "-reference:$_" })
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "First-paint fixture compilation failed (exit $LASTEXITCODE)." }
$results = @()
foreach ($testMode in $Mode) {
    $runOutput = & $hostPath $resolvedEngine $resolvedDictionary $testMode 2>&1
    $exitCode = $LASTEXITCODE
    $runOutput | Tee-Object -FilePath (Join-Path $resolvedOutput "first_paint_$testMode.log")
    $results += [PSCustomObject]@{ Mode = $testMode; ExitCode = $exitCode }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resolvedOutput 'first_paint_results.json') -Encoding utf8
if (@($results | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0) { exit 1 }
exit 0
