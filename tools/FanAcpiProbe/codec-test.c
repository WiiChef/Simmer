#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>
#include <stdio.h>
#include "protocol.h"
#include "acpi_codec.h"

#define CHECK(x) do { if (!(x)) { printf("FAIL line %d: %s\n", __LINE__, #x); return 1; } } while (0)

int main(void)
{
    DECLSPEC_ALIGN(8) UCHAR input[FAN_DSM_INPUT_BYTES];
    DECLSPEC_ALIGN(8) UCHAR output[128];
    ACPI_EVAL_INPUT_BUFFER_COMPLEX *in = (ACPI_EVAL_INPUT_BUFFER_COMPLEX *)input;
    ACPI_EVAL_OUTPUT_BUFFER *out = (ACPI_EVAL_OUTPUT_BUFFER *)output;
    PACPI_METHOD_ARGUMENT arg;
    ULONGLONG value;
    GUID uuid = {0x196d5cf2,0x31c5,0x43c4,{0x9a,0xcf,0x23,0x8c,0x22,0xd7,0x0f,0x86}};
    ULONG i;
    FanBuildDsm(input, &uuid, 2, 0x44);
    C_ASSERT(FAN_DSM_INPUT_BYTES == 60);
    CHECK(in->Size == sizeof(input) && in->ArgumentCount == 4);
    CHECK(memcmp(in->MethodName, "_DSM", 4) == 0);
    arg = in->Argument;
    CHECK(arg->Type == ACPI_METHOD_ARGUMENT_BUFFER && arg->DataLength == 16);
    CHECK(memcmp(arg->Data, &uuid, 16) == 0);
    arg = ACPI_METHOD_NEXT_ARGUMENT(arg);
    CHECK(arg->Type == ACPI_METHOD_ARGUMENT_INTEGER && arg->Argument == 0);
    arg = ACPI_METHOD_NEXT_ARGUMENT(arg);
    CHECK(arg->Type == ACPI_METHOD_ARGUMENT_INTEGER && arg->Argument == 2);
    arg = ACPI_METHOD_NEXT_ARGUMENT(arg);
    CHECK(arg->Type == ACPI_METHOD_ARGUMENT_INTEGER && arg->Argument == 0x44);
    CHECK((UCHAR *)ACPI_METHOD_NEXT_ARGUMENT(arg) == input + sizeof(input));
    ZeroMemory(output, sizeof(output));
    out->Signature = ACPI_EVAL_OUTPUT_BUFFER_SIGNATURE;
    out->Count = 1;
    out->Length = 20;
    out->Argument[0].Type = ACPI_METHOD_ARGUMENT_INTEGER;
    out->Argument[0].DataLength = 4;
    out->Argument[0].Argument = 0x8000;
    CHECK(FanParseInteger(output, 20, &value) && value == 0x8000);
    for (i = 0; i < 20; ++i) CHECK(!FanParseInteger(output, i, &value));
    out->Length = 24;
    out->Argument[0].DataLength = 8;
    value = 0x123456789abcdef0ULL;
    CopyMemory(out->Argument[0].Data, &value, 8);
    CHECK(FanParseInteger(output, 24, &value) && value == 0x123456789abcdef0ULL);
    CHECK(!FanParseInteger(output, 23, &value));
    out->Argument[0].Type = ACPI_METHOD_ARGUMENT_BUFFER;
    CHECK(!FanParseInteger(output, 24, &value));
    out->Argument[0].Type = ACPI_METHOD_ARGUMENT_INTEGER;
    out->Count = 2;
    CHECK(!FanParseInteger(output, 24, &value));
    out->Count = 1;
    out->Signature = 0;
    CHECK(!FanParseInteger(output, 24, &value));
    out->Signature = ACPI_EVAL_OUTPUT_BUFFER_SIGNATURE;
    for (i = 0; i < 65536; ++i) {
        out->Argument[0].DataLength = (USHORT)i;
        CHECK(FanParseInteger(output, 24, &value) == (i == 4 || i == 8));
    }
    puts("PASS: DSM wire layout, 32/64-bit values, truncation, malformed headers/types, all 65536 argument lengths.");
    return 0;
}

