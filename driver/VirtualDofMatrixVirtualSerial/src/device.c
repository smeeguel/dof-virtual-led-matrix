#include "device.h"
#include "queue.h"

static volatile LONG g_PairCount = 0;

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

    return STATUS_SUCCESS;
}

NTSTATUS
VdmCreateVirtualPair(
    _In_ WDFDEVICE Device,
    _In_ PCUNICODE_STRING PairName)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(PairName);

    InterlockedIncrement(&g_PairCount);
    return STATUS_SUCCESS;
}

NTSTATUS
VdmDeleteVirtualPair(
    _In_ WDFDEVICE Device,
    _In_ PCUNICODE_STRING PairName)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(PairName);

    LONG count = InterlockedCompareExchange(&g_PairCount, 0, 0);
    if (count > 0)
    {
        InterlockedDecrement(&g_PairCount);
    }

    return STATUS_SUCCESS;
}

ULONG
VdmGetPairCount()
{
    return (ULONG)InterlockedCompareExchange(&g_PairCount, 0, 0);
}
