#include <initguid.h>
#include "Driver.h"

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, HBashtonVdsEvtDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS
HBashtonVdsEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    UNREFERENCED_PARAMETER(Driver);

    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    PDEVICE_CONTEXT deviceContext;
    DECLARE_CONST_UNICODE_STRING(symbolicLink, HBASHTON_VDS_SYMBOLIC_LINK);

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);
    WdfDeviceInitSetExclusive(DeviceInit, FALSE);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, DEVICE_CONTEXT);
    deviceAttributes.EvtCleanupCallback = HBashtonVdsEvtDeviceContextCleanup;
    deviceAttributes.ExecutionLevel = WdfExecutionLevelPassive;

    status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    deviceContext = DeviceGetContext(device);
    deviceContext->NextPadId = 1;

    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &deviceContext->PadLock);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = WdfSpinLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &deviceContext->OutputReportLock);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = WdfDeviceCreateSymbolicLink(device, &symbolicLink);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = WdfDeviceCreateDeviceInterface(
        device,
        &GUID_DEVINTERFACE_HBASHTON_VIRTUAL_DUALSENSE,
        NULL);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = HBashtonVdsEvtIoDeviceControl;

    status = WdfIoQueueCreate(
        device,
        &queueConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        &deviceContext->IoQueue);

    return status;
}

VOID
HBashtonVdsEvtDeviceContextCleanup(
    _In_ WDFOBJECT DeviceObject
    )
{
    WDFDEVICE device = (WDFDEVICE)DeviceObject;
    PDEVICE_CONTEXT deviceContext = DeviceGetContext(device);

    for (ULONG i = 0; i < HBASHTON_VDS_MAX_PADS; i++)
    {
        if (deviceContext->Pads[i].Active && deviceContext->Pads[i].VhfHandle != NULL)
        {
            VhfDelete(deviceContext->Pads[i].VhfHandle, TRUE);
            deviceContext->Pads[i].VhfHandle = NULL;
            deviceContext->Pads[i].Active = FALSE;
        }
    }
}

VOID
HBashtonVdsEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    size_t bytesReturned = 0;
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);

    UNREFERENCED_PARAMETER(OutputBufferLength);

    switch (IoControlCode)
    {
        case IOCTL_HBASHTON_VDS_CREATE_PAD:
        {
            ULONG busMode = HBASHTON_VDS_BUS_MODE_USB;
            PHBASHTON_VDS_CREATE_PAD_IN input;
            PHBASHTON_VDS_CREATE_PAD_OUT output;

            if (InputBufferLength != 0)
            {
                status = WdfRequestRetrieveInputBuffer(
                    Request,
                    sizeof(HBASHTON_VDS_CREATE_PAD_IN),
                    (PVOID*)&input,
                    NULL);
                if (!NT_SUCCESS(status))
                {
                    break;
                }

                if (input->Size < sizeof(HBASHTON_VDS_CREATE_PAD_IN) ||
                    input->Version != HBASHTON_VDS_CREATE_PAD_VERSION)
                {
                    status = STATUS_REVISION_MISMATCH;
                    break;
                }

                busMode = input->BusMode;
            }

            status = WdfRequestRetrieveOutputBuffer(
                Request,
                sizeof(HBASHTON_VDS_CREATE_PAD_OUT),
                (PVOID*)&output,
                NULL);
            if (NT_SUCCESS(status))
            {
                status = HBashtonVdsCreatePad(device, busMode, &output->PadId);
                if (NT_SUCCESS(status))
                {
                    bytesReturned = sizeof(HBASHTON_VDS_CREATE_PAD_OUT);
                }
            }

            break;
        }

        case IOCTL_HBASHTON_VDS_DESTROY_PAD:
        {
            PHBASHTON_VDS_PAD_REQUEST input;

            status = WdfRequestRetrieveInputBuffer(
                Request,
                sizeof(HBASHTON_VDS_PAD_REQUEST),
                (PVOID*)&input,
                NULL);
            if (NT_SUCCESS(status))
            {
                status = HBashtonVdsDestroyPad(device, input->PadId);
            }

            break;
        }

        case IOCTL_HBASHTON_VDS_SUBMIT_INPUT_REPORT:
        {
            PHBASHTON_VDS_SUBMIT_INPUT_REPORT_IN input;
            ULONG reportLength;

            status = WdfRequestRetrieveInputBuffer(
                Request,
                HBASHTON_VDS_SUBMIT_INPUT_REPORT_MIN_SIZE,
                (PVOID*)&input,
                NULL);
            if (NT_SUCCESS(status))
            {
                if (InputBufferLength <= FIELD_OFFSET(HBASHTON_VDS_SUBMIT_INPUT_REPORT_IN, Report))
                {
                    status = STATUS_BUFFER_TOO_SMALL;
                    break;
                }

                reportLength = (ULONG)(InputBufferLength -
                    FIELD_OFFSET(HBASHTON_VDS_SUBMIT_INPUT_REPORT_IN, Report));
                status = HBashtonVdsSubmitInputReport(
                    device,
                    input->PadId,
                    input->Report,
                    reportLength);
            }

            break;
        }

        case IOCTL_HBASHTON_VDS_READ_OUTPUT_REPORT:
        {
            PHBASHTON_VDS_PAD_REQUEST input;
            PHBASHTON_VDS_READ_OUTPUT_REPORT_OUT output;

            status = WdfRequestRetrieveInputBuffer(
                Request,
                sizeof(HBASHTON_VDS_PAD_REQUEST),
                (PVOID*)&input,
                NULL);
            if (!NT_SUCCESS(status))
            {
                break;
            }

            status = WdfRequestRetrieveOutputBuffer(
                Request,
                sizeof(HBASHTON_VDS_READ_OUTPUT_REPORT_OUT),
                (PVOID*)&output,
                NULL);
            if (NT_SUCCESS(status))
            {
                status = HBashtonVdsReadOutputReport(device, input->PadId, output);
                if (NT_SUCCESS(status))
                {
                    bytesReturned = sizeof(HBASHTON_VDS_READ_OUTPUT_REPORT_OUT);
                }
            }

            break;
        }

        default:
            status = STATUS_INVALID_DEVICE_REQUEST;
            break;
    }

    WdfRequestCompleteWithInformation(Request, status, bytesReturned);
}
