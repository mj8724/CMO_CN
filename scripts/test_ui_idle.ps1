# Hidden ApplicationContext/control fixtures only; never starts or operates CMO.
[CmdletBinding()]
param(
    [string]$Engine,
    [string]$OutputDir,
    [ValidateSet('idle', 'cancel', 'fallback', 'reentrant', 'lifecycle')]
    [string[]]$Mode = @('idle', 'cancel', 'fallback', 'reentrant', 'lifecycle')
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..'))
if (-not $Engine) { $Engine = Join-Path $repoRoot 'build\CMOLocalizer.dll' }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\ui-idle' }
$resolvedEngine = [IO.Path]::GetFullPath($Engine)
$resolvedOutput = [IO.Path]::GetFullPath($OutputDir)
$source = [IO.Path]::GetFullPath((Join-Path $repoRoot 'src\tests\UiIdleContinuationTests.cs'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach ($required in @($compiler, $source, $resolvedEngine)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing test input: $required" }
}
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$fixture = Join-Path $resolvedOutput 'UiIdleContinuationTests.exe'
& $compiler -nologo -utf8output -optimize+ -target:exe "-out:$fixture" -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll $source
if ($LASTEXITCODE) { throw 'UI idle fixture compilation failed' }
$utf8 = New-Object Text.UTF8Encoding($false)
$lf = [string][char]10
$results = @()
foreach ($testMode in $Mode) {
    $output = @(& $fixture $resolvedEngine $testMode 2>&1)
    $code = $LASTEXITCODE
    $text = ($output -join $lf) + $lf
    [IO.File]::WriteAllText((Join-Path $resolvedOutput "idle_$testMode.log"), $text, $utf8)
    Write-Output $text
    $results += [PSCustomObject]@{ Mode = $testMode; ExitCode = $code }
}
$metadata = [PSCustomObject]@{
    Engine = $resolvedEngine
    EngineSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedEngine).Hash.ToLowerInvariant()
    SourceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash.ToLowerInvariant()
    FixtureSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $fixture).Hash.ToLowerInvariant()
    WindowPolicy = 'Hidden controls and ApplicationContext only; no Form.Show, focus or activation.'
    Results = $results
}
[IO.File]::WriteAllText((Join-Path $resolvedOutput 'idle_results.json'), (($metadata | ConvertTo-Json -Depth 6) -replace ([string][char]13 + [char]10), $lf) + $lf, $utf8)
if (@($results | Where-Object { $_.ExitCode -ne 0 }).Count) { throw 'UI idle continuation regression failure' }
