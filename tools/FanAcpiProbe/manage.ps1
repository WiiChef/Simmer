#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Install','Uninstall','Query')][string]$Action,
    [string]$BuildDirectory=(Join-Path $PSScriptRoot 'build'),
    [switch]$TrustTestCertificate,
    [switch]$DeferRestart
)
$ErrorActionPreference='Stop'
if([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'Arm64') {
    throw 'Use native ARM64 PowerShell for driver installation.'
}
$target='ACPI\ACPI0013\0'
$service='FanAcpiProbe'
$build=[IO.Path]::GetFullPath($BuildDirectory)
$package=Join-Path $build 'package'
$client=Join-Path $build 'FanAcpiProbe.exe'
$location=@((Get-PnpDeviceProperty -InstanceId $target -KeyName DEVPKEY_Device_LocationPaths).Data)
if($location -notcontains 'ACPI(_SB_)#ACPI(ECGE)') { throw 'The expected device is not ECGE. Nothing changed.' }
$function=(Get-PnpDeviceProperty -InstanceId $target -KeyName DEVPKEY_Device_Service).Data
if($function -ne 'AcpiDev') { throw "Unexpected function driver '$function'. Nothing changed." }
if(-not ('FanProbeInstallApi' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'installer-api.cs') }

function Restart-Target {
    & "$env:SystemRoot\System32\pnputil.exe" /restart-device $target | Out-Host
    if($LASTEXITCODE -eq 3010) {
        Write-Host 'Windows requires a reboot to apply this filter change. Registration and package retained. No reboot initiated.'
        return $true
    }
    if($LASTEXITCODE -ne 0){throw "Device restart failed with exit code $LASTEXITCODE; no automatic reboot was initiated."}
    $dev=Get-PnpDevice -InstanceId $target
    if($dev.Status -ne 'OK'){throw "ECGE status after restart: $($dev.Status). Use the documented filter rollback before rebooting."}
    return $false
}

if($Action -eq 'Query') {
    & $client --identity
    if($LASTEXITCODE -ne 0){throw "Identity diagnostic did not establish capabilities (exit $LASTEXITCODE). Preserve its JSON output."}
    return
}

if($Action -eq 'Install') {
    $preflightText=& $client --preflight
    if($LASTEXITCODE -ne 0){throw 'Firmware preflight failed. Nothing installed.'}
    $preflight=$preflightText | ConvertFrom-Json
    if(-not $preflight.firmwareMatchesAudit){throw 'Firmware does not match the audit.'}
    if($preflight.testSigningEnabled -ne $true){throw 'Test signing is off. This installer does not change boot policy. Enable test signing and reboot before using this test-signed package.'}
    if($preflight.secureBootEnabled -ne $false){throw 'Secure Boot state is enabled or unknown; stop and review the signing route.'}
    $certificatePath=Join-Path $package 'FanAcpiProbe-Test.cer'
    $certificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    if($certificate.Subject -ne 'CN=PowerCap ECGE Probe Test' -or $certificate.NotAfter -le (Get-Date)) {
        throw 'Unexpected or expired test certificate.'
    }
    foreach($file in @('FanAcpiProbe.sys','FanAcpiProbe.cat')) {
        $signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $package $file)
        if(-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw "Unexpected signer on $file. Nothing installed."
        }
    }
    if($TrustTestCertificate) {
        foreach($store in @('Root','TrustedPublisher')) {
            if(-not (Test-Path -LiteralPath "Cert:\LocalMachine\$store\$($certificate.Thumbprint)")) {
                Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
            }
        }
    }
    $sign='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\arm64\signtool.exe'
    & $sign verify /pa /v /c "$package\FanAcpiProbe.cat" "$package\FanAcpiProbe.sys"
    if($LASTEXITCODE -ne 0){throw 'Catalog/member signature verification failed. Driver not installed.'}
    $old=@([FanProbeInstallApi]::GetUpperFilters())
    if($old -contains $service){throw 'Filter is already registered. Use Query or Uninstall before replacing the package.'}
    $reboot=$false
    if(-not [FanProbeInstallApi]::DiInstallDriver([IntPtr]::Zero,(Join-Path $package 'FanAcpiProbe.inf'),0,[ref]$reboot)) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    # Re-read immediately before writing, to preserve the current list of other filters.
    $current=@([FanProbeInstallApi]::GetUpperFilters())
    if($current -notcontains $service){[FanProbeInstallApi]::SetUpperFilters([string[]]($current+$service))}
    if($DeferRestart) {
        Write-Host "Filter registered only on $target. Device restart deferred; reboot manually, then run Query."
        return
    }
    try { $restartRequiresReboot=Restart-Target } catch {
        # Keep the installed package available, remove only our own registration.
        $remaining=@([FanProbeInstallApi]::GetUpperFilters() | Where-Object {$_ -ne $service})
        [FanProbeInstallApi]::SetUpperFilters([string[]]$remaining)
        throw
    }
    if($restartRequiresReboot) {
        Write-Host "Filter registered only on $target; driver activation is pending a manual reboot. Run Query after reboot, not Install."
        return
    }
    Write-Host "Installed only on $target. Package requested reboot: $reboot. No ACPI methods have been queried by this installer."
    Write-Host 'Run the client --identity first; --telemetry is a separate optional diagnostic.'
    return
}

# Remove only this service from the instance's list; retain every other filter.
$filters=@([FanProbeInstallApi]::GetUpperFilters())
if($filters -contains $service) {
    [FanProbeInstallApi]::SetUpperFilters([string[]]@($filters | Where-Object {$_ -ne $service}))
    if(Restart-Target) {
        Write-Host 'Filter removal is pending a manual reboot. Keep the package until reboot, then rerun Uninstall.'
        return
    }
}
$key="HKLM:\SYSTEM\CurrentControlSet\Services\$service"
if(Test-Path -LiteralPath $key) {
    $image=(Get-ItemProperty -LiteralPath $key -Name ImagePath).ImagePath
    if($image.StartsWith('\SystemRoot\',[StringComparison]::OrdinalIgnoreCase)) {
        $image=Join-Path $env:SystemRoot $image.Substring(12)
    }
    $image=$image -replace '^\\\?\?\\',''
    $absolute=[IO.Path]::GetFullPath($image)
    $store=[IO.Path]::GetFullPath((Join-Path $env:SystemRoot 'System32\DriverStore\FileRepository'))+'\'
    if(-not $absolute.StartsWith($store,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($absolute) -ne 'FanAcpiProbe.sys') {
        throw 'Unexpected service image path. Filter registration removed; package removal stopped.'
    }
    $installedInf=Join-Path ([IO.Path]::GetDirectoryName($absolute)) 'FanAcpiProbe.inf'
    $reboot=$false
    if(-not [FanProbeInstallApi]::DiUninstallDriver([IntPtr]::Zero,$installedInf,0,[ref]$reboot)) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    Write-Host "Uninstalled driver package. Windows requested reboot: $reboot. No reboot initiated."
} else {Write-Host 'Driver service is absent; filter registration is removed.'}
Write-Host 'The public test certificate remains trusted if you imported it. See README for explicit cleanup.'
