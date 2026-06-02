# Moonlight virtual controller support

ViGEm-based virtual controllers are supported from version 3.7.0 up.
You can enable it in `Settings -> Device Options -> Virtual Controller Support`.

## Support modes

### Simple support
Not selecting `Advanced Support` means you're using the simple implementation of the support,
which should be sufficient for most users.

#### Technicalities
When a new controller is detected, it's checked for being virtual. Normally, if it's detected that it's a virtual device,
it simply wouldn't be added to the controllers list. However, this behaviour makes the detection of the Moonlight virtual controller impossible.
To counteract this, this support mode makes all devices with a matching Vendor and Product ID skip the check.

#### When to use it
If you can connect both physical and virtual controllers and all the behaviour is as usual.

### Advanced support
This option is meant to counteract what is described in [this issue](https://github.com/schmaldeo/DS4Windows/issues/4#issuecomment-2325040872).
This behaviour doesn't appear to be consistent across all machines. Most users should never have to use this option.
It does its work to counteract that issue, but it's got its tradeoffs.

Namely, you cannot connect 2 devices one right after another, you have to wait for 5 seconds after connecting one.
Additionally, sometimes you will have to disconnect a device that's already connected for the program to detect more controllers.

#### Technicalities
To counteract the issue, a timeout on device detection is used. For 5 seconds after connecting a controller, any further detections are skipped.

#### When to use it
If when using simple support you connect a single controller and the program starts adding multiple devices to the list.
