# Switch 2 in-app gyro lock

This behavior is pinned to the operative `GYRO_LOCK` mapping action in
`Switch2Connect` commit `61ac6642ce12fe7217e38a860b14863b18ca7e28`,
`src/config.py` and `src/controller.py`.

## Profile contract

**In-app gyro lock** appears under **Switch 2 Controls**. Gyro Mouse and Gyro
Mouse Joystick have independent binding scopes. Every Switch 2 semantic button,
including C and source-specific paddles, may be assigned to one of two modes:

- **Hold-to-lock** pauses output only while the button is held;
- **Press-to-toggle** changes the lock state on each press edge.

A button can have only one lock mode in a scope. The editor removes it from the
other mode when selected, and profile normalization resolves malformed overlap
in favor of Hold. Legacy profiles contain no selected lock buttons and retain
their prior behavior.

## Runtime contract

The normal DS4Windows Gyro Mouse or Gyro Mouse Joystick activation policy stays
engaged while locked. The lock suppresses only presentation: mouse remainders
or mouse-joystick smoothing are reset, and the existing high-rate mouse source
is withdrawn so no prior velocity can repeat. Unlocking resumes the same active
gyro mode without synthesizing another activation event.

Toggle state resets whenever gyro disengages, matching the donor. Held toggle
buttons establish a baseline at activation, reconnect, profile change, binding
change, mode change, or timestamp regression and therefore cannot manufacture
a toggle edge. Multiple toggle buttons pressed in one report follow the donor's
shared-boolean behavior: an odd number changes the lock and an even number
leaves it unchanged.

The lock and trigger-stabilization policies share one canonical Switch 2 report
observation per SixAxis callback. Exact Joy-Con pair or Pro device/transport
generations, report QPC time, and profile revision fence state. If neither
policy has selected buttons, the default report path returns before reading the
Switch 2 sidecar. The warmed lock state machine allocates no managed memory.

Physical parsing, canonical motion, Cemuhook/DSU, ordinary mappings,
virtual-pad motion, Xbox One encoding, and feedback are not modified.
