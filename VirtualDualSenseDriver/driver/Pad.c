#include "Driver.h"
#include "DualSenseDescriptor.h"

static
PVDS_PAD_CONTEXT
HBashtonVdsFindPadById(
    _In_ PDEVICE_CONTEXT DeviceContext,
    _In_ ULONG PadId
    )
{
    for (ULONG i = 0; i < HBASHTON_VDS_MAX_PADS; i++)
    {
        if (DeviceContext->Pads[i].Active &&
            !DeviceContext->Pads[i].Destroying &&
            DeviceContext->Pads[i].PadId == PadId)
        {
            return &DeviceContext->Pads[i];
        }
    }

    return NULL;
}

NTSTATUS
HBashtonVdsCreatePad(
    _In_ WDFDEVICE Device,
    _Out_ PULONG PadId
    )
{
    NTSTATUS status = STATUS_INSUFFICIENT_RESOURCES;
    PDEVICE_CONTEXT deviceContext = DeviceGetContext(Device);
    PVDS_PAD_CONTEXT pad = NULL;
    VHF_CONFIG vhfConfig;

    *PadId = 0;

    WdfWaitLockAcquire(deviceContext->PadLock, NULL);
    for (ULONG i = 0; i < HBASHTON_VDS_MAX_PADS; i++)
    {
        if (!deviceContext->Pads[i].Active)
        {
            pad = &deviceContext->Pads[i];
            RtlZeroMemory(pad, sizeof(*pad));
            pad->PadId = deviceContext->NextPadId++;
            pad->ParentContext = deviceContext;
            if (deviceContext->NextPadId == 0)
            {
                deviceContext->NextPadId = 1;
            }

            pad->Active = TRUE;
            break;
        }
    }
    WdfWaitLockRelease(deviceContext->PadLock);

    if (pad == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    VHF_CONFIG_INIT(
        &vhfConfig,
        WdfDeviceWdmGetDeviceObject(Device),
        sizeof(DualSenseUsbReportDescriptor),
        (PUCHAR)DualSenseUsbReportDescriptor);

    vhfConfig.VendorID = SONY_VENDOR_ID;
    vhfConfig.ProductID = DUALSENSE_PRODUCT_ID;
    vhfConfig.VersionNumber = 0x0100;
    vhfConfig.VhfClientContext = pad;
    vhfConfig.EvtVhfAsyncOperationWriteReport = HBashtonVdsEvtVhfWriteReport;

    status = VhfCreate(&vhfConfig, &pad->VhfHandle);
    if (NT_SUCCESS(status))
    {
        status = VhfStart(pad->VhfHandle);
    }

    if (!NT_SUCCESS(status))
    {
        if (pad->VhfHandle != NULL)
        {
            VhfDelete(pad->VhfHandle, TRUE);
        }

        WdfWaitLockAcquire(deviceContext->PadLock, NULL);
        RtlZeroMemory(pad, sizeof(*pad));
        WdfWaitLockRelease(deviceContext->PadLock);
        return status;
    }

    *PadId = pad->PadId;
    return STATUS_SUCCESS;
}

NTSTATUS
HBashtonVdsDestroyPad(
    _In_ WDFDEVICE Device,
    _In_ ULONG PadId
    )
{
    PDEVICE_CONTEXT deviceContext = DeviceGetContext(Device);
    VHFHANDLE vhfHandle = NULL;
    PVDS_PAD_CONTEXT pad;

    WdfWaitLockAcquire(deviceContext->PadLock, NULL);
    pad = HBashtonVdsFindPadById(deviceContext, PadId);
    if (pad != NULL)
    {
        vhfHandle = pad->VhfHandle;
        pad->Destroying = TRUE;
        pad->VhfHandle = NULL;
    }
    WdfWaitLockRelease(deviceContext->PadLock);

    if (vhfHandle == NULL)
    {
        return STATUS_NOT_FOUND;
    }

    VhfDelete(vhfHandle, TRUE);

    WdfWaitLockAcquire(deviceContext->PadLock, NULL);
    WdfSpinLockAcquire(deviceContext->OutputReportLock);
    if (pad != NULL)
    {
        RtlZeroMemory(pad, sizeof(*pad));
    }
    WdfSpinLockRelease(deviceContext->OutputReportLock);
    WdfWaitLockRelease(deviceContext->PadLock);

    return STATUS_SUCCESS;
}

NTSTATUS
HBashtonVdsSubmitInputReport(
    _In_ WDFDEVICE Device,
    _In_ ULONG PadId,
    _In_reads_bytes_(HBASHTON_DUALSENSE_USB_INPUT_REPORT_SIZE) PUCHAR Report
    )
{
    NTSTATUS status;
    PDEVICE_CONTEXT deviceContext = DeviceGetContext(Device);
    PVDS_PAD_CONTEXT pad;
    HID_XFER_PACKET transferPacket;

    if (Report[0] != HBASHTON_DUALSENSE_USB_INPUT_REPORT_ID)
    {
        return STATUS_INVALID_PARAMETER;
    }

    WdfWaitLockAcquire(deviceContext->PadLock, NULL);
    pad = HBashtonVdsFindPadById(deviceContext, PadId);
    if (pad == NULL || pad->VhfHandle == NULL)
    {
        WdfWaitLockRelease(deviceContext->PadLock);
        return STATUS_NOT_FOUND;
    }

    RtlZeroMemory(&transferPacket, sizeof(transferPacket));
    transferPacket.reportId = HBASHTON_DUALSENSE_USB_INPUT_REPORT_ID;
    transferPacket.reportBuffer = Report;
    transferPacket.reportBufferLen = HBASHTON_DUALSENSE_USB_INPUT_REPORT_SIZE;

    status = VhfReadReportSubmit(pad->VhfHandle, &transferPacket);
    WdfWaitLockRelease(deviceContext->PadLock);
    return status;
}

NTSTATUS
HBashtonVdsReadOutputReport(
    _In_ WDFDEVICE Device,
    _In_ ULONG PadId,
    _Out_ PHBASHTON_VDS_READ_OUTPUT_REPORT_OUT OutputReport
    )
{
    PDEVICE_CONTEXT deviceContext = DeviceGetContext(Device);
    PVDS_PAD_CONTEXT pad;
    NTSTATUS status = STATUS_SUCCESS;

    RtlZeroMemory(OutputReport, sizeof(*OutputReport));

    WdfWaitLockAcquire(deviceContext->PadLock, NULL);
    pad = HBashtonVdsFindPadById(deviceContext, PadId);
    if (pad != NULL)
    {
        WdfSpinLockAcquire(deviceContext->OutputReportLock);
    }

    if (pad == NULL)
    {
        status = STATUS_NOT_FOUND;
    }
    else
    {
        OutputReport->PadId = pad->PadId;
        OutputReport->Sequence = pad->OutputSequence;
        OutputReport->ReportLength = pad->LastOutputReportLength;
        if (pad->LastOutputReportLength != 0)
        {
            RtlCopyMemory(OutputReport->Report, pad->LastOutputReport, pad->LastOutputReportLength);
        }

        WdfSpinLockRelease(deviceContext->OutputReportLock);
    }
    WdfWaitLockRelease(deviceContext->PadLock);

    return status;
}

VOID
HBashtonVdsEvtVhfWriteReport(
    _In_ PVOID VhfClientContext,
    _In_ VHFOPERATIONHANDLE VhfOperationHandle,
    _In_opt_ PVOID VhfOperationContext,
    _In_ PHID_XFER_PACKET HidTransferPacket
    )
{
    PVDS_PAD_CONTEXT pad = (PVDS_PAD_CONTEXT)VhfClientContext;
    ULONG length;
    ULONG normalizedPayloadLength;

    UNREFERENCED_PARAMETER(VhfOperationContext);

    if (pad == NULL || pad->ParentContext == NULL || HidTransferPacket == NULL ||
        HidTransferPacket->reportBuffer == NULL)
    {
        VhfAsyncOperationComplete(VhfOperationHandle, STATUS_INVALID_PARAMETER);
        return;
    }

    length = HidTransferPacket->reportBufferLen;
    if (HidTransferPacket->reportId != 0 &&
        (length == 0 || HidTransferPacket->reportBuffer[0] != HidTransferPacket->reportId))
    {
        normalizedPayloadLength = length;
        if (normalizedPayloadLength > HBASHTON_DUALSENSE_MAX_OUTPUT_REPORT_SIZE - 1)
        {
            normalizedPayloadLength = HBASHTON_DUALSENSE_MAX_OUTPUT_REPORT_SIZE - 1;
        }

        WdfSpinLockAcquire(pad->ParentContext->OutputReportLock);
        pad->LastOutputReport[0] = HidTransferPacket->reportId;
        if (normalizedPayloadLength != 0)
        {
            RtlCopyMemory(&pad->LastOutputReport[1], HidTransferPacket->reportBuffer, normalizedPayloadLength);
        }

        pad->LastOutputReportLength = normalizedPayloadLength + 1;
        pad->OutputSequence++;
        WdfSpinLockRelease(pad->ParentContext->OutputReportLock);

        VhfAsyncOperationComplete(VhfOperationHandle, STATUS_SUCCESS);
        return;
    }

    if (length > HBASHTON_DUALSENSE_MAX_OUTPUT_REPORT_SIZE)
    {
        length = HBASHTON_DUALSENSE_MAX_OUTPUT_REPORT_SIZE;
    }

    WdfSpinLockAcquire(pad->ParentContext->OutputReportLock);
    pad->LastOutputReportLength = length;
    pad->OutputSequence++;
    if (length != 0)
    {
        RtlCopyMemory(pad->LastOutputReport, HidTransferPacket->reportBuffer, length);
    }
    WdfSpinLockRelease(pad->ParentContext->OutputReportLock);

    VhfAsyncOperationComplete(VhfOperationHandle, STATUS_SUCCESS);
}
