[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
$build=[IO.Path]::GetFullPath($BuildDirectory)
$package=Join-Path $build 'package'
$sign='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\arm64\signtool.exe'
$inf2cat='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x86\Inf2Cat.exe'
$public=Join-Path $package 'FanAcpiProbe-Test.cer'
$certificate=$null
if(Test-Path -LiteralPath $public) {
    $previous=[Security.Cryptography.X509Certificates.X509Certificate2]::new($public)
    $certificate=Get-Item -LiteralPath "Cert:\CurrentUser\My\$($previous.Thumbprint)" -ErrorAction SilentlyContinue
    if($certificate -and -not $certificate.HasPrivateKey){$certificate=$null}
}
if(-not $certificate) {
    $certificate=New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=PowerCap ECGE Probe Test' `
        -FriendlyName 'PowerCap ECGE diagnostic test signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddDays(90)
}
Export-Certificate -Cert $certificate -FilePath $public -Force | Out-Null
& $sign sign /fd SHA256 /s My /sha1 $certificate.Thumbprint "$package\FanAcpiProbe.sys"
if($LASTEXITCODE -ne 0){throw 'Driver signing failed'}
# Regenerate after embedding the driver signature, so catalog hashes match final bytes.
& $inf2cat "/driver:$package" /os:10_CO_ARM64 /uselocaltime
if($LASTEXITCODE -ne 0){throw 'Signed-driver catalog generation failed'}
& $sign sign /fd SHA256 /s My /sha1 $certificate.Thumbprint "$package\FanAcpiProbe.cat"
if($LASTEXITCODE -ne 0){throw 'Catalog signing failed'}
Write-Host "Test-signed with certificate $($certificate.Thumbprint). No trust store or boot policy changed."

