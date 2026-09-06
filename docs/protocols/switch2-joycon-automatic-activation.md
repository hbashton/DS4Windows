# Automatic standalone Joy-Con 2 activation (b80)

The BLE production coordinator now activates the first remembered Joy-Con as
a normal standalone controller immediately after calibration and slot
preparation. It no longer holds that controller in an idle pairing queue.
First-time controller association is still explicit; this does not rewrite
Nintendo host association or Windows bonds.

With automatic pairing off, each half activates separately and remains a
candidate for the existing explicit pair-selection UI. With automatic pairing
on, the oldest compatible left/right halves form one controller. Enabling the
setting also reconciles already-active standalone halves. The existing
"separate" action is idempotent for an active standalone half.

## Ownership transition

Pair selection removes UI candidates, then retires their exact standalone slot
tokens through the registration service. This includes output neutral, host
cleanup and complete native lease release. Only then may the adapter issue a
fresh, one-use admission and reopen the same persistent peer. Calibration and
physical generations are renewed; the consumed admission is never reused.
Bluetooth addresses stay private to the Windows adapter. A new scan, another
adapter, a different persistent peer, an active successor or ambiguous cleanup
cannot use the old reopen capability. This cold transition can take time for
Windows GATT and virtual USB setup; it is not a seamless or zero-latency merge.

A one-sided physical disconnect retires the pair and restores its surviving
half automatically. That half remains eligible for oldest-compatible pairing.
User Disconnect, application Stop and dual loss do not trigger this recovery.
An early-removal notification is fenced by full slot token, not slot number.

The joined feedback owner previously required a physical Stop on a missing
half, which could quarantine the pair before survivor recovery. Physical-loss
cleanup now requires that half's exact disconnected-and-released proof and
sends a real framed Stop to every surviving actuator. Failure to stop a live
actuator remains a retirement failure. This does not manufacture a delivery
receipt or ACK from an absent controller. Ordinary reports and feedback still
use the existing canonical mapper, queues and generation checks.

## Verification

The new fake-Windows end-to-end tests exercise the actual adapter, calibration
exchange, registration service, host terminal-neutral path and coordinator:
automatic standalone activation, automatic/explicit joining of active halves,
stale selection rejection, survivor restoration and ambiguous/replayed reopen
rejection. Four feedback tests cover either missing side and either successful
or rejected survivor Stop. Existing allocation assertions are unchanged.

These tests establish source lifecycle behavior, not a claim that sleeping
physical Joy-Cons were exercised. Physical radio reconnect and game acceptance
must be reported separately from the synthetic test results.

Final Release/x64 validation: 3,784 passed, zero failed, three opt-in live-audio
tests skipped (3,787 total), including all eight pinned Go/C# interoperability
tests. The latest targeted Bluetooth/Joy-Con run passed 589 cases. Portable b80
also passed five actual Windows virtual-Xbox create/remove cycles with VIIPER's
new automatic retry cleanup. The physical Joy-Cons were not advertising during
this run, so their live wake/join/game acceptance was not claimed.
