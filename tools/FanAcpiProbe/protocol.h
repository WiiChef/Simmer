#pragma once

/* Include ntddk.h or Windows.h plus winioctl.h before this file. */
#define FAN_PROBE_VERSION 1u
#define FAN_PROBE_DEVICE_TYPE 0x8337u
#define IOCTL_FAN_PROBE_IDENTITY CTL_CODE(FAN_PROBE_DEVICE_TYPE, 0x800, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)
#define IOCTL_FAN_PROBE_TELEMETRY CTL_CODE(FAN_PROBE_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)
#define FAN_PROBE_MAX_RAW 128u
#define FAN_PROBE_VALUE_COUNT 13u

/* {52B7E680-8C59-4FE1-8E6C-305B5618E371} */
static const GUID FanProbeInterface =
    {0x52b7e680, 0x8c59, 0x4fe1, {0x8e,0x6c,0x30,0x5b,0x56,0x18,0xe3,0x71}};

enum FAN_PROBE_INDEX {
    FanHid, FanWho, FanCaps, FanVersion, FanCountConfig,
    Fan1StatusAck, Fan1SpeedAck, Fan1StatusCache, Fan1SpeedCache,
    Fan2StatusAck, Fan2SpeedAck, Fan2StatusCache, Fan2SpeedCache
};

typedef struct FAN_PROBE_VALUE {
    LONG Status;             /* Transport or validation NTSTATUS, not firmware ack. */
    ULONG IntegerValid;
    ULONGLONG Integer;
    ULONG RawLength;         /* Complete ACPI output, at most FAN_PROBE_MAX_RAW. */
    ULONG Reserved;
    UCHAR Raw[FAN_PROBE_MAX_RAW];
} FAN_PROBE_VALUE;

typedef struct FAN_PROBE_REPLY {
    ULONG Size;
    ULONG Version;
    ULONG Operation;
    ULONG Reserved;
    WCHAR InstanceId[64];
    WCHAR Location[64];
    FAN_PROBE_VALUE Values[FAN_PROBE_VALUE_COUNT];
} FAN_PROBE_REPLY;

C_ASSERT(sizeof(FAN_PROBE_VALUE) == 152);
C_ASSERT(FIELD_OFFSET(FAN_PROBE_REPLY, Values) == 272);
C_ASSERT(sizeof(FAN_PROBE_REPLY) == 2248);

