# Independent WebView2 fixture. Reads SDK binaries from the game; never launches it.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$EnginePath,
    [string]$DictionaryPath,
    [ValidateSet(600, 1500)][int]$Interval = 1500,
    [ValidateRange(5, 10)][int]$Samples = 8,
    [string]$OutputDir,
    [string]$Compiler = (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe')
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if (-not $DictionaryPath) { $DictionaryPath = Join-Path $repo 'dict\CMOLocalizer.json' }
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'build\webview2-host' }
$output = [IO.Path]::GetFullPath($OutputDir)
$game = [IO.Path]::GetFullPath($GameDir).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (($output + [IO.Path]::DirectorySeparatorChar).StartsWith($game, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The independent fixture output directory must be outside the game directory.'
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($name in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')) {
    $source = Join-Path $GameDir $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required WebView2 SDK binary missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $name) -Force
}
if (-not $EnginePath) {
    $engineOutput = Join-Path $output 'engine'
    & (Join-Path $scriptDir 'build.ps1') -OutputDir $engineOutput -Compiler $Compiler
    $EnginePath = Join-Path $engineOutput 'CMOLocalizer.dll'
}
$engine = [IO.Path]::GetFullPath($EnginePath)
$dictionary = [IO.Path]::GetFullPath($DictionaryPath)
$exe = Join-Path $output 'WebView2LatencyTests.exe'
$sourceFile = Join-Path $repo 'src\tests\WebView2LatencyTests.cs'
$compileArgs = @('-nologo', '-utf8output', '-target:winexe', '-platform:x64', "-out:$exe",
    '-reference:System.dll', '-reference:System.Core.dll', '-reference:System.Windows.Forms.dll', '-reference:System.Drawing.dll',
    ('-reference:' + (Join-Path $output 'Microsoft.Web.WebView2.Core.dll')),
    ('-reference:' + (Join-Path $output 'Microsoft.Web.WebView2.WinForms.dll')), $sourceFile)
& $Compiler @compileArgs
if ($LASTEXITCODE) { throw "WebView2 fixture compilation failed (exit $LASTEXITCODE)." }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stdout = Join-Path $output ("latency-$Interval-$stamp.log")
$stderr = Join-Path $output ("latency-$Interval-$stamp.stderr.log")
# A dedicated profile lives under the fixture's own directory. SDKs and profiles are ignored build outputs.
$runArgs = '"' + $engine + '" "' + $dictionary + '" ' + $Interval + ' ' + $Samples
$process = Start-Process -FilePath $exe -ArgumentList $runArgs -WorkingDirectory $output -WindowStyle Hidden `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(120)
while (-not $process.WaitForExit(1000)) {
    if ([DateTime]::UtcNow -gt $deadline) { $process.Kill(); throw "WebView2 fixture exceeded 120 seconds; see $stdout" }
}
$process.Refresh()
$text = Get-Content -LiteralPath $stdout -Raw -Encoding UTF8
if ($text) { Write-Output $text.TrimEnd() }
$errors = Get-Content -LiteralPath $stderr -Raw -Encoding UTF8
if ($errors) { Write-Output $errors.TrimEnd() }
if ($process.ExitCode) { throw "WebView2 fixture failed (exit $($process.ExitCode)); see $stdout" }
$json = @($text -split '\r?\n' | Where-Object { $_.StartsWith('{') }) | Select-Object -Last 1
if ($json) {
    $resultPath = [IO.Path]::ChangeExtension($stdout, '.json')
    [IO.File]::WriteAllText($resultPath, $json + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Result: $resultPath"
}
Write-Output "Log: $stdout"
