#include <windows.h>
#include <winioctl.h>
#include <stdio.h>
#include "../FanAcpiProbe/protocol.h"
#include "../FanAcpiProbe/acpi_codec.h"
#include "debug_codec.h"
#define CHECK(x) do{if(!(x)){printf("FAIL line %d\n",__LINE__);return 1;}}while(0)
int main(void)
{
    DECLSPEC_ALIGN(8) UCHAR bytes[FAN_DEBUG_INPUT_BYTES];
    PACPI_EVAL_INPUT_BUFFER_COMPLEX input=(PACPI_EVAL_INPUT_BUFFER_COMPLEX)bytes;
    PACPI_METHOD_ARGUMENT arg,child;
    BuildDebugDsm(bytes,2,3,3000);
    CHECK(input->Size==80 && input->ArgumentCount==4);
    arg=input->Argument;CHECK(arg->DataLength==16 && arg->Type==ACPI_METHOD_ARGUMENT_BUFFER);
    arg=ACPI_METHOD_NEXT_ARGUMENT(arg);CHECK(arg->Argument==0);
    arg=ACPI_METHOD_NEXT_ARGUMENT(arg);CHECK(arg->Argument==5);
    arg=ACPI_METHOD_NEXT_ARGUMENT(arg);CHECK(arg->Type==ACPI_METHOD_ARGUMENT_PACKAGE && arg->DataLength==24);
    child=(PACPI_METHOD_ARGUMENT)arg->Data;CHECK(child->Argument==2);
    child=ACPI_METHOD_NEXT_ARGUMENT(child);CHECK(child->Argument==3);
    child=ACPI_METHOD_NEXT_ARGUMENT(child);CHECK(child->Argument==3000);
    CHECK((UCHAR *)ACPI_METHOD_NEXT_ARGUMENT(child)==bytes+sizeof(bytes));
    BuildDebugDsm(bytes,2,0,0);
    arg=input->Argument;arg=ACPI_METHOD_NEXT_ARGUMENT(arg);arg=ACPI_METHOD_NEXT_ARGUMENT(arg);arg=ACPI_METHOD_NEXT_ARGUMENT(arg);
    child=(PACPI_METHOD_ARGUMENT)arg->Data;child=ACPI_METHOD_NEXT_ARGUMENT(child);CHECK(child->Argument==0);
    child=ACPI_METHOD_NEXT_ARGUMENT(child);CHECK(child->Argument==0);
    puts("PASS: 80-byte DSM package, fan selector, RPM flags/value and automatic restore encoding.");return 0;
}
