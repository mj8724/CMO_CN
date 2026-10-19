# Invisible isolated controls; never starts or operates CMO.
[CmdletBinding()]
param([string]$Engine, [string]$Dictionary, [string]$OutputDir)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..'))
if (-not $Engine) { $Engine = Join-Path $repoRoot 'build\CMOLocalizer.dll' }
if (-not $Dictionary) { $Dictionary = Join-Path $repoRoot 'dict\CMOLocalizer.json' }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\ui-scheduling' }
$resolvedOutput = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$fixture = Join-Path $resolvedOutput 'UiSchedulingTests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler -nologo -utf8output -optimize+ -target:exe "-out:$fixture" -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll (Join-Path $repoRoot 'src\tests\UiSchedulingTests.cs')
if ($LASTEXITCODE) { throw 'UI scheduling fixture compilation failed' }
$results = @()
foreach ($testMode in @('coalesce', 'visible', 'yield', 'reentrant', 'lifecycle', 'failure', 'nested')) {
    $output = & $fixture ([IO.Path]::GetFullPath($Engine)) ([IO.Path]::GetFullPath($Dictionary)) $testMode 2>&1
    $code = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $resolvedOutput "scheduling_$testMode.log"), ($output -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
    Write-Output ($output -join "`n")
    $results += [PSCustomObject]@{ Mode = $testMode; ExitCode = $code }
}
[IO.File]::WriteAllText((Join-Path $resolvedOutput 'scheduling_results.json'), (($results | ConvertTo-Json) -replace "`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
if (@($results | Where-Object { $_.ExitCode -ne 0 }).Count) { throw 'UI scheduling regression failure' }
