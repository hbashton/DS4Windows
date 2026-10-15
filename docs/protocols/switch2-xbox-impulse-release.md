# Xbox impulse-trigger release on physical Switch 2 controllers

## Source audit

The parity target is Switch2Connect commit
`61ac6642ce12fe7217e38a860b14863b18ca7e28`.
`src/virtual_controller.py` defines a 90 ms linear release independently for
the left and right Xbox impulse-trigger channels. A positive replacement on a
side cancels that side's pending release. Repeated zero updates do not restart
the release. Dynamic-frequency mode derives frequency from the instantaneous
decayed strength; fixed-frequency mode retains the selected carrier.

The donor resolves this state from its approximately 16.6 ms rumble loop.
DS4Windows uses a 16 ms presentation cadence with an exact 90 ms terminal
point. The linear state law is transport-independent and uses the canonical
microsecond clock.

## Ownership and ordering

The release is a downstream presentation continuation of one already accepted
Xbox CFBK frame. It does not fabricate a new canonical source sequence,
ownership epoch, producer lane, or mapping path. This distinction matters:
inventing canonical sequence numbers for intermediate decay points would make
the next real VIIPER CFBK frame appear stale.

The exact `Switch2VirtualFeedbackSession` owns the envelope and lazily creates
one timer only when an impulse side begins releasing. Each point stages reduced
LT/RT amplitudes against the exact current canonical frame, asks the existing
feedback runtime for a presentation refresh, and reaches the same
`Switch2HdRumbleDeliverySink` and sole USB/Bluetooth physical writer. Body
motors and the other trigger side remain unchanged.

The sink records a separate monotonic presentation revision. An unchanged
revision is idempotent. If a physical result is uncertain, the exact prior
derived amplitudes, tuning, canonical frame, and delivery epoch are retained
and retried before a newer decay point can present. A newer real canonical
frame may supersede the uncertain point only through the existing canonical
ordering law.

## Lifecycle

Disabling impulse translation, changing to a non-Xbox feedback path, receiving
a terminal Stop, or retiring the virtual session clears the envelope and
fences its timer. A superseding delayed frame retains the configured delay;
the active release continues only until that newer frame reaches presentation.
Session retirement disposes the timer before the sole physical owner emits
terminal neutral. A copied callback cannot write afterward.

An explicitly configured rumble delay runs first; the 90 ms release begins
when the delayed CFBK frame reaches presentation. If the canonical frame expires
before a pending refresh succeeds, the ordinary runtime expiry/neutral path is
authoritative.

This is a perceptual translation from Xbox impulse motors to Switch 2 LRA HD
rumble. It does not claim waveform identity with an Xbox trigger motor.
