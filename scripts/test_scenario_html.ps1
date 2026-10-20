# Real installer methods with synthetic embedded HTML only; no installer entry point or game access.
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$Compiler = (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe')
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..'))
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build\scenario-html-tests' }
$output = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$hostPath = Join-Path $output 'ScenarioHtmlInstallerTests.exe'
$source = Join-Path $repoRoot 'src\tests\ScenarioHtmlInstallerTests.cs'
$references = @('-reference:System.dll', '-reference:System.Core.dll',
    '-reference:System.IO.Compression.dll', '-reference:System.IO.Compression.FileSystem.dll')
& $Compiler '-nologo' '-utf8output' '-codepage:65001' '-target:exe' "-out:$hostPath" @references $source
if ($LASTEXITCODE) { throw 'Scenario HTML fixture compilation failed' }
$payload = Join-Path $output 'ScenarioHtmlFixture.zip'
& $hostPath '--prepare' $payload
if ($LASTEXITCODE) { throw 'Synthetic scenario payload preparation failed' }
$installer = Join-Path $output 'ScenarioHtmlInstaller.dll'
$installerArgs = @('-nologo', '-utf8output', '-codepage:65001', '-target:library', "-out:$installer",
    "-resource:$payload,SCEN_HTML_ZIP", '-reference:System.Windows.Forms.dll',
    '-reference:System.Drawing.dll', '-reference:System.Xml.dll') + $references
$installerArgs += @((Join-Path $repoRoot 'src\shared\CMOShared.cs'),
    (Join-Path $repoRoot 'src\installer\CMO_Chinese_Setup.cs'))
$sharedRenderer = Join-Path $repoRoot 'src\shared\MapLabelRenderer.cs'
if (Test-Path -LiteralPath $sharedRenderer -PathType Leaf) {
    $installerArgs += $sharedRenderer
}
& $Compiler @installerArgs
if ($LASTEXITCODE) { throw 'Installer test library compilation failed' }
$runOutput = @(& $hostPath $installer (Join-Path $output 'cases') 2>&1)
$code = $LASTEXITCODE
$lf = [string][char]10
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $output 'scenario_html.log'), ($runOutput -join $lf) + $lf, $utf8)
Write-Output ($runOutput -join $lf)
$result = [PSCustomObject]@{
    ExitCode = $code
    InstallerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash.ToLowerInvariant()
    PayloadSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $payload).Hash.ToLowerInvariant()
    SourceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $output 'scenario_html_results.json'), (($result | ConvertTo-Json) -replace "`r`n", $lf) + $lf, $utf8)
if ($code) { throw 'Scenario HTML installer contract failed; see scenario_html.log' }
