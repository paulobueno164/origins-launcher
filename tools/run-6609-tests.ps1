# Headless end-to-end test of the launcher against a 6609 update set (docs\launcher-6609.md, "Teste"):
# tools\DevUpdateServer serves the set with fault injection, tools\LauncherTests runs the launcher's own update and
# start code. No window, no game: the start is captured (Updater.StartProcess), never run.
#
#   powershell -File tools\run-6609-tests.ps1 [-Updates C:\conquer\site-data\updates-6609-staging] [-Dir <temp install>] [-Keep]
#
# Needs ~2x the client on disk while it runs (the install + its downloaded pack parts); the install is deleted at the
# end unless -Keep.
param(
    [string]$Updates = 'C:\conquer\site-data\updates-6609-staging',
    [string]$Client = 'C:\conquer\client\6609',
    [string]$LangRoot = 'C:\conquer\client-lang-6609',
    [string]$Dir = (Join-Path $env:TEMP 'origins-6609-launcher-test'),
    [int]$Port = 5099,
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$root = Split-Path $PSScriptRoot
$logDir = Join-Path $env:TEMP 'origins-6609-launcher-test-logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
if ([IO.Path]::GetFullPath($Dir).TrimEnd('\') -ieq [IO.Path]::GetFullPath($Client).TrimEnd('\')) { throw 'the test install must not be the client folder' }

foreach ($p in 'DevUpdateServer', 'LauncherTests') {
    dotnet build "$root\tools\$p\$p.csproj" -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build of $p failed" }
}
$server = "$root\tools\DevUpdateServer\bin\Release\net10.0\DevUpdateServer.dll"
$tests = "$root\tools\LauncherTests\bin\Release\net48\LauncherTests.exe"
$base = "http://127.0.0.1:$Port/updates/"
$failed = @()
$script:serverProc = $null

function StartServer([string]$name, [string[]]$extra) {
    StopServer
    $out = Join-Path $logDir "server-$name.txt"
    $argList = @("`"$server`"", '--root', "`"$Updates`"", '--port', $Port) + $extra
    $script:serverProc = Start-Process dotnet -ArgumentList $argList -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) {
        try { $c = New-Object Net.Sockets.TcpClient('127.0.0.1', $Port); $c.Close(); return $out } catch { Start-Sleep -Milliseconds 500 }
    }
    throw "DevUpdateServer did not start ($out)"
}
function StopServer {
    if ($script:serverProc -and -not $script:serverProc.HasExited) { Stop-Process -Id $script:serverProc.Id -Force; $script:serverProc.WaitForExit() }
    $script:serverProc = $null
}
function T([string]$title) {
    Write-Output "---- $title"
    $out = & $tests @args
    $code = $LASTEXITCODE
    $out | ForEach-Object { Write-Output "   $_" }
    if ($code -ne 0) { $script:failed += $title }
}
function Require([bool]$ok, [string]$what) {
    Write-Output "   $(if ($ok) { 'ok  ' } else { 'FAIL' }) $what"
    if (-not $ok) { $script:failed += $what }
}

if (Test-Path $Dir) { Remove-Item -LiteralPath $Dir -Recurse -Force }
New-Item -ItemType Directory -Force $Dir | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    # 1. first install (pt), the launcher closed at 30% of the pack; every 9th download is cut half-way
    $log1 = StartServer 'install-cut' @('--cut-every', '9')
    T 'install pt, closed at 30% of the pack' update --dir $Dir --base-url $base --lang pt --cancel-at 0.3
    # 2. the next start resumes: finished parts are kept, the partial one continues with a Range request
    $log2 = StartServer 'install-resume' @('--cut-every', '9')
    T 'install pt, resumed' update --dir $Dir --base-url $base --lang pt
    StopServer
    $resumed = @(Select-String -LiteralPath $log2 -Pattern 'GET /updates/pack/.*range=bytes=\d+-').Count
    $cuts = @(Select-String -LiteralPath $log1, $log2 -Pattern '   cut ').Count
    $parts = @(Select-String -LiteralPath $log2 -Pattern 'GET /updates/pack/.*\.bin range=$').Count
    Require ($resumed -gt 0) "resume: $resumed pack request(s) with a Range header after the restart and the cuts"
    Require ($cuts -gt 0) "the server cut $cuts download(s) half-way; all were resumed"
    Write-Output "   pack parts downloaded from 0 after the restart: $parts"
    T 'fresh install = client\6609 + pt pack' verify --dir $Dir --lang pt --client $Client --lang-root $LangRoot
    Require (-not (Test-Path (Join-Path $Dir '.originsupdate\pack\*'))) 'pack parts deleted after the install'

    # 3. language switches: only the pack files change
    $pt = @(Get-ChildItem (Join-Path $LangRoot 'pt') -Recurse -File).Count
    $fil = @(Get-ChildItem (Join-Path $LangRoot 'fil') -Recurse -File).Count
    StartServer 'languages' @() | Out-Null
    T 'switch to English' update --dir $Dir --base-url $base --lang en --expect $pt
    T 'English install = client\6609' verify --dir $Dir --lang en --client $Client --lang-root $LangRoot
    T 'switch to Filipino' update --dir $Dir --base-url $base --lang fil --expect $fil
    T 'Filipino install = client\6609 + fil pack' verify --dir $Dir --lang fil --client $Client --lang-root $LangRoot
    T 'switch back to Portuguese' update --dir $Dir --base-url $base --lang pt
    StopServer

    # 4. repair: missing, truncated and silently corrupted files; one download corrupted by the server once
    T 'damage the install' damage --dir $Dir
    StartServer 'repair' @('--corrupt', 'ini/StrRes.ini.gz') | Out-Null
    T 'normal start repairs missing/truncated (4)' update --dir $Dir --base-url $base --lang pt --expect 4
    T '"Verify files" repairs the silent corruption (2)' update --dir $Dir --base-url $base --lang pt --full --expect 2
    $launcherLog = Join-Path $Dir '.originsupdate\launcher.log'
    Require ((Select-String -LiteralPath $launcherLog -Pattern 'retry 1 ini\\StrRes.ini: InvalidDataException' -Quiet)) 'the corrupted download failed its SHA-256 and was downloaded again'
    T 'repaired install = client\6609 + pt pack (player''s GameSetUp.ini kept)' verify --dir $Dir --lang pt --client $Client --lang-root $LangRoot --keep-changed 'ini\GameSetUp.ini'

    # 5. start, 5095 -> 6609, conditional manifest, offline, settings
    T 'game start per language (stubbed, nothing runs)' start --dir $Dir --base-url $base
    T '5095 install updated to 6609' migrate --dir $Dir --base-url $base
    T 'up-to-date folder: manifest 304' etag --dir $Dir --base-url $base
    StartServer 'down' @('--down') | Out-Null
    T 'update server down: play offline' offline --dir $Dir --base-url $base
    StopServer
    T '1.5.0 settings.json' settings --dir $Dir
    T 'screen choice and graphics-mode restart' screen --dir $Dir
    T '2.3.0: 5095 header, login tokens for the game''s own login screen' relogin --dir $Dir
    # 2.4.0: the UDP tunnel's route; through the dev stack's gateway when it runs (dev-6609.ps1: UDP 25817)
    if (Get-NetUDPEndpoint -LocalAddress 127.0.0.1 -LocalPort 25817 -ErrorAction SilentlyContinue) {
        T '2.4.0: the UDP tunnel route, cut and restored (dev gateway)' route --dir $Dir --gateway 127.0.0.1:25817 --login 29958 --game 25816
    }
    else {
        T '2.4.0: the UDP tunnel route (no dev gateway: direct cases only)' route --dir $Dir
    }
}
finally {
    StopServer
    Copy-Item -LiteralPath (Join-Path $Dir '.originsupdate\launcher.log') -Destination (Join-Path $logDir 'launcher.log') -ErrorAction SilentlyContinue
    if (-not $Keep -and (Test-Path $Dir)) { Remove-Item -LiteralPath $Dir -Recurse -Force }
}

Write-Output "---- $(if ($failed.Count -eq 0) { 'ALL PASSED' } else { "FAILED: $($failed -join '; ')" }) in $([int]$clock.Elapsed.TotalMinutes) min (logs: $logDir)"
if ($failed.Count -gt 0) { exit 1 }
