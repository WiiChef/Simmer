#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>
#include <setupapi.h>
#include <bcrypt.h>
#include <stdio.h>
#include <wchar.h>
#include "protocol.h"

typedef LONG (WINAPI *QUERY_SYSTEM_INFORMATION)(ULONG, PVOID, ULONG, PULONG);
typedef struct { ULONG Length; ULONG Options; } CODE_INTEGRITY_INFO;

static const WCHAR *ValueNames[FAN_PROBE_VALUE_COUNT] = {
    L"hid",L"whoAmI",L"capabilities",L"interfaceVersion",L"fanConfiguration",
    L"fan1StatusAck",L"fan1SpeedAck",L"fan1StatusCache",L"fan1SpeedCache",
    L"fan2StatusAck",L"fan2SpeedAck",L"fan2StatusCache",L"fan2SpeedCache"
};
static const UCHAR ExpectedDsdt[32] = {
    0xd7,0x43,0xa7,0x73,0x40,0xa0,0xb0,0xa1,0x8c,0x79,0xec,0x7e,0x4f,0xe3,0xe2,0x75,
    0xa5,0x7f,0x43,0xd8,0x95,0x5c,0xbd,0xd8,0xe4,0x46,0x46,0xf4,0x31,0xf0,0x42,0x52
};

static int FirmwareHash(UCHAR digest[32])
{
    DWORD n = GetSystemFirmwareTable(0x41435049, 0x54445344, NULL, 0);
    UCHAR *data;
    LONG status;
    if (n == 0 || n > 16 * 1024 * 1024) return 0;
    data = (UCHAR *)HeapAlloc(GetProcessHeap(), 0, n);
    if (!data) return 0;
    if (GetSystemFirmwareTable(0x41435049, 0x54445344, data, n) != n) {
        HeapFree(GetProcessHeap(), 0, data);
        return 0;
    }
    status = BCryptHash(BCRYPT_SHA256_ALG_HANDLE, NULL, 0, data, n, digest, 32);
    HeapFree(GetProcessHeap(), 0, data);
    return status >= 0;
}

static int EnablePrivilege(void)
{
    HANDLE token;
    TOKEN_PRIVILEGES privilege;
    DWORD error;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, &token))
        return 0;
    ZeroMemory(&privilege, sizeof(privilege));
    privilege.PrivilegeCount = 1;
    if (!LookupPrivilegeValueW(NULL, SE_LOAD_DRIVER_NAME, &privilege.Privileges[0].Luid)) {
        CloseHandle(token);
        return 0;
    }
    privilege.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    SetLastError(ERROR_SUCCESS);
    if (!AdjustTokenPrivileges(token, FALSE, &privilege, 0, NULL, NULL)) {
        CloseHandle(token);
        return 0;
    }
    error = GetLastError();
    CloseHandle(token);
    return error == ERROR_SUCCESS;
}

static HANDLE OpenProbe(void)
{
    HDEVINFO set = SetupDiGetClassDevsW(&FanProbeInterface, NULL, NULL,
        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    SP_DEVICE_INTERFACE_DATA interfaceData;
    PSP_DEVICE_INTERFACE_DETAIL_DATA_W detail;
    DWORD n = 0;
    HANDLE file = INVALID_HANDLE_VALUE;
    if (set == INVALID_HANDLE_VALUE) return file;
    ZeroMemory(&interfaceData, sizeof(interfaceData));
    interfaceData.cbSize = sizeof(interfaceData);
    if (!SetupDiEnumDeviceInterfaces(set, NULL, &FanProbeInterface, 0, &interfaceData)) goto exit;
    SetupDiGetDeviceInterfaceDetailW(set, &interfaceData, NULL, 0, &n, NULL);
    if (n < sizeof(*detail) || n > 32768) goto exit;
    detail = (PSP_DEVICE_INTERFACE_DETAIL_DATA_W)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, n);
    if (!detail) goto exit;
    detail->cbSize = sizeof(*detail);
    if (SetupDiGetDeviceInterfaceDetailW(set, &interfaceData, detail, n, NULL, NULL)) {
        SP_DEVICE_INTERFACE_DATA extra;
        ZeroMemory(&extra, sizeof(extra));
        extra.cbSize = sizeof(extra);
        if (!SetupDiEnumDeviceInterfaces(set, NULL, &FanProbeInterface, 1, &extra) &&
            GetLastError() == ERROR_NO_MORE_ITEMS)
            file = CreateFileW(detail->DevicePath, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    }
    HeapFree(GetProcessHeap(), 0, detail);
exit:
    SetupDiDestroyDeviceInfoList(set);
    return file;
}

static void JsonString(const WCHAR *value, SIZE_T maximum)
{
    SIZE_T i;
    wprintf(L"\"");
    for (i = 0; i < maximum && value[i]; ++i) {
        WCHAR c = value[i];
        if (c == L'\\' || c == L'\"') wprintf(L"\\%lc", c);
        else if (c < 32 || c > 126) wprintf(L"\\u%04x", (unsigned)c);
        else wprintf(L"%lc", c);
    }
    wprintf(L"\"");
}

static void PrintReply(const FAN_PROBE_REPLY *reply)
{
    ULONG i, j;
    const FAN_PROBE_VALUE *caps = &reply->Values[FanCaps];
    const FAN_PROBE_VALUE *who = &reply->Values[FanWho];
    int validCaps = who->Status >= 0 && who->IntegerValid && who->Integer == 1 &&
        caps->Status >= 0 && caps->IntegerValid && caps->Integer != 0 && caps->Integer <= MAXDWORD;
    wprintf(L"{\n  \"schemaVersion\": %lu,\n  \"operation\": \"%ls\",\n  \"instanceId\": ",
        reply->Version, reply->Operation == IOCTL_FAN_PROBE_TELEMETRY ? L"telemetry" : L"identity");
    JsonString(reply->InstanceId, ARRAYSIZE(reply->InstanceId));
    wprintf(L",\n  \"location\": ");
    JsonString(reply->Location, ARRAYSIZE(reply->Location));
    wprintf(L",\n  \"capabilitiesEstablished\": %ls,\n  \"rpmSetterAdvertisedByEc\": ",
        validCaps ? L"true" : L"false");
    if (!validCaps) wprintf(L"null");
    else wprintf(L"%ls", (caps->Integer & 0x8000) ? L"true" : L"false");
    wprintf(L",\n  \"rpmSetterImplementedInProbe\": false,\n  \"values\": {\n");
    for (i = 0; i < FAN_PROBE_VALUE_COUNT; ++i) {
        const FAN_PROBE_VALUE *v = &reply->Values[i];
        wprintf(L"    \"%ls\": {\"ntstatus\": \"0x%08lX\", \"integer\": ",
            ValueNames[i], (ULONG)v->Status);
        if (v->Status >= 0 && v->IntegerValid) wprintf(L"%llu", v->Integer);
        else wprintf(L"null");
        wprintf(L", \"rawAcpi\": \"");
        for (j = 0; j < v->RawLength && j < FAN_PROBE_MAX_RAW; ++j)
            wprintf(L"%02X", v->Raw[j]);
        wprintf(L"\"}%ls\n", i+1 < FAN_PROBE_VALUE_COUNT ? L"," : L"");
    }
    wprintf(L"  }\n}\n");
}

static int Preflight(void)
{
    UCHAR digest[32];
    ULONG i;
    DWORD secureBoot = MAXDWORD, size = sizeof(secureBoot);
    CODE_INTEGRITY_INFO ci = {sizeof(ci), 0};
    QUERY_SYSTEM_INFORMATION query = (QUERY_SYSTEM_INFORMATION)(void *)GetProcAddress(
        GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation");
    LONG ciStatus = query ? query(103, &ci, sizeof(ci), NULL) : (LONG)0xc0000002;
    LSTATUS regStatus = RegGetValueW(HKEY_LOCAL_MACHINE,
        L"SYSTEM\\CurrentControlSet\\Control\\SecureBoot\\State", L"UEFISecureBootEnabled",
        RRF_RT_REG_DWORD, NULL, &secureBoot, &size);
    int hashOk = FirmwareHash(digest);
    wprintf(L"{\n  \"architecture\": \"ARM64\",\n  \"dsdtSha256\": ");
    if (hashOk) {
        wprintf(L"\"");
        for (i = 0; i < 32; ++i) wprintf(L"%02X", digest[i]);
        wprintf(L"\"");
    } else wprintf(L"null");
    wprintf(L",\n  \"firmwareMatchesAudit\": %ls,\n  \"secureBootEnabled\": ",
        hashOk && memcmp(digest, ExpectedDsdt, 32) == 0 ? L"true" : L"false");
    if (regStatus == ERROR_SUCCESS) wprintf(L"%ls", secureBoot ? L"true" : L"false");
    else wprintf(L"null");
    wprintf(L",\n  \"codeIntegrityQueryStatus\": \"0x%08lX\",\n  \"testSigningEnabled\": ", (ULONG)ciStatus);
    if (ciStatus >= 0) wprintf(L"%ls", (ci.Options & 2) ? L"true" : L"false");
    else wprintf(L"null");
    wprintf(L",\n  \"codeIntegrityOptions\": \"0x%08lX\"\n}\n", ci.Options);
    return hashOk && memcmp(digest, ExpectedDsdt, 32) == 0 ? 0 : 2;
}

int wmain(int argc, WCHAR **argv)
{
    HANDLE device;
    FAN_PROBE_REPLY reply;
    DWORD returned = 0, code;
    UCHAR digest[32];
    if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--preflight") == 0)) return Preflight();
    if (argc != 2 || (wcscmp(argv[1], L"--identity") != 0 && wcscmp(argv[1], L"--telemetry") != 0)) {
        fwprintf(stderr, L"Usage: FanAcpiProbe.exe --preflight | --identity | --telemetry\n");
        return 1;
    }
    if (!FirmwareHash(digest) || memcmp(digest, ExpectedDsdt, 32) != 0) {
        fwprintf(stderr, L"Firmware differs from the audited DSDT. Re-audit before probing.\n");
        return 2;
    }
    if (!EnablePrivilege()) {
        fwprintf(stderr, L"Run this diagnostic from an elevated ARM64 terminal (SeLoadDriverPrivilege required).\n");
        return 3;
    }
    device = OpenProbe();
    if (device == INVALID_HANDLE_VALUE) {
        fwprintf(stderr, L"ECGE probe interface unavailable (Win32 %lu). Check installation and device status.\n", GetLastError());
        return 4;
    }
    code = wcscmp(argv[1], L"--telemetry") == 0 ? IOCTL_FAN_PROBE_TELEMETRY : IOCTL_FAN_PROBE_IDENTITY;
    ZeroMemory(&reply, sizeof(reply));
    if (!DeviceIoControl(device, code, NULL, 0, &reply, sizeof(reply), &returned, NULL)) {
        DWORD error = GetLastError();
        CloseHandle(device);
        fwprintf(stderr, L"Probe IOCTL failed (Win32 %lu).\n", error);
        return 5;
    }
    CloseHandle(device);
    if (returned != sizeof(reply) || reply.Size != sizeof(reply) ||
        reply.Version != FAN_PROBE_VERSION || reply.Operation != code) {
        fwprintf(stderr, L"Invalid probe response.\n");
        return 6;
    }
    PrintReply(&reply);
    return reply.Values[FanWho].Status >= 0 && reply.Values[FanWho].IntegerValid &&
        reply.Values[FanWho].Integer == 1 && reply.Values[FanCaps].Status >= 0 &&
        reply.Values[FanCaps].IntegerValid && reply.Values[FanCaps].Integer != 0 &&
        reply.Values[FanCaps].Integer <= MAXDWORD ? 0 : 7;
}

