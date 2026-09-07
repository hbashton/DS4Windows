# Joy-Con Link/Unlink and retained virtual pad — b87 preview

## User-visible changes

- Controllers cards now have a labeled Link button with the paired-controller
  glyph. First click selects/highlights it; clicking it again cancels. Link on
  an opposite-side Joy-Con joins the two. The unmatched panel also has labels.
- Joined cards have Unlink. Unlink removes remembered Joy-Con linking, not
  Bluetooth association, and restores both halves as standalone controllers.
- Manual actions remain unavailable while automatic pairing is enabled, with
  an explanatory tooltip. Operations run off the WPF dispatcher; concurrent
  Link/Unlink/association actions are disabled while a transition is running.

## Backend behavior

The first manually selected half donates its exact virtual output and active
profile. Automatic joins prefer the oldest active half. The existing output
manager keeps the donated pad privately Bound while the old physical lifetime
is retired. Ordinary unbound-output lookup cannot steal that reservation.
The new joined registration claims the exact former input slot and adopts the
same output object without Connect/Disconnect. Only the other output retires.
Unlink similarly retains the joined output for the left half and starts a
separate output for the right half. A no-virtual-output profile stays that way.

This still uses verified BLE release/reopen and canonical runtime admission.
It removes virtual-device reconstruction for the retained pad; it does not yet
claim uninterrupted physical input or a measured end-to-end join duration.

Input neutral/drain precedes transfer. Feedback is paused across the physical
handoff, then bound to a fresh physical session. For Xbox, the immutable
broker feedback identity remains authoritative: source, generations, epoch,
sequence, and original timestamp/TTL are checked before translating into the
new physical session. Queued pre-handoff rumble is consumed but not replayed.
Stop remains terminal. Normal teardown recognizes an already-proven physical
feedback retirement when acknowledging the broker's later Stop.

Failures do not authorize arbitrary slot reuse. Unadopted retained output is
retired; uncertain native cleanup remains fenced. A rejected handoff restores
still-active standalone candidates. Unlink attempts both halves independently
and reports a partial restore rather than claiming both succeeded.

## Evidence and remaining acceptance

- Full Release/x64: **3,881 passed, 0 failed, 3 opt-in audio skipped**.
  Allocation assertions remained enabled. TRX: `full-b87-joycon-link-release.trx`.
- Added coverage for first-click identity, exact-slot handoff, same output
  object/Connect count, inaccessible retained outputs, cancellation/abandonment,
  no-output profiles, UI labels/state, unlink persistence, stale tokens,
  automatic-mode exclusion, failed catalog deletion, rejected handoffs, and
  failed retained-output cleanup preserving its exact reservation for retry.
- Xbox translation tests cover all four amplitudes, Apply/Neutral/Stop,
  freshness preservation, replay/foreign generation rejection, and discarding
  pre-handoff effects.
- Portable preview published on Desktop as **5.0.4.87-JoyCon-Link-Preview**.
  VIIPER is the unchanged pinned b86 broker. No installed files or drivers changed.
- **Live Link/Unlink, measured transition duration, and post-transfer rumble
  acceptance remain pending.** Do not equate these automated checks with a
  hardware acceptance pass.

## Follow-up: Switch 2 Pro headset jack

This build makes no audio-protocol or firmware changes. The user's proposed
controller headphone-out to PC line-in cable can validate analog playback and
delay, but cannot reveal microphone transport framing. A compatible headset
microphone is needed for input validation. Use a real line input at low output
level; do not directly drive the controller's mic contact from a headphone output.

The checked-out `ndeadly/switch2_controller_research` reference at
`d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92` has Pro USB PCM interface descriptors
in `descriptors.md`, and firmware-2.0.0+ Bluetooth headset characteristics in
`bluetooth_interface.md`. The latter identifies audio payload boundaries but
explicitly leaves the 50-byte audio format unknown. The Switch2Connect README
also lists wireless Pro-controller jack/mic audio as unsupported. USB-first
endpoint inspection and a known-working capture are justified; Bluetooth codec
or raw-PCM claims are not established. None of this is tested on this controller.
