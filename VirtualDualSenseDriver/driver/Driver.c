#include <initguid.h>
#include "Driver.h"

static NTSTATUS HBashtonVdsCreateControlDevice(_In_ WDFDRIVER Driver, _In_ WDFDEVICE ParentDevice);
static VOID HBashtonVdsProcessIoDeviceControl(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode);

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
    DECLARE_CONST_UNICODE_STRING(deviceName, HBASHTON_VDS_DEVICE_NAME);
    DECLARE_CONST_UNICODE_STRING(symbolicLink, HBASHTON_VDS_SYMBOLIC_LINK);

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);
    WdfDeviceInitSetExclusive(DeviceInit, FALSE);

    status = WdfDeviceInitAssignName(DeviceInit, &deviceName);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

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

    status = HBashtonVdsCreateControlDevice(Driver, device);
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
    queueConfig.PowerManaged = WdfFalse;
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

    if (deviceContext->ControlDevice != NULL)
    {
        WdfObjectDelete(deviceContext->ControlDevice);
        deviceContext->ControlDevice = NULL;
    }

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
HBashtonVdsEvtControlDeviceContextCleanup(
    _In_ WDFOBJECT DeviceObject
    )
{
    PCONTROL_DEVICE_CONTEXT controlContext = ControlDeviceGetContext((WDFDEVICE)DeviceObject);
    controlContext->ParentDevice = NULL;
}

static
NTSTATUS
HBashtonVdsCreateControlDevice(
    _In_ WDFDRIVER Driver,
    _In_ WDFDEVICE ParentDevice
    )
{
    NTSTATUS status;
    PWDFDEVICE_INIT controlInit;
    WDFDEVICE controlDevice;
    WDF_OBJECT_ATTRIBUTES controlAttributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    PCONTROL_DEVICE_CONTEXT controlContext;
    PDEVICE_CONTEXT parentContext = DeviceGetContext(ParentDevice);
    DECLARE_CONST_UNICODE_STRING(deviceName, HBASHTON_VDS_CONTROL_DEVICE_NAME);
    DECLARE_CONST_UNICODE_STRING(symbolicLink, HBASHTON_VDS_CONTROL_SYMBOLIC_LINK);
    DECLARE_CONST_UNICODE_STRING(sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;BU)");

    controlInit = WdfControlDeviceInitAllocate(Driver, &sddl);
    if (controlInit == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    WdfDeviceInitSetDeviceType(controlInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetIoType(controlInit, WdfDeviceIoBuffered);
    WdfDeviceInitSetExclusive(controlInit, FALSE);

    status = WdfDeviceInitAssignName(controlInit, &deviceName);
    if (!NT_SUCCESS(status))
    {
        WdfDeviceInitFree(controlInit);
        return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&controlAttributes, CONTROL_DEVICE_CONTEXT);
    controlAttributes.EvtCleanupCallback = HBashtonVdsEvtControlDeviceContextCleanup;
    controlAttributes.ExecutionLevel = WdfExecutionLevelPassive;

    status = WdfDeviceCreate(&controlInit, &controlAttributes, &controlDevice);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    controlContext = ControlDeviceGetContext(controlDevice);
    controlContext->ParentDevice = ParentDevice;

    status = WdfDeviceCreateSymbolicLink(controlDevice, &symbolicLink);
    if (!NT_SUCCESS(status))
    {
        WdfObjectDelete(controlDevice);
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.PowerManaged = WdfFalse;
    queueConfig.EvtIoDeviceControl = HBashtonVdsEvtControlIoDeviceControl;

    status = WdfIoQueueCreate(controlDevice, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status))
    {
        WdfObjectDelete(controlDevice);
        return status;
    }

    parentContext->ControlDevice = controlDevice;
    WdfControlFinishInitializing(controlDevice);
    return STATUS_SUCCESS;
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
    HBashtonVdsProcessIoDeviceControl(
        WdfIoQueueGetDevice(Queue),
        Request,
        OutputBufferLength,
        InputBufferLength,
        IoControlCode);
}

VOID
HBashtonVdsEvtControlIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    WDFDEVICE controlDevice = WdfIoQueueGetDevice(Queue);
    PCONTROL_DEVICE_CONTEXT controlContext = ControlDeviceGetContext(controlDevice);
    WDFDEVICE parentDevice = controlContext->ParentDevice;

    if (parentDevice == NULL)
    {
        WdfRequestComplete(Request, STATUS_DEVICE_NOT_READY);
        return;
    }

    HBashtonVdsProcessIoDeviceControl(
        parentDevice,
        Request,
        OutputBufferLength,
        InputBufferLength,
        IoControlCode);
}

static
VOID
HBashtonVdsProcessIoDeviceControl(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    size_t bytesReturned = 0;

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
                status = HBashtonVdsCreatePad(Device, busMode, &output->PadId);
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
                status = HBashtonVdsDestroyPad(Device, input->PadId);
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
                    Device,
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
                status = HBashtonVdsReadOutputReport(Device, input->PadId, output);
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
