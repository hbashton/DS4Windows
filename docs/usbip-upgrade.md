# USB/IP 0.9.8.1 integration (draft)

This contribution updates the required USB/IP version, vendor installer and
file identities, userspace/driver ABI probes, serial parsing and the
uninstall/restart/install boundary. It depends on the VIIPER 0.9.8.1 ABI port.

## Required before merging or publishing

The bundled VIIPER backend remains the existing upstream package while this
change is reviewed. It must be replaced by the maintainer-approved build of
the matching VIIPER contribution, with source/provenance/digest records updated
as one package. The existing package must not be advertised as compatible
with the new driver. CI packages from this draft are not a hardware-test release.

Upstream release tags, signing policy, updater destinations and trusted
repository identities are unchanged. No fork-produced VIIPER executable is
substituted for the approved upstream dependency.

Driver replacement still requires a user-controlled restart. Setup checks the
matching usbip.exe, libusbip.dll and driver identities before enabling output.
Port cleanup preserves the exact localhost endpoint and device ownership.
