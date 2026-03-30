#pragma once

#include <ntddk.h>
#include <wdf.h>

EVT_WDF_IO_QUEUE_IO_READ VdmEvtIoRead;
EVT_WDF_IO_QUEUE_IO_WRITE VdmEvtIoWrite;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL VdmEvtIoDeviceControl;

NTSTATUS VdmQueueInitialize(_In_ WDFDEVICE Device);
