[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string[]]$File,
    [Parameter(Mandatory=$true)][string]$PublisherSubject,
    [string]$Receipt,
    [string]$SignTool
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($PublisherSubject)){throw 'RELEASE_PUBLISHER_REQUIRED'}
if(-not $SignTool){
    $SignTool=Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if(-not $SignTool -or -not(Test-Path -LiteralPath $SignTool)){throw 'SIGNTOOL_REQUIRED'}
$rows=@()
foreach($inputFile in $File){
    $path=(Resolve-Path -LiteralPath $inputFile).Path
    $bytes=[IO.File]::ReadAllBytes($path)
    if($bytes.Length -lt 64 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90){throw "RELEASE_NOT_PE: $path"}
    $signature=Get-AuthenticodeSignature -LiteralPath $path
    if($signature.Status -ne 'Valid'){throw "RELEASE_SIGNATURE_INVALID: $($signature.Status): $path"}
    if(-not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -cne $PublisherSubject){throw "RELEASE_PUBLISHER_MISMATCH: $path"}
    if(-not $signature.TimeStamperCertificate){throw "RELEASE_TIMESTAMP_REQUIRED: $path"}
    $eku=@($signature.SignerCertificate.Extensions | Where-Object {$_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]} | ForEach-Object {$_.EnhancedKeyUsages} | ForEach-Object {$_.Value})
    if($eku -notcontains '1.3.6.1.5.5.7.3.3'){throw "RELEASE_CODE_SIGNING_EKU_REQUIRED: $path"}
    $details=& $SignTool verify /pa /all /tw /v $path 2>&1
    if($LASTEXITCODE -ne 0){throw "RELEASE_AUTHENTICODE_POLICY_FAILED: $path`n$($details -join [Environment]::NewLine)"}
    $rows+=@([pscustomobject]@{
        File=$path
        Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        Publisher=$signature.SignerCertificate.Subject
        CertificateThumbprint=$signature.SignerCertificate.Thumbprint
        TimestampSigner=$signature.TimeStamperCertificate.Subject
        AuthenticodeVerified=$true
    })
}
$result=[pscustomobject]@{Passed=$true;VerifiedUTC=[datetime]::UtcNow.ToString('o');Files=$rows;TrustedRootStoreChanged=$false}
if($Receipt){
    if(Test-Path -LiteralPath $Receipt){throw 'RELEASE_RECEIPT_ALREADY_EXISTS'}
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Receipt -Encoding utf8
}
$result
