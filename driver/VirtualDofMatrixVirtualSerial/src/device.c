#include "device.h"
#include "queue.h"

static LIST_ENTRY g_Pairs;
static FAST_MUTEX g_PairLock;
static BOOLEAN g_Initialized = FALSE;

static UCHAR g_SharedQueue[VDM_BUFFER_SIZE];
static ULONG g_SharedWrite = 0;
static ULONG g_SharedRead = 0;

static NTSTATUS VdmAllocateUnicodeCopy(_In_ PCUNICODE_STRING Source, _Out_ PUNICODE_STRING Destination)
{
    Destination->Length = 0;
    Destination->MaximumLength = Source->Length + sizeof(WCHAR);
    Destination->Buffer = (PWCH)ExAllocatePool2(POOL_FLAG_NON_PAGED, Destination->MaximumLength, 'mvDV');
    if (Destination->Buffer == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    RtlZeroMemory(Destination->Buffer, Destination->MaximumLength);
    RtlCopyUnicodeString(Destination, Source);
    return STATUS_SUCCESS;
}

static VOID VdmEnsureStateInitialized()
{
    if (g_Initialized)
    {
        return;
    }

    InitializeListHead(&g_Pairs);
    ExInitializeFastMutex(&g_PairLock);
    g_SharedWrite = 0;
    g_SharedRead = 0;
    g_Initialized = TRUE;
}

NTSTATUS
VdmEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    NTSTATUS status;
    WDFDEVICE device;
    UNICODE_STRING deviceName;
    UNICODE_STRING symbolicLink;

    VdmEnsureStateInitialized();

    RtlInitUnicodeString(&deviceName, L"\\Device\\VirtualDofMatrixSerial");
    status = WdfDeviceInitAssignName(DeviceInit, &deviceName);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = WdfDeviceCreate(&DeviceInit, WDF_NO_OBJECT_ATTRIBUTES, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    RtlInitUnicodeString(&symbolicLink, L"\\DosDevices\\VirtualDofMatrixSerial");
    status = WdfDeviceCreateSymbolicLink(device, &symbolicLink);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = VdmQueueInitialize(device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL, "[VDM] Device add complete.\\n"));
    return STATUS_SUCCESS;
}

NTSTATUS
VdmCreateVirtualPair(
    _In_ WDFDEVICE Device,
    _In_ PCUNICODE_STRING PairName,
    _In_ PCUNICODE_STRING RxPort)
{
    UNREFERENCED_PARAMETER(Device);

    NTSTATUS status;
    PVDM_PAIR_STATE pair = (PVDM_PAIR_STATE)ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(VDM_PAIR_STATE), 'pvDV');
    if (pair == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    RtlZeroMemory(pair, sizeof(VDM_PAIR_STATE));

    status = VdmAllocateUnicodeCopy(PairName, &pair->TxPort);
    if (!NT_SUCCESS(status))
    {
        ExFreePool(pair);
        return status;
    }

    status = VdmAllocateUnicodeCopy(RxPort, &pair->RxPort);
    if (!NT_SUCCESS(status))
    {
        ExFreePool(pair->TxPort.Buffer);
        ExFreePool(pair);
        return status;
    }

    ExAcquireFastMutex(&g_PairLock);
    InsertTailList(&g_Pairs, &pair->Entry);
    ExReleaseFastMutex(&g_PairLock);

    KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL, "[VDM] Pair created %wZ <-> %wZ. (PDO registration TODO)\\n", PairName, RxPort));
    return STATUS_SUCCESS;
}

NTSTATUS
VdmDeleteVirtualPair(
    _In_ WDFDEVICE Device,
    _In_ PCUNICODE_STRING PairName,
    _In_ PCUNICODE_STRING RxPort)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(RxPort);

    NTSTATUS status = STATUS_NOT_FOUND;

    ExAcquireFastMutex(&g_PairLock);

    for (PLIST_ENTRY entry = g_Pairs.Flink; entry != &g_Pairs; entry = entry->Flink)
    {
        PVDM_PAIR_STATE pair = CONTAINING_RECORD(entry, VDM_PAIR_STATE, Entry);
        if (RtlEqualUnicodeString(&pair->TxPort, PairName, TRUE))
        {
            RemoveEntryList(&pair->Entry);
            ExFreePool(pair->TxPort.Buffer);
            ExFreePool(pair->RxPort.Buffer);
            ExFreePool(pair);
            status = STATUS_SUCCESS;
            break;
        }
    }

    ExReleaseFastMutex(&g_PairLock);

    KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL, "[VDM] Pair delete %wZ status=0x%08X.\\n", PairName, status));
    return status;
}

ULONG
VdmGetPairCount()
{
    ULONG count = 0;

    ExAcquireFastMutex(&g_PairLock);
    for (PLIST_ENTRY entry = g_Pairs.Flink; entry != &g_Pairs; entry = entry->Flink)
    {
        count++;
    }
    ExReleaseFastMutex(&g_PairLock);

    return count;
}

NTSTATUS
VdmWritePairPayload(_In_reads_bytes_(Length) const UCHAR* Data, _In_ size_t Length)
{
    if (Length == 0)
    {
        return STATUS_SUCCESS;
    }

    ExAcquireFastMutex(&g_PairLock);

    for (size_t i = 0; i < Length; i++)
    {
        g_SharedQueue[g_SharedWrite] = Data[i];
        g_SharedWrite = (g_SharedWrite + 1) % VDM_BUFFER_SIZE;
        if (g_SharedWrite == g_SharedRead)
        {
            g_SharedRead = (g_SharedRead + 1) % VDM_BUFFER_SIZE;
        }
    }

    ExReleaseFastMutex(&g_PairLock);

    KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_TRACE_LEVEL, "[VDM] queued %Iu bytes.\\n", Length));
    return STATUS_SUCCESS;
}

size_t
VdmReadPairPayload(_Out_writes_bytes_(Length) UCHAR* Data, _In_ size_t Length)
{
    size_t copied = 0;

    ExAcquireFastMutex(&g_PairLock);

    while (copied < Length && g_SharedRead != g_SharedWrite)
    {
        Data[copied++] = g_SharedQueue[g_SharedRead];
        g_SharedRead = (g_SharedRead + 1) % VDM_BUFFER_SIZE;
    }

    ExReleaseFastMutex(&g_PairLock);

    if (copied > 0)
    {
        KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_TRACE_LEVEL, "[VDM] dequeued %Iu bytes.\\n", copied));
    }

    return copied;
}
