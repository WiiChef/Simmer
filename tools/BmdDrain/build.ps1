# Builds and test-signs SimmerBmdDrain.sys (ARM64 KMDF 1.33). Based on the BatteryDischargePulse build.
[CmdletBinding()]
param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference='Stop'
$kit='C:\Program Files (x86)\Windows Kits\10'
$sdk='10.0.26100.0'
$vc='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207'
$cert='12DDA6F523753BDF1C63446DC12BAEAFCDB5A73A'   # test-signing certificate in CurrentUser\My
$out=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $out | Out-Null
function Run([string]$program,[string[]]$arguments) { & $program @arguments; if($LASTEXITCODE -ne 0){throw "$program failed: $LASTEXITCODE"} }
$oldInclude=$env:INCLUDE;$oldLib=$env:LIB
try {
    $env:INCLUDE="$kit\Include\$sdk\km;$kit\Include\$sdk\shared;$kit\Include\$sdk\km\crt;$kit\Include\wdf\kmdf\1.33;$vc\include;$kit\Include\$sdk\ucrt"
    Run "$vc\bin\Hostarm64\arm64\cl.exe" @('/nologo','/W4','/WX','/O2','/Z7','/Brepro','/DUNICODE','/D_UNICODE',
        '/c','/kernel','/Zl','/GS','/guard:cf','/D_ARM64_','/analyze','/analyze:external-','/external:env:INCLUDE','/external:W0',
        '/D_WIN32_WINNT=0x0A00','/DNTDDI_VERSION=0x0A00000C','/DKMDF_VERSION_MAJOR=1','/DKMDF_VERSION_MINOR=33','/DKMDF_MINIMUM_VERSION_REQUIRED=33',
        "/Fo$out\drain.obj","$PSScriptRoot\drain.c")
    $env:LIB="$kit\Lib\$sdk\km\arm64;$kit\Lib\wdf\kmdf\arm64\1.33"
    Run "$vc\bin\Hostarm64\arm64\link.exe" @('/nologo','/driver','/subsystem:native','/entry:FxDriverEntry','/machine:arm64',
        '/nodefaultlib','/dynamicbase','/nxcompat','/guard:cf','/integritycheck','/release','/debug','/Brepro',
        "/out:$out\SimmerBmdDrain.sys","/pdb:$out\SimmerBmdDrain.pdb","$out\drain.obj",'ntoskrnl.lib','hal.lib',
        'WdfLdr.lib','WdfDriverEntry.lib','BufferOverflowFastFailK.lib','libcntpr.lib','wdmsec.lib')
    Run "$kit\bin\$sdk\arm64\signtool.exe" @('sign','/fd','SHA256','/s','My','/sha1',$cert,"$out\SimmerBmdDrain.sys")
    @{ 'SimmerBmdDrain.sys'=(Get-FileHash "$out\SimmerBmdDrain.sys").Hash } | ConvertTo-Json | Set-Content (Join-Path $out 'hash.json')
    Copy-Item "$out\SimmerBmdDrain.sys" (Join-Path $PSScriptRoot '..\..\SimmerBmdDrain.sys') -Force   # installed next to Simmer.exe
} finally {$env:INCLUDE=$oldInclude;$env:LIB=$oldLib}
