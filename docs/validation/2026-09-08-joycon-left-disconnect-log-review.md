# Left Joy-Con disconnect log review, 2026-09-08

**Superseded by retained-memory evidence:** the later joined-pair disconnect
prompted an authorized local heap dump. It retained both incidents and proves
that the earlier left-hand removal was a software rejection of a device-counter
reset, not an observed Bluetooth disconnect. See
[counter reset evidence](2026-09-08-joycon-counter-reset.md). The original
log-only findings below are preserved to distinguish what was known at each step.

The user reported a random left Joy-Con disconnect during current development
and asked to inspect logs. This was a read-only review, not a reproduction or
a transport fix.

## Observed timeline (machine local time)

The active portable app's `lab-data/Logs/ds4windows_log.txt` records:

| Time | Event |
| --- | --- |
| 18:47:23 | Left Joy-Con active independently in controller slot 2. |
| 18:58:17–19 | Profile changed to `1PASSTHRU`; virtual output changed from Xbox One to Xbox 360. |
| 19:09:29 | Right Joy-Con joined; existing virtual Xbox 360 retained for the pair. |
| 19:09:35–40 | Pair manually unlinked; left retained slot 2, right became a standalone controller in slot 3. Both ready. |
| 19:11:08.675–.817 | Left Joy-Con's Xbox 360 disassociated, USB/IP port detached, virtual output unplugged. |
| 19:11:21.423 | Left Joy-Con Bluetooth startup acknowledged again. |
| 19:11:23.862 | Left reactivated in slot 2 with `1PASSTHRU`; reported battery 90%. |

The running app process still had its original 2026-09-07 23:51 launch time;
there is no app restart/crash in this interval. Neither the app log nor the
VIIPER session logs records an initiating Bluetooth failure or controller
disconnect reason for the 19:11 removal. The later broker-log review confirms
orderly client stream closure and remove commands, but still provides no
initiating input-failure reason. A System event-log query for 19:09–19:12 returned
no matching events.

## Relevant configuration, not proof of cause

The on-disk active profile has `idleDisconnectTimeout=0`, Switch 2 mode
`LegacyProfile`, and Switch 2 timeout zero. Its configured idle policy is
therefore off. This does not inspect a potentially unsaved live editor value.

The same profile enables the existing `Disconnect Controller` special action,
whose trigger in `Actions.xml` is `PS/Options`. On a sideways left Joy-Con,
the canonical PS control is physical Capture. No button trace was running,
so this configuration is not evidence that the user pressed the shortcut.
Likewise, cleanup alone is not proof of radio loss, a queue overflow, or a
hardware fault. The source carries lifecycle reasons internally, but this
active build's normal log does not retain the initiating reason here.

## Result

The disconnect and successful recovery are confirmed. The initiating cause
remains **unproven**; no speculative timeout, shortcut, association, or hardware
change was made. No process was restarted, profile edited, controller probed,
or Bluetooth event channel enabled for this review.
