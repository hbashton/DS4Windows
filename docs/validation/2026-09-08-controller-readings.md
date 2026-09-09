# Controller Readings update repair, 2026-09-08

The user reported no live axes for Switch 2 Pro in the profile editor's
Controller Readings page. The old UI stopped its timer and waited indefinitely
on `DS4Device.ReadWaitEv`. Switch 2's serialized runtime does not use or signal
that legacy HID event, so the first readings tick never completed.

## Implementation

- A device-specific, nonblocking copy seam replaces the UI's direct event wait.
  Legacy HID devices retain their read-window handshake, with zero waiting if
  no read window is available and guaranteed release after copying.
- Switch 2 copies only between publications under its existing publication
  gate. A busy gate, active publication, or retiring controller skips this UI
  tick. It does not enqueue UI work on the controller, wait for physical IO,
  add per-report observers, or stop gameplay publication.
- Raw and mapped readings use separate preallocated `DS4StateOwnedSnapshot`
  objects. UI dead-zone preview and rendering cannot mutate report-owned
  motion or observe a reused motion reference after the copy.
- The timer rearms in `finally`, prevents overlapping callbacks, and serializes
  start/stop so disabling readings cannot leave the timer running after a late
  rearm. No timer lock is held across mapping or dispatcher work.
- Rendering rejects a changed controller reference or changed selection.
  The profile editor also uses the selected physical controller for offline
  profile readings rather than assuming slot zero (see mapping repair).

## Evidence and limits

`pro-motion-readings-after.trx`: **8/8 ControllerReadingsSnapshotTests passed**.
These cover USB and Bluetooth Pro reports without a legacy event signal,
raw/mapped axes and gyro, independent motion copies, unavailable/retired
devices, publication reentrancy, concurrent gate contention, legacy recovery
and exception release, and zero steady-state copy allocations. A source-wiring
check verifies the UI consumes this seam; it is not a physical UI test.

`controller-ui-motion-integration.trx`: **4,326 passed, 0 failed, 11 existing
skips** with the existing CI filter. This initial integration run preceded the
small timer stop/rearm gate repair found in independent review; the final
consolidated validation records the later rebuilt tree.

Independent review found no publication/snapshot ownership blocker. Tests do
not claim a new physical mouse-direction or live WPF/controller acceptance
test. No running app, controller, or profile was changed during validation.
