$ErrorActionPreference='Stop'
$result='C:\Users\Snapdragon\Documents\Codex\2026-11-02\given\outputs\FanAcpiDiag\verified-pair'
New-Item -ItemType Directory -Force $result | Out-Null
$done='C:\Users\Snapdragon\Documents\Codex\2026-11-02\given\work\mailbox-range-completed.flag'
if(Test-Path -LiteralPath $done){Remove-Item -LiteralPath $done}
$ec=Get-CimInstance -Namespace root\wmi -ClassName ATD_STS | Select-Object -First 1
Add-Type -Path (Join-Path $PSScriptRoot 'FanTelemetry.cs')
$rows=[Collections.Generic.List[object]]::new()
function ReadFeature([byte]$feature) {
    $r=Invoke-CimMethod -InputObject $ec -MethodName ec_read_command_byte -Arguments @{commandcode=$feature;param0=[byte]1}
    if($r.value -gt 255){throw 'Invalid mailbox read'}
    return [int]$r.value
}
function WriteFeature([byte]$feature,[byte]$value) {
    $r=Invoke-CimMethod -InputObject $ec -MethodName ec_write_command_byte -Arguments @{commandcode=$value;param0=$feature;param1=[byte]1}
    ("{0:o} feature {1:X2} value {2} ack {3}" -f [DateTime]::UtcNow,$feature,$value,$r.value) | Add-Content "$result\mailbox-range-events.txt"
    if($r.value -ne 0){throw 'Mailbox write failed'}
}
function SetPwm([int]$fan,[byte]$pwm) {
    WriteFeature 0x8c $fan;WriteFeature 0x8a $pwm
    $value=ReadFeature 0x0a
    if($value -ne $pwm){throw "PWM readback differs: $value vs $pwm"}
}
function Sample([string]$phase) {
    $chip=Get-CimInstance -Namespace root\wmi MSAcpi_ThermalZoneTemperature | Where-Object InstanceName -like '*QCOM0F5A*' | Select-Object -First 1
    if(-not $chip){throw 'Chip sensor unavailable'}
    $row=[pscustomobject]@{TimeUtc=[DateTime]::UtcNow.ToString('o');Phase=$phase;Fan1=[FanTelemetry]::FanRpm(0x00110013);Fan2=[FanTelemetry]::FanRpm(0x00110035);ChipC=[Math]::Round($chip.CurrentTemperature/10-273.15,2);Mode=(ReadFeature 2)}
    $rows.Add($row);$rows | Export-Csv -LiteralPath "$result\mailbox-range-telemetry.csv" -NoTypeInformation
    if($row.ChipC -ge 45){throw 'Temperature guard reached; returning to automatic mode'}
    return $row
}
Start-Transcript -Path "$result\mailbox-range-run.txt" -Force | Out-Null
$changed=$false
try {
    $before=Sample 'baseline'
    if($before.Mode -ne 0 -or $before.ChipC -gt 40){throw 'Require automatic mode and chip below 40 C'}
    $shell=(Get-Process -Id $PID).Path
    $watchdog=Start-Process -FilePath $shell -WindowStyle Hidden -ArgumentList @('-NoProfile','-File',('"'+$PSScriptRoot+'\restore-mailbox-watchdog.ps1"'),'-OwnerProcess',"$PID",'-CompletedFile',('"'+$done+'"'),'-LogPath',('"'+$result+'\mailbox-range-watchdog.txt"'),'-MaximumSeconds','110') -PassThru
    $changed=$true;WriteFeature 0x82 2
    if((ReadFeature 2) -ne 2){throw 'Manual mode not verified'}
    foreach($stage in @(@('fan0-120-fan1-75',120,75,10),@('fan0-75-fan1-120',75,120,10),@('both30',30,30,10),@('both0',0,0,10),@('restart30',30,30,10))) {
        SetPwm 0 $stage[1];SetPwm 1 $stage[2]
        for($i=0;$i -lt $stage[3];$i++){Sample $stage[0] | Out-Host;Start-Sleep -Seconds 1}
    }
    'All manual stages completed.' | Add-Content "$result\mailbox-range-status.txt"
} catch {('FAILED: '+$_.Exception.Message) | Add-Content "$result\mailbox-range-status.txt";Write-Host $_}
finally {
    if($changed){
        WriteFeature 0x82 0
        if((ReadFeature 2) -ne 0){throw 'Automatic restore not verified; watchdog remains active'}
        'Automatic mode restored and verified.' | Add-Content "$result\mailbox-range-status.txt"
        'restored' | Set-Content -LiteralPath $done
        for($i=0;$i -lt 8;$i++){Sample 'restoredAuto' | Out-Host;Start-Sleep -Seconds 1}
    }
    Stop-Transcript | Out-Null
}
