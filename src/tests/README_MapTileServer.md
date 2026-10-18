# Isolated map tile server regression tests

The test host loads a built CMOLocalizer.dll and invokes only its map service. It does not initialize the game, install localization, or change registry settings. Each mode runs in a fresh .NET Framework process and uses loopback HTTP.

After building the engine, run from the repository root in Windows PowerShell:

```powershell
New-Item -ItemType Directory -Force build | Out-Null
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:exe /reference:System.Drawing.dll /out:build/MapTileServerTests.exe src/tests/MapTileServerTests.cs
if ($LASTEXITCODE) { throw 'Test host compilation failed' }
$mapRun = [Guid]::NewGuid().ToString('N')
foreach ($mapMode in @('starvation', 'protocol', 'idle', 'batch')) {
    $mapFixture = Join-Path $PWD ('build/map-tests-' + $mapRun + '-' + $mapMode)
    foreach ($mapSet in @('Synoptic', 'Cities')) {
        $mapTarget = Join-Path $mapFixture ('WW/PlacenameSets/' + $mapSet)
        New-Item -ItemType Directory -Force $mapTarget | Out-Null
        Copy-Item -LiteralPath ('assets/placenames/' + $mapSet + '.wwp') -Destination (Join-Path $mapTarget '0000_0000.wwp')
    }
    & ./build/MapTileServerTests.exe (Join-Path $PWD 'build/CMOLocalizer.dll') $mapFixture $mapMode
    if ($LASTEXITCODE) { throw ('Map test failed: ' + $mapMode) }
}
```

The unique run ID keeps each fixture fresh because the batch test counts the sampled log lines. A test runner should enforce a 35-second process timeout.

The starvation test checks the actual worker maximum and available count before issuing a synchronous request with all workers blocked. It releases every blocker and verifies recovery. Protocol checks cover real WWP landmarks at zoom levels 3, 7, 9 and 22, malformed paths, invalid coordinates and non-GET methods. The idle test checks that an incomplete request is closed and the server remains usable. The batch test uses eight dedicated client threads, twenty sequential requests and the diagnostic log limit.

Stock engine results: starvation, protocol and idle fail. Fixed engine results: all four modes pass. The stock batch failure concerns missing bounded diagnostics, not concurrent throughput.
