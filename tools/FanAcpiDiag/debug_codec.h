#pragma once
#define FAN_DEBUG_INPUT_BYTES 80u
/* Fixed protocol operation, not an arbitrary ACPI evaluator. Callers only use
   fan1/fan2, RPM mode flags3 or automatic flags0, and 3000/0 RPM. */
static void BuildDebugDsm(UCHAR *bytes,ULONG fan,ULONG flags,ULONG rpm)
{
    static const GUID uuid={0x5792aa21,0x7727,0x443f,{0x8c,0xa2,0xe6,0xb3,0xbe,0x94,0xb6,0xb1}};
    PACPI_EVAL_INPUT_BUFFER_COMPLEX input=(PACPI_EVAL_INPUT_BUFFER_COMPLEX)bytes;
    PACPI_METHOD_ARGUMENT arg,child;
    RtlZeroMemory(bytes,FAN_DEBUG_INPUT_BYTES);
    input->Signature=ACPI_EVAL_INPUT_BUFFER_COMPLEX_SIGNATURE;
    RtlCopyMemory(input->MethodName,"_DSM",4);
    input->Size=FAN_DEBUG_INPUT_BYTES;input->ArgumentCount=4;
    arg=input->Argument;arg->Type=ACPI_METHOD_ARGUMENT_BUFFER;arg->DataLength=16;
    RtlCopyMemory(arg->Data,&uuid,16);
    arg=ACPI_METHOD_NEXT_ARGUMENT(arg);ACPI_METHOD_SET_ARGUMENT_INTEGER(arg,0);
    arg=ACPI_METHOD_NEXT_ARGUMENT(arg);ACPI_METHOD_SET_ARGUMENT_INTEGER(arg,5);
    arg=ACPI_METHOD_NEXT_ARGUMENT(arg);arg->Type=ACPI_METHOD_ARGUMENT_PACKAGE;arg->DataLength=24;
    child=(PACPI_METHOD_ARGUMENT)arg->Data;ACPI_METHOD_SET_ARGUMENT_INTEGER(child,fan);
    child=ACPI_METHOD_NEXT_ARGUMENT(child);ACPI_METHOD_SET_ARGUMENT_INTEGER(child,flags);
    child=ACPI_METHOD_NEXT_ARGUMENT(child);ACPI_METHOD_SET_ARGUMENT_INTEGER(child,rpm);
}
