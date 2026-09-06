# Xbox One lifecycle process interoperability

This opt-in gate exercises the real DS4Windows client against the real Go API
in a separate, bounded test process. It is not a hardware test or a broker
deployment. No production implementation is replaced by a second client/mapper.

## What is exercised

- Real auth v2 handshake/encrypted records, using only a public synthetic key.
- C# request creation, including full-width feedback generations and ownership.
- Go routing, authorized factory/receipt, exact stream capability and production
  X1BR ConsumerReady acknowledgement.
- Successful activation/port binding and exact registration removal.
- Client disposal while native activation is pending, including a simulated
  positive native result after cancellation. No late C# port may bind.
- Server activation deadline, with exact cleanup and no late success response.
- Client broker transport closure, private port-lease retirement, and Go-side
  assertions that the actual production registry is empty and that there was
  one create, attach and explicit removal request per case.

The native attach function is deliberately a **process-lifetime test stub**.
The three management-only cases return a synthetic port or wait for the real
activation context to cancel, then return a positive port to exercise the
cancellation/success race. The two retained cases additionally establish an
actual loopback USB/IP import through the simulated host described below. Three
additional retained cases inject terminal-delivery/ACK failures. Native attach is
never restored during cleanup, so a late handler after a failed assertion cannot
fall through to a real native attach. The peer requires an exact test selector
and explicit child-only environment flag; ordinary `go test ./...` skips it.

### Retained USB and canonical HD-feedback cases

`retained` and `retained-no-impulse` start the production USB/IP listener on an
ephemeral loopback port, never through a Windows driver. The simulated host
reuses the wire operations and startup sequence from VIIPER's existing retained
transport and retirement fixtures: exact public export alias, configuration,
Hello, START, and the input permit. No alternate mapper/feedback codec is added.

The C# test submits mapped DS4 input through `XboxOneEgressState` and the actual
encrypted broker stream. The host checks A, full left trigger and rightmost left
stick in the resulting Share-capable GIP input packet, then checks a complete
neutral release. It independently submits four normative Direct Motor commands
through retained interrupt OUT. These become production canonical CFBK frames,
cross to the real C# stream reader/feedback dispatcher, and enter the production
Switch 2 feedback session/runtime/HD encoder. Only the final BLE lease is a
recording fake; it is the existing feedback-lifetime test fixture, not hardware.

Assertions verify distinct channel identity and ordered magnitude, fresh encoded
output per command, body output with either policy, and impulse output enabled
versus neutral when the conversion option is disabled. This tests the option
supplied to the session policy, **not a WPF checkbox interaction**.
The assertions await C# feedback acknowledgements for all four distinct effects: USB OUT
acceptance and an idle dispatcher alone do not prove that all four feedback
frames have reached that dispatcher yet. The first full-suite attempt exposed
that test-ordering mistake; the bounded ACK barrier preserves the four-effect
requirement instead of relying on a sleep or accepting a smaller count.

During exact removal, the test holds terminal Stop before canonical delivery.
Removal must remain pending, the broker must remain open, and the distinct-effect
ACK count must remain at four with no Stop ACK. Releasing Stop permits real
neutral encoding and exactly one Stop ACK, after which the Go registry is empty,
its retained USB connection returns
EOF, and the C# transport/private port lease retire. A subsequent 120 ms check
covers the existing 90 ms impulse-release envelope: no delayed output may return.
This is a bounded lifecycle test, not a latency measurement.

The race-instrumented peer exposed another fixture assumption: the production
executor can refresh an unchanged nonzero Apply lease between commands and
terminal Stop. Those accepted frames still require semantic ACKs, but need not
cause another physical write if the HD encoder's output is unchanged. The fixture
therefore distinguishes all ACKs, four distinct-effect ACKs and the exact Stop
ACK; it still requires fresh encoding for each requested effect and terminal
Stop. Ordinary neutral can use the existing release envelope; terminal Stop
must bypass it. The test no longer mistakes permitted refreshes for a fifth
requested motor effect or a delivery failure.

These cases do **not** open a physical controller, perform a Windows native
import/detach, transmit output to hardware, exercise WPF binding, prove physical
USB unplug or Bluetooth association, or establish games, tactile fidelity or
measured latency. Full live-app acceptance remains a separate gate. The first
fixture failures (zero listener timeout and
an 18-byte decoder expectation for the 36-byte Share-capable packet) were fixed
in the test host/configuration, not by changing the production protocol.

### Failed terminal delivery and acknowledgements

Three additional retained cases first complete the same four-effects/input/
neutral sequence, then hold Stop so removal must remain pending with its broker
open. Releasing Stop injects one specific failure:

- `retained-stop-reject`: the existing recording transport rejects writes;
  actual canonical `TryPublish` must reject Stop and the dispatcher sends NACK.
- `retained-stop-ack-drop`: canonical Stop really encodes neutral, then the
  test ACK callback throws before sending it, simulating a failed ACK write.
- `retained-stop-ack-timeout`: canonical Stop really encodes neutral, but the
  test withholds the ACK and keeps the socket open for the real server deadline.

Each case requires zero positive Stop ACKs and an exact management conflict,
not a successful removal response. Go checks that the exact fenced registration
remains, its broker is not ready, activation/reimport are refused, and a repeated
close cannot invent safe-neutral proof. The old retained USB socket must end.
The physical test lease is then made writable, and the existing feedback owner
must neutralize/retire without delayed output returning. That local recovery
does not retroactively acknowledge the failed broker Stop or clear its fence.

The timeout fixture uses a two-second USB lifecycle period and asserts the real
factory receipt advertises a six-second removal budget. The initial attempt
used the ordinary eight-second period (24-second removal), which exceeded the
test's ten-second observation bound. That mismatch was fixed only in the test
configuration. All owned startup/activation/removal tasks are joined before
restoring the isolated authentication context, even after observation failure.
These cases do not prove handling of a permanently failed physical transport,
arbitrary kernel noncompletion, or user-visible recovery after hardware loss.

## Files and isolation

- `DS4WindowsTests/XboxOneProcessInteropTests.cs` uses production `ViiperClient`,
  `ViiperAuthentication`, `ViiperDeviceStream` and exact lifetime/port objects.
- `VIIPER/internal/server/api/handler/xboxone_ds4interop_test.go` is compiled as
  a test executable, never as the VIIPER product.
- `xboxone_ds4interop_retained_test.go` contains the simulated USB host;
  `XboxOneInteropFeedbackTarget.cs` composes the existing canonical target with
  the recording lease and controlled terminal-delivery gate.
- Its `viiper.exe` basename exists only to reuse the read-only portable hash pin.
  **Never copy this executable into a runtime or installer.**
- The selected root must pass `PortableLabContext` path/reparse/hash validation.
  Its `lab-data/viiper.key.txt` must contain exactly the public synthetic password
  `synthetic-xbox-lifecycle-interop-not-a-deployment-key`.
- Only ephemeral loopback API/USB-IP ports are used. The C# test refuses the installed
  broker's usual ports, uses no shell/window, owns the exact child process, and
  joins startup creation/rollback before restoring its test-only portable context,
  including when an assertion times out. It never
  reads installed credentials. No controller/profile/driver/startup changes occur.

## Reproduce

Use a new dedicated Desktop lab directory and create the synthetic key above.
Build the peer from VIIPER (Go 1.27 in the recorded local run):

```powershell
go test -c ./internal/server/api/handler -o '<desktop-test-root>/viiper.exe'
Get-FileHash -LiteralPath '<desktop-test-root>/viiper.exe' -Algorithm SHA256
```

Record that hash independently. In a test-only PowerShell process, from the
workspace containing DS4Windows, set:

```powershell
$env:DS4W_XBOX_INTEROP_ROOT = '<absolute-desktop-test-root>'
$env:DS4W_XBOX_INTEROP_SHA256 = '<recorded-sha256>'
dotnet test DS4Windows/DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --filter 'FullyQualifiedName~XboxOneProcessInteropTests'
```

Without both opt-in variables, the eight C# cases are inconclusive/skipped, not
passed. With a prepared peer, include them in the full suite by omitting the
filter. The test process's child uses only
`-test.run=^TestXboxDS4WindowsInteropPeer$ -test.v -test.timeout=35s` and its own
explicit mode; no general-purpose broker CLI is launched.

For Go concurrency coverage, build a separate peer root using `go test -race -c`
with a configured local C toolchain. Pin that distinct binary and repeat the same
C# tests. This is Go race detection across the real connection, not a .NET race
detector or a timing guarantee. Each peer asserts its own registry outcome and
exits; the C# test requires its pass marker and zero process exit status.

Results and exact local artifact hashes are recorded in the dated controller
platform validation ledger, separately from the unmodified b56/b57 payloads.
