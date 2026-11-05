#pragma once
/* Simmer battery drain on AC: ACPI _BMC bit 2 (discharge with AC present), held by the kernel only while a
   controller keeps its handle open and sends a heartbeat. See drain.c. */
#define DRAIN_DEVICE_TYPE 0x833Bu
#define IOCTL_DRAIN_START  CTL_CODE(DRAIN_DEVICE_TYPE,0x800,METHOD_BUFFERED,FILE_READ_DATA|FILE_WRITE_DATA)
#define IOCTL_DRAIN_BEAT   CTL_CODE(DRAIN_DEVICE_TYPE,0x801,METHOD_BUFFERED,FILE_READ_DATA|FILE_WRITE_DATA)
#define IOCTL_DRAIN_STOP   CTL_CODE(DRAIN_DEVICE_TYPE,0x802,METHOD_BUFFERED,FILE_READ_DATA|FILE_WRITE_DATA)
#define IOCTL_DRAIN_STATUS CTL_CODE(DRAIN_DEVICE_TYPE,0x803,METHOD_BUFFERED,FILE_READ_DATA|FILE_WRITE_DATA)

#define DRAIN_MAX_MINUTES 240u   /* hard cap, whatever the caller asks for */
#define DRAIN_BEAT_SECONDS 30u   /* no heartbeat for this long: the kernel ends the drain */

/* Why the last drain ended; | 0x80 = restoration not yet verified (the kernel keeps retrying). */
enum { STOP_NONE=0, STOP_REQUEST=1, STOP_CLOSED=2, STOP_HEARTBEAT=3, STOP_DEADLINE=4, STOP_UNLOAD=5,
       STOP_SHUTDOWN=6, STOP_START_FAILED=7 };

/* The battery (ACPI\PNP0C0A) and PMIC (ACPI\QCOM0F8E) PDO names, "\Device\<hex>", which can change between boots.
   The kernel accepts them only if the devices' instance IDs and location paths are the expected ones. */
#define DRAIN_PDO_CHARS 32
typedef struct DRAIN_START { ULONG MaxMinutes; WCHAR BatteryPdo[DRAIN_PDO_CHARS]; WCHAR PmicPdo[DRAIN_PDO_CHARS]; } DRAIN_START;
C_ASSERT(sizeof(DRAIN_START)==132);
typedef struct DRAIN_STATUS {
    ULONG Size; ULONG Version;
    ULONG Active;          /* the kernel holds discharge mode (or still has to restore it) */
    ULONG Control;         /* last fresh maintenance status (BMD0), MAXULONG if unreadable */
    ULONG SavedControl;    /* control value to restore (charger-inhibit bit as found) */
    ULONG StopReason;
    ULONG ElapsedSeconds;
    LONG Result;           /* NTSTATUS of this request's operation */
} DRAIN_STATUS;
C_ASSERT(sizeof(DRAIN_STATUS)==32);
