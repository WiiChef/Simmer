[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference = 'Stop'
$kit = 'C:\Program Files (x86)\Windows Kits\10'
$sdk = '10.0.26100.0'
$vc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207'
$compiler = Join-Path $vc 'bin\Hostarm64\arm64\cl.exe'
$linker = Join-Path $vc 'bin\Hostarm64\arm64\link.exe'
$validator = Join-Path $kit "Tools\$sdk\arm64\infverif.exe"
$catalog = Join-Path $kit "bin\$sdk\x86\Inf2Cat.exe"
$out = [IO.Path]::GetFullPath($OutputDirectory)
$package = Join-Path $out 'package'
New-Item -ItemType Directory -Force $out,$package | Out-Null

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}

$savedInclude=$env:INCLUDE
$savedLib=$env:LIB
try {
    $env:INCLUDE = "$kit\Include\$sdk\km;$kit\Include\$sdk\shared;$kit\Include\$sdk\km\crt;$kit\Include\wdf\kmdf\1.33;$vc\include;$kit\Include\$sdk\ucrt"
    $common = @('/nologo','/W4','/WX','/O2','/Z7','/Brepro','/DUNICODE','/D_UNICODE')
    Invoke-Checked $compiler ($common + @('/c','/kernel','/Zl','/GS','/guard:cf','/D_ARM64_',
        '/analyze','/analyze:external-','/external:env:INCLUDE','/external:W0',
        '/D_WIN32_WINNT=0x0A00','/DNTDDI_VERSION=0x0A00000C',
        '/DKMDF_VERSION_MAJOR=1','/DKMDF_VERSION_MINOR=33','/DKMDF_MINIMUM_VERSION_REQUIRED=33',
        "/Fo$out\driver.obj", "$PSScriptRoot\driver.c"))
    $env:LIB = "$kit\Lib\$sdk\km\arm64;$kit\Lib\wdf\kmdf\arm64\1.33"
    Invoke-Checked $linker @('/nologo','/driver','/subsystem:native','/entry:FxDriverEntry','/machine:arm64',
        '/nodefaultlib','/dynamicbase','/nxcompat','/guard:cf','/integritycheck','/release','/debug','/Brepro',
        "/out:$package\FanAcpiProbe.sys", "/pdb:$out\FanAcpiProbe.pdb", "$out\driver.obj",
        'ntoskrnl.lib','hal.lib','wmilib.lib','WdfLdr.lib','WdfDriverEntry.lib','BufferOverflowFastFailK.lib','libcntpr.lib')
    $env:INCLUDE = "$vc\include;$kit\Include\$sdk\ucrt;$kit\Include\$sdk\shared;$kit\Include\$sdk\um"
    $env:LIB = "$vc\lib\arm64;$kit\Lib\$sdk\ucrt\arm64;$kit\Lib\$sdk\um\arm64"
    Invoke-Checked $compiler ($common + @('/MT',"/Fo$out\client.obj","/Fe$out\FanAcpiProbe.exe",
        "$PSScriptRoot\client.c",'/link','setupapi.lib','bcrypt.lib','advapi32.lib',"/pdb:$out\client.pdb"))
    Invoke-Checked $compiler ($common + @('/MT',"/Fo$out\codec-test.obj","/Fe$out\codec-test.exe",
        "$PSScriptRoot\codec-test.c",'/link',"/pdb:$out\codec-test.pdb"))
    Invoke-Checked "$out\codec-test.exe" @()
    Copy-Item -LiteralPath "$PSScriptRoot\FanAcpiProbe.inf" -Destination $package -Force
    Invoke-Checked $validator @('/w',"$package\FanAcpiProbe.inf")
    Invoke-Checked $catalog @("/driver:$package",'/os:10_CO_ARM64','/uselocaltime')
    Write-Host "Built and validated: $out"
} finally {
    $env:INCLUDE=$savedInclude
    $env:LIB=$savedLib
}
