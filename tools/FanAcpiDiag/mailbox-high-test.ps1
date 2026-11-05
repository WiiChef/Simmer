$ErrorActionPreference='Stop'
$result='C:\Users\Snapdragon\Documents\Codex\2026-11-02\given\outputs\FanAcpiDiag'
$source=$PSScriptRoot
$done='C:\Users\Snapdragon\Documents\Codex\2026-11-02\given\work\mailbox-high-completed.flag'
if(Test-Path -LiteralPath $done){Remove-Item -LiteralPath $done}
$ec=Get-CimInstance -Namespace root\wmi -ClassName ATD_STS | Select-Object -First 1
if(-not $ec){throw 'ASUS EC WMI device unavailable'}
Add-Type -Path "$source\FanTelemetry.cs"
function ReadFeature([byte]$feature) {
    $r=Invoke-CimMethod -InputObject $ec -MethodName ec_read_command_byte -Arguments @{commandcode=$feature;param0=[byte]1}
    if($r.value -gt 255){throw "Invalid feature read: $($r.value)"}
    return [int]$r.value
}
function WriteFeature([byte]$feature,[byte]$value) {
    $r=Invoke-CimMethod -InputObject $ec -MethodName ec_write_command_byte -Arguments @{commandcode=$value;param0=$feature;param1=[byte]1}
    ("{0:o} feature {1:X2} value {2} ack {3}" -f [DateTime]::UtcNow,$feature,$value,$r.value) | Add-Content "$result\mailbox-high-events.txt"
    if($r.value -ne 0){throw "Mailbox write failed: $($r.value)"}
}
$rows=[Collections.Generic.List[object]]::new()
function Sample([string]$phase) {
    $zones=Get-CimInstance -Namespace root\wmi MSAcpi_ThermalZoneTemperature
    $chip=$zones | Where-Object InstanceName -like '*QCOM0F5A*' | Select-Object -First 1
    if(-not $chip){throw 'Chip temperature unavailable'}
    $row=[pscustomobject]@{TimeUtc=[DateTime]::UtcNow.ToString('o');Phase=$phase;Fan1=[FanTelemetry]::FanRpm(0x00110013);Fan2=[FanTelemetry]::FanRpm(0x00110035);ChipC=[Math]::Round($chip.CurrentTemperature/10-273.15,2);Mode=(ReadFeature 2)}
    $rows.Add($row);$rows | Export-Csv -LiteralPath "$result\mailbox-high-telemetry.csv" -NoTypeInformation
    return $row
}
Start-Transcript -Path "$result\mailbox-high-run.txt" -Force | Out-Null
$changed=$false
try {
    $before=Sample 'baseline'
    if($before.Mode -ne 0){throw 'Existing fan controller is not in auto mode; preserve it'}
    if($before.ChipC -gt 45){throw 'Require chip below 45 C for isolated test'}
    $shell=(Get-Process -Id $PID).Path
    $watchdog=Start-Process -FilePath $shell -WindowStyle Hidden -ArgumentList @('-NoProfile','-File',('"'+$source+'\restore-mailbox-watchdog.ps1"'),'-OwnerProcess',"$PID",'-CompletedFile',('"'+$done+'"'),'-LogPath',('"'+$result+'\mailbox-high-watchdog.txt"')) -PassThru
    $changed=$true
    WriteFeature 0x82 2
    if((ReadFeature 2) -ne 2){throw 'Manual mode did not latch'}
    foreach($fan in 0,1) {
        WriteFeature 0x8c $fan
        WriteFeature 0x8a 120
        $pwm=ReadFeature 0x0a
        "Fan selector $fan PWM readback $pwm" | Add-Content "$result\mailbox-high-events.txt"
        if($pwm -ne 120){throw 'PWM readback did not match 120'}
    }
    for($i=0;$i -lt 15;$i++){Sample 'manual120' | Out-Host;Start-Sleep -Seconds 1}
} catch {('FAILED: '+$_.Exception.Message) | Add-Content "$result\mailbox-high-status.txt";Write-Host $_}
finally {
    if($changed) {
        WriteFeature 0x82 0
        if((ReadFeature 2) -ne 0){throw 'Automatic restore not verified; watchdog remains active'}
        'Automatic mode restored and verified.' | Add-Content "$result\mailbox-high-status.txt"
        'restored' | Set-Content -LiteralPath $done
        for($i=0;$i -lt 8;$i++){Sample 'restoredAuto' | Out-Host;Start-Sleep -Seconds 1}
    }
    Stop-Transcript | Out-Null
}
