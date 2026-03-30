#pragma once

#include <ntddk.h>
#include <wdf.h>

#define VDM_BUFFER_SIZE 8192

typedef struct _VDM_PAIR_STATE
{
    LIST_ENTRY Entry;
    UNICODE_STRING TxPort;
    UNICODE_STRING RxPort;

    UCHAR BufferAtoB[VDM_BUFFER_SIZE];
    ULONG BufferAtoBWrite;
    ULONG BufferAtoBRead;

    UCHAR BufferBtoA[VDM_BUFFER_SIZE];
    ULONG BufferBtoAWrite;
    ULONG BufferBtoARead;
} VDM_PAIR_STATE, *PVDM_PAIR_STATE;

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD VdmEvtDeviceAdd;

NTSTATUS VdmCreateVirtualPair(_In_ WDFDEVICE Device, _In_ PCUNICODE_STRING PairName, _In_ PCUNICODE_STRING RxPort);
NTSTATUS VdmDeleteVirtualPair(_In_ WDFDEVICE Device, _In_ PCUNICODE_STRING PairName, _In_ PCUNICODE_STRING RxPort);
ULONG VdmGetPairCount();

NTSTATUS VdmWritePairPayload(_In_reads_bytes_(Length) const UCHAR* Data, _In_ size_t Length);
size_t VdmReadPairPayload(_Out_writes_bytes_(Length) UCHAR* Data, _In_ size_t Length);
