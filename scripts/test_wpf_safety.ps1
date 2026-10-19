# Hidden STA native sources only; no game process or active window.
[CmdletBinding()]
param(
    [string]$Engine,
    [string]$OutputDir,
    [ValidateSet('headers', 'editable', 'collection', 'lifecycle', 'dispatchers')]
    [string[]]$Mode = @('headers', 'editable', 'collection', 'lifecycle', 'dispatchers')
)
$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if (-not $Engine) { $Engine = Join-Path $repoRoot 'build\CMOLocalizer.dll' }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\wpf-safety' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path -Parent $compiler
$resolvedOutput = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$hostPath = Join-Path $resolvedOutput 'WpfSafetyTests.exe'
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll',
    (Join-Path $framework 'WPF\WindowsBase.dll'),
    (Join-Path $framework 'WPF\PresentationCore.dll'),
    (Join-Path $framework 'WPF\PresentationFramework.dll'))
$compilerArgs = @('-nologo', '-utf8output', '-target:exe', "-out:$hostPath",
    (Join-Path $repoRoot 'src\tests\WpfSafetyTests.cs'))
$compilerArgs += @($references | ForEach-Object { "-reference:$_" })
& $compiler @compilerArgs
if ($LASTEXITCODE) { throw 'WPF safety fixture compilation failed' }
$results = @()
foreach ($testMode in $Mode) {
    $output = & $hostPath ([IO.Path]::GetFullPath($Engine)) $testMode 2>&1
    $code = $LASTEXITCODE
    $output | Tee-Object -FilePath (Join-Path $resolvedOutput "wpf_safety_$testMode.log")
    $results += [pscustomobject]@{ Mode = $testMode; ExitCode = $code }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resolvedOutput 'results.json') -Encoding utf8
if (@($results | Where-Object { $_.ExitCode -ne 0 }).Count) { exit 1 }
exit 0
