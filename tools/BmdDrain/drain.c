/* Simmer battery drain on AC (test-signed KMDF control device).
   Puts the battery maintenance control into "discharge with AC present" (_BMC bit 2) and takes it out again.
   Derived from the BatteryDischargePulse diagnostic: same validated PDOs, same baseline gate, same fixed _BMC
   argument set; no caller-provided paths, methods or arguments.
   The kernel, not the caller, guarantees the drain ends:
     - STOP request, or the controller's handle closing (process exit or crash)   -> restore
     - no heartbeat for DRAIN_BEAT_SECONDS, or DRAIN_MAX_MINUTES elapsed            -> restore (5 s timer)
     - system shutdown, driver unload                                               -> restore
   Restoration is verified by a fresh status read; until then the timer keeps retrying. */
#include <ntifs.h>
#pragma warning(push)
#pragma warning(disable:4324)
#include <wdf.h>
#pragma warning(pop)
#include <wdmsec.h>
#include <initguid.h>
#include <devpkey.h>
#include <acpiioct.h>
#include "protocol.h"

#define RAW_SIZE 256u
typedef struct BMD_VALUE { LONG Status; ULONG Length; UCHAR Raw[RAW_SIZE]; } BMD_VALUE;

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_UNLOAD DrainUnload;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL DrainIoctl;
EVT_WDF_FILE_CLEANUP DrainCleanup;
EVT_WDF_TIMER DrainTimer;
EVT_WDF_DEVICE_SHUTDOWN_NOTIFICATION DrainShutdown;

static WDFDEVICE Control;
static WDFWAITLOCK Lock;
static WDFTIMER Timer;
/* All below under Lock. */
static BOOLEAN Active, RestorePending, Closing, Unloading;   /* Closing: shutdown or unload, no new drain */
static ULONG SavedControl, StopReason, LastControl = MAXULONG;
static WDFFILEOBJECT Owner;
static WCHAR BatteryPdo[DRAIN_PDO_CHARS], PmicPdo[DRAIN_PDO_CHARS];   /* from START, checked by Validate on every use */
static ULONGLONG Started, LastBeat, Deadline;
static BMD_VALUE V[8];

static PCSTR const Objects[8] = { "_HID", "_BMD", "BMDT", "BMD0", "BMD1", "BMD2", "BMD3", "BMD4" };
#define SECONDS(n) ((ULONGLONG)(n) * 10000000ULL)

static BOOLEAN Equal(PCWSTR a, PCWSTR b)
{
    UNICODE_STRING x, y; RtlInitUnicodeString(&x, a); RtlInitUnicodeString(&y, b);
    return RtlEqualUnicodeString(&x, &y, TRUE);
}
static NTSTATUS Validate(PDEVICE_OBJECT pdo, PCWSTR instance, PCWSTR location)
{
    WCHAR text[512] = { 0 }; ULONG actual = 0; DEVPROPTYPE type = 0; NTSTATUS s;
    s = IoGetDevicePropertyData(pdo, &DEVPKEY_Device_InstanceId, LOCALE_NEUTRAL, 0, sizeof(text) - sizeof(WCHAR), text, &actual, &type);
    if (!NT_SUCCESS(s)) return s;
    if (type != DEVPROP_TYPE_STRING || actual < 2 || actual > sizeof(text) - 2 || actual % 2 || !Equal(text, instance)) return STATUS_OBJECT_TYPE_MISMATCH;
    RtlZeroMemory(text, sizeof(text));
    s = IoGetDevicePropertyData(pdo, &DEVPKEY_Device_LocationPaths, LOCALE_NEUTRAL, 0, sizeof(text) - sizeof(WCHAR), text, &actual, &type);
    if (!NT_SUCCESS(s)) return s;
    if (type != DEVPROP_TYPE_STRING_LIST || actual < 4 || actual > sizeof(text) - 2 || actual % 2 || !Equal(text, location)) return STATUS_OBJECT_TYPE_MISMATCH;
    return STATUS_SUCCESS;
}
static VOID Read(WDFIOTARGET target, PCSTR name, BMD_VALUE *value)
{
    ACPI_EVAL_INPUT_BUFFER_EX input; WDF_MEMORY_DESCRIPTOR in, out; WDF_REQUEST_SEND_OPTIONS options; ULONG_PTR actual = 0; SIZE_T i = 0;
    RtlZeroMemory(&input, sizeof(input)); input.Signature = ACPI_EVAL_INPUT_BUFFER_SIGNATURE_EX;
    while (name[i] && i < sizeof(input.MethodName) - 1) { input.MethodName[i] = name[i]; ++i; }
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&in, &input, sizeof(input));
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&out, value->Raw, sizeof(value->Raw));
    WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&options, WDF_REL_TIMEOUT_IN_SEC(3));
    value->Status = WdfIoTargetSendIoctlSynchronously(target, NULL, IOCTL_ACPI_EVAL_METHOD_EX, &in, &out, &options, &actual);
    if (actual > sizeof(value->Raw)) { value->Status = STATUS_DATA_ERROR; return; }
    value->Length = (ULONG)actual;
}
/* Reads Objects[first..first+count) into values, or (write) evaluates _BMC(argument) on the battery device. */
static NTSTATUS Group(BOOLEAN pmic, BMD_VALUE *values, ULONG first, ULONG count, BOOLEAN write, ULONG argument)
{
    WDFIOTARGET target = NULL; PFILE_OBJECT file = NULL; PDEVICE_OBJECT top = NULL, base = NULL; UNICODE_STRING path;
    WDF_OBJECT_ATTRIBUTES attrs; WDF_IO_TARGET_OPEN_PARAMS params; NTSTATUS s; ULONG i;
    if (first >= 8 || count == 0 || count > 8 - first) return STATUS_INVALID_PARAMETER;
    for (i = 0; i < count; ++i) { values[i].Status = STATUS_NOT_SUPPORTED; values[i].Length = 0; }
    RtlInitUnicodeString(&path, pmic ? PmicPdo : BatteryPdo);
    if (!path.Length) { s = STATUS_NO_SUCH_DEVICE; goto done; }
    s = IoGetDeviceObjectPointer(&path, FILE_READ_ATTRIBUTES, &file, &top);
    if (!NT_SUCCESS(s)) goto done;
    base = IoGetDeviceAttachmentBaseRef(top);
    if (!base) { s = STATUS_NO_SUCH_DEVICE; goto done; }
    s = Validate(base, pmic ? L"ACPI\\QCOM0F8E\\2&daba3ff&0" : L"ACPI\\PNP0C0A\\2&daba3ff&0", pmic ? L"ACPI(_SB_)#ACPI(PMGK)" : L"ACPI(_SB_)#ACPI(CMBD)");
    if (!NT_SUCCESS(s)) goto done;
    WDF_OBJECT_ATTRIBUTES_INIT(&attrs); attrs.ParentObject = Control;
    s = WdfIoTargetCreate(Control, &attrs, &target);
    if (!NT_SUCCESS(s)) goto done;
    WDF_IO_TARGET_OPEN_PARAMS_INIT_EXISTING_DEVICE(&params, base);
    s = WdfIoTargetOpen(target, &params); if (!NT_SUCCESS(s)) goto done;
    if (write) {
        ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER input; WDF_MEMORY_DESCRIPTOR in, out; WDF_REQUEST_SEND_OPTIONS options; ULONG_PTR actual = 0;
        /* Only the four values 0/2/4/6: charger-inhibit (2) and discharge-on-AC (4). Never calibration (1). */
        if (pmic || count != 1 || (argument != 0 && argument != 2 && argument != 4 && argument != 6)) {
            s = STATUS_INVALID_PARAMETER; WdfIoTargetClose(target); goto done;
        }
        RtlZeroMemory(&input, sizeof(input));
        input.Signature = ACPI_EVAL_INPUT_BUFFER_SIMPLE_INTEGER_SIGNATURE;
        RtlCopyMemory(input.MethodName, "_BMC", 4); input.IntegerArgument = argument;
        WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&in, &input, sizeof(input));
        WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&out, values[0].Raw, sizeof(values[0].Raw));
        WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_TIMEOUT);
        WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&options, WDF_REL_TIMEOUT_IN_SEC(3));
        values[0].Status = WdfIoTargetSendIoctlSynchronously(target, NULL, IOCTL_ACPI_EVAL_METHOD, &in, &out, &options, &actual);
        if (actual > sizeof(values[0].Raw)) values[0].Status = STATUS_DATA_ERROR;
        else values[0].Length = (ULONG)actual;
    } else {
        for (i = 0; i < 8; ++i) if (i >= first && i - first < count) Read(target, Objects[i], &values[i - first]);
    }
    WdfIoTargetClose(target);
done:
    if (target) WdfObjectDelete(target);
    if (base) ObDereferenceObject(base);
    if (file) ObDereferenceObject(file);
    if (!NT_SUCCESS(s)) for (i = 0; i < count; ++i) values[i].Status = s;
    return s;
}
static BOOLEAN Integer(BMD_VALUE *v, ULONG *value)
{
    ULONG signature, length, count; USHORT type, bytes; ULONGLONG n = 0;
    if (!NT_SUCCESS(v->Status) || (v->Length != 20 && v->Length != 24)) return FALSE;
    RtlCopyMemory(&signature, v->Raw, 4); RtlCopyMemory(&length, v->Raw + 4, 4);
    RtlCopyMemory(&count, v->Raw + 8, 4); RtlCopyMemory(&type, v->Raw + 12, 2); RtlCopyMemory(&bytes, v->Raw + 14, 2);
    if (signature != ACPI_EVAL_OUTPUT_BUFFER_SIGNATURE || length != v->Length || count != 1 || type != 0 ||
        !((bytes == 4 && length == 20) || (bytes == 8 && length == 24))) return FALSE;
    RtlCopyMemory(&n, v->Raw + 16, bytes);
    if (n > MAXULONG) return FALSE;
    *value = (ULONG)n; return TRUE;
}
/* Fresh maintenance status: _BMD refreshes the cache, BMD0 on the PMIC bridge reads it (the _BMD package itself
   is unreliable on this firmware). */
static BOOLEAN ReadControl(ULONG *status)
{
    Group(FALSE, &V[0], 1, 1, FALSE, 0);
    Group(TRUE, &V[1], 3, 1, FALSE, 0);
    if (NT_SUCCESS(V[0].Status) && Integer(&V[1], status)) { LastControl = *status; return TRUE; }
    LastControl = MAXULONG; return FALSE;
}
static BOOLEAN Bmc(ULONG argument)
{
    ULONG result = MAXULONG;
    Group(FALSE, &V[0], 0, 1, TRUE, argument);
    return Integer(&V[0], &result) && result == 0;
}
/* The pulse driver's gate: raw BMDT valid and matching the cached fields, discharge-on-AC supported (capability
   bit 2), and neither calibration nor a discharge already running. */
static BOOLEAN Baseline(ULONG *status)
{
    BMD_VALUE *raw = &V[2]; ULONG signature, length, count, fields[5], n, i; USHORT type, bytes;
    Group(FALSE, &V[0], 0, 2, FALSE, 0);
    Group(TRUE, &V[2], 2, 6, FALSE, 0);
    if (!NT_SUCCESS(V[1].Status) || !NT_SUCCESS(raw->Status) || raw->Length != 38) return FALSE;
    RtlCopyMemory(&signature, raw->Raw, 4); RtlCopyMemory(&length, raw->Raw + 4, 4);
    RtlCopyMemory(&count, raw->Raw + 8, 4); RtlCopyMemory(&type, raw->Raw + 12, 2); RtlCopyMemory(&bytes, raw->Raw + 14, 2);
    if (signature != ACPI_EVAL_OUTPUT_BUFFER_SIGNATURE || length != 38 || count != 1 || type != 2 || bytes != 22 || raw->Raw[16] != 0) return FALSE;
    RtlCopyMemory(fields, raw->Raw + 18, sizeof(fields));
    for (i = 0; i < 5; ++i) if (!Integer(&V[3 + i], &n) || n != fields[i]) return FALSE;
    if (!(fields[1] & 4) || (fields[0] & 5)) return FALSE;
    *status = fields[0]; LastControl = fields[0]; return TRUE;
}
/* Under Lock. Puts the saved control back and confirms it with fresh reads (the firmware's status can lag the
   acknowledgement by a few seconds). Unverified: stays Active with RestorePending, and the timer retries. */
static NTSTATUS RestoreLocked(ULONG reason)
{
    ULONG st = 0, i; LARGE_INTEGER delay;
    if (!Active) return STATUS_SUCCESS;
    RestorePending = TRUE;
    for (i = 0; i < 6; ++i) {
        if (i == 0 || i == 3) Bmc(SavedControl);
        delay.QuadPart = -10000000LL; KeDelayExecutionThread(KernelMode, FALSE, &delay);
        if (ReadControl(&st) && (st & 6) == SavedControl) {
            Active = FALSE; RestorePending = FALSE; Owner = NULL; StopReason = reason;
            return STATUS_SUCCESS;
        }
    }
    StopReason = reason | 0x80;
    return STATUS_IO_TIMEOUT;
}
/* "\Device\" followed by 1-16 hex digits, terminated within the field. */
static BOOLEAN PdoName(const WCHAR *name)
{
    static const WCHAR prefix[] = L"\\Device\\"; ULONG i, n = ARRAYSIZE(prefix) - 1;
    for (i = 0; i < n; ++i) if (name[i] != prefix[i]) return FALSE;
    for (; i < DRAIN_PDO_CHARS && name[i]; ++i)
        if (i - n >= 16 || !((name[i] >= L'0' && name[i] <= L'9') || (name[i] >= L'a' && name[i] <= L'f') || (name[i] >= L'A' && name[i] <= L'F'))) return FALSE;
    return i > n && i < DRAIN_PDO_CHARS;
}
static VOID Fill(DRAIN_STATUS *o, NTSTATUS result)
{
    RtlZeroMemory(o, sizeof(*o));
    o->Size = sizeof(*o); o->Version = 2; o->Active = Active; o->Control = LastControl; o->SavedControl = SavedControl;
    o->StopReason = StopReason; o->Result = result;
    o->ElapsedSeconds = Started ? (ULONG)((KeQueryInterruptTime() - Started) / SECONDS(1)) : 0;
}
VOID DrainIoctl(WDFQUEUE queue, WDFREQUEST request, size_t outLength, size_t inLength, ULONG code)
{
    DRAIN_STATUS *out; DRAIN_START *in; NTSTATUS s, result = STATUS_SUCCESS; ULONG st = 0, minutes; ULONGLONG now;
    UNREFERENCED_PARAMETER(queue);
    if (outLength != sizeof(DRAIN_STATUS) || inLength != (code == IOCTL_DRAIN_START ? sizeof(DRAIN_START) : 0)) {
        WdfRequestComplete(request, STATUS_INVALID_PARAMETER); return;
    }
    s = WdfRequestRetrieveOutputBuffer(request, sizeof(*out), (PVOID *)&out, NULL);
    if (!NT_SUCCESS(s)) { WdfRequestComplete(request, s); return; }
    WdfWaitLockAcquire(Lock, NULL);
    now = KeQueryInterruptTime();
    switch (code) {
    case IOCTL_DRAIN_START:
        s = WdfRequestRetrieveInputBuffer(request, sizeof(*in), (PVOID *)&in, NULL);
        if (!NT_SUCCESS(s)) { result = s; break; }
        if (Active || Closing) { result = STATUS_DEVICE_BUSY; break; }
        if (!PdoName(in->BatteryPdo) || !PdoName(in->PmicPdo)) { result = STATUS_INVALID_PARAMETER; break; }
        RtlCopyMemory(BatteryPdo, in->BatteryPdo, sizeof(BatteryPdo)); RtlCopyMemory(PmicPdo, in->PmicPdo, sizeof(PmicPdo));
        if (!Baseline(&st)) { result = STATUS_DEVICE_NOT_READY; break; }
        minutes = in->MaxMinutes < 1 ? 1 : in->MaxMinutes > DRAIN_MAX_MINUTES ? DRAIN_MAX_MINUTES : in->MaxMinutes;
        SavedControl = st & 2; StopReason = STOP_NONE;
        Started = LastBeat = now; Deadline = now + SECONDS(60ULL * minutes);
        Owner = WdfRequestGetFileObject(request);
        Active = TRUE; RestorePending = FALSE;   /* before the request: a start that times out after the firmware took it still gets restored */
        if (!Bmc(SavedControl | 4)) { RestoreLocked(STOP_START_FAILED); result = STATUS_IO_DEVICE_ERROR; break; }
        ReadControl(&st);
        break;
    case IOCTL_DRAIN_BEAT:
        if (!Active || RestorePending || Owner != WdfRequestGetFileObject(request)) { result = STATUS_INVALID_DEVICE_STATE; break; }
        LastBeat = now;
        break;
    case IOCTL_DRAIN_STOP:
        result = RestoreLocked(STOP_REQUEST);
        break;
    case IOCTL_DRAIN_STATUS:
        ReadControl(&st);
        break;
    default:
        result = STATUS_INVALID_DEVICE_REQUEST;
    }
    Fill(out, result);
    WdfWaitLockRelease(Lock);
    WdfRequestCompleteWithInformation(request, STATUS_SUCCESS, sizeof(*out));
}
VOID DrainCleanup(WDFFILEOBJECT file)
{
    WdfWaitLockAcquire(Lock, NULL);
    if (Active && Owner == file) RestoreLocked(STOP_CLOSED);
    if (Owner == file) Owner = NULL;
    WdfWaitLockRelease(Lock);
}
/* One-shot passive timer (KMDF does not allow periodic passive timers), rearmed here every 5 s until unload. */
VOID DrainTimer(WDFTIMER timer)
{
    ULONGLONG now;
    WdfWaitLockAcquire(Lock, NULL);
    now = KeQueryInterruptTime();
    if (Active) {
        if (RestorePending) RestoreLocked(StopReason & 0x7F);
        else if (now - LastBeat > SECONDS(DRAIN_BEAT_SECONDS)) RestoreLocked(STOP_HEARTBEAT);
        else if (now > Deadline) RestoreLocked(STOP_DEADLINE);
    }
    if (!Unloading) WdfTimerStart(timer, WDF_REL_TIMEOUT_IN_MS(5000));
    WdfWaitLockRelease(Lock);
}
VOID DrainShutdown(WDFDEVICE device)
{
    UNREFERENCED_PARAMETER(device);
    WdfWaitLockAcquire(Lock, NULL);
    Closing = TRUE;
    RestoreLocked(STOP_SHUTDOWN);
    WdfWaitLockRelease(Lock);
}
VOID DrainUnload(WDFDRIVER driver)
{
    ULONG i;
    UNREFERENCED_PARAMETER(driver);
    if (!Lock) return;
    WdfWaitLockAcquire(Lock, NULL); Closing = Unloading = TRUE; WdfWaitLockRelease(Lock);
    if (Timer) WdfTimerStop(Timer, TRUE);
    /* Unload cannot be refused, so try hard: up to five rounds of restore and verify (about 30 s). The runner only
       unloads after the driver reports the restore verified. */
    WdfWaitLockAcquire(Lock, NULL);
    for (i = 0; i < 5 && Active; ++i) RestoreLocked(STOP_UNLOAD);
    WdfWaitLockRelease(Lock);
    if (Control) { WdfObjectDelete(Control); Control = NULL; }
}
NTSTATUS DriverEntry(PDRIVER_OBJECT object, PUNICODE_STRING registry)
{
    WDF_DRIVER_CONFIG config; WDFDRIVER driver; PWDFDEVICE_INIT init; WDF_OBJECT_ATTRIBUTES attrs;
    WDF_IO_QUEUE_CONFIG queue; WDF_FILEOBJECT_CONFIG files; WDF_TIMER_CONFIG timer; NTSTATUS s;
    DECLARE_CONST_UNICODE_STRING(name, L"\\Device\\SimmerBmdDrain");
    DECLARE_CONST_UNICODE_STRING(link, L"\\DosDevices\\SimmerBmdDrain");
    WDF_DRIVER_CONFIG_INIT(&config, WDF_NO_EVENT_CALLBACK);
    config.DriverInitFlags = WdfDriverInitNonPnpDriver; config.EvtDriverUnload = DrainUnload; config.DriverPoolTag = 'nrDS';
    s = WdfDriverCreate(object, registry, WDF_NO_OBJECT_ATTRIBUTES, &config, &driver);
    if (!NT_SUCCESS(s)) return s;
    s = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &Lock);
    if (!NT_SUCCESS(s)) return s;
    init = WdfControlDeviceInitAllocate(driver, &SDDL_DEVOBJ_SYS_ALL_ADM_ALL);   /* SYSTEM and administrators only */
    if (!init) return STATUS_INSUFFICIENT_RESOURCES;
    WdfDeviceInitSetDeviceType(init, DRAIN_DEVICE_TYPE);
    WdfDeviceInitSetCharacteristics(init, FILE_DEVICE_SECURE_OPEN, FALSE);
    WdfDeviceInitSetIoType(init, WdfDeviceIoBuffered); WdfDeviceInitSetExclusive(init, TRUE);
    WDF_FILEOBJECT_CONFIG_INIT(&files, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, DrainCleanup);
    WdfDeviceInitSetFileObjectConfig(init, &files, WDF_NO_OBJECT_ATTRIBUTES);
    WdfControlDeviceInitSetShutdownNotification(init, DrainShutdown, WdfDeviceShutdown);
    s = WdfDeviceInitAssignName(init, &name);
    if (!NT_SUCCESS(s)) { WdfDeviceInitFree(init); return s; }
    WDF_OBJECT_ATTRIBUTES_INIT(&attrs); attrs.ExecutionLevel = WdfExecutionLevelPassive;
    s = WdfDeviceCreate(&init, &attrs, &Control);
    if (!NT_SUCCESS(s)) { if (init) WdfDeviceInitFree(init); return s; }
    s = WdfDeviceCreateSymbolicLink(Control, &link); if (!NT_SUCCESS(s)) goto fail;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queue, WdfIoQueueDispatchSequential);
    queue.PowerManaged = WdfFalse; queue.EvtIoDeviceControl = DrainIoctl;
    s = WdfIoQueueCreate(Control, &queue, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(s)) goto fail;
    WDF_TIMER_CONFIG_INIT(&timer, DrainTimer);
    timer.AutomaticSerialization = FALSE;
    WDF_OBJECT_ATTRIBUTES_INIT(&attrs); attrs.ParentObject = Control; attrs.ExecutionLevel = WdfExecutionLevelPassive;
    s = WdfTimerCreate(&timer, &attrs, &Timer);
    if (!NT_SUCCESS(s)) goto fail;
    WdfTimerStart(Timer, WDF_REL_TIMEOUT_IN_MS(5000));
    WdfControlFinishInitializing(Control);
    return STATUS_SUCCESS;
fail:
    WdfObjectDelete(Control); Control = NULL; return s;
}
