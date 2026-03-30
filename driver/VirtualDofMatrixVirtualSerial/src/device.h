#pragma once

#include <ntddk.h>
#include <wdf.h>

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD VdmEvtDeviceAdd;

NTSTATUS VdmCreateVirtualPair(_In_ WDFDEVICE Device, _In_ PCUNICODE_STRING PairName);
