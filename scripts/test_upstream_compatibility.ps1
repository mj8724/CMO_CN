# Hidden controls only. No game process, files, installer or SDK is used.
[CmdletBinding()]
param([string]$Engine, [string]$OutputDir)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..'))
if (-not $Engine) { $Engine = Join-Path $repo 'build\CMOLocalizer.dll' }
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'build\upstream-compatibility' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path -Parent $compiler
$references = @('System.dll', 'System.Core.dll', 'System.Data.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll',
    (Join-Path $framework 'WPF\WindowsBase.dll'),
    (Join-Path $framework 'WPF\PresentationCore.dll'),
    (Join-Path $framework 'WPF\PresentationFramework.dll'))
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$hostPath = Join-Path $OutputDir 'UpstreamCompatibilityTests.exe'
$argsForCompiler = @('/nologo', '/target:exe', "/out:$hostPath", (Join-Path $repo 'src\tests\UpstreamCompatibilityTests.cs'))
$argsForCompiler += @($references | ForEach-Object { "/reference:$_" })
& $compiler @argsForCompiler
if ($LASTEXITCODE) { throw 'Compatibility host compilation failed' }
& $hostPath ([IO.Path]::GetFullPath($Engine)) 2>&1 | Tee-Object -FilePath (Join-Path $OutputDir 'compatibility.log')
if ($LASTEXITCODE) { throw 'Upstream compatibility regression' }
