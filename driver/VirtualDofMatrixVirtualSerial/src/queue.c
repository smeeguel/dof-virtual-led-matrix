#include "queue.h"
#include "device.h"
#include "ioctl_contract.h"

NTSTATUS
VdmQueueInitialize(_In_ WDFDEVICE Device)
{
    WDF_IO_QUEUE_CONFIG queueConfig;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoRead = VdmEvtIoRead;
    queueConfig.EvtIoWrite = VdmEvtIoWrite;
    queueConfig.EvtIoDeviceControl = VdmEvtIoDeviceControl;

    return WdfIoQueueCreate(Device,
                            &queueConfig,
                            WDF_NO_OBJECT_ATTRIBUTES,
                            WDF_NO_HANDLE);
}

VOID
VdmEvtIoRead(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length)
{
    UNREFERENCED_PARAMETER(Queue);

    PUCHAR outBuffer = NULL;
    size_t outLength = 0;
    NTSTATUS status = WdfRequestRetrieveOutputBuffer(Request, Length, (PVOID*)&outBuffer, &outLength);
    if (!NT_SUCCESS(status))
    {
        WdfRequestCompleteWithInformation(Request, status, 0);
        return;
    }

    size_t copied = VdmReadPairPayload(outBuffer, outLength);
    WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, copied);
}

VOID
VdmEvtIoWrite(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length)
{
    UNREFERENCED_PARAMETER(Queue);

    PUCHAR inBuffer = NULL;
    size_t inLength = 0;
    NTSTATUS status = WdfRequestRetrieveInputBuffer(Request, Length, (PVOID*)&inBuffer, &inLength);
    if (!NT_SUCCESS(status))
    {
        WdfRequestCompleteWithInformation(Request, status, 0);
        return;
    }

    status = VdmWritePairPayload(inBuffer, inLength);
    WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? inLength : 0);
}

VOID
VdmEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(InputBufferLength);

    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    size_t bytesReturned = 0;
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);

    if (IoControlCode == IOCTL_VDM_GET_HEALTH)
    {
        PVDM_OPERATION_RESPONSE response = NULL;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(VDM_OPERATION_RESPONSE), (PVOID*)&response, NULL);
        if (NT_SUCCESS(status))
        {
            response->Version = VDM_IOCTL_VERSION;
            response->Status = STATUS_SUCCESS;
            response->PairCount = VdmGetPairCount();
            bytesReturned = sizeof(VDM_OPERATION_RESPONSE);
            KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL, "[VDM] IOCTL health -> pairCount=%lu\\n", response->PairCount));
        }
    }
    else if (IoControlCode == IOCTL_VDM_CREATE_PAIR || IoControlCode == IOCTL_VDM_DELETE_PAIR)
    {
        PVDM_PAIR_REQUEST request = NULL;
        PVDM_OPERATION_RESPONSE response = NULL;
        UNICODE_STRING tx;
        UNICODE_STRING rx;

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(VDM_PAIR_REQUEST), (PVOID*)&request, NULL);
        if (NT_SUCCESS(status))
        {
            RtlInitUnicodeString(&tx, request->TxPort);
            RtlInitUnicodeString(&rx, request->RxPort);

            if (IoControlCode == IOCTL_VDM_CREATE_PAIR)
            {
                status = VdmCreateVirtualPair(device, &tx, &rx);
            }
            else
            {
                status = VdmDeleteVirtualPair(device, &tx, &rx);
            }
        }

        if (OutputBufferLength >= sizeof(VDM_OPERATION_RESPONSE))
        {
            NTSTATUS outStatus = WdfRequestRetrieveOutputBuffer(Request, sizeof(VDM_OPERATION_RESPONSE), (PVOID*)&response, NULL);
            if (NT_SUCCESS(outStatus))
            {
                response->Version = VDM_IOCTL_VERSION;
                response->Status = status;
                response->PairCount = VdmGetPairCount();
                bytesReturned = sizeof(VDM_OPERATION_RESPONSE);
            }
        }

        KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL, "[VDM] IOCTL pair op=0x%08lX status=0x%08X pairCount=%lu\\n", IoControlCode, status, VdmGetPairCount()));
    }

    WdfRequestCompleteWithInformation(Request, status, bytesReturned);
}
