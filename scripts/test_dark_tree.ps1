# Independent DarkUI-shaped WinForms fixture; never loads Command.exe.
[CmdletBinding()]
param(
    [string]$Engine,
    [string]$Dictionary,
    [string]$OutputDir,
    [ValidateSet('search', 'nested', 'reentrant', 'collection-clear-refill', 'collection-remove-readd', 'collection-add-remove', 'collection-move', 'collection-burst', 'collection-during-write', 'replace', 'dispose', 'prehandle', 'benchmark', 'layout', 'layout-subscriptions', 'layout-removal', 'layout-exit', 'layout-unsubscribe', 'layout-rehook', 'exception', 'exception-replace', 'visible-priority', 'expand-scroll', 'collapse-priority', 'callback-scroll')]
    [string[]]$Mode = @('search', 'nested', 'reentrant', 'collection-clear-refill', 'collection-remove-readd', 'collection-add-remove', 'collection-move', 'collection-burst', 'collection-during-write', 'replace', 'dispose', 'prehandle', 'benchmark', 'layout', 'layout-subscriptions', 'layout-removal', 'layout-exit', 'layout-unsubscribe', 'layout-rehook', 'exception', 'exception-replace', 'visible-priority', 'expand-scroll', 'collapse-priority', 'callback-scroll')
)
$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if (-not $Engine) { $Engine = Join-Path $repoRoot 'build\CMOLocalizer.dll' }
if (-not $Dictionary) { $Dictionary = Join-Path $repoRoot 'dict\CMOLocalizer.json' }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\tests\dark-tree' }
$resolvedOutput = [IO.Path]::GetFullPath($OutputDir)
$resolvedEngine = [IO.Path]::GetFullPath($Engine)
$resolvedDictionary = [IO.Path]::GetFullPath($Dictionary)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$fixture = Join-Path $resolvedOutput 'DarkTreeTranslationTests.exe'
& $compiler -nologo -utf8output -target:exe -platform:anycpu "-out:$fixture" -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll (Join-Path $repoRoot 'src\tests\DarkTreeTranslationTests.cs')
if ($LASTEXITCODE) { throw 'Dark tree fixture compilation failed' }
$results = @()
foreach ($testMode in $Mode) {
    $output = & $fixture $resolvedEngine $resolvedDictionary $testMode 2>&1
    $code = $LASTEXITCODE
    $output | Tee-Object -FilePath (Join-Path $resolvedOutput "dark_tree_$testMode.log")
    $results += [PSCustomObject]@{ Mode = $testMode; ExitCode = $code }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resolvedOutput 'dark_tree_results.json') -Encoding utf8
if (@($results | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0) { exit 1 }
exit 0
