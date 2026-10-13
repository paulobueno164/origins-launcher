# Publishes the game client and the launcher to the site's /updates folder.
# Players get the change the next time they open the launcher.
#
#   .\publish-update.ps1                       # client from C:\conquer\client\5095, launcher built here
#   .\publish-update.ps1 -Client D:\novo-cliente -DryRun
#   .\publish-update.ps1 -Release v1.2.1       # launcher = the (SignPath-signed) exe of that GitHub release
#
# To ship a new launcher, raise <Version> in src\OriginsLauncher\OriginsLauncher.csproj first
# (and push the tag v<version> so GitHub Actions builds and signs it): running launchers with an
# older version update themselves before checking the game files.
#
# Signing with a certificate of our own instead (a token or Certum SimplySign's virtual card puts
# it in the Windows certificate store; signtool from the Windows SDK or tools\signtool\signtool.exe):
#   .\publish-update.ps1 -SignThumbprint <thumbprint>
# Either way the exe is final before the manifest is built, so self-updates verify the signed file.
param(
    [string]$Client = 'C:\conquer\client\5095',
    [string]$Out    = 'C:\conquer\site-data\updates',
    [string]$Languages = 'C:\conquer\client-lang',
    [string]$Release,
    [string]$Repository = 'paulobueno164/origins-launcher',
    [string]$SignThumbprint = $env:ORIGINS_SIGN_THUMBPRINT,
    [string]$TimestampUrl = 'http://time.certum.pl',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine')

dotnet build "$PSScriptRoot\tools\UpdateBuilder\UpdateBuilder.csproj" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'UpdateBuilder build failed' }

if ($Release) {
    $dir = Join-Path $env:TEMP "origins-release-$Release"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $launcher = Join-Path $dir 'OriginsLauncher.exe'
    Invoke-WebRequest -UseBasicParsing "https://github.com/$Repository/releases/download/$Release/OriginsLauncher.exe" -OutFile $launcher
    $signature = Get-AuthenticodeSignature $launcher
    Write-Output "release $Release signature: $($signature.Status) $($signature.SignerCertificate.Subject)"
    if ($signature.Status -ne 'Valid') { Write-Warning 'this release is not signed: players will see the SmartScreen block' }
}
else {
    dotnet build "$PSScriptRoot\src\OriginsLauncher\OriginsLauncher.csproj" -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'launcher build failed' }
    $launcher = "$PSScriptRoot\src\OriginsLauncher\bin\Release\net48\OriginsLauncher.exe"
}

if ($SignThumbprint -and -not $Release) {
    $signtool = @("$PSScriptRoot\tools\signtool\signtool.exe") +
        @(Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | ForEach-Object FullName) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found (Windows SDK, or tools\signtool\signtool.exe from Microsoft.Windows.SDK.BuildTools)' }
    & $signtool sign /sha1 $SignThumbprint /fd sha256 /tr $TimestampUrl /td sha256 /d 'Origins Conquer' $launcher
    if ($LASTEXITCODE -ne 0) { throw 'signing failed' }
    & $signtool verify /pa $launcher
    if ($LASTEXITCODE -ne 0) { throw 'signature does not verify' }
}

$builderArgs = @('--source', $Client, '--out', $Out, '--launcher', $launcher, '--lang-root', $Languages)
if ($DryRun) { $builderArgs += '--dry-run' }
dotnet "$PSScriptRoot\tools\UpdateBuilder\bin\Release\net10.0\UpdateBuilder.dll" @builderArgs
if ($LASTEXITCODE -ne 0) { throw 'UpdateBuilder failed' }
