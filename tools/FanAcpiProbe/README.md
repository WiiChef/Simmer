# PowerCap ECGE diagnostic probe

ARM64 KMDF 1.33 filter and native diagnostic client for the ASUS UX3607OA, BIOS 312, DSDT SHA-256 `D743A77340A0B0A18C79EC7E4FE3E275A57F43D8955CBDD8E44646F431F04252`.

This is a test driver. It has been compiled, statically analyzed, package-validated, installed, and run against ECGE. The ACPI hardware-ID query succeeded, but the firmware identity handshake returned 0 twice; capabilities could not be established. It contains no fan RPM setter, curve setter, debug-mode setter, arbitrary ACPI evaluator, or raw memory/register IOCTL.

## Latest machine state

Test signing is active. The signed primitive package and its public certificate trust are installed. `FanAcpiProbe` is running as an upper filter only on `ACPI\ACPI0013\0`; AcpiDev remains the function driver and the device reports OK with problem code 0. Two live identity queries returned NTSTATUS success and integer 0 instead of the required 1. Capabilities and telemetry were therefore not attempted. The `0xC00000BB` entries for those fields are the probe's default not-attempted statuses, not firmware capability responses. Driver/capability results are saved in `validation/identity-first.json` and `validation/identity.json`.

The query command, if further investigation requires it, is:

```powershell
.\src\manage.ps1 -Action Query -BuildDirectory .\build
```

The initial installer treated the restart-required result as a failure and rolled back its registration. That handling was corrected. Final staging used `-DeferRestart`, and the subsequent reboot activated the filter. Installation history is in `validation/live-stage-filter-deferred.txt`; the latest query result is in `validation/live-query-status.txt`.

## Package layout

- `src/`: source and scripts in the delivered bundle. In the PowerCap source folder, these files sit directly in `tools/FanAcpiProbe`.
- `build/FanAcpiProbe.exe`: native ARM64 client.
- `build/package/`: test-signed SYS, INF, catalog, and public signing certificate. No private key is distributed.
- `validation/`: recorded build/test/preflight results for this machine.

## Initial installation prerequisites

The November 2, 2026 preflight matched the audited firmware. Secure Boot was disabled, but test signing was **off** and the Codex process had no administrator token. No driver was installed, no device restarted, no fan queried through ACPI, no machine trust store changed, and no boot policy changed during development. A nonexportable test signing key/certificate was created in the current user's Personal certificate store to sign the package.

The following commands deliberately require an elevated **native ARM64 PowerShell**. Changing boot policy and rebooting are separate user actions; neither script performs them. To use this test-signed package, enable test signing in an elevated terminal:

```powershell
bcdedit /set testsigning on
```

Then save work and restart Windows when ready. If Windows rejects the change, stop and preserve the error; do not disable additional protections to force the driver to load. A Microsoft release-signing route is the alternative to test mode. This package does not provide that signature.

## Install and query

From the root of the extracted bundle, after rebooting into test mode:

```powershell
.\build\FanAcpiProbe.exe --preflight
.\src\manage.ps1 -Action Install -BuildDirectory .\build -TrustTestCertificate
.\build\FanAcpiProbe.exe --identity | Tee-Object identity.json
```

`-TrustTestCertificate` imports the package's public test certificate into the local machine's Root and TrustedPublisher stores. The installer verifies the signer, catalog membership, exact device location, existing AcpiDev function driver, firmware hash, and test-signing state. It registers a primitive driver package, appends only `FanAcpiProbe` to the **single instance** `ACPI\ACPI0013\0` UpperFilters, and restarts that device. It does not register a class filter or replace AcpiDev. Other filter entries are retained. Installation does not execute the diagnostic ACPI methods; that begins only with an explicit client query.

Run `--identity` first. `whoAmI` must be the integer 1, the capability query must succeed, and the mask must be nonzero. `rpmSetterAdvertisedByEc` is `null` when capabilities are not established, `false` when bit 15 is absent, and `true` when bit 15 is present. Even `true` establishes only advertised support, not working RPM control. Exit code 7 means identity/capabilities were not established; preserve the JSON anyway.

Optional, after reviewing identity output:

```powershell
.\build\FanAcpiProbe.exe --telemetry | Tee-Object telemetry.json
```

Telemetry honors the firmware capability bits. It queries selector 1; selector 2 is attempted only if a successful live `FNTR` read reports at least two selectors. Firmware status/speed methods return acknowledgements and update cache objects, so acknowledgements and cache reads are separate fields. A successful cache read may still be stale; compare with ASUS RPM telemetry. No ACK is presented as a measured RPM.

`EVER`, `FNTR`, `_HID`, and cache reads use fixed ACPI object paths. Windows may reject evaluations of non-method objects or sibling paths; their separate NTSTATUS/raw outputs record that outcome. Missing optional reads do not mean zero RPM. If a speed query fails on a missing firmware object, preserve its status and stop rather than bypassing firmware gates.

Logical firmware queries are **not strictly hardware-read-only**: IC10's normal DSM dispatch can send its initialization command before handling a query. Firmware telemetry methods can also update AML state and issue notifications. The probe uses those existing methods; it does not infer or send an independent manual-mode command.

## Remove the probe

From an elevated native ARM64 terminal:

```powershell
.\src\manage.ps1 -Action Uninstall -BuildDirectory .\build
```

The uninstaller removes only this instance's `FanAcpiProbe` filter entry, restarts the device, then removes the primitive driver package. If restart fails or requires reboot, package removal stops so the referenced driver remains available. It never initiates a reboot.

If installation failed during device restart, its rollback removes only its own filter registration and leaves the package installed. Reboot when ready, then rerun Uninstall to remove the package. Retain the bundle until the driver is removed.

To remove the public test certificate after uninstalling, inspect the certificate thumbprint, then remove only that exact certificate from the two stores:

```powershell
$cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path .\build\package\FanAcpiProbe-Test.cer))
$thumbprint = $cert.Thumbprint
Get-Item "Cert:\LocalMachine\Root\$thumbprint", "Cert:\LocalMachine\TrustedPublisher\$thumbprint"
Remove-Item -LiteralPath "Cert:\LocalMachine\Root\$thumbprint", "Cert:\LocalMachine\TrustedPublisher\$thumbprint"
```

When test mode is no longer needed, restore normal signing policy with `bcdedit /set testsigning off`, then restart Windows when ready. The build certificate/private key in the current user's Personal store is separate; retain it for rebuilding or remove that exact thumbprint from `Cert:\CurrentUser\My` after signing work is finished.

## Source build

The included build script uses the toolchain paths found on this machine: MSVC 14.44.35207, Windows SDK/WDK 10.0.26100.0, and KMDF 1.33. Adjust those paths if building elsewhere.

```powershell
.\build.ps1 -OutputDirectory .\build
.\test-sign.ps1 -BuildDirectory .\build
```

Run these commands from the source directory; in the bundle, that is `src`. Build uses warnings as errors, kernel static analysis (external WDK headers excluded), stack cookies, Control Flow Guard, NX, relocation support, and integrity checking. It runs ACPI codec tests, INF validation, and ARM64 catalog generation. Test signing embeds the SYS signature, regenerates the catalog against the signed bytes, then signs the catalog. It does not trust the certificate or change boot policy.

The ABI is fixed at 2248 bytes and checked at compile time in both components. The driver validates output-buffer bounds and integer types/widths, uses a sequential power-managed queue, and sends fixed ACPI calls directly to the ECGE PDO. Diagnostic IOCTLs require SeLoadDriverPrivilege checked in the original caller context; the elevated client enables that privilege for its own process. Other I/O is forwarded to the existing driver stack. The synchronous ACPI timeout requests cancellation after three seconds; it is not a guarantee that firmware execution can be forcibly interrupted.

## Next decision

The live identity/capability result determines whether to continue. An absent capability is a stopping result. A present RPM capability warrants further work on selector mapping, return-status ambiguity, valid ranges, and a demonstrated automatic-mode restore operation before implementing any RPM setter.

References: [Microsoft ACPI evaluation](https://learn.microsoft.com/en-us/windows-hardware/drivers/acpi/evaluating-acpi-control-methods-synchronously), [primitive driver packages](https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/creating-a-primitive-driver), and [test-signed driver loading](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option).
