# Controller platform source checkpoint, 2026-09-06

This checkpoint preserves the accumulated Switch 2 Pro/Joy-Con USB/Bluetooth,
canonical mapping, feedback/HD-rumble, profile/lifecycle and Xbox One VIIPER
output work on `feature/native-udecx-landing-zone`. It is not a release tag or
completion claim for the remaining hardware/game/latency acceptance matrix.

All 1,054 entries in b74's pre-packaging source manifest matched the working
tree before checkpoint edits. The only edits since that comparison are this
document and removal of one trailing blank line each from
`DS4WindowsTests/ViiperSwitch2RuntimeStatusTests.cs` and
`docs/protocols/switch2-virtual-runtime-status.md`. No production behavior changed.

The existing b74 canonical-source and exact-published suites each passed 3,738
tests, zero failures, with three opt-in live-audio skips (3,741 total), including
eight real Go/C# interoperability cases. This checkpoint does not claim a new
.NET suite run. Strict allocation assertions remain zero; the bounded allocation
measurement scopes are test-only and production garbage collection is unchanged.

The matching VIIPER checkpoint has a fresh successful Windows Go 1.27.0
`CGO_ENABLED=0 go test -count=1 ./...` run (33 packages passed; 23 without tests).
Its dated architecture/provenance ledgers preserve original failed evidence and
remaining acceptance gates. Staged whitespace and common private-key/token
signature checks passed; generated binaries and deployment keys are excluded.

The delivered b74 installer and source archive remain unchanged. This operation
does not install software, restart controllers, publish a binary release, complete
the native backend, or fix the still-unattributed multi-controller displayed
report-interval increase. The unsigned personal-preview/public-binary licensing
limitations and fresh combined-build hardware acceptance requirements remain.
