#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <vhf.h>
#include <hidport.h>

#include "..\include\HBashtonVirtualDualSenseIoctl.h"
#include "..\include\DualSenseHidReport.h"

#define HBASHTON_VDS_POOL_TAG 'sDbV'
#define HBASHTON_VDS_MAX_PADS 8

typedef struct _VDS_PAD_CONTEXT
{
    ULONG PadId;
    BOOLEAN Active;
    VHFHANDLE VhfHandle;
    struct _DEVICE_CONTEXT* ParentContext;
    ULONG OutputSequence;
    ULONG LastOutputReportLength;
    UCHAR LastOutputReport[HBASHTON_DUALSENSE_MAX_OUTPUT_REPORT_SIZE];
} VDS_PAD_CONTEXT, *PVDS_PAD_CONTEXT;

typedef struct _DEVICE_CONTEXT
{
    WDFQUEUE IoQueue;
    WDFWAITLOCK PadLock;
    WDFSPINLOCK OutputReportLock;
    ULONG NextPadId;
    VDS_PAD_CONTEXT Pads[HBASHTON_VDS_MAX_PADS];
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, DeviceGetContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD HBashtonVdsEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP HBashtonVdsEvtDeviceContextCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL HBashtonVdsEvtIoDeviceControl;
EVT_VHF_ASYNC_OPERATION HBashtonVdsEvtVhfWriteReport;

NTSTATUS HBashtonVdsCreatePad(_In_ WDFDEVICE Device, _Out_ PULONG PadId);
NTSTATUS HBashtonVdsDestroyPad(_In_ WDFDEVICE Device, _In_ ULONG PadId);
NTSTATUS HBashtonVdsSubmitInputReport(
    _In_ WDFDEVICE Device,
    _In_ ULONG PadId,
    _In_reads_bytes_(HBASHTON_DUALSENSE_USB_INPUT_REPORT_SIZE) PUCHAR Report);
NTSTATUS HBashtonVdsReadOutputReport(
    _In_ WDFDEVICE Device,
    _In_ ULONG PadId,
    _Out_ PHBASHTON_VDS_READ_OUTPUT_REPORT_OUT OutputReport);
