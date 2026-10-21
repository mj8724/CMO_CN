# Isolated fixtures only. Never builds the engine, runs CMO or writes installed files.
[CmdletBinding()]
param(
    [ValidateSet('first-paint', 'combobox', 'dark-tree', 'queue', 'idle', 'wpf', 'upstream', 'lifecycle', 'webview', 'bulk', 'richtext', 'help-tooltip')]
    [string[]]$Suite = @('first-paint', 'combobox', 'dark-tree', 'queue', 'idle', 'wpf', 'upstream', 'lifecycle', 'webview', 'bulk', 'richtext', 'help-tooltip'),
    [string[]]$Mode,
    [string]$Engine,
    [string]$Dictionary,
    [string]$OutputDir,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Engine) { $Engine = Join-Path $repo 'build\CMOLocalizer.dll' }
if (-not $Dictionary) { $Dictionary = Join-Path $repo 'dict\CMOLocalizer.json' }
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'build\ui-tests' }
$Engine = [IO.Path]::GetFullPath($Engine)
$Dictionary = [IO.Path]::GetFullPath($Dictionary)
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path -Parent $compiler
$winforms = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
$wpf = @((Join-Path $framework 'WPF\WindowsBase.dll'), (Join-Path $framework 'WPF\PresentationCore.dll'),
    (Join-Path $framework 'WPF\PresentationFramework.dll'), (Join-Path $framework 'System.Xaml.dll'))
$plans = [ordered]@{
    'first-paint' = @{ Source = 'FirstPaintTests'; References = $winforms + $wpf; Options = @('-platform:anycpu'); Dictionary = $true
        Modes = @('startup', 'mutation', 'wpf', 'wpf-dynamic', 'wpf-editable', 'input', 'burst', 'benchmark') }
    'combobox' = @{ Source = 'ComboBoxTranslationTests'; References = $winforms + 'System.Data.dll'; Options = @('-platform:anycpu')
        Modes = @('refill', 'protection', 'lifecycle', 'reentrant-refill', 'queued-retry', 'append-retry', 'gate', 'bound',
            'benchmark', 'fight', 'noop', 'recovery', 'binding-swap', 'object-identity') }
    'dark-tree' = @{ Source = 'DarkTreeTranslationTests'; References = $winforms; Options = @('-platform:anycpu'); Dictionary = $true
        Modes = @('search', 'nested', 'reentrant', 'collection-clear-refill', 'collection-remove-readd', 'collection-add-remove',
            'collection-move', 'collection-burst', 'collection-during-write', 'replace', 'dispose', 'prehandle', 'benchmark',
            'layout', 'layout-subscriptions', 'layout-removal', 'layout-exit', 'layout-unsubscribe', 'layout-rehook',
            'exception', 'exception-replace', 'visible-priority', 'expand-scroll', 'collapse-priority', 'callback-scroll') }
    'queue' = @{ Source = 'UiSchedulingTests'; References = $winforms; Options = @('-optimize+'); Dictionary = $true
        Modes = @('coalesce', 'visible', 'yield', 'reentrant', 'lifecycle', 'failure', 'nested') }
    'idle' = @{ Source = 'UiIdleContinuationTests'; References = $winforms; Options = @('-optimize+')
        Modes = @('idle', 'cancel', 'fallback', 'reentrant', 'lifecycle') }
    'wpf' = @{ Source = 'WpfSafetyTests'; References = @('System.dll', 'System.Core.dll') + $wpf
        Modes = @('headers', 'editable', 'collection', 'lifecycle', 'dispatchers') }
    'upstream' = @{ Source = 'UpstreamCompatibilityTests'; NoMode = $true
        References = @('System.dll', 'System.Core.dll', 'System.Data.dll', 'System.Windows.Forms.dll') + $wpf; Modes = @('all') }
    'lifecycle' = @{ Source = 'UiLifecycleTests'; References = $winforms + $wpf; Options = @('-platform:anycpu'); Dictionary = $true
        Modes = @('control', 'window', 'existing', 'inputs', 'secondary', 'wpf', 'dispose', 'benchmark', 'polling-benchmark') }
    'webview' = @{ Source = 'WebViewTranslationHostTests'; References = @('System.Windows.Forms.dll'); NoMode = $true; Modes = @('all') }
    'richtext' = @{ Source = 'RichTextTranslationTests'; References = $winforms + @('System.Web.Extensions.dll')
        Options = @('-platform:anycpu'); Dictionary = $true; Modes = @('all', 'corpus') }
    'help-tooltip' = @{ Source = 'HelpTooltipTests'; References = $winforms + @('System.Web.Extensions.dll'); NoMode = $true
        Options = @('-platform:anycpu'); Dictionary = $true; Modes = @('all') }
    'bulk' = @{ Source = 'BulkTranslationTests'; References = $winforms + @('System.Data.dll', 'System.Web.Extensions.dll')
        Options = @('-optimize+', '-platform:anycpu'); Dictionary = $true; Modes = @('contracts') }
}
if (-not $Suite.Count) { throw 'Select at least one suite.' }
if ($Mode -and $Suite.Count -ne 1) { throw '-Mode requires exactly one -Suite.' }
if ($Mode) {
    foreach ($name in $Mode) {
        if ($plans[$Suite[0]].Modes -notcontains $name) { throw "Unknown mode '$name' for suite '$($Suite[0])'." }
    }
}
foreach ($required in @($compiler, $Engine)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing test input: $required" }
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
function Invoke-Logged([string]$File, [string[]]$Arguments, [string]$Log) {
    # Native stderr belongs in the log; its exit code determines test failure.
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = $null
    $lines = @(& $File @Arguments 2>&1)
    $code = $global:LASTEXITCODE
    [IO.File]::WriteAllText($Log, (($lines -join "`n") -replace "`r`n", "`n") + "`n", $utf8)
    if ($null -eq $code) { throw "Could not execute $File; see $Log" }
    return $code
}
$fixtures = @()
$results = @()
$completed = $false
$engineHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Engine).Hash.ToLowerInvariant()
$usesDictionary = @($Suite | Where-Object { $plans[$_].Dictionary }).Count -gt 0
$dictionaryHash = if ($usesDictionary -and (Test-Path -LiteralPath $Dictionary -PathType Leaf)) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Dictionary).Hash.ToLowerInvariant()
} else { $null }
try {
    foreach ($name in $Suite) {
        $plan = $plans[$name]
        $source = Join-Path $repo ("src\tests\" + $plan.Source + '.cs')
        if ($plan.Dictionary -and -not (Test-Path -LiteralPath $Dictionary -PathType Leaf)) { throw "Missing dictionary: $Dictionary" }
        $fixture = Join-Path $OutputDir ($plan.Source + '.exe')
        $compileLog = Join-Path $OutputDir "$name-compile.log"
        $arguments = @('-nologo', '-utf8output', '-codepage:65001', '-target:exe', "-out:$fixture", $source) + $plan.Options
        $arguments += @($plan.References | ForEach-Object { "-reference:$_" })
        $code = Invoke-Logged $compiler $arguments $compileLog
        $fixtures += [pscustomobject]@{ Suite = $name; Source = $source
            SourceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash.ToLowerInvariant(); CompileExitCode = $code }
        if ($code) { throw "Fixture compilation failed for $name; see $compileLog" }
        $fixtures[-1] | Add-Member NoteProperty FixtureSha256 (Get-FileHash -Algorithm SHA256 -LiteralPath $fixture).Hash.ToLowerInvariant()
        $modes = if ($Mode) { $Mode } else { $plan.Modes }
        foreach ($testMode in $modes) {
            $corpus = $null
            if ($name -eq 'richtext' -and $testMode -eq 'corpus') {
                $corpus = Join-Path $OutputDir 'hotkeys-corpus.json'
                $auditLog = Join-Path $OutputDir 'richtext-corpus-audit.log'
                $auditArgs = @((Join-Path $repo 'scripts\audit_richtext.py'), '--dictionary', $Dictionary, '--output', $corpus)
                $code = Invoke-Logged $Python $auditArgs $auditLog
                if ($code) { throw "Rich text corpus audit failed; see $auditLog" }
            }
            $arguments = @($Engine)
            if ($plan.Dictionary) { $arguments += $Dictionary }
            if (-not $plan.NoMode) { $arguments += $testMode }
            if ($corpus) { $arguments += $corpus }
            $log = Join-Path $OutputDir "$name-$testMode.log"
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $code = Invoke-Logged $fixture $arguments $log
            $results += [pscustomobject]@{ Suite = $name; Mode = $testMode; ExitCode = $code
                WallSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3); Log = $log }
            Write-Output "$name/$testMode exit=$code"
        }
    }
    $completed = $true
}
finally {
    $report = [pscustomobject]@{ Engine = $Engine; EngineSha256 = $engineHash; Fixtures = $fixtures; Results = $results
        DictionaryPath = $(if ($usesDictionary) { $Dictionary } else { $null }); DictionarySha256 = $dictionaryHash
        Completed = $completed; Passed = $completed -and @($results | Where-Object { $_.ExitCode -ne 0 }).Count -eq 0 }
    [IO.File]::WriteAllText((Join-Path $OutputDir 'results.json'),
        (($report | ConvertTo-Json -Depth 6) -replace "`r`n", "`n") + "`n", $utf8)
}
if (-not $report.Passed) { exit 1 }
