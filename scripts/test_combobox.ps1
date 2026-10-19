# Isolated STA ComboBox fixtures; no game assembly, process, or installed writes.
[CmdletBinding()]
param(
    [string]$Engine,
    [string]$OutputDir,
    [ValidateSet('refill', 'protection', 'lifecycle', 'reentrant-refill', 'queued-retry', 'append-retry', 'gate', 'bound', 'benchmark', 'fight', 'noop', 'recovery', 'binding-swap', 'object-identity')]
    [string[]]$Mode = @('refill', 'protection', 'lifecycle', 'reentrant-refill', 'queued-retry', 'append-retry', 'gate', 'bound', 'benchmark', 'fight', 'noop', 'recovery', 'binding-swap', 'object-identity')
)
$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if (-not $Engine) { $Engine = Join-Path $repoRoot 'build\CMOLocalizer.dll' }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\combobox-tests' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $repoRoot 'src\tests\ComboBoxTranslationTests.cs'
$resolvedEngine = [IO.Path]::GetFullPath($Engine)
foreach ($required in @($compiler, $source, $resolvedEngine)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing fixture input: $required" }
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$hostPath = Join-Path $resolvedOutput 'ComboBoxTranslationTests.exe'
& $compiler '-nologo' '-utf8output' '-target:exe' '-platform:anycpu' "-out:$hostPath" '-reference:System.dll' '-reference:System.Core.dll' '-reference:System.Data.dll' '-reference:System.Drawing.dll' '-reference:System.Windows.Forms.dll' $source
if ($LASTEXITCODE -ne 0) { throw "Combo fixture compilation failed (exit $LASTEXITCODE)." }
$results = @()
foreach ($testMode in $Mode) {
    $runOutput = & $hostPath $resolvedEngine $testMode 2>&1
    $exitCode = $LASTEXITCODE
    $runOutput | Tee-Object -FilePath (Join-Path $resolvedOutput "combobox_$testMode.log")
    $results += [PSCustomObject]@{ Mode = $testMode; ExitCode = $exitCode }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resolvedOutput 'combobox_results.json') -Encoding utf8
if (@($results | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0) { exit 1 }
exit 0
