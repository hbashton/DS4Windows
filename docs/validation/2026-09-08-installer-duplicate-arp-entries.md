# Duplicate Apps & Features entries, 2026-09-08

## Observed registration state

The read-only machine inventory found 19 visible DS4Windows Burn registrations
and one hidden current Windows Installer registration, all in the machine-wide
64-bit registry view. The visible entries comprised nine RC4.1 bundles, eight
RC4.2 bundles, one RC4.3 bundle, and the current b74 bundle. The installed
application file version and sole current MSI both identify b74 (`5.0.4.74`).
The older entries do not represent 18 separate current application directories.
All 19 cached bundle executables still existed; that alone does not establish
that their separately needed uninstall payloads were cached.

## Exact source/log cause

The installation log `DS4Windows_20260905174122.log` distinguishes two causes:

1. The 17 RC4.1/RC4.2 bundles have an empty bundle tag. Burn recommends removing
   them, but the bootstrapper explicitly changes the request to `None` (the
   `i207` records before the RC4.3 entry). This is the existing safety policy
   for legacy bundles whose old uninstallers could remove shared infrastructure.
   `InstallerApplication.DetectRelatedBundle`/`PlanRelatedBundle` admit automatic
   outgoing removal only for the `DS4WindowsManagedV2` generation.
2. The RC4.3 bundle has that managed tag and was actually scheduled for removal.
   Its outgoing RC4.3 child log, lines 111–121, reports `0x80070002` while
   acquiring `WixAttachedContainer` for
   `CloseRunningApplicationsForUninstall`. Lines 122–132 retain the bundle's
   registration because the obsolete MSI still has a cache registration.
   The parent records this as a failed non-vital related bundle and continues
   successfully. Another outgoing managed bundle in that same transaction was
   removed successfully.

The second cause is the missing uninstall-helper caching defect already fixed
for issue #76. `UninstallHelperPackagePlan` now requests `Cache` for all three
one-shot uninstall helpers during both installation and repair. It does not
execute those helpers during installation. A future outgoing upgrade can run
its cached preflight while continuing to preserve the incoming installation's
shared VIIPER/USB-IP infrastructure. This source fix cannot retroactively add
missing payloads to an old cached installer.

## Why the upgrade identities were not changed

The MSI upgrade family (`65E808E3-D35A-4825-AE11-8D9415F16446`) and bundle
upgrade family (`BC70CCB1-AD65-42A0-B468-8A7278A37A62`) are already stable.
The MSI is already per-machine and hidden inside the visible bundle. Its
`MajorUpgrade` already permits equal product versions, accounting for
[Windows Installer's three-field comparison](https://learn.microsoft.com/en-us/windows/win32/msi/productversion).
WiX 5 also inherits Burn's
[same-version bundle replacement behavior](https://docs.firegiant.com/wix/whatsnew/faqs/).
The logs demonstrate successful related-bundle detection, not failed family
matching, split per-user installs, or an extra visible MSI. Changing an upgrade
GUID would create a new family and make this problem worse.

## Bounded recurrence coverage and existing-entry recovery

`InstallerUpgradeRegistrationTests` adds six native-free cases tied to the actual
WiX XML and production helper planner: stable family identities, hidden MSI,
equal-three-field upgrades, and install/repair followed by direct/related
uninstall with the original downloaded installer unavailable. The tests assert
that every required uninstall helper was cached and that related removal uses
only the preflight, never the direct infrastructure uninstaller. They do not
run Windows Installer, touch the registry, or claim live upgrade acceptance.
All six cases passed in the final 54-case focused installer run, alongside
seven uninstall-helper cases and 41 release-channel policy cases.

For the already retained registrations, the machine-operation owner completed
a separately reviewed, reversible Apps & Features visibility change after
exporting the affected registrations and creating a visibility-only undo.
Only the 18 obsolete entries were hidden with `SystemComponent=1`; verification
across all four registry roots found exactly one visible DS4Windows entry, the
current b74 bundle. This is not a new automatic cleanup policy.
The current bundle and hidden MSI, cached installers, dependency metadata,
application files, profiles, and shared drivers were preserved. The installed
DS4Windows executable hash and both running applications were unchanged.
This hides obsolete entries; it does not uninstall software or reclaim its
cache. Recovery backups and the undo remain operator-local, not in the public
source or distributable.
