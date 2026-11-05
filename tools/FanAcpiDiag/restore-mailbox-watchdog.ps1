param([Parameter(Mandatory)][int]$OwnerProcess,[Parameter(Mandatory)][string]$CompletedFile,[Parameter(Mandatory)][string]$LogPath,[ValidateRange(10,120)][int]$MaximumSeconds=60)
$ErrorActionPreference='Stop'
$deadline=[DateTime]::UtcNow.AddSeconds($MaximumSeconds)
while([DateTime]::UtcNow -lt $deadline) {
    if(Test-Path -LiteralPath $CompletedFile){exit 0}
    if(-not (Get-Process -Id $OwnerProcess -ErrorAction SilentlyContinue)){break}
    Start-Sleep -Seconds 1
}
if(Test-Path -LiteralPath $CompletedFile){exit 0}
for($attempt=0;$attempt -lt 3;$attempt++) {
    try {
        $ec=Get-CimInstance -Namespace root\wmi -ClassName ATD_STS | Select-Object -First 1
        $response=Invoke-CimMethod -InputObject $ec -MethodName ec_write_command_byte -Arguments @{commandcode=[byte]0;param0=[byte]0x82;param1=[byte]1}
        $read=Invoke-CimMethod -InputObject $ec -MethodName ec_read_command_byte -Arguments @{commandcode=[byte]2;param0=[byte]1}
        ('Watchdog restored auto at '+[DateTime]::UtcNow.ToString('o')+' write='+$response.value+' mode='+$read.value) | Add-Content -LiteralPath $LogPath
        if($read.value -eq 0){exit 0}
    } catch {$_ | Out-String | Add-Content -LiteralPath $LogPath}
    Start-Sleep -Seconds 1
}
exit 1
