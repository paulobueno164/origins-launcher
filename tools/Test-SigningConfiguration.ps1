[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
foreach ($name in @('SIGNPATH_API_TOKEN', 'SIGNPATH_ORGANIZATION_ID', 'SIGNPATH_PROJECT_SLUG', 'ORIGINS_SIGNER_SUBJECT')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "SIGNING_CONFIGURATION_REQUIRED: $name"
    }
}
Write-Output 'SIGNING_CONFIGURATION_PRESENT (values not displayed)'
