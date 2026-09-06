# Explicit portable controller lab

Development/testing only. This is an opt-in startup/infrastructure policy,
not a new backend, release authenticity claim, or filesystem/security sandbox.
Normal launches retain existing configuration discovery and production VIIPER
package pins. A Desktop executable location alone is **not** isolation.

## Layout and launch

Use a dedicated ordinary directory under Desktop, containing the complete
source-built DS4Windows publish and the exact separately source-built
`viiper.exe`. No installation, task registration, or Program Files replacement
is required. The user must already have supported, verified usbip-win2 0.9.7.7.

```text
Desktop/<dedicated lab>/
  DS4Windows.exe                 complete publish, including dependencies
  viiper.exe                     explicitly pinned local build
  lab-data/
    Profiles.xml                 lab application settings
    Auto Profiles.xml
    Profiles/                    lab profiles
    Logs/
    viiper.key.txt                private local deployment password
    viiper.json                  explicit broker configuration
```

Start DS4Windows with `--portable-lab <expected-VIIPER-SHA256>`; only optional
`-m` and `-stop` are accepted alongside it. Record the expected 64-digit digest
when building/staging VIIPER, independently of the startup check. A sidecar is
not an authority. Unknown, duplicate, malformed, missing, or helper arguments
reject before helper dispatch. Without the lab option behavior is unchanged.

Lab paths are fixed relative to the app, not configurable through profiles.
Shared application storage, drive/user/Desktop roots, network paths and
reparse points are rejected. Existing lab-data is checked for reparse points.
The verified backend image is held open without write/delete sharing for the
app lifetime. Configuration writes have no roaming fallback. The startup
failure log remains in lab-data or is omitted if that location is unusable.

This is not protection against an adversarial same-user process swapping
directories/hardlinks, imported profiles running programs/macros, third-party
plugins, or Windows' own diagnostics/caches. Use newly generated, reviewed lab
profiles, not a wholesale copy of production actions and auto-profiles.

## Broker and single ownership

Start the exact lab broker externally after verifying no other mapper/broker
owns the controller/endpoints. VIIPER's explicit `--key-file` and
`--config-only --config <absolute-file>` options allow the server to use
lab-local credentials and a single reviewed configuration. Consult VIIPER's
CLI docs for the exact flags. Pass loopback listeners, local-host
authentication, update notifications disabled, and lab-local log destinations
explicitly; environmental configuration still needs review/clearing in the
launcher. Xbox One additionally needs the existing authorized-persona and
nonzero retained-import authority configuration. Lab mode does not grant or
bypass those protocol capabilities.

DS4Windows **does not** start, repair, replace, terminate, or fall back to any
broker in this mode. Exact selected-image ownership, normal usbip version,
executable and driver hashes, runtime probe and conflict checks still apply.
Lab readiness uses an authenticated encrypted ping with **only** the lab key;
a plain VIIPER greeting or a roaming key is insufficient. Authentication and
controller encryption are unchanged. No native backend is introduced.

The normal global DS4Windows single-instance event remains authoritative.
Existing/inaccessible owner or a lost creation race rejects the lab without
signaling/activating that owner. The lab does not accept legacy WM_COPYDATA
commands. Close an earlier mapper normally before starting the lab. `-stop`
only prevents controller-service start; it does not weaken prerequisites.

## Deliberately disabled integration

- DS4Windows/VIIPER startup shortcut/task creation, retargeting and removal.
- Backend/driver installers, auto-repair/elevation, and updater execution.
- Automatic HidHide whitelist/blacklist/active-state changes and Steam reclaim.
- Legacy exclusive-open recovery and its elevated HID disable/re-enable helper;
  a saved exclusive-mode setting remains stored but is ineffective in lab mode.
- Fake executable copies and configuration-location migration dialogs.

Affected Settings controls are disabled; the title and startup log identify
the lab. No change is made to existing HidHide policy. Consequently a physical
controller already hidden from the lab may be inaccessible, or games may see
both physical and virtual inputs. That is a preflight requirement, **not** a
claim that double-input containment passed. Lab-local association/calibration
stores do not remove the need to authorize and validate Bluetooth association.

## Verification

`PortableLabContextTests` covers fixed paths, exact hash and image lifetime,
strict argument rejection, protected/shared paths, explicit config selection,
missing-key failure, authenticated/encrypted readiness with matching and
different server keys, local-only failure logging and the creation-race event
boundary. Test broker peers bind ephemeral loopback ports and never create a
virtual controller. Run the ordinary startup/authentication/prerequisite tests
alongside them, followed by the full suite.

Before and after a real lab run, compare installed binary/driver hashes,
startup actions, roaming profile timestamps, HidHide policy and process paths.
Software tests alone do not prove absence of all machine side effects or
physical input/feedback delivery. Record hardware evidence separately in the
controller-platform validation ledger. A reboot is optional recovery, not a
substitute for exact ownership and release verification.
