<#
.SYNOPSIS
OCTA one-time setup (run by a person, not by Claude): create the "OCTA Code Signing"
certificate and trust it for this Windows user, so Revit loads OCTA-signed add-in builds
without the "Always Load / Load Once" security prompt.

.DESCRIPTION
1. Creates a self-signed code-signing certificate in Cert:\CurrentUser\My (if missing).
2. Adds its public part to CurrentUser\Root (Windows shows a security warning - click Yes)
   and CurrentUser\TrustedPublisher.
The private key never leaves this user's certificate store. Only builds signed with it are
trusted. To undo: delete "OCTA Code Signing" from certmgr.msc (Personal, Trusted Root,
Trusted Publishers).
#>
$ErrorActionPreference = "Stop"
$subject = "CN=OCTA Code Signing"

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq $subject | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(10)
    Write-Host "Created certificate $($cert.Thumbprint)"
} else {
    Write-Host "Certificate already exists: $($cert.Thumbprint)"
}

$tmp = Join-Path $env:TEMP "octa-code-signing.cer"
Export-Certificate -Cert $cert -FilePath $tmp | Out-Null
foreach ($store in "Root", "TrustedPublisher") {
    if (-not (Get-ChildItem "Cert:\CurrentUser\$store" | Where-Object Thumbprint -eq $cert.Thumbprint)) {
        Import-Certificate -FilePath $tmp -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
        Write-Host "Trusted in CurrentUser\$store"
    }
}
Remove-Item $tmp
Write-Host "Done. Future OCTA builds of the Revit MCP add-in will be signed and load without the prompt."
