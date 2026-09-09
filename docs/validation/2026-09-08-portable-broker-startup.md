# Normal portable broker startup

Follow-up to the initial RC4.5 packaging checkpoint. The user requested a
top-level `viiper.exe` in the portable ZIP and automatic startup. They then
clarified that an already-running compatible copy should be reusable, rather
than being rejected merely because it is already running.

## Package identity and compatibility

Only the portable ZIP contains `DS4Windows.portable`, with the exact versioned
marker `DS4Windows portable package v1`, plus root `viiper.exe` and its SHA-256
sidecar. The executable is byte-identical to the existing pinned extras
payload. No second broker build or relaxed identity check is introduced.
The ZIP's managed-file manifest owns the three additions. They are absent
from the shared publish tree, MSI harvest and installer manifest.

Marker absence preserves the normal managed/development startup path. The
production portable context is separate from `PortableLabContext`: it does
not change the profile mapper, profile storage selection or lab capabilities.
Its sibling image must match the compiled release SHA and is held read-only
throughout the session. Portable reparse paths, invalid markers and unregistered
system locations are rejected rather than falling back to an installed broker.
A registered managed installation does not activate this context, even if an
older updater has copied the ZIP marker into its folder. That preserves its
normal installed-backend path rather than selecting the unused root alias.

## Startup and process ownership

1. Existing helper/legacy command handling remains first. Normal launch then
   prepares its portable context before passive task maintenance, profile
   loading, configuration creation or controller-service construction.
2. Foreign, multiple or unreadable broker identities stop startup with a
   visible close-and-relaunch instruction; none is terminated.
3. DS4Windows acquires its exclusive mapper gate before starting or borrowing
   a broker. A second ordinary matching launch may activate the existing
   mapper. Explicit lab launches retain their no-signal behavior.
4. A broker is started directly, without running or repairing an installed
   task. It gets explicit config-only/local-key/loopback/authentication and
   retained-import authority arguments. Inherited VIIPER configuration
   environment variables are removed from a newly started child.
5. One pre-existing broker from the exact same folder is reusable only if its
   image and live identity match, its local config/key exist, and its explicit
   command line matches the controlled configuration. Matching a filename or
   executable hash alone does not establish compatible running settings.
6. An authenticated API ping is required before controller setup, followed
   by another process-ownership check. The readiness loop has an eight-second
   budget and each probe closes its own socket at its total deadline, including
   a trickled authentication reply. No abandoned background probe is used.

The key and generated empty configuration live under `portable-data/VIIPER`.
They are runtime data, not package assets. No key is copied from an installed
deployment or added to the ZIP. Paths are checked again during key access.

Both startup-task refresh entry points and automatic installed-server fallback
are bypassed for this context. Forced repair/status entry points explain
portable status rather than launching installed-component repair. Existing
USB/IP version, executable/driver hashes, ABI checks and Citrix-conflict gates
remain enforced. Portable does not mean driverless or automatically elevated.

Only the exact newly started child is eligible for cleanup, after controller
drain (or its bounded shutdown attempt). A reused instance is borrowed and is
never stopped by this context. The existing forced `Environment.Exit` branch
also retires owned resources before exiting. No process-name termination or
PID-only takeover is used.

## Validation

The real Python ZIP/WiX fixture first reproduced six failures and one error
(three cases passed). The corrected composer passes all ten cases, including
alias bytes/hash/provenance, ZIP ownership, MSI exclusion, case-insensitive
reserved-path collisions and reparse rejection before mutation. No skips.

The explicit ZIP loop also exposed a nested ownership-manifest omission:
only the root manifest may be synthesized. A fixture failed before the
root-path-only correction; all ten cases passed afterward, preserving the
nested file in the ZIP, publish tree and MSI harvest.

The first focused C# run passed 115 of 116 tests and exposed an incompatible
write-access configuration pin. Creation now closes its exclusive writer,
then reopens and validates a read-only pin. Ordinary readers work; writes and
deletes remain blocked. Existing empty/changed files are not overwritten, and
replacement between creation and read-open is checked. The corrected focused
run passed **118 tests**, zero failures or skips.

Initial portable implementation, before the updater compatibility guard:

- `rc45-portable-complete.trx`: **4,509 passed**, zero failures, the same
  11 existing opt-in skips (4,520 total).
- `rc45-portable-repeat.trx`: a separate run with the same counts.
- `rc45-portable-allocation-all.trx`: **152 passed**, zero failures or skips,
  using `Name~Alloc`, matching the initial qualification set. A narrower
  `Name~Allocation` run also passed its 20 cases; it is not substituted for
  the full 152-case allocation run.
- The three existing CI exclusions remain unchanged (`CheckSettingsSave`,
  `CheckWriteProfile`, `CheckJaysProfileRead`). No assertion or threshold was
  weakened, and no new exclusion was added.
- The full controlled launch arguments parsed successfully against the real
  pinned VIIPER candidate with `--help`. That check did not start a server,
  create a key, or touch hardware.

- `rc45-portable-xbox-process-interop.trx`: all **eight** real client/Go-peer
  protocol and lifecycle cases passed again, with all eight success markers.
  The same clean-source, hash-pinned release-tag test peer used in the initial
  qualification ran on ephemeral loopback ports with a synthetic key and
  simulated native attachment; it is not the packaged server's hardware test.

## Replacement artifact acceptance

The pre-distribution updater audit found two additional compatibility hazards
in [DS4Updater v2.0.4 source](https://github.com/hbashton/DS4Updater/blob/ae7ac56a3f3496b13aeed3f0ca1897d18f0fe702/Updater2/MainWindow.xaml.cs).
Its asset selection always chooses a ZIP and its extraction moves all payload
files, including a portable marker, into managed folders. The production
context now checks the registered local `InstallPath` before parsing the marker
or pinning a root broker. A matching managed folder retains ordinary startup;
unregistered system folders do not gain an exception. Stale portable assets
there are inert, not silently launched. Malformed registry authority is rejected.

The updater also immediately kills DS4Windows after user confirmation; its
five-second delay follows the kill. It never retires VIIPER. That can bypass
owned-child cleanup, while a borrowed broker intentionally survives ordinary
exit. Both portable update UI paths now give fresh-ZIP/new-folder guidance
before updater preparation, and the launcher has a hard portable guard. This
is an explicit **manual portable update boundary**, not a claim that the
external updater was fixed. Managed update behavior is unchanged. No updater
repository, release or installed process was modified.

After those guards, the **final source was rebuilt and requalified**:

- `rc45-portable-final-targeted.trx`: **125 passed**, zero failures/skips.
- `rc45-portable-final-complete.trx` and `rc45-portable-final-repeat.trx`:
  **4,516 passed each**, zero failures and the same 11 opt-in skips each
  (4,527 total). No new CI exclusion.
- `rc45-portable-final-allocation.trx`: **153 matched tests passed** with
  `Name~Alloc`. Set comparison confirms this includes all previous 152 cases
  plus one new updater source-wiring test whose name contains `Allocating`;
  it is not an extra measured-allocation assertion. None were loosened.
- `rc45-portable-final-xbox-process-interop.trx`: all **eight passed** again
  using the same isolated, pinned Go peer, without native/hardware attachment.

Fresh self-contained Windows x64 publish succeeded under a new Desktop staging
directory. File/assembly/MSI/bundle version remains `5.0.5.0`; the channel is
`VIIPERRC4.5`. The initial candidate had not been released or installed. The
following files replace its distribution, rather than forming a second release.

- New portable ZIP: **549 files**, including exactly three portable-only
  additions. All **545 shared file hashes** match publish; the root ownership
  manifest owns 548 paths versus 545 on disk. The marker is the exact 31-byte
  versioned UTF-8 record. Root and extras broker hashes, both sidecars, release
  provenance and compiled runtime pins agree.
- No portable alias/sidecar/marker appears in the shared publish tree or
  installer manifest. All **546 installer-manifest size/hash entries** pass.
  Required license/provenance documents and 252 satellite assemblies remain.
  No profiles, keys, runtime broker data, logs, captures, lab/test clients or
  reparse paths are in the payload.
- WiX MSI and Burn rebuilds passed with zero warnings/errors. Default ICE
  validation and its existing ICE61 exception were unchanged. The build gate
  extracted the completed bundle and matched its MSI, setup-helper and
  bootstrapper hashes to their build inputs before atomic publication.
- Independent read-only inspection of the **actual MSI** found 547 File rows
  and none of the three portable-only files; versioned extras broker and
  notices are present. MSI SHA-256:
  `A9ED4995600E1D7BDB71F931D040CDF3A1E611DF2C0D28CB6EA1036C09E86B90`.
- USB/IP reboot-boundary, exact-SID task/rollback/ownership and installer
  transaction-state simulations passed again without changing installed state.
- Two resource-only child probes passed against the new published assemblies
  from both package and outside working directories. They did not start App
  or the controller service.
- Replacement installer: **200,065,773 bytes**, SHA-256
  `D12B9B749AB960BE457B8893D0C4951EC3246A37B4DD3F62ECD77736DE1B2144`.
  Authenticode reports `NotSigned`; the tester readme states this explicitly.
- Replacement ZIP: **137,730,046 bytes**, SHA-256
  `D259010BBF4529377E882CFA009A1D6CD952528D6CE2F0C01B304539A1186C15`.

The distribution includes matching source archives, notices, source revisions,
assessment and checksums. No public tag, push or release was performed. The
earlier [RC4.5 qualification](2026-09-08-rc45-qualification.md) remains historical
evidence, not the checksum authority for these replacement packages.

This pass has not restarted the live application or performed a real MSI
transaction or new controller acceptance test. Automatic startup/reuse has
source, fake-process, real loopback authentication/deadline and package
validation; it is not claimed as a completed live portable startup test.
