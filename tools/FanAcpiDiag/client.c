#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>
#include <stdio.h>
#include <stdlib.h>
#include "../FanAcpiProbe/protocol.h"
#define IOCTL_FAN_DIAG CTL_CODE(0x8338u,0x800u,METHOD_BUFFERED,FILE_READ_DATA|FILE_WRITE_DATA)
static const char *Names[6][13]={
    {"_HID","QWAI","QCAP","EVER","AVER","FNTR","EMPF",NULL},
    {"_HID","AVBL","ECRD","PRCT","FC43","FC44","UMPC","SL5B",NULL},
    {"_HID","F1ST","F1SP","F2ST","F2SP","FNTR","EMPF",NULL},
    {"displayOffAck","displayOnAck",NULL},
    {"fan1Debug3000Ack","fan1AutomaticRestoreAck",NULL},
    {"fan2Debug3000Ack","fan2AutomaticRestoreAck",NULL}};
int main(int argc,char **argv)
{
    HANDLE file;ULONG operation,i,j;DWORD actual=0;FAN_PROBE_REPLY reply;
    if(argc!=2 || strlen(argv[1])!=1 || argv[1][0]<'0' || argv[1][0]>'5'){
        fprintf(stderr,"Usage: FanAcpiDiag.exe 0..5 (ECGE|IC10|PAID snapshot|display pulse|fan1 3000RPM pulse|fan2 pulse)\n");return 1;
    }
    operation=(ULONG)(argv[1][0]-'0');
    file=CreateFileW(L"\\\\.\\PowerCapFanDiag",GENERIC_READ|GENERIC_WRITE,0,NULL,OPEN_EXISTING,0,NULL);
    if(file==INVALID_HANDLE_VALUE){fprintf(stderr,"Open failed: %lu\n",GetLastError());return 2;}
    ZeroMemory(&reply,sizeof(reply));
    if(!DeviceIoControl(file,IOCTL_FAN_DIAG,&operation,sizeof(operation),&reply,sizeof(reply),&actual,NULL)){
        DWORD error=GetLastError();CloseHandle(file);fprintf(stderr,"IOCTL failed: %lu\n",error);return 3;
    }
    CloseHandle(file);
    if(actual!=sizeof(reply) || reply.Size!=sizeof(reply) || reply.Operation!=operation || reply.Version!=1)return 4;
    printf("{\n  \"target\":%lu,\n  \"values\":{\n",operation);
    for(i=0;i<13 && Names[operation][i];++i){
        FAN_PROBE_VALUE *v=&reply.Values[i];
        printf("    \"%s\":{\"ntstatus\":\"0x%08lX\",\"integer\":",Names[operation][i],(ULONG)v->Status);
        if(v->Status>=0 && v->IntegerValid)printf("%llu",v->Integer);else printf("null");
        printf(",\"rawAcpi\":\"");
        for(j=0;j<v->RawLength && j<FAN_PROBE_MAX_RAW;++j)printf("%02X",v->Raw[j]);
        printf("\"}%s\n",i+1<13 && Names[operation][i+1] ? "," : "");
    }
    puts("  }\n}");return 0;
}
