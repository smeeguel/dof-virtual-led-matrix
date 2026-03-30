#include "device.h"
#include "queue.h"

NTSTATUS
VdmEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    NTSTATUS status;
    WDFDEVICE device;

    status = WdfDeviceCreate(&DeviceInit, WDF_NO_OBJECT_ATTRIBUTES, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = VdmQueueInitialize(device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    // TODO: Implement virtual bus child PDO creation so each configured
    // pair appears as two linked COM endpoints under Ports (COM & LPT).
    return STATUS_SUCCESS;
}

NTSTATUS
VdmCreateVirtualPair(
    _In_ WDFDEVICE Device,
    _In_ PCUNICODE_STRING PairName)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(PairName);

    // TODO: Add pair provisioning implementation:
    // - create endpoint A child PDO
    // - create endpoint B child PDO
    // - register Ports class interfaces for COM assignment
    // - create bidirectional in-memory ring link between endpoints
    return STATUS_NOT_IMPLEMENTED;
}
