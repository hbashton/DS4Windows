#pragma once

#include <ntdef.h>

#define SONY_VENDOR_ID 0x054C
#define DUALSENSE_PRODUCT_ID 0x0CE6
#define DUALSENSE_PRODUCT_STRING L"Wireless Controller"

#pragma pack(push, 1)
typedef struct _DUALSENSE_USB_INPUT_REPORT
{
    UCHAR ReportId;
    UCHAR LeftStickX;
    UCHAR LeftStickY;
    UCHAR RightStickX;
    UCHAR RightStickY;
    UCHAR LeftTrigger;
    UCHAR RightTrigger;
    UCHAR Sequence;
    UCHAR DpadAndFaceButtons;
    UCHAR ShoulderAndMenuButtons;
    UCHAR SystemButtons;
    UCHAR Reserved0;
    SHORT GyroYaw;
    SHORT GyroPitch;
    SHORT GyroRoll;
    SHORT AccelX;
    SHORT AccelY;
    SHORT AccelZ;
    UCHAR Reserved1[4];
    ULONG SensorTimestamp;
    UCHAR Reserved2;
    UCHAR Touch0[4];
    UCHAR Touch1[4];
    UCHAR TouchPacketCounter;
    UCHAR Reserved3[11];
    UCHAR BatteryStatus;
    UCHAR Reserved4[10];
} DUALSENSE_USB_INPUT_REPORT, *PDUALSENSE_USB_INPUT_REPORT;

typedef struct _DUALSENSE_BLUETOOTH_INPUT_REPORT
{
    UCHAR ReportId;
    UCHAR LeftStickX;
    UCHAR LeftStickY;
    UCHAR RightStickX;
    UCHAR RightStickY;
    UCHAR DpadAndFaceButtons;
    UCHAR ShoulderAndMenuButtons;
    UCHAR SystemButtons;
    UCHAR LeftTrigger;
    UCHAR RightTrigger;
} DUALSENSE_BLUETOOTH_INPUT_REPORT, *PDUALSENSE_BLUETOOTH_INPUT_REPORT;
#pragma pack(pop)

C_ASSERT(sizeof(DUALSENSE_USB_INPUT_REPORT) == 64);
C_ASSERT(sizeof(DUALSENSE_BLUETOOTH_INPUT_REPORT) == 10);
