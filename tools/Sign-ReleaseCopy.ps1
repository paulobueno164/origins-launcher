[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InputFile,
    [Parameter(Mandatory=$true)][string]$OutputFile,
    [Parameter(Mandatory=$true)][string]$CertificateThumbprint,
    [Parameter(Mandatory=$true)][string]$PublisherSubject,
    [Parameter(Mandatory=$true)][uri]$TimestampUrl,
    [string]$Receipt,
    [switch]$PreflightOnly
)
$ErrorActionPreference='Stop'
$inputPath=(Resolve-Path -LiteralPath $InputFile).Path
$outputPath=[IO.Path]::GetFullPath($OutputFile)
if($inputPath -ieq $outputPath -or (Test-Path -LiteralPath $outputPath)){throw 'SIGN_OUTPUT_MUST_BE_NEW_COPY'}
if($TimestampUrl.Scheme -ne 'https' -or -not [string]::IsNullOrEmpty($TimestampUrl.UserInfo)){throw 'SIGN_TIMESTAMP_HTTPS_REQUIRED'}
if($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$'){throw 'SIGN_CERTIFICATE_THUMBPRINT_INVALID'}
$cert=Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Thumbprint -EQ $CertificateThumbprint | Select-Object -First 1
if(-not $cert){throw 'SIGN_CERTIFICATE_NOT_AVAILABLE'}
if(-not $cert.HasPrivateKey -or $cert.Subject -cne $PublisherSubject){throw 'SIGN_CERTIFICATE_IDENTITY_OR_KEY_MISMATCH'}
if($cert.Subject -eq $cert.Issuer){throw 'SIGN_SELF_SIGNED_NOT_PUBLICLY_TRUSTED'}
$chain=[Security.Cryptography.X509Certificates.X509Chain]::new()
try{
    $chain.ChainPolicy.RevocationMode=[Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    if(-not $chain.Build($cert)){throw 'SIGN_CERTIFICATE_CHAIN_NOT_TRUSTED'}
}finally{$chain.Dispose()}
$signTool=Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse |
    Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if(-not $signTool){throw 'SIGNTOOL_REQUIRED'}
if($PreflightOnly){[pscustomobject]@{Ready=$true;Publisher=$cert.Subject;FilesChanged=$false};return}
$before=(Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
$directory=[IO.Path]::GetDirectoryName($outputPath)
if(-not(Test-Path -LiteralPath $directory)){New-Item -ItemType Directory -Path $directory | Out-Null}
Copy-Item -LiteralPath $inputPath -Destination $outputPath
if((Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash -ne $before){throw 'SIGN_SOURCE_CHANGED_DURING_COPY'}
& $signTool sign /sha1 $CertificateThumbprint /s My /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 /d 'Origins Conquer' $outputPath
if($LASTEXITCODE -ne 0){throw 'SIGN_OPERATION_FAILED_NO_PUBLICATION'}
& (Join-Path $PSScriptRoot 'Test-ReleaseTrust.ps1') -File $outputPath -PublisherSubject $PublisherSubject -Receipt $Receipt -SignTool $signTool
if((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ne $before){throw 'SIGN_INPUT_CHANGED_EXTERNALLY'}
