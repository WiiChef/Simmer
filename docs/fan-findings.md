# Fan control findings — ASUS Zenbook A16 UX3607OA

## Result

**Direct fan control works on this laptop. No custom kernel driver is required.**

ASUS's existing driver exposes an EC mailbox through the `root\wmi:ATD_STS` WMI class. That mailbox can select manual mode, set each fan's PWM independently, stop both fans, restart them, and return control to the EC's automatic curve.

The earlier conclusion that software could only select Whisper/Standard/Performance was incorrect. The failed register writes and Qualcomm control paths did not cover the working ASUS mailbox mechanism.

### Machine and scope

| Item | Value |
| --- | --- |
| Laptop | ASUS Zenbook A16 UX3607OA |
| SoC | Snapdragon X2 Elite Extreme |
| Firmware | UX3607OA.312 |
| OS | Windows 11 ARM64; observed build 28000 |
| Access needed | Elevated PowerShell and the installed ASUS WMI provider/driver |
| Custom kernel driver needed for working control | No |
| Firmware changes required | None |
| Control demonstrated | Independent PWM, low speed, fan-off, restart, automatic restoration |

The audited DSDT was 523,295 bytes. Its SHA-256 matched a fresh read of the live firmware:

```text
D743A77340A0B0A18C79EC7E4FE3E275A57F43D8955CBDD8E44646F431F04252
```

## Working mechanism

The EC appears at two I²C addresses: the custom ASUS interface at `0x5B` and a Qualcomm-derived interface at `0x76`. The working manual controls use the **ASUS command mailbox in device/page `0xC4`, bank `0x01`**, rather than the previously tested fan-curve engine in page `0xC9`.

The sequence was identified in [independent A16 Windows/Linux investigation notes, section 7.2](https://github.com/zoan37/omarchy-setup/blob/master/zenbook-a16-omarchy-snapdragon.md#72-windows-side-reverse-engineering-2026-09-25-night-after-the-cloud-recovery) and then verified on this laptop. The command implementation is also shown in the author's [a16-fan.sh](https://github.com/zoan37/omarchy-setup/blob/master/assets/zenbook-a16/a16-fan.sh).

### Mailbox operations

All operations below use bank `0x01`.

| Feature | Direction | Meaning |
| --- | --- | --- |
| `0x82` | Write | Fan mode: `0` = automatic, `2` = manual |
| `0x02` | Read | Current fan mode; manual/automatic transitions were verified |
| `0x8C` | Write | Select fan `0` or `1` |
| `0x8A` | Write | PWM for the selected fan; `0` stops it |
| `0x0A` | Read | PWM readback for the selected fan |

The documented PWM field is an unsigned byte (`0..255`). The values tested locally were **0, 30, 45, 75, and 120**. Values above 120 and the absolute lowest reliable starting PWM were not characterized.

### WMI argument order

The argument order is important. For an underlying operation `ECCW(bank, feature, value)`, call:

```powershell
Invoke-CimMethod -InputObject $ec -MethodName ec_write_command_byte -Arguments @{
    commandcode = [byte]$value
    param0      = [byte]$feature
    param1      = [byte]$bank
}
```

For `ECCR(bank, feature)`, call:

```powershell
Invoke-CimMethod -InputObject $ec -MethodName ec_read_command_byte -Arguments @{
    commandcode = [byte]$feature
    param0      = [byte]$bank
}
```

Here `$ec` is the `ATD_STS` instance from `root\wmi`. The write response's `value` was zero for successful commands. A successful acknowledgement was **not** treated as proof of control: mode/PWM readback and actual fan telemetry were checked as well.

The verified sequence is:

1. Read mode and confirm that the EC currently owns automatic control.
2. Write feature `0x82 = 2` and verify mode readback `2`.
3. Select fan 0 with `0x8C = 0`, then write its PWM with `0x8A`.
4. Select fan 1 with `0x8C = 1`, then write its PWM with `0x8A`.
5. Check each selected fan's PWM readback and both actual RPM channels.
6. Restore `0x82 = 0` and verify mode readback `0`.

## Measurements on this laptop

The following values are representative settled readings from the corrected two-fan test. ASUS's DSTS telemetry reports these channels in 100-RPM units, so the numbers are quantized.

| Requested PWM: fan 0 / fan 1 | Observed RPM: fan 0 / fan 1 | Result |
| --- | --- | --- |
| 120 / 75 | 4,200 / 2,600 | Independent control verified |
| 75 / 120 | 2,700 / approximately 4,100 | Swapped commands changed the corresponding fans |
| 30 / 30 | 900 / 900 | Low-speed operation verified |
| 0 / 0 | 0 / 0 | Both fans stopped in manual mode |
| 30 / 30 after stopping | 900 / 900 | Both restarted from rest |
| Return to automatic mode | Mode 0; both eventually 0 RPM in the cool test | Automatic restoration verified |

A separate test of the reusable script requested PWM `30 / 45` and observed approximately `900 / 1,500 RPM`.

The corrected range test's chip-temperature samples stayed below 36 °C. These were short, cool-machine tests, not thermal qualification under sustained high load. PWM controls duty; it is not a firmware promise to maintain an exact RPM. Exact RPM targeting would need a measured feedback controller.

### Important correction: second-fan telemetry

The original investigation and the first version of the diagnostic sampler mistakenly read fan 0 twice:

- `DSTS 0x00110013` returns fan 0 as a scalar.
- `DSTS 0x00110035` returns a **package containing both fans**. Its first DWORD is fan 0, and its second DWORD is fan 1.
- Reading the first DWORD of both device IDs duplicates fan 0. Fan 1 must be decoded at byte offset 4 of the package response.

The sampler was corrected, and the asymmetric/off/restart tests were repeated. The authoritative two-fan measurements are in `FanAcpiDiag/verified-pair/mailbox-range-telemetry.csv` beside this report. Earlier CSVs outside `verified-pair` have an incorrectly labelled second channel and must not be used to establish independent control or second-fan behavior.

The existing PowerCap application source also uses the scalar interpretation for both device IDs. Its second-fan telemetry needs the same correction when this mechanism is integrated. The running application executable was not replaced during this investigation.

## Kernel investigation and failed paths

### ECGE and the Qualcomm identity/capability interface

The DSDT does declare `\_SB.ECGE`. Windows binds it as `ACPI\ACPI0013\0`, with the inbox `AcpiDev` function driver. The earlier claim that this device was absent or unbound was incorrect.

A test-signed ARM64 filter successfully evaluated ECGE's `_HID` and obtained `ACPI0013`, proving that the driver reached the correct ACPI device. Two separate firmware identity calls returned NTSTATUS success but integer **0**, whereas the generic interface expects identity **1**. The initial probe correctly stopped before capability discovery; its unattempted fields were not evidence that the firmware rejected those particular operations.

A subsequent temporary diagnostic driver read the underlying state directly:

| Field/read | Observed result |
| --- | --- |
| `IC10.AVBL` | 1 — operation region available |
| `IC10.ECRD` | 1 |
| `IC10.PRCT` | 15 — initial dispatch counter exhausted |
| Raw `FC43` identity read | Transport status 0, one data byte equal to 0 |
| Raw `FC44` capability/version read | Transport status 0, six zero data bytes |
| Cached `ECGE.QWAI`, `QCAP`, `EVER` | All zero |
| `ECGE.AVER` | 17 (`0x11`) |

Thus the zero identity was not caused by a missing ACPI region. The [upstream ASUS Glymur EC driver](https://github.com/torvalds/linux/blob/master/drivers/platform/arm64/asus-glymur-ec.c) independently describes ASUS's substantial changes to the reference protocol, including the absent capability bitmap. A zero generic capability response therefore does **not** rule out the custom ASUS fan controls.

Some older interpretation of the endpoint split was also too strong: the working ASUS fan telemetry and coarse profile selection already use portions of the `0x76` interface. It is not an entirely unusable endpoint.

### Other paths

| Path | Evidence / disposition |
| --- | --- |
| ASUS Whisper/Standard/Performance | Working coarse control, still useful for the EC's automatic policy |
| Fan-curve WMI IDs `0x00110024/25/32` | Prior tests accepted writes without effective curve changes; distinct from the working mailbox |
| Direct duty/register writes under EC automatic mode | Prior writes were reverted; this did not establish that manual mode was impossible |
| Qualcomm PEP thermal IOCTLs | Prior binary review found telemetry/stub handlers, not a demonstrated fan setter |
| Qualcomm fan-debug command `0x30` | Bounded 3,000-RPM requests for selectors 1 and 2 returned zero acknowledgements without changing the observed fan-0 channel; no working control established. Those trials predated the second-channel correction, so they do not independently prove what fan 1 did. Automatic-debug-off commands were sent afterward. |
| MPTF RPM command `0x37` | Implemented in AML but no working direct-RPM behavior established; it is not the mechanism used by the successful PWM tests |
| Standard ACPI `_FSL` / `_FIF` / `_FPS` | No definitions found in the audited DSDT |
| `FAN0` trip-point mailbox command `0x85` | AML derives notification bounds from current RPM bins; no demonstrated chosen-speed control |
| Display-off/reset behavior | Original observation remains an indirect lead. Its second-fan result was affected by the telemetry bug. The later isolated display-state pulse was not performed because both fans were already off at baseline. |

The working mailbox makes further experimentation with unsupported generic commands unnecessary for obtaining manual control. This report does not claim that every possible firmware or hardware modification was exhausted.

## Reusable user-mode tool

Source location:

```text
C:\Users\Snapdragon\Documents\PowerCap\tools\AsusFanControl\
    fan-control.ps1
    FanTelemetry.cs
    restore-mailbox-watchdog.ps1
```

From an elevated PowerShell:

```powershell
$tool = 'C:\Users\Snapdragon\Documents\PowerCap\tools\AsusFanControl\fan-control.ps1'

# Read current mode, both RPMs, and chip temperature.
& $tool -Mode Status

# Run both fans at PWM 30 for 15 seconds, then restore automatic mode.
& $tool -Mode Pulse -Fan0Pwm 30 -Fan1Pwm 30 -DurationSeconds 15

# Explicitly return ownership to the firmware.
& $tool -Mode Automatic
```

The tool is model/BIOS gated, refuses to take over an already-manual controller, checks PWM readback, samples temperature, and restores automatic mode in `finally`. Manual pulses are limited to 60 seconds; the default temperature guard is 45 °C. An independent watchdog also restores automatic mode if the controlling process exits or exceeds its lease.

The watchdog was tested by entering manual mode and forcibly terminating the test controller process. Manual mode was observed before termination, and the watchdog returned the EC to mode 0 within the verification window. Normal completion/restoration was tested separately.

Manual mode replaces the EC's automatic fan curve while active. A persistent PowerCap feature should use a supervised controller with reliable automatic restoration, temperature/sensor-failure handling, and restart/resume behavior. The bounded script is a verified diagnostic tool; sustained high-load and long-duration control have not been qualified.

## System state after testing

- Fans were returned to **automatic mode 0**, with readback verified.
- The cleanup snapshot showed both fans at 0 RPM and the chip near 40 °C.
- The existing PowerCap application and its power configuration were not replaced or intentionally changed.
- The transient diagnostic driver was stopped and its service deleted after each run.
- The original probe's instance filter registration was removed, and the driver-package uninstall API returned success.
- The probe's public test certificate was removed from LocalMachine Root and TrustedPublisher.
- Boot configuration was restored to **`testsigning No`**. The running boot's code-integrity mode does not change until a normal reboot.
- Stack inspection still showed the already-loaded `FanAcpiProbe` filter after package removal. Its remaining loaded state should be rechecked after the next normal reboot; no reboot was forced.
- Secure Boot was already disabled before this work and was left unchanged.

## Evidence locations

Relative to this report's output directory:

| Evidence | File |
| --- | --- |
| Corrected independent PWM/off/restart measurements | `FanAcpiDiag/verified-pair/mailbox-range-telemetry.csv` |
| Raw mailbox write acknowledgements | `FanAcpiDiag/verified-pair/mailbox-range-events.txt` |
| Completed range test and auto restoration | `FanAcpiDiag/verified-pair/mailbox-range-status.txt` |
| Reusable tool's PWM 30/45 test | `FanAcpiDiag/verified-pair/tool-pulse.csv` |
| Killed-controller watchdog validation | `FanAcpiDiag/verified-pair/watchdog-validation.json` |
| Underlying ECGE / IC10 / PAID snapshots | `FanAcpiDiag/target-0.json`, `target-1.json`, `target-2.json` |
| Generic debug-command trial | `FanAcpiDiag/debug-pulse-4.json`, `debug-pulse-5.json`, `debug-pulse-telemetry.csv` |
| Probe and signing cleanup | `FanAcpiDiag/probe-cleanup.txt` |
| Original ACPI disassembly | `fan-acpi-audit/DSDT.dsl` |

The machine-wide Beads database was unavailable during this work; tracker operations failed. These files are experiment evidence and implementation artifacts, not a replacement task tracker.
