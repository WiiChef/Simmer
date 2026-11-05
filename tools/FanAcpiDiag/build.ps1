[CmdletBinding()]
param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference='Stop'
$kit='C:\Program Files (x86)\Windows Kits\10'
$sdk='10.0.26100.0'
$vc='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207'
$out=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $out | Out-Null
$lines=foreach($pair in @(@('ECGE','ACPI\ACPI0013\0'),@('IC10','ACPI\QCOM0F10\A'),@('PAID','ACPI\QCOM1033\0'))) {
    $pdo=(Get-PnpDeviceProperty -InstanceId $pair[1] -KeyName DEVPKEY_Device_PDOName).Data
    if($pdo -notmatch '^\\Device\\[0-9a-fA-F]+$'){throw 'Unexpected PDO name'}
    '#define '+$pair[0]+'_PDO L"'+$pdo.Replace('\','\\')+'"'
}
$lines | Set-Content -Encoding ascii (Join-Path $PSScriptRoot 'targets.h')
function Run([string]$program,[string[]]$arguments) {
    & $program @arguments
    if($LASTEXITCODE -ne 0){throw "$program failed: $LASTEXITCODE"}
}
$oldInclude=$env:INCLUDE;$oldLib=$env:LIB
try {
    $env:INCLUDE="$kit\Include\$sdk\km;$kit\Include\$sdk\shared;$kit\Include\$sdk\km\crt;$kit\Include\wdf\kmdf\1.33;$vc\include;$kit\Include\$sdk\ucrt"
    $common=@('/nologo','/W4','/WX','/O2','/Z7','/Brepro','/DUNICODE','/D_UNICODE')
    Run "$vc\bin\Hostarm64\arm64\cl.exe" ($common+@('/c','/kernel','/Zl','/GS','/guard:cf','/D_ARM64_',
        '/analyze','/analyze:external-','/external:env:INCLUDE','/external:W0',
        '/D_WIN32_WINNT=0x0A00','/DNTDDI_VERSION=0x0A00000C',
        '/DKMDF_VERSION_MAJOR=1','/DKMDF_VERSION_MINOR=33','/DKMDF_MINIMUM_VERSION_REQUIRED=33',
        "/Fo$out\diag.obj","$PSScriptRoot\diag.c"))
    $env:LIB="$kit\Lib\$sdk\km\arm64;$kit\Lib\wdf\kmdf\arm64\1.33"
    Run "$vc\bin\Hostarm64\arm64\link.exe" @('/nologo','/driver','/subsystem:native','/entry:FxDriverEntry','/machine:arm64',
        '/nodefaultlib','/dynamicbase','/nxcompat','/guard:cf','/integritycheck','/release','/debug','/Brepro',
        "/out:$out\FanAcpiDiag.sys","/pdb:$out\FanAcpiDiag.pdb","$out\diag.obj",'ntoskrnl.lib','hal.lib',
        'WdfLdr.lib','WdfDriverEntry.lib','BufferOverflowFastFailK.lib','libcntpr.lib','wdmsec.lib')
    $env:INCLUDE="$vc\include;$kit\Include\$sdk\ucrt;$kit\Include\$sdk\shared;$kit\Include\$sdk\um"
    $env:LIB="$vc\lib\arm64;$kit\Lib\$sdk\ucrt\arm64;$kit\Lib\$sdk\um\arm64"
    Run "$vc\bin\Hostarm64\arm64\cl.exe" ($common+@('/MT',"/Fo$out\client.obj","/Fe$out\FanAcpiDiag.exe",
        "$PSScriptRoot\client.c",'/link',"/pdb:$out\client.pdb"))
    Run "$vc\bin\Hostarm64\arm64\cl.exe" ($common+@('/MT',"/Fo$out\codec-test.obj","/Fe$out\codec-test.exe",
        "$PSScriptRoot\codec-test.c",'/link',"/pdb:$out\codec-test.pdb"))
    Run "$out\codec-test.exe" @()
    Run "$kit\bin\$sdk\arm64\signtool.exe" @('sign','/fd','SHA256','/s','My','/sha1',
        '12DDA6F523753BDF1C63446DC12BAEAFCDB5A73A',"$out\FanAcpiDiag.sys")
} finally {$env:INCLUDE=$oldInclude;$env:LIB=$oldLib}
