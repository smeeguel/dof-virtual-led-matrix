#include "queue.h"

NTSTATUS
VdmQueueInitialize(_In_ WDFDEVICE Device)
{
    WDF_IO_QUEUE_CONFIG queueConfig;
    NTSTATUS status;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoRead = VdmEvtIoRead;
    queueConfig.EvtIoWrite = VdmEvtIoWrite;

    status = WdfIoQueueCreate(Device,
                              &queueConfig,
                              WDF_NO_OBJECT_ATTRIBUTES,
                              WDF_NO_HANDLE);

    return status;
}

VOID
VdmEvtIoRead(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(Length);

    // TODO: complete from linked endpoint write buffer.
    WdfRequestCompleteWithInformation(Request, STATUS_NOT_IMPLEMENTED, 0);
}

VOID
VdmEvtIoWrite(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(Length);

    // TODO: route bytes to opposite endpoint buffer and signal pending readers.
    WdfRequestCompleteWithInformation(Request, STATUS_NOT_IMPLEMENTED, 0);
}
