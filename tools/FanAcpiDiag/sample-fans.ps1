param([int]$DurationSeconds=50,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'FanTelemetry.cs')
$watch=[Diagnostics.Stopwatch]::StartNew()
$rows=[Collections.Generic.List[object]]::new()
while($watch.Elapsed.TotalSeconds -lt $DurationSeconds) {
    $record=[ordered]@{TimeUtc=[DateTime]::UtcNow.ToString('o');Seconds=[Math]::Round($watch.Elapsed.TotalSeconds,2);Fan1=$null;Fan2=$null;ChipC=$null;SkinC=$null;Error=''}
    try {
        $record.Fan1=[FanTelemetry]::FanRpm(0x00110013)
        $record.Fan2=[FanTelemetry]::FanRpm(0x00110035)
        $zones=Get-CimInstance -Namespace root\wmi -ClassName MSAcpi_ThermalZoneTemperature
        $chip=$zones | Where-Object InstanceName -like '*QCOM0F5A*' | Select-Object -First 1
        $skin=$zones | Where-Object InstanceName -like '*TZ33*' | Select-Object -First 1
        if($chip){$record.ChipC=[Math]::Round($chip.CurrentTemperature/10-273.15,2)}
        if($skin){$record.SkinC=[Math]::Round($skin.CurrentTemperature/10-273.15,2)}
    } catch {$record.Error=$_.Exception.Message}
    $rows.Add([pscustomobject]$record)
    $rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation
    Start-Sleep -Milliseconds 1000
}
