#include <ntifs.h>
#pragma warning(push)
#pragma warning(disable:4324)
#include <wdf.h>
#pragma warning(pop)
#include <wdmsec.h>
#include <initguid.h>
#include <devpkey.h>
#include "../FanAcpiProbe/protocol.h"
#include "../FanAcpiProbe/acpi_codec.h"
#include "debug_codec.h"
#include "targets.h"

#define IOCTL_FAN_DIAG CTL_CODE(0x8338u,0x800u,METHOD_BUFFERED,FILE_READ_DATA|FILE_WRITE_DATA)
DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_UNLOAD DiagUnload;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL DiagIoctl;
static WDFDEVICE Control;

typedef struct DIAG_TARGET {
    PCWSTR Name;
    PCWSTR Instance;
    PCWSTR Location;
    PCSTR Objects[13];
} DIAG_TARGET;
static const DIAG_TARGET Targets[] = {
    {ECGE_PDO,L"ACPI\\ACPI0013\\0",L"ACPI(_SB_)#ACPI(ECGE)",
     {"_HID","QWAI","QCAP","EVER","AVER","\\_SB.FNTR","\\_SB.EMPF",NULL}},
    {IC10_PDO,L"ACPI\\QCOM0F10\\A",L"ACPI(_SB_)#ACPI(IC10)",
     {"_HID","AVBL","ECRD","PRCT","FC43","FC44","UMPC","SL5B",NULL}},
    {PAID_PDO,L"ACPI\\QCOM1033\\0",L"ACPI(_SB_)#ACPI(PAID)",
     {"_HID","F1ST","F1SP","F2ST","F2SP","\\_SB.FNTR","\\_SB.EMPF",NULL}}
};

static BOOLEAN Equal(PCWSTR left,PCWSTR right)
{
    UNICODE_STRING a,b;
    RtlInitUnicodeString(&a,left); RtlInitUnicodeString(&b,right);
    return RtlEqualUnicodeString(&a,&b,TRUE);
}

static NTSTATUS Validate(PDEVICE_OBJECT pdo,const DIAG_TARGET *expected)
{
    WCHAR text[512]={0}; ULONG actual=0; DEVPROPTYPE type=0; NTSTATUS status;
    status=IoGetDevicePropertyData(pdo,&DEVPKEY_Device_InstanceId,LOCALE_NEUTRAL,0,
        sizeof(text)-sizeof(WCHAR),text,&actual,&type);
    if(!NT_SUCCESS(status)) return status;
    if(type!=DEVPROP_TYPE_STRING || actual<2 || actual>sizeof(text)-2 || actual%2 ||
        !Equal(text,expected->Instance)) return STATUS_OBJECT_TYPE_MISMATCH;
    RtlZeroMemory(text,sizeof(text));
    status=IoGetDevicePropertyData(pdo,&DEVPKEY_Device_LocationPaths,LOCALE_NEUTRAL,0,
        sizeof(text)-sizeof(WCHAR),text,&actual,&type);
    if(!NT_SUCCESS(status)) return status;
    if(type!=DEVPROP_TYPE_STRING_LIST || actual<4 || actual>sizeof(text)-2 || actual%2 ||
        !Equal(text,expected->Location)) return STATUS_OBJECT_TYPE_MISMATCH;
    return STATUS_SUCCESS;
}

static NTSTATUS Open(WDFDEVICE device,ULONG index,WDFIOTARGET *target,
    PFILE_OBJECT *file,PDEVICE_OBJECT *base)
{
    UNICODE_STRING name;
    PDEVICE_OBJECT top=NULL;
    WDF_IO_TARGET_OPEN_PARAMS params;
    WDF_OBJECT_ATTRIBUTES attrs;
    NTSTATUS status;
    *target=NULL; *file=NULL; *base=NULL;
    RtlInitUnicodeString(&name,Targets[index].Name);
    status=IoGetDeviceObjectPointer(&name,FILE_READ_ATTRIBUTES,file,&top);
    if(!NT_SUCCESS(status)) return status;
    *base=IoGetDeviceAttachmentBaseRef(top);
    if(*base==NULL){status=STATUS_NO_SUCH_DEVICE;goto done;}
    status=Validate(*base,&Targets[index]);
    if(!NT_SUCCESS(status)) goto done;
    WDF_OBJECT_ATTRIBUTES_INIT(&attrs); attrs.ParentObject=device;
    status=WdfIoTargetCreate(device,&attrs,target);
    if(!NT_SUCCESS(status)) goto done;
    WDF_IO_TARGET_OPEN_PARAMS_INIT_EXISTING_DEVICE(&params,*base);
    status=WdfIoTargetOpen(*target,&params);
done:
    if(!NT_SUCCESS(status)) {
        if(*target){WdfObjectDelete(*target);*target=NULL;}
        if(*base){ObDereferenceObject(*base);*base=NULL;}
        if(*file){ObDereferenceObject(*file);*file=NULL;}
    }
    return status;
}

static void ReadObject(WDFIOTARGET target,PCSTR name,FAN_PROBE_VALUE *v)
{
    ACPI_EVAL_INPUT_BUFFER_EX input;
    WDF_MEMORY_DESCRIPTOR in,out;
    WDF_REQUEST_SEND_OPTIONS options;
    ULONG_PTR actual=0; SIZE_T i=0;
    RtlZeroMemory(&input,sizeof(input));
    input.Signature=ACPI_EVAL_INPUT_BUFFER_SIGNATURE_EX;
    while(name[i] && i<sizeof(input.MethodName)-1){input.MethodName[i]=name[i];++i;}
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&in,&input,sizeof(input));
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&out,v->Raw,sizeof(v->Raw));
    WDF_REQUEST_SEND_OPTIONS_INIT(&options,WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&options,WDF_REL_TIMEOUT_IN_SEC(3));
    v->Status=WdfIoTargetSendIoctlSynchronously(target,NULL,IOCTL_ACPI_EVAL_METHOD_EX,
        &in,&out,&options,&actual);
    if(actual>sizeof(v->Raw)){v->Status=STATUS_DATA_ERROR;return;}
    v->RawLength=(ULONG)actual;
    if(NT_SUCCESS(v->Status))v->IntegerValid=FanParseInteger(v->Raw,actual,&v->Integer);
}

/* Reproduce only the EC status pair used by PEP's display-off/on methods.
   Always restore state 4 before returning, including an unsuccessful state-3 call. */
static void DisplayStatus(WDFIOTARGET target,ULONG state,FAN_PROBE_VALUE *v)
{
    static const GUID uuid={0xaf61c341,0x9ed6,0x4707,{0x9a,0x09,0xa5,0x1c,0x5a,0x8a,0xf9,0x2c}};
    DECLSPEC_ALIGN(8) UCHAR input[FAN_DSM_INPUT_BYTES];
    WDF_MEMORY_DESCRIPTOR in,out;
    WDF_REQUEST_SEND_OPTIONS options;
    ULONG_PTR actual=0;
    FanBuildDsm(input,&uuid,4,state);
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&in,input,sizeof(input));
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&out,v->Raw,sizeof(v->Raw));
    WDF_REQUEST_SEND_OPTIONS_INIT(&options,WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&options,WDF_REL_TIMEOUT_IN_SEC(3));
    v->Status=WdfIoTargetSendIoctlSynchronously(target,NULL,IOCTL_ACPI_EVAL_METHOD,&in,&out,&options,&actual);
    if(actual>sizeof(v->Raw)){v->Status=STATUS_DATA_ERROR;return;}
    v->RawLength=(ULONG)actual;
    if(NT_SUCCESS(v->Status))v->IntegerValid=FanParseInteger(v->Raw,actual,&v->Integer);
}

static void DebugRpm(WDFIOTARGET target,ULONG fan,ULONG flags,ULONG rpm,FAN_PROBE_VALUE *v)
{
    DECLSPEC_ALIGN(8) UCHAR input[FAN_DEBUG_INPUT_BYTES];
    WDF_MEMORY_DESCRIPTOR in,out;
    WDF_REQUEST_SEND_OPTIONS options;
    ULONG_PTR actual=0;
    BuildDebugDsm(input,fan,flags,rpm);
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&in,input,sizeof(input));
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&out,v->Raw,sizeof(v->Raw));
    WDF_REQUEST_SEND_OPTIONS_INIT(&options,WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&options,WDF_REL_TIMEOUT_IN_SEC(3));
    v->Status=WdfIoTargetSendIoctlSynchronously(target,NULL,IOCTL_ACPI_EVAL_METHOD,&in,&out,&options,&actual);
    if(actual>sizeof(v->Raw)){v->Status=STATUS_DATA_ERROR;return;}
    v->RawLength=(ULONG)actual;
    if(NT_SUCCESS(v->Status))v->IntegerValid=FanParseInteger(v->Raw,actual,&v->Integer);
}

VOID DiagIoctl(WDFQUEUE queue,WDFREQUEST request,size_t outLength,size_t inLength,ULONG code)
{
    ULONG *input,index,i,operation;
    FAN_PROBE_REPLY *reply;
    WDFIOTARGET target=NULL;
    PFILE_OBJECT file=NULL;
    PDEVICE_OBJECT base=NULL;
    NTSTATUS status;
    UNREFERENCED_PARAMETER(outLength);
    if(code!=IOCTL_FAN_DIAG || inLength!=sizeof(ULONG)){
        WdfRequestComplete(request,STATUS_INVALID_PARAMETER);return;
    }
    status=WdfRequestRetrieveInputBuffer(request,sizeof(ULONG),(PVOID *)&input,NULL);
    if(!NT_SUCCESS(status)){WdfRequestComplete(request,status);return;}
    operation=*input;
    if(operation>5){WdfRequestComplete(request,STATUS_INVALID_PARAMETER);return;}
    index=operation>=4 ? 1 : (operation==3 ? 0 : operation);
    status=WdfRequestRetrieveOutputBuffer(request,sizeof(*reply),(PVOID *)&reply,NULL);
    if(!NT_SUCCESS(status)){WdfRequestComplete(request,status);return;}
    RtlZeroMemory(reply,sizeof(*reply));
    reply->Size=sizeof(*reply); reply->Version=1;reply->Operation=operation;
    for(i=0;i<13;++i)reply->Values[i].Status=STATUS_NOT_SUPPORTED;
    status=Open(WdfIoQueueGetDevice(queue),index,&target,&file,&base);
    if(!NT_SUCCESS(status)){WdfRequestComplete(request,status);return;}
    for(i=0;Targets[index].Instance[i] && i<63;++i)reply->InstanceId[i]=Targets[index].Instance[i];
    for(i=0;Targets[index].Location[i] && i<63;++i)reply->Location[i]=Targets[index].Location[i];
    if(operation>=4) {
        LARGE_INTEGER delay;
        ULONG fan=operation-3;
        DebugRpm(target,fan,3,3000,&reply->Values[0]);
        if(NT_SUCCESS(reply->Values[0].Status)) {
            delay.QuadPart=-15LL*1000*1000*10;
            KeDelayExecutionThread(KernelMode,FALSE,&delay);
        }
        DebugRpm(target,fan,0,0,&reply->Values[1]);
    } else if(operation==3) {
        LARGE_INTEGER delay;
        DisplayStatus(target,3,&reply->Values[0]);
        if(NT_SUCCESS(reply->Values[0].Status)) {
            delay.QuadPart=-20LL*1000*1000*10;
            KeDelayExecutionThread(KernelMode,FALSE,&delay);
        }
        DisplayStatus(target,4,&reply->Values[1]);
    } else {
        for(i=0;i<13 && Targets[index].Objects[i];++i)
            ReadObject(target,Targets[index].Objects[i],&reply->Values[i]);
    }
    WdfIoTargetClose(target);WdfObjectDelete(target);
    ObDereferenceObject(base);ObDereferenceObject(file);
    WdfRequestCompleteWithInformation(request,STATUS_SUCCESS,sizeof(*reply));
}

VOID DiagUnload(WDFDRIVER driver)
{
    UNREFERENCED_PARAMETER(driver);
    if(Control){WdfObjectDelete(Control);Control=NULL;}
}

NTSTATUS DriverEntry(PDRIVER_OBJECT object,PUNICODE_STRING registry)
{
    WDF_DRIVER_CONFIG config;
    WDFDRIVER driver;
    PWDFDEVICE_INIT init=NULL;
    WDF_OBJECT_ATTRIBUTES attrs;
    WDF_IO_QUEUE_CONFIG queue;
    NTSTATUS status;
    DECLARE_CONST_UNICODE_STRING(name,L"\\Device\\PowerCapFanDiag");
    DECLARE_CONST_UNICODE_STRING(link,L"\\DosDevices\\PowerCapFanDiag");
    WDF_DRIVER_CONFIG_INIT(&config,WDF_NO_EVENT_CALLBACK);
    config.DriverInitFlags=WdfDriverInitNonPnpDriver;
    config.EvtDriverUnload=DiagUnload;
    config.DriverPoolTag='dnaF';
    status=WdfDriverCreate(object,registry,WDF_NO_OBJECT_ATTRIBUTES,&config,&driver);
    if(!NT_SUCCESS(status))return status;
    init=WdfControlDeviceInitAllocate(driver,&SDDL_DEVOBJ_SYS_ALL_ADM_ALL);
    if(!init)return STATUS_INSUFFICIENT_RESOURCES;
    WdfDeviceInitSetDeviceType(init,0x8338);
    WdfDeviceInitSetCharacteristics(init,FILE_DEVICE_SECURE_OPEN,FALSE);
    WdfDeviceInitSetIoType(init,WdfDeviceIoBuffered);
    WdfDeviceInitSetExclusive(init,TRUE);
    status=WdfDeviceInitAssignName(init,&name);
    if(!NT_SUCCESS(status)){WdfDeviceInitFree(init);return status;}
    WDF_OBJECT_ATTRIBUTES_INIT(&attrs);attrs.ExecutionLevel=WdfExecutionLevelPassive;
    status=WdfDeviceCreate(&init,&attrs,&Control);
    if(!NT_SUCCESS(status)){if(init)WdfDeviceInitFree(init);return status;}
    status=WdfDeviceCreateSymbolicLink(Control,&link);
    if(!NT_SUCCESS(status))goto fail;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queue,WdfIoQueueDispatchSequential);
    queue.PowerManaged=WdfFalse;queue.EvtIoDeviceControl=DiagIoctl;
    status=WdfIoQueueCreate(Control,&queue,WDF_NO_OBJECT_ATTRIBUTES,WDF_NO_HANDLE);
    if(!NT_SUCCESS(status))goto fail;
    WdfControlFinishInitializing(Control);
    return STATUS_SUCCESS;
fail:
    WdfObjectDelete(Control);Control=NULL;return status;
}
