param([Parameter(Mandatory)][string[]]$Files)
$ErrorActionPreference = 'Stop'
# Build-time only. A CA-issued code-signing identity must be provided by protected CI secrets.
if (!$env:CODE_SIGNING_PFX_BASE64) { Write-Output 'UNSIGNED: production code-signing certificate is not configured.'; return }
if (!$env:CODE_SIGNING_PFX_PASSWORD) { throw 'Signing password secret is missing' }
$tool = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin/*/x64/signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (!$tool) { throw 'Windows SDK SignTool is required' }
$cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($env:CODE_SIGNING_PFX_BASE64), $env:CODE_SIGNING_PFX_PASSWORD, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
$store = [Security.Cryptography.X509Certificates.X509Store]::new('My','CurrentUser')
$added = $false
try {
    if (!$cert.HasPrivateKey -or $cert.Subject -eq $cert.Issuer -or !$cert.Verify()) { throw 'A trusted, non-self-signed production certificate with private key is required' }
    if (!($cert.EnhancedKeyUsageList | Where-Object ObjectId -eq '1.3.6.1.5.5.7.3.3')) { throw 'Certificate is not valid for code signing' }
    $store.Open('ReadWrite')
    if ($store.Certificates.Find('FindByThumbprint',$cert.Thumbprint,$false).Count -eq 0) { $store.Add($cert); $added = $true }
    foreach ($file in $Files) {
        $resolved = (Resolve-Path -LiteralPath $file).Path
        & $tool.FullName sign /sha1 $cert.Thumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $resolved
        if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed' }
        & $tool.FullName verify /pa $resolved
        if ($LASTEXITCODE -ne 0 -or (Get-AuthenticodeSignature -LiteralPath $resolved).Status -ne 'Valid') { throw 'Authenticode verification failed' }
    }
} finally {
    if ($added) { $store.Remove($cert) }
    $store.Dispose(); $cert.Dispose()
}
