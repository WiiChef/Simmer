# Runs the watt-target self-test at several targets and summarises how closely CPU watts hold.
# Usage: holdtest.ps1 [-Targets 5,8,12,16] [-Threads 18] [-Seconds 60] [-Exe tools\selftest.exe]
param([double[]]$Targets = @(5, 8, 12, 16), [int]$Threads = 18, [int]$Seconds = 60, [string]$Exe = "$PSScriptRoot\selftest.exe", [int]$Skip = 10)
foreach ($t in $Targets) {
  $csv = Join-Path $PSScriptRoot ("hold-{0}W-{1}t.csv" -f $t, $Threads)
  & $Exe --selftest $t $Threads $Seconds $csv | Out-Null
  $rows = Import-Csv $csv | Where-Object { [int]$_.t -ge $Skip }
  $cpu = $rows | ForEach-Object { [double]$_.cpu } | Sort-Object
  if (-not $cpu) { "{0} W: no data" -f $t; continue }
  $mean = ($cpu | Measure-Object -Average).Average
  $mae = ($cpu | ForEach-Object { [Math]::Abs($_ - $t) } | Measure-Object -Average).Average
  $p95 = $cpu[[int][Math]::Floor(0.95 * ($cpu.Count - 1))]
  $within = ($cpu | Where-Object { [Math]::Abs($_ - $t) -le [Math]::Max(1, 0.1 * $t) }).Count / $cpu.Count * 100
  $sys = ($rows | ForEach-Object { [double]$_.system } | Measure-Object -Average).Average
  "target {0,5:N1} W | {1,2} threads | CPU mean {2,5:N2} W  abs err {3,4:N2} W  p95 {4,5:N2} W  max {5,5:N2} W  within +-max(1W,10%): {6,3:N0}% | system {7,5:N1} W" -f $t, $Threads, $mean, $mae, $p95, $cpu[-1], $within, $sys
}
