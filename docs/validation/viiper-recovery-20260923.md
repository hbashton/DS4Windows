# VIIPER recovery contract

RC4.6.5 keeps broker maintenance separate from full application installation.

- A marked portable package repairs only `viiper.exe` beside its running app.
  Its marker, credentials, configuration, profiles and installed startup tasks
  are not replaced. An active portable session cannot fall back to the managed
  broker if its marker changes.
- An installed copy repairs the canonical Program Files VIIPER executable.
  A narrowly scoped elevated helper is used when needed; it does not reinstall,
  migrate or close DS4Windows. Windows may request administrator approval.
- The exact matching bundled payload is preferred. Missing or invalid bundle
  bytes fall back to a pinned release URL and compiled SHA-256, not “latest”.
  Downloads are size/time bounded and verified before controller teardown.
- Normal input is not routed through maintenance. During an actual repair,
  the controller service drains first. Start/hotplug cannot overtake it. Only
  captured VIIPER identities are stopped; PID reuse, changed images, newcomers
  and unknown identities fail closed. A separate stop-only elevation helper
  handles identified elevated brokers without changing any installation files.
- A verified same-directory stage is committed atomically. Failed commits roll
  back; a backup is retained when rollback cannot safely complete. Credentials
  and user settings are never part of this replacement transaction.
- Output resumes only after broker launch and a successful readiness check.
  Both installed and portable recovery recheck USB/IP and system safety before
  restarting output; broker repair does not substitute for required driver setup.
  A failed transaction leaves the window and repair controls available. A
  successful retry restores the original running intent. An indeterminate
  elevated-helper timeout never reports success or resumes output.
- Startup portable repair acquires the mapper's single-instance gate first.
  A second launch cannot replace a broker owned by an already-running mapper.
- Explicit development lab mode remains externally managed and does not repair
  or start a backend.

Regression suites: `BackendMaintenanceTransactionTests`,
`PortableBrokerRepairTests`, `PortableBrokerRepairLifecycleTests`,
`PortableBrokerIntegrationTests`, `ViiperPayloadProviderTests`,
`ViiperManagedRepairTests`, plus existing portable-context/setup tests.
They use temporary files, fake process boundaries and bounded loopback sockets,
not live controllers, installed applications, tasks or drivers. Package/CI
validation is separate from hardware testing.

Recovery cannot guarantee success when Windows denies access, network access is
unavailable, the folder identity is invalid, or another process retains an image
lock. Those failures must remain visible and retryable without migration.
