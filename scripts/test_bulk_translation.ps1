# Independent WinForms performance fixture. Never reads or runs the game.
[CmdletBinding()]
param(
    [string]$Engine,
    [string]$Dictionary,
    [string]$OutputDir,
    [ValidateSet('strings', 'tabs', 'combos', 'contracts')]
    [string[]]$Mode = @('strings', 'tabs', 'combos')
)
$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if (-not $Engine) { $Engine = Join-Path $repoRoot 'build\CMOLocalizer.dll' }
if (-not $Dictionary) { $Dictionary = Join-Path $repoRoot 'dict\CMOLocalizer.json' }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\bulk-profile' }
$resolvedOutput = [IO.Path]::GetFullPath($OutputDir)
$resolvedEngine = [IO.Path]::GetFullPath($Engine)
$resolvedDictionary = [IO.Path]::GetFullPath($Dictionary)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$fixture = Join-Path $resolvedOutput 'BulkTranslationTests.exe'
& $compiler -nologo -utf8output -optimize+ -target:exe -platform:anycpu "-out:$fixture" -reference:System.dll -reference:System.Core.dll -reference:System.Data.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Web.Extensions.dll (Join-Path $repoRoot 'src\tests\BulkTranslationTests.cs')
if ($LASTEXITCODE) { throw 'Bulk fixture compilation failed' }
$failed = $false
foreach ($testMode in $Mode) {
    $output = & $fixture $resolvedEngine $resolvedDictionary $testMode 2>&1
    $code = $LASTEXITCODE
    $jsonPath = Join-Path $resolvedOutput "bulk_$testMode.json"
    [IO.File]::WriteAllText($jsonPath, ($output -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
    Write-Output "Bulk fixture $testMode exit=$code output=$jsonPath"
    if ($code) { $failed = $true }
}
if ($failed) { throw 'Bulk fixture contract failure; see the JSON contractFailures array.' }
