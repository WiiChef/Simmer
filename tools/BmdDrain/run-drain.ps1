# Drains the battery to -Target % with the charger connected, through SimmerBmdDrain.sys. Run elevated.
# The kernel ends the drain by itself if this script stops sending heartbeats, closes its handle, exits or crashes.
# Stop early: create stop.request next to this script.
param([int]$Target=0, [int]$MaxMinutes=150)
$ErrorActionPreference='Stop'
# Default target: Simmer's charge limit (charge.limit in its data folder), so the drain stops where charging is held.
if(!$Target){ $f=Join-Path $PSScriptRoot '..\..\charge.limit'; $Target=if(Test-Path $f){[int](Get-Content $f -Raw).Trim()}else{0} }
if($Target -lt 50 -or $Target -gt 99){ throw "no usable target (Simmer charge limit is off or outside 50-99): $Target" }
$dir=$PSScriptRoot; $build=Join-Path $dir 'build'; $service='SimmerBmdDrain'
$log=Join-Path $dir 'drain.log'; $csv=Join-Path $dir 'drain.csv'; $stopFile=Join-Path $dir 'stop.request'
function Log([string]$m){ $line=(Get-Date -Format 'HH:mm:ss')+' '+$m; Add-Content $log $line; Write-Host $line }
function Sample([string]$stage, $st) {
    $b=Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus; $soc=@((Get-CimInstance Win32_Battery).EstimatedChargeRemaining)
    if($soc.Count -ne 1 -or $soc[0] -isnot [ValueType] -or $soc[0] -lt 0 -or $soc[0] -gt 100){throw "battery percentage unreadable: $soc"}; $soc=[int]$soc[0]
    $row=[pscustomobject]@{Time=(Get-Date -Format 'HH:mm:ss');Stage=$stage;SOC=$soc;PowerOnline=$b.PowerOnline;Charging=$b.Charging;Discharging=$b.Discharging;
        RemainingMWh=$b.RemainingCapacity;ChargeMW=$b.ChargeRate;DischargeMW=$b.DischargeRate;KernelActive=$(if($st){$st.Active}else{''});Control=$(if($st){'0x{0:X}' -f $st.Control}else{''})}
    $row | Export-Csv $csv -Append -NoTypeInformation; return $row
}
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class Drain {
    [StructLayout(LayoutKind.Sequential)] public struct Status { public uint Size, Version, Active, Control, SavedControl, StopReason, ElapsedSeconds; public int Result; }
    public const uint Start = 0x833BE000, Beat = 0x833BE004, Stop = 0x833BE008, Query = 0x833BE00C;
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string p, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle h, uint c, byte[] i, uint il, out Status o, uint ol, out uint n, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle h, uint c, IntPtr i, uint il, out Status o, uint ol, out uint n, IntPtr ov);
    public static SafeFileHandle Open() { var h = CreateFile(@"\\.\SimmerBmdDrain", 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero); if (h.IsInvalid) throw new Exception("open failed " + Marshal.GetLastWin32Error()); return h; }
    public static Status Call(SafeFileHandle h, uint code, uint minutes, string bat = null, string pmic = null) {
        Status o; uint n; bool ok;
        if (code == Start) {
            var i = new byte[132]; Buffer.BlockCopy(BitConverter.GetBytes(minutes), 0, i, 0, 4);
            var b = System.Text.Encoding.Unicode.GetBytes(bat); var p = System.Text.Encoding.Unicode.GetBytes(pmic);
            if (b.Length > 62 || p.Length > 62) throw new Exception("device name too long");
            Buffer.BlockCopy(b, 0, i, 4, b.Length); Buffer.BlockCopy(p, 0, i, 68, p.Length);
            ok = DeviceIoControl(h, code, i, 132, out o, 32, out n, IntPtr.Zero);
        }
        else ok = DeviceIoControl(h, code, IntPtr.Zero, 0, out o, 32, out n, IntPtr.Zero);
        if (!ok || n != 32 || o.Size != 32) throw new Exception("ioctl 0x" + code.ToString("X") + " failed " + Marshal.GetLastWin32Error());
        return o;
    }
}
'@
$h=$null; $created=$false; $restored=$true
try {
    Remove-Item $stopFile -ea 0
    Log "--- drain to $Target %, at most $MaxMinutes min"
    $model=(Get-CimInstance Win32_ComputerSystem).Model; $bios=(Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
    if($model -notmatch 'UX3607OA' -or $bios -ne 'UX3607OA.312'){throw "unexpected machine $model / $bios"}
    if((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').SystemStartOptions -notmatch 'TESTSIGNING'){throw 'test signing is not live in this boot; nothing changed'}
    $bat=(Get-PnpDeviceProperty -InstanceId 'ACPI\PNP0C0A\2&daba3ff&0' -KeyName DEVPKEY_Device_PDOName).Data
    $pmic=(Get-PnpDeviceProperty -InstanceId 'ACPI\QCOM0F8E\2&daba3ff&0' -KeyName DEVPKEY_Device_PDOName).Data
    $sys=Join-Path $build 'SimmerBmdDrain.sys'
    if((Get-FileHash $sys).Hash -ne (Get-Content (Join-Path $build 'hash.json') -Raw | ConvertFrom-Json).'SimmerBmdDrain.sys'){throw 'driver hash mismatch'}
    if(Get-Service $service -ea 0){throw 'service already exists'}
    $s=Sample 'baseline' $null
    if(!$s.PowerOnline -or $s.Discharging -or $s.SOC -le $Target){throw "needs the charger on, no discharge, and more than $Target % (now $($s.SOC) %)"}
    & sc.exe create $service type= kernel start= demand binPath= ('\??\'+$sys) | Out-Null; if($LASTEXITCODE){throw 'service create failed'}; $created=$true
    & sc.exe start $service | Out-Null; if($LASTEXITCODE){throw 'driver load failed'}
    Log 'driver loaded'
    $h=[Drain]::Open()
    $restored=$false
    $st=[Drain]::Call($h,[Drain]::Start,$MaxMinutes,$bat,$pmic)
    Log ("start: result 0x{0:X8} active {1} control 0x{2:X} saved 0x{3:X}" -f $st.Result,$st.Active,$st.Control,$st.SavedControl)
    if($st.Result -ne 0 -or $st.Active -ne 1){throw 'kernel refused or could not start the drain'}
    $notDischarging=0; $i=0
    while($true) {
        Start-Sleep 5; $i++
        $st=[Drain]::Call($h,[Drain]::Beat,0)
        if($st.Result -ne 0 -or $st.Active -ne 1){ Log ("kernel ended the drain itself: reason {0} result 0x{1:X8}" -f $st.StopReason,$st.Result); break }
        if($i % 6 -eq 0){ $st=[Drain]::Call($h,[Drain]::Query,0) }
        $s=Sample 'drain' $(if($i % 6 -eq 0){$st}else{$null})
        if($i % 12 -eq 0){ Log ("{0} % {1} mWh discharge {2} mW control 0x{3:X}" -f $s.SOC,$s.RemainingMWh,$s.DischargeMW,$st.Control) }
        if($s.SOC -le $Target){ Log "reached $($s.SOC) %"; break }
        if(Test-Path $stopFile){ Log 'stop.request found'; break }
        if($s.Discharging){ $notDischarging=0 } elseif(++$notDischarging -ge 12){ Log 'battery not discharging for 60 s: stopping'; break }
    }
} catch { Log "ERROR: $($_.Exception.Message)" }
finally {
    # STOP until the kernel reports the saved control back (each call restores and verifies for up to 6 s).
    if($h){ foreach($n in 1..10){ try {
        $st=[Drain]::Call($h,[Drain]::Stop,0)
        Log ("stop: result 0x{0:X8} active {1} control 0x{2:X} saved 0x{3:X} reason 0x{4:X}" -f $st.Result,$st.Active,$st.Control,$st.SavedControl,$st.StopReason)
        if($st.Active -eq 0 -and ($st.Control -band 6) -eq $st.SavedControl){ $restored=$true; break }
    } catch { Log "stop failed: $($_.Exception.Message)" } } }
    if($h){ try { $h.Dispose() } catch {} }   # closing the handle also makes the kernel restore
    if($created -and !$restored){ Log 'WARNING: restore NOT verified; driver left loaded so its timer keeps retrying every 5 s' }
    elseif($created){
        & sc.exe stop $service | Out-Null
        foreach($n in 1..30){ if((Get-Service $service -ea 0).Status -eq 'Stopped'){break}; Start-Sleep 1 }
        if((Get-Service $service -ea 0).Status -ne 'Stopped'){ Log 'WARNING: driver did not unload' }
        else {
            & sc.exe delete $service | Out-Null; Start-Sleep 1
            if(Get-Service $service -ea 0){ Log "WARNING: service delete failed ($LASTEXITCODE)" } else { Log 'driver unloaded and service deleted' }
        }
    }
    foreach($n in 1..6){ Start-Sleep 5; try { $s=Sample 'after' $null } catch {} }
    Log ("after: {0} %, AC online {1}, charging {2}, discharging {3}" -f $s.SOC,$s.PowerOnline,$s.Charging,$s.Discharging)
    Log '--- done'
}
