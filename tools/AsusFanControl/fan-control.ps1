#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('Status','Automatic','Pulse')][string]$Mode='Status',
    [ValidateRange(0,255)][int]$Fan0Pwm=30,
    [ValidateRange(0,255)][int]$Fan1Pwm=30,
    [ValidateRange(1,60)][int]$DurationSeconds=15,
    [ValidateRange(35,50)][int]$StopTemperatureC=45,
    [string]$OutputCsv
)
$ErrorActionPreference='Stop'
$model=(Get-CimInstance Win32_ComputerSystem).Model
$bios=(Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
if($model -notlike '*UX3607OA*' -or $bios -ne 'UX3607OA.312') {
    throw "This tool was validated only on UX3607OA BIOS 312. Found: $model / $bios"
}
if(-not ('FanTelemetry' -as [type])) {Add-Type -Path (Join-Path $PSScriptRoot 'FanTelemetry.cs')}
$ec=Get-CimInstance -Namespace root\wmi -ClassName ATD_STS | Select-Object -First 1
if(-not $ec){throw 'ASUS ATD_STS provider unavailable'}
function ReadMode {
    $value=(Invoke-CimMethod -InputObject $ec -MethodName ec_read_command_byte -Arguments @{commandcode=[byte]2;param0=[byte]1}).value
    if($value -gt 255){throw "Invalid fan-mode response: $value"}
    return [int]$value
}
function WriteFeature([byte]$feature,[byte]$value) {
    # ECCW(bank=1,feature,value). The WMI argument order is value,feature,bank.
    $response=Invoke-CimMethod -InputObject $ec -MethodName ec_write_command_byte -Arguments @{commandcode=$value;param0=$feature;param1=[byte]1}
    if($response.value -ne 0){throw "ASUS mailbox write failed: $($response.value)"}
}
function RestoreAutomatic {
    WriteFeature 0x82 0
    if((ReadMode) -ne 0){throw 'Automatic-mode restoration could not be verified'}
}
function ReadStatus([string]$phase) {
    $chip=Get-CimInstance -Namespace root\wmi MSAcpi_ThermalZoneTemperature | Where-Object InstanceName -like '*QCOM0F5A*' | Select-Object -First 1
    if(-not $chip){throw 'Chip temperature unavailable'}
    [pscustomobject]@{
        TimeUtc=[DateTime]::UtcNow.ToString('o');Phase=$phase;Mode=(ReadMode)
        Fan0Rpm=[FanTelemetry]::FanRpm(0x00110013)
        Fan1Rpm=[FanTelemetry]::FanRpm(0x00110035)
        ChipC=[Math]::Round($chip.CurrentTemperature/10-273.15,2)
    }
}
if($Mode -eq 'Automatic') {RestoreAutomatic;ReadStatus 'automatic';return}
$mutex=[Threading.Mutex]::new($false,'Global\PowerCapAsusFanMailbox')
$locked=$false;$changed=$false;$watchdog=$null
$done=Join-Path ([IO.Path]::GetTempPath()) ('PowerCapFan-'+[Guid]::NewGuid().ToString('N')+'.done')
$watchLog=Join-Path ([IO.Path]::GetTempPath()) ('PowerCapFanWatch-'+[Guid]::NewGuid().ToString('N')+'.log')
$rows=[Collections.Generic.List[object]]::new()
try {
    try {$locked=$mutex.WaitOne(2000)} catch [Threading.AbandonedMutexException] {$locked=$true}
    if(-not $locked){throw 'Another fan-control experiment is running'}
    $status=ReadStatus 'initial'
    if($Mode -eq 'Status'){$status;return}
    if($status.Mode -ne 0){throw 'Fan control is already manual; preserve the current controller or select Automatic explicitly'}
    if($status.ChipC -ge $StopTemperatureC){throw 'Chip is above the requested temperature guard'}
    $shell=(Get-Process -Id $PID).Path
    $watchdog=Start-Process -FilePath $shell -WindowStyle Hidden -ArgumentList @('-NoProfile','-File',('"'+$PSScriptRoot+'\restore-mailbox-watchdog.ps1"'),'-OwnerProcess',"$PID",'-CompletedFile',('"'+$done+'"'),'-LogPath',('"'+$watchLog+'"'),'-MaximumSeconds',"$($DurationSeconds+15)") -PassThru
    $changed=$true
    WriteFeature 0x82 2
    if((ReadMode) -ne 2){throw 'Manual mode was not accepted'}
    foreach($fan in 0,1) {
        $pwm=if($fan -eq 0){$Fan0Pwm}else{$Fan1Pwm}
        WriteFeature 0x8c $fan;WriteFeature 0x8a $pwm
        $read=(Invoke-CimMethod -InputObject $ec -MethodName ec_read_command_byte -Arguments @{commandcode=[byte]0x0a;param0=[byte]1}).value
        if($read -ne $pwm){throw "Fan $fan PWM readback $read differs from request $pwm"}
    }
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $sample=ReadStatus 'manual'
        $rows.Add($sample);$sample
        if($OutputCsv){$rows | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation}
        if($sample.Mode -ne 2){throw 'Another controller or the watchdog returned fan control to automatic'}
        if($sample.ChipC -ge $StopTemperatureC){throw 'Temperature guard reached; restoring automatic mode'}
        Start-Sleep -Milliseconds 1000
    }
} finally {
    try {
        if($changed){
            RestoreAutomatic
            'restored' | Set-Content -LiteralPath $done
            ReadStatus 'restoredAutomatic'
            if($watchdog -and $watchdog.WaitForExit(3000)) {Remove-Item -LiteralPath $done -ErrorAction SilentlyContinue}
        }
    } finally {
        if($locked){$mutex.ReleaseMutex()}
        $mutex.Dispose()
    }
}
