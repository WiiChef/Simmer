#include <ntddk.h>
#pragma warning(push)
#pragma warning(disable:4324) /* WDK's deliberately aligned framework structures. */
#include <wdf.h>
#pragma warning(pop)
#include <initguid.h>
#include <devpkey.h>
#include "protocol.h"
#include "acpi_codec.h"

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD FanDeviceAdd;
EVT_WDF_IO_IN_CALLER_CONTEXT FanInCallerContext;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL FanIoctl;
EVT_WDF_DEVICE_PREPARE_HARDWARE FanPrepare;
EVT_WDF_DEVICE_RELEASE_HARDWARE FanRelease;

typedef struct FAN_CONTEXT {
    WDFIOTARGET Acpi;
    BOOLEAN TargetMatches;
    WCHAR InstanceId[64];
    WCHAR Location[64];
} FAN_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FAN_CONTEXT, FanContext);

static const GUID IdentityUuid =
    {0x196d5cf2,0x31c5,0x43c4,{0x9a,0xcf,0x23,0x8c,0x22,0xd7,0x0f,0x86}};
static const GUID FanUuid =
    {0xaf61c341,0x9ed6,0x4707,{0x9a,0x09,0xa5,0x1c,0x5a,0x8a,0xf9,0x2c}};

static BOOLEAN EqualString(PCWSTR left, PCWSTR right)
{
    UNICODE_STRING a, b;
    RtlInitUnicodeString(&a, left);
    RtlInitUnicodeString(&b, right);
    return RtlEqualUnicodeString(&a, &b, TRUE);
}

/* Query into an oversized temporary buffer, with an explicit terminated boundary. */
static BOOLEAN MatchDevice(WDFDEVICE device)
{
    WCHAR instance[128] = {0};
    WCHAR paths[512] = {0};
    ULONG actual;
    DEVPROPTYPE type;
    NTSTATUS status;
    SIZE_T i = 0;
    BOOLEAN locationMatches = FALSE;
    PDEVICE_OBJECT pdo = WdfDeviceWdmGetPhysicalDevice(device);
    FAN_CONTEXT *ctx = FanContext(device);
    status = IoGetDevicePropertyData(pdo, &DEVPKEY_Device_InstanceId,
        LOCALE_NEUTRAL, 0, sizeof(instance) - sizeof(WCHAR), instance, &actual, &type);
    if (!NT_SUCCESS(status) || type != DEVPROP_TYPE_STRING || actual < sizeof(WCHAR) ||
        actual > sizeof(instance) - sizeof(WCHAR) || actual % sizeof(WCHAR) != 0 ||
        instance[actual / sizeof(WCHAR) - 1] != L'\0' ||
        !EqualString(instance, L"ACPI\\ACPI0013\\0")) return FALSE;
    status = IoGetDevicePropertyData(pdo, &DEVPKEY_Device_LocationPaths,
        LOCALE_NEUTRAL, 0, sizeof(paths) - 2 * sizeof(WCHAR), paths, &actual, &type);
    if (!NT_SUCCESS(status) || type != DEVPROP_TYPE_STRING_LIST ||
        actual < 2 * sizeof(WCHAR) || actual > sizeof(paths) - 2 * sizeof(WCHAR) ||
        actual % sizeof(WCHAR) != 0) return FALSE;
    while (i < actual / sizeof(WCHAR) && paths[i] != L'\0') {
        if (EqualString(&paths[i], L"ACPI(_SB_)#ACPI(ECGE)")) locationMatches = TRUE;
        while (i < actual / sizeof(WCHAR) && paths[i] != L'\0') ++i;
        ++i;
    }
    if (!locationMatches) return FALSE;
    RtlCopyMemory(ctx->InstanceId, L"ACPI\\ACPI0013\\0", sizeof(L"ACPI\\ACPI0013\\0"));
    RtlCopyMemory(ctx->Location, L"ACPI(_SB_)#ACPI(ECGE)", sizeof(L"ACPI(_SB_)#ACPI(ECGE)"));
    return TRUE;
}

NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING path)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, FanDeviceAdd);
    config.DriverPoolTag = 'pnaF';
    return WdfDriverCreate(driver, path, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS FanDeviceAdd(WDFDRIVER driver, PWDFDEVICE_INIT init)
{
    WDF_OBJECT_ATTRIBUTES attrs;
    WDF_PNPPOWER_EVENT_CALLBACKS pnp;
    WDF_IO_QUEUE_CONFIG queue;
    WDFDEVICE device;
    FAN_CONTEXT *ctx;
    NTSTATUS status;
    UNREFERENCED_PARAMETER(driver);
    WdfFdoInitSetFilter(init);
    WdfDeviceInitSetIoInCallerContextCallback(init, FanInCallerContext);
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
    pnp.EvtDevicePrepareHardware = FanPrepare;
    pnp.EvtDeviceReleaseHardware = FanRelease;
    WdfDeviceInitSetPnpPowerEventCallbacks(init, &pnp);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attrs, FAN_CONTEXT);
    attrs.ExecutionLevel = WdfExecutionLevelPassive;
    status = WdfDeviceCreate(&init, &attrs, &device);
    if (!NT_SUCCESS(status)) return status;
    ctx = FanContext(device);
    ctx->TargetMatches = MatchDevice(device);
    /* An accidental attachment to another ACPI0013 remains a passive filter. */
    if (!ctx->TargetMatches) return STATUS_SUCCESS;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queue, WdfIoQueueDispatchSequential);
    queue.PowerManaged = WdfTrue;
    queue.EvtIoDeviceControl = FanIoctl;
    status = WdfIoQueueCreate(device, &queue, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) return status;
    return WdfDeviceCreateDeviceInterface(device, &FanProbeInterface, NULL);
}

NTSTATUS FanPrepare(WDFDEVICE device, WDFCMRESLIST raw, WDFCMRESLIST translated)
{
    FAN_CONTEXT *ctx = FanContext(device);
    WDF_IO_TARGET_OPEN_PARAMS open;
    WDF_OBJECT_ATTRIBUTES attrs;
    NTSTATUS status;
    UNREFERENCED_PARAMETER(raw);
    UNREFERENCED_PARAMETER(translated);
    if (!ctx->TargetMatches) return STATUS_SUCCESS;
    WDF_OBJECT_ATTRIBUTES_INIT(&attrs);
    attrs.ParentObject = device;
    status = WdfIoTargetCreate(device, &attrs, &ctx->Acpi);
    if (!NT_SUCCESS(status)) return status;
    /* Address Acpi.sys PDO directly, avoiding AcpiDev's private IOCTL dispatch. */
    WDF_IO_TARGET_OPEN_PARAMS_INIT_EXISTING_DEVICE(&open,
        WdfDeviceWdmGetPhysicalDevice(device));
    status = WdfIoTargetOpen(ctx->Acpi, &open);
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(ctx->Acpi);
        ctx->Acpi = NULL;
    }
    return status;
}

NTSTATUS FanRelease(WDFDEVICE device, WDFCMRESLIST translated)
{
    FAN_CONTEXT *ctx = FanContext(device);
    UNREFERENCED_PARAMETER(translated);
    if (ctx->Acpi != NULL) {
        WdfIoTargetClose(ctx->Acpi);
        WdfObjectDelete(ctx->Acpi);
        ctx->Acpi = NULL;
    }
    return STATUS_SUCCESS;
}

VOID FanInCallerContext(WDFDEVICE device, WDFREQUEST request)
{
    WDF_REQUEST_PARAMETERS params;
    NTSTATUS status;
    WDF_REQUEST_PARAMETERS_INIT(&params);
    WdfRequestGetParameters(request, &params);
    if (params.Type == WdfRequestTypeDeviceControl &&
        (params.Parameters.DeviceIoControl.IoControlCode == IOCTL_FAN_PROBE_IDENTITY ||
         params.Parameters.DeviceIoControl.IoControlCode == IOCTL_FAN_PROBE_TELEMETRY)) {
        /* Runs in the issuer's context, before a passive queue can switch threads. */
        if (WdfRequestGetRequestorMode(request) != KernelMode &&
            !SeSinglePrivilegeCheck(RtlConvertLongToLuid(SE_LOAD_DRIVER_PRIVILEGE), UserMode)) {
            WdfRequestComplete(request, STATUS_PRIVILEGE_NOT_HELD);
            return;
        }
    }
    status = WdfDeviceEnqueueRequest(device, request);
    if (!NT_SUCCESS(status)) WdfRequestComplete(request, status);
}

static NTSTATUS Evaluate(FAN_CONTEXT *ctx, ULONG code, PVOID input, ULONG inputSize,
    FAN_PROBE_VALUE *value)
{
    WDF_MEMORY_DESCRIPTOR in, out;
    WDF_REQUEST_SEND_OPTIONS options;
    ULONG_PTR returned = 0;
    NTSTATUS status;
    RtlZeroMemory(value, sizeof(*value));
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&in, input, inputSize);
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&out, value->Raw, sizeof(value->Raw));
    WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&options, WDF_REL_TIMEOUT_IN_SEC(3));
    status = WdfIoTargetSendIoctlSynchronously(ctx->Acpi, NULL, code,
        &in, &out, &options, &returned);
    value->Status = status;
    if (returned > sizeof(value->Raw)) {
        value->Status = STATUS_DATA_ERROR;
        return value->Status;
    }
    value->RawLength = (ULONG)returned;
    if (NT_SUCCESS(status)) {
        value->IntegerValid = FanParseInteger(value->Raw, returned, &value->Integer);
    }
    return status;
}

static void Dsm(FAN_CONTEXT *ctx, const GUID *uuid, ULONG function, ULONG arg3,
    FAN_PROBE_VALUE *value)
{
    DECLSPEC_ALIGN(8) UCHAR input[FAN_DSM_INPUT_BYTES];
    FanBuildDsm(input, uuid, function, arg3);
    Evaluate(ctx, IOCTL_ACPI_EVAL_METHOD, input, sizeof(input), value);
    if (value->Status >= 0 && !value->IntegerValid) value->Status = STATUS_DATA_ERROR;
}

/* Fixed paths only; these optional object reads may be rejected by Acpi.sys. */
static void Object(FAN_CONTEXT *ctx, PCSTR path, FAN_PROBE_VALUE *value)
{
    ACPI_EVAL_INPUT_BUFFER_EX input;
    SIZE_T n = 0;
    RtlZeroMemory(&input, sizeof(input));
    input.Signature = ACPI_EVAL_INPUT_BUFFER_SIGNATURE_EX;
    while (path[n] != '\0' && n < sizeof(input.MethodName) - 1) {
        input.MethodName[n] = path[n];
        ++n;
    }
    Evaluate(ctx, IOCTL_ACPI_EVAL_METHOD_EX, &input, sizeof(input), value);
}

static void Query(FAN_CONTEXT *ctx, BOOLEAN telemetry, FAN_PROBE_REPLY *reply)
{
    FAN_PROBE_VALUE *v = reply->Values;
    ULONG i, caps;
    for (i = 0; i < FAN_PROBE_VALUE_COUNT; ++i) v[i].Status = STATUS_NOT_SUPPORTED;
    Object(ctx, "_HID", &v[FanHid]);
    Dsm(ctx, &IdentityUuid, 1, 0x43, &v[FanWho]);
    if (!FanProbeIsInteger(&v[FanWho]) || v[FanWho].Integer != 1) return;
    Dsm(ctx, &IdentityUuid, 2, 0x44, &v[FanCaps]);
    Object(ctx, "EVER", &v[FanVersion]);
    Object(ctx, "\\_SB.FNTR", &v[FanCountConfig]);
    if (!telemetry || !FanProbeIsInteger(&v[FanCaps]) || v[FanCaps].Integer == 0 ||
        v[FanCaps].Integer > MAXULONG) return;
    caps = (ULONG)v[FanCaps].Integer;
    /* Fan 1 is the only selector in initial FNTR; fan 2 requires a live count of 2. */
    for (i = 0; i < 2; ++i) {
        ULONG slot = i == 0 ? Fan1StatusAck : Fan2StatusAck;
        ULONG selector = i + 1;
        if (i == 1 && (!FanProbeIsInteger(&v[FanCountConfig]) ||
            (v[FanCountConfig].Integer & 3) < 2)) break;
        if ((caps & 2) != 0) {
            Dsm(ctx, &FanUuid, 2, 0x10 | selector, &v[slot]);
            if (FanProbeIsInteger(&v[slot]) && v[slot].Integer == 0)
                Object(ctx, i == 0 ? "\\_SB.PAID.F1ST" : "\\_SB.PAID.F2ST", &v[slot+2]);
        }
        if ((caps & 4) != 0) {
            Dsm(ctx, &FanUuid, 3, 0x10 | selector, &v[slot+1]);
            if (FanProbeIsInteger(&v[slot+1]) && v[slot+1].Integer == 0)
                Object(ctx, i == 0 ? "\\_SB.PAID.F1SP" : "\\_SB.PAID.F2SP", &v[slot+3]);
        }
    }
}

VOID FanIoctl(WDFQUEUE queue, WDFREQUEST request, size_t outputLength,
    size_t inputLength, ULONG code)
{
    WDFDEVICE device = WdfIoQueueGetDevice(queue);
    FAN_CONTEXT *ctx = FanContext(device);
    FAN_PROBE_REPLY *reply;
    NTSTATUS status;
    WDF_REQUEST_SEND_OPTIONS send;
    UNREFERENCED_PARAMETER(outputLength);
    if (code != IOCTL_FAN_PROBE_IDENTITY && code != IOCTL_FAN_PROBE_TELEMETRY) {
        WdfRequestFormatRequestUsingCurrentType(request);
        WDF_REQUEST_SEND_OPTIONS_INIT(&send, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
        if (!WdfRequestSend(request, WdfDeviceGetIoTarget(device), &send))
            WdfRequestComplete(request, WdfRequestGetStatus(request));
        return;
    }
    if (!ctx->TargetMatches || ctx->Acpi == NULL) {
        WdfRequestComplete(request, STATUS_DEVICE_NOT_READY);
        return;
    }
    if (inputLength != 0) {
        WdfRequestComplete(request, STATUS_INVALID_PARAMETER);
        return;
    }
    status = WdfRequestRetrieveOutputBuffer(request, sizeof(*reply), (PVOID *)&reply, NULL);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(request, status);
        return;
    }
    RtlZeroMemory(reply, sizeof(*reply));
    reply->Size = sizeof(*reply);
    reply->Version = FAN_PROBE_VERSION;
    reply->Operation = code;
    RtlCopyMemory(reply->InstanceId, ctx->InstanceId, sizeof(reply->InstanceId));
    RtlCopyMemory(reply->Location, ctx->Location, sizeof(reply->Location));
    Query(ctx, code == IOCTL_FAN_PROBE_TELEMETRY, reply);
    WdfRequestCompleteWithInformation(request, STATUS_SUCCESS, sizeof(*reply));
}
