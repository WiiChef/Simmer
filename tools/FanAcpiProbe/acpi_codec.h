#pragma once
#include <acpiioct.h>

/* Only small, fixed DSM calls with integer Arg3 are representable here. */
#define FAN_DSM_INPUT_BYTES (FIELD_OFFSET(ACPI_EVAL_INPUT_BUFFER_COMPLEX, Argument) + 20u + 3u * 8u)

static void FanBuildDsm(UCHAR *bytes, const GUID *uuid, ULONG function, ULONG arg3)
{
    PACPI_EVAL_INPUT_BUFFER_COMPLEX input = (PACPI_EVAL_INPUT_BUFFER_COMPLEX)bytes;
    PACPI_METHOD_ARGUMENT arg;
    RtlZeroMemory(bytes, FAN_DSM_INPUT_BYTES);
    input->Signature = ACPI_EVAL_INPUT_BUFFER_COMPLEX_SIGNATURE;
    RtlCopyMemory(input->MethodName, "_DSM", 4);
    input->Size = FAN_DSM_INPUT_BYTES;
    input->ArgumentCount = 4;
    arg = input->Argument;
    arg->Type = ACPI_METHOD_ARGUMENT_BUFFER;
    arg->DataLength = sizeof(GUID);
    RtlCopyMemory(arg->Data, uuid, sizeof(GUID));
    arg = ACPI_METHOD_NEXT_ARGUMENT(arg);
    ACPI_METHOD_SET_ARGUMENT_INTEGER(arg, 0);
    arg = ACPI_METHOD_NEXT_ARGUMENT(arg);
    ACPI_METHOD_SET_ARGUMENT_INTEGER(arg, function);
    arg = ACPI_METHOD_NEXT_ARGUMENT(arg);
    ACPI_METHOD_SET_ARGUMENT_INTEGER(arg, arg3);
}

/* Validate before inspecting flexible-array data; accepts integer32 or integer64. */
static int FanParseInteger(const UCHAR *bytes, SIZE_T bytesReturned, ULONGLONG *value)
{
    const ACPI_EVAL_OUTPUT_BUFFER *output;
    const ACPI_METHOD_ARGUMENT *arg;
    SIZE_T offset = FIELD_OFFSET(ACPI_EVAL_OUTPUT_BUFFER, Argument);
    SIZE_T dataOffset = offset + FIELD_OFFSET(ACPI_METHOD_ARGUMENT, Data);
    ULONG length;
    if (bytesReturned < dataOffset + sizeof(ULONG)) return 0;
    output = (const ACPI_EVAL_OUTPUT_BUFFER *)bytes;
    if (output->Signature != ACPI_EVAL_OUTPUT_BUFFER_SIGNATURE || output->Count != 1)
        return 0;
    length = output->Length;
    if (length > bytesReturned || length < dataOffset + sizeof(ULONG)) return 0;
    arg = &output->Argument[0];
    if (arg->Type != ACPI_METHOD_ARGUMENT_INTEGER ||
        (arg->DataLength != sizeof(ULONG) && arg->DataLength != sizeof(ULONGLONG)))
        return 0;
    if ((SIZE_T)arg->DataLength > (SIZE_T)length - dataOffset) return 0;
    *value = 0;
    RtlCopyMemory(value, arg->Data, arg->DataLength);
    return 1;
}

static int FanProbeIsInteger(const FAN_PROBE_VALUE *v)
{
    return v->Status >= 0 && v->IntegerValid != 0;
}

