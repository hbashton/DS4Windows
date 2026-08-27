using DS4Windows;
using DS4Windows.InputDevices;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseNativeLedLeaseTests
    {
        private static readonly byte[] ObservedSdlPlayerZeroReport =
            Convert.FromHexString(
                "020014000000000000000000000000000000000000000000" +
                "000000000000000000000000000000000000000024000040");

        [TestMethod]
        public void ExactObservedSdlPlayerZeroReportIsPolicyCandidate()
        {
            byte[] carrier = new byte[80];
            const int offset = 17;
            ObservedSdlPlayerZeroReport.CopyTo(carrier, offset);

            Assert.IsTrue(ViiperOutDevice.
                IsExactSdlDualSenseAutomaticLedInitialization(
                    carrier, offset));
        }

        [TestMethod]
        public void EveryNearbyByteDifferenceRejectsStrictSdlCandidate()
        {
            for (int index = 0;
                 index < ObservedSdlPlayerZeroReport.Length; index++)
            {
                byte[] changed =
                    (byte[])ObservedSdlPlayerZeroReport.Clone();
                changed[index] ^= 0x01;

                Assert.IsFalse(ViiperOutDevice.
                    IsExactSdlDualSenseAutomaticLedInitialization(
                        changed, 0),
                    $"Byte {index} was not part of the exact match. " +
                    "Trigger, rumble, audio, microphone, LED, and unknown " +
                    "fields must all fence the policy candidate.");
            }
        }

        [TestMethod]
        public void ForegroundCandidateCannotMakeExactSdlLeasePersistent()
        {
            Assert.IsTrue(ShouldExpire(foregroundCandidatePresent: false));
            Assert.IsTrue(ShouldExpire(foregroundCandidatePresent: true),
                "Foreground-window association is not sender provenance.");
        }

        [TestMethod]
        public void RetainedHadesExitReleasesNewestVisualLease()
        {
            Assert.IsTrue(ShouldReleaseForegroundExit(),
                "A dead retained owner with the exact current target, stream, " +
                "and newest native revision should release only LED ownership.");
            Assert.IsFalse(ShouldReleaseForegroundExit(
                ownerProcessLiveness: ViiperOutDevice.
                    NativeGameOwnerProcessLiveness.Running),
                "A live retained game must keep its latched visual state.");
        }

        [TestMethod]
        public void ProcessQueryFailureCannotAuthorizeVisualRelease()
        {
            Assert.AreEqual(
                ViiperOutDevice.NativeGameOwnerProcessLiveness.Unknown,
                ViiperOutDevice.ClassifyNativeGameOwnerProcessLiveness(
                    queryCompleted: false, hasExited: false));
            Assert.AreEqual(
                ViiperOutDevice.NativeGameOwnerProcessLiveness.Unknown,
                ViiperOutDevice.ClassifyNativeGameOwnerProcessLiveness(
                    queryCompleted: false, hasExited: true));
            Assert.AreEqual(
                ViiperOutDevice.NativeGameOwnerProcessLiveness.Running,
                ViiperOutDevice.ClassifyNativeGameOwnerProcessLiveness(
                    queryCompleted: true, hasExited: false));
            Assert.AreEqual(
                ViiperOutDevice.NativeGameOwnerProcessLiveness.ConfirmedExited,
                ViiperOutDevice.ClassifyNativeGameOwnerProcessLiveness(
                    queryCompleted: true, hasExited: true));
            Assert.IsFalse(ShouldReleaseForegroundExit(
                ownerProcessLiveness: ViiperOutDevice.
                    NativeGameOwnerProcessLiveness.Unknown));
        }

        [TestMethod]
        public void RetainedOwnerAdvancesAcrossHadesFinalNeutralReport()
        {
            byte[] neutral = new byte[48];
            neutral[0] = 0x02;
            Assert.IsFalse(ViiperOutDevice.HasMeaningfulNativeGameOutput(
                neutral, 0));
            Assert.IsTrue(ViiperOutDevice.
                ShouldAdvanceForegroundOwnerLease(
                    retainedOwnerPresent: true,
                    ownerTargetMatchesReport: true,
                    ownerStreamGeneration: 3,
                    reportStreamGeneration: 3,
                    nativeOutputRevision: 8),
                "A neutral shutdown report is still the newest admitted " +
                "native revision for the retained target and stream.");
        }

        [TestMethod]
        public void LatestVisualClaimOrUnverifiedWriterFencesExitedOwner()
        {
            foreach (byte visualValidity in new byte[] { 0x04, 0x10, 0x14 })
            {
                byte[] report = new byte[48];
                report[0] = 0x02;
                report[2] = visualValidity;
                Assert.IsTrue(ViiperOutDevice.NativeReportControlsVisuals(
                    report, 0));
                Assert.IsFalse(ShouldReleaseForegroundExit(
                    latestReportControlsVisuals: true),
                    $"Visual validity 0x{visualValidity:X2} was allowed to " +
                    "be overwritten by an exited foreground heuristic.");
            }

            byte[] neutral = new byte[48];
            neutral[0] = 0x02;
            Assert.IsFalse(ViiperOutDevice.NativeReportControlsVisuals(
                neutral, 0));
            Assert.IsTrue(ShouldReleaseForegroundExit());
            Assert.IsFalse(ShouldReleaseForegroundExit(
                verifiedVisualClaim: false),
                "A rumble-only foreground candidate inherited an older " +
                "writer's visual ownership.");
            Assert.IsFalse(ShouldReleaseForegroundExit(
                unverifiedVisualClaim: true),
                "A newer writer's visual claim followed by a neutral report " +
                "must remain a sticky fence.");
        }

        [TestMethod]
        public void ExplicitSonyReleaseWinsConflictingVisualValidity()
        {
            foreach (byte visualValidity in
                     new byte[] { 0x08, 0x0C, 0x18, 0x1C })
            {
                byte[] report = new byte[48];
                report[0] = 0x02;
                report[2] = visualValidity;

                Assert.AreEqual(-1, ViiperOutDevice.
                    GetNativeReportVisualOwnershipUpdate(report, 0),
                    $"Explicit release 0x{visualValidity:X2} did not win " +
                    "the trace classifier.");
                Assert.IsFalse(ViiperOutDevice.NativeReportControlsVisuals(
                    report, 0));
                Assert.AreEqual(-1, DualSenseDevice.
                    GetNativeGameLedOwnershipUpdate(report, 1),
                    "Trace and physical ownership classifiers diverged.");
            }
        }

        [TestMethod]
        public void ExplicitSonyReleaseRetiresForegroundVisualProof()
        {
            bool verified = true;
            bool unverified = true;

            ViiperOutDevice.UpdateForegroundOwnerVisualLeaseState(
                visualOwnershipUpdate: -1,
                retainedOwnerPresent: true,
                retainedOwnerVerifiedForReport: false,
                ref verified, ref unverified);

            Assert.IsFalse(verified,
                "A released controller retained stale positive proof.");
            Assert.IsFalse(unverified,
                "Sony's authoritative release retained stale ambiguity.");
        }

        [TestMethod]
        public void VisualWriterFenceIsStickyUntilSameOwnerIsVerified()
        {
            Assert.IsTrue(ViiperOutDevice.
                ForegroundProcessMatchesRetainedOwner(123, 123,
                    ViiperOutDevice.NativeGameOwnerProcessLiveness.Running));
            Assert.IsFalse(ViiperOutDevice.
                ForegroundProcessMatchesRetainedOwner(123, 456,
                    ViiperOutDevice.NativeGameOwnerProcessLiveness.Running),
                "An excluded overlay or unrelated foreground PID was treated " +
                "as affirmative writer attribution.");
            Assert.IsFalse(ViiperOutDevice.
                ForegroundProcessMatchesRetainedOwner(123, 123,
                    ViiperOutDevice.NativeGameOwnerProcessLiveness.
                        ConfirmedExited),
                "A reused numeric PID verified an already-dead Process " +
                "lease.");

            bool fenced = ViiperOutDevice.
                UpdateForegroundOwnerVisualClaimFence(
                    previousUnverifiedClaim: false,
                    reportControlsVisuals: true,
                    retainedOwnerPresent: true,
                    retainedOwnerVerifiedForReport: false);
            Assert.IsTrue(fenced);

            fenced = ViiperOutDevice.UpdateForegroundOwnerVisualClaimFence(
                previousUnverifiedClaim: fenced,
                reportControlsVisuals: false,
                retainedOwnerPresent: true,
                retainedOwnerVerifiedForReport: false);
            Assert.IsTrue(fenced,
                "A later neutral report erased an unverified visual claim.");

            fenced = ViiperOutDevice.UpdateForegroundOwnerVisualClaimFence(
                previousUnverifiedClaim: fenced,
                reportControlsVisuals: true,
                retainedOwnerPresent: true,
                retainedOwnerVerifiedForReport: true);
            Assert.IsFalse(fenced,
                "A freshly verified visual claim from the exact retained PID " +
                "did not supersede the ambiguity fence.");
        }

        [TestMethod]
        public void PhysicalTargetRebindScopesVisualProofToNewController()
        {
            bool verified = true;
            bool unverified = false;
            ViiperOutDevice.RebindForegroundOwnerVisualLeaseState(
                targetChanged: true, latestVisualOwnershipUpdate: 0,
                latestVisualClaimVerified: false,
                ref verified, ref unverified);
            Assert.IsFalse(verified,
                "Target A's visual proof migrated through a neutral report " +
                "to target B.");

            verified = true;
            unverified = true;
            ViiperOutDevice.RebindForegroundOwnerVisualLeaseState(
                targetChanged: true, latestVisualOwnershipUpdate: 0,
                latestVisualClaimVerified: false,
                ref verified, ref unverified);
            Assert.IsFalse(verified);
            Assert.IsTrue(unverified,
                "A failed-to-attribute visual claim on B was erased by its " +
                "later neutral rebind.");

            ViiperOutDevice.RebindForegroundOwnerVisualLeaseState(
                targetChanged: true, latestVisualOwnershipUpdate: 1,
                latestVisualClaimVerified: true,
                ref verified, ref unverified);
            Assert.IsTrue(verified,
                "An exact same-PID visual report on B did not establish B's " +
                "own proof.");
            Assert.IsFalse(unverified);

            ViiperOutDevice.RebindForegroundOwnerVisualLeaseState(
                targetChanged: false, latestVisualOwnershipUpdate: -1,
                latestVisualClaimVerified: false,
                ref verified, ref unverified);
            Assert.IsFalse(verified);
            Assert.IsFalse(unverified);
        }

        [TestMethod]
        public void CaptureCannotRetroactivelyVerifyOverlayVisualClaim()
        {
            Assert.IsTrue(ViiperOutDevice.
                ForegroundCandidateMatchesObservedVisualProcess(321, 321));
            Assert.IsFalse(ViiperOutDevice.
                ForegroundCandidateMatchesObservedVisualProcess(321, 654),
                "A different process foregrounded after report observation " +
                "inherited the earlier visual claim.");

            bool verified = true;
            bool unverified = true;

            // The exact visual report was observed while another process
            // (for example Game Bar) owned foreground. Focus returning to the
            // retained PID before Capture runs cannot rewrite that report-time
            // ambiguity into positive proof.
            ViiperOutDevice.RebindForegroundOwnerVisualLeaseState(
                targetChanged: true, latestVisualOwnershipUpdate: 1,
                latestVisualClaimVerified: false,
                ref verified, ref unverified);

            Assert.IsFalse(verified);
            Assert.IsTrue(unverified);
            Assert.IsFalse(ShouldReleaseForegroundExit(
                verifiedVisualClaim: verified,
                unverifiedVisualClaim: unverified));
        }

        [TestMethod]
        public void TargetOrStreamMismatchCannotAdvanceRetainedOwner()
        {
            Assert.IsFalse(ViiperOutDevice.
                ShouldAdvanceForegroundOwnerLease(
                    retainedOwnerPresent: true,
                    ownerTargetMatchesReport: false,
                    ownerStreamGeneration: 3,
                    reportStreamGeneration: 3,
                    nativeOutputRevision: 8));
            Assert.IsFalse(ViiperOutDevice.
                ShouldAdvanceForegroundOwnerLease(
                    retainedOwnerPresent: true,
                    ownerTargetMatchesReport: true,
                    ownerStreamGeneration: 3,
                    reportStreamGeneration: 4,
                    nativeOutputRevision: 8));
        }

        [TestMethod]
        public void NeutralReportRecapturesOnlyChangedRetainedBinding()
        {
            Assert.IsTrue(ViiperOutDevice.
                ShouldCaptureForegroundOwnerLease(
                    meaningfulOutput: false,
                    automaticLedInitialization: false,
                    sessionStarted: false,
                    retainedOwnerPresent: true,
                    ownerTargetMatchesReport: true,
                    ownerStreamGeneration: 3,
                    reportStreamGeneration: 4));
            Assert.IsTrue(ViiperOutDevice.
                ShouldCaptureForegroundOwnerLease(
                    meaningfulOutput: false,
                    automaticLedInitialization: false,
                    sessionStarted: false,
                    retainedOwnerPresent: true,
                    ownerTargetMatchesReport: false,
                    ownerStreamGeneration: 3,
                    reportStreamGeneration: 3));
            Assert.IsFalse(ViiperOutDevice.
                ShouldCaptureForegroundOwnerLease(
                    meaningfulOutput: false,
                    automaticLedInitialization: false,
                    sessionStarted: true,
                    retainedOwnerPresent: false,
                    ownerTargetMatchesReport: false,
                    ownerStreamGeneration: 0,
                    reportStreamGeneration: 3),
                "An initial neutral transport report captured an unrelated " +
                "foreground process.");
            Assert.IsFalse(ViiperOutDevice.
                ShouldCaptureForegroundOwnerLease(
                    meaningfulOutput: false,
                    automaticLedInitialization: false,
                    sessionStarted: false,
                    retainedOwnerPresent: true,
                    ownerTargetMatchesReport: true,
                    ownerStreamGeneration: 3,
                    reportStreamGeneration: 3));
            Assert.IsTrue(ViiperOutDevice.
                ShouldAcceptForegroundOwnerCandidate(
                    requireSameLiveOwner: true, sameLiveOwner: true));
            Assert.IsFalse(ViiperOutDevice.
                ShouldAcceptForegroundOwnerCandidate(
                    requireSameLiveOwner: true, sameLiveOwner: false),
                "A neutral rebind accepted an unrelated foreground PID.");
        }

        [TestMethod]
        public void SameLiveOwnerRebindsAcrossStreamRecovery()
        {
            Assert.IsTrue(ViiperOutDevice.
                ShouldCaptureForegroundOwnerLease(
                    meaningfulOutput: true,
                    automaticLedInitialization: false,
                    sessionStarted: false,
                    retainedOwnerPresent: true,
                    ownerTargetMatchesReport: true,
                    ownerStreamGeneration: 3,
                    reportStreamGeneration: 4),
                "A live retained PID must be revalidated when its report " +
                "moves from stream S to S+1.");
            Assert.IsTrue(ShouldInstallForegroundOwner(
                expectedStreamGeneration: 4,
                latestReportStreamGeneration: 4,
                currentStreamGeneration: 4,
                expectedRevision: 8,
                latestRevision: 8));
        }

        [TestMethod]
        public void SameLiveOwnerRebindsAcrossPhysicalTargetChange()
        {
            Assert.IsTrue(ViiperOutDevice.
                ShouldCaptureForegroundOwnerLease(
                    meaningfulOutput: true,
                    automaticLedInitialization: false,
                    sessionStarted: false,
                    retainedOwnerPresent: true,
                    ownerTargetMatchesReport: false,
                    ownerStreamGeneration: 3,
                    reportStreamGeneration: 3),
                "A live retained PID must be revalidated when its report " +
                "moves from physical target A to B.");
            Assert.IsTrue(ShouldInstallForegroundOwner(
                targetMatchesLatest: true,
                targetBindingMatches: true,
                expectedRevision: 8,
                latestRevision: 8));
            Assert.IsFalse(ViiperOutDevice.
                ShouldCaptureForegroundOwnerLease(
                    meaningfulOutput: true,
                    automaticLedInitialization: false,
                    sessionStarted: false,
                    retainedOwnerPresent: true,
                    ownerTargetMatchesReport: true,
                    ownerStreamGeneration: 3,
                    reportStreamGeneration: 3),
                "A stable live binding does not need a redundant foreground " +
                "process capture on every report.");
        }

        [TestMethod]
        public void CaptureRejectsStaleReportRevisionOrStream()
        {
            Assert.IsTrue(ShouldInstallForegroundOwner());
            Assert.IsFalse(ShouldInstallForegroundOwner(
                expectedRevision: 7, latestRevision: 8));
            Assert.IsFalse(ShouldInstallForegroundOwner(
                expectedStreamGeneration: 3,
                latestReportStreamGeneration: 4));
            Assert.IsFalse(ShouldInstallForegroundOwner(
                targetMatchesLatest: false));
        }

        [TestMethod]
        public void NewerNativeReportFencesForegroundExitRelease()
        {
            Assert.IsFalse(ShouldReleaseForegroundExit(
                ownerRevision: 7, currentRevision: 8));

            var buffer = new ViiperFeedbackDispatchBuffer(2, 8, 16, 2);
            long exitAdmissionRevision = buffer.ControlAdmissionRevision;
            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 0x02, 0x00, 0x14 }, 3,
                generation: 5, deviceIndex: 0));
            Assert.IsFalse(buffer.TryObserveControlIdle(
                exitAdmissionRevision),
                "A native report admitted before the exit observation must " +
                "cancel the stale visual release.");
        }

        [TestMethod]
        public void StreamChangeFencesForegroundExitRelease()
        {
            Assert.IsFalse(ShouldReleaseForegroundExit(
                ownerStreamGeneration: 3, currentStreamGeneration: 4));
            Assert.IsFalse(ShouldReleaseForegroundExit(
                ownerStreamGeneration: 3,
                latestReportStreamGeneration: 4));
        }

        [TestMethod]
        public void TraceRetainsAdmittedStreamInsteadOfSamplingRecoveredStream()
        {
            const BindingFlags instanceFields = BindingFlags.Instance |
                BindingFlags.NonPublic;
            var output = new ViiperOutDevice(OutContType.None,
                ViiperVirtualDeviceType.DualSense);
            var hidDevice = (HidDevice)
                RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            var device = new DualSenseDevice(hidDevice,
                "native stream provenance test");
            typeof(ViiperOutDevice).GetField("streamGeneration",
                    instanceFields).
                SetValue(output, 4L);

            MethodInfo trace = typeof(ViiperOutDevice).GetMethod(
                "TraceNativeGameOutput", instanceFields);
            trace.Invoke(output, new object[]
            {
                ObservedSdlPlayerZeroReport,
                0,
                7L,
                device,
                3L,
            });

            Assert.AreEqual(3L, (long)typeof(ViiperOutDevice).GetField(
                    "lastNativeGameOutputStreamGeneration", instanceFields).
                GetValue(output),
                "An old callback claimed in stream S was relabeled as S+1 " +
                "after recovery published the replacement stream.");
            Assert.AreEqual(3L, (long)typeof(ViiperOutDevice).GetField(
                    "sdlAutomaticLedCandidateStreamGeneration",
                    instanceFields).
                GetValue(output));
        }

        [TestMethod]
        [DoNotParallelize]
        public void FinalVisualReleaseCommitsInsideExactStreamAdmissionLease()
        {
            const BindingFlags instanceFields = BindingFlags.Instance |
                BindingFlags.NonPublic;
            ControlService previousHub = DS4Windows.Program.rootHub;
            try
            {
                var hidDevice = (HidDevice)
                    RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
                var device = new DualSenseDevice(hidDevice,
                    "native stream release test");
                typeof(DualSenseDevice).GetField("nativeGameOutputRevision",
                        instanceFields).
                    SetValue(device, 7L);

                var hub = (ControlService)
                    RuntimeHelpers.GetUninitializedObject(
                        typeof(ControlService));
                hub.DS4Controllers = new DS4Device[4];
                hub.DS4Controllers[0] = device;
                DS4Windows.Program.rootHub = hub;

                var output = new ViiperOutDevice(OutContType.None,
                    ViiperVirtualDeviceType.DualSense);
                typeof(ViiperOutDevice).GetField("connected",
                        instanceFields).
                    SetValue(output, true);
                typeof(ViiperOutDevice).GetField(
                        "feedbackDispatchStopRequested", instanceFields).
                    SetValue(output, false);
                typeof(ViiperOutDevice).GetField("streamGeneration",
                        instanceFields).
                    SetValue(output, 3L);
                MethodInfo publishBinding = typeof(ViiperOutDevice).GetMethod(
                    "PublishPhysicalControllerBinding", instanceFields);
                publishBinding.Invoke(output, new object[] { 0 });
                Assert.AreSame(device, typeof(ViiperOutDevice).GetField(
                        "publishedPhysicalControllerTargetDevice",
                        instanceFields).
                    GetValue(output));
                object admissionLock = typeof(ViiperOutDevice).GetField(
                        "feedbackCallbackAdmissionLock", instanceFields).
                    GetValue(output);
                int hookCalls = 0;
                output.NativeGameLedReleaseAdmissionTestHook = () =>
                {
                    hookCalls++;
                    Assert.IsTrue(Monitor.IsEntered(admissionLock),
                        "The physical release was not committed inside the " +
                        "same lease that publishes stream recovery and target " +
                        "binding changes.");
                };

                MethodInfo request = typeof(ViiperOutDevice).GetMethod(
                    "RequestNativeDualSenseLedOwnershipRelease",
                    instanceFields);
                typeof(ViiperOutDevice).GetField("activeFeedbackCallbacks",
                        instanceFields).
                    SetValue(output, 1);
                Assert.IsFalse((bool)request.Invoke(output, new object[]
                {
                    device,
                    7L,
                    3L,
                }), "An active legacy feedback callback was overtaken by " +
                    "the idle visual release.");
                Assert.AreEqual(0, hookCalls);

                typeof(ViiperOutDevice).GetField("activeFeedbackCallbacks",
                        instanceFields).
                    SetValue(output, 0);
                Assert.IsTrue((bool)request.Invoke(output, new object[]
                {
                    device,
                    7L,
                    3L,
                }));
                Assert.AreEqual(1, hookCalls);

                typeof(ViiperOutDevice).GetField("streamGeneration",
                        instanceFields).
                    SetValue(output, 4L);
                Assert.IsFalse((bool)request.Invoke(output, new object[]
                {
                    device,
                    7L,
                    3L,
                }),
                    "A release from retired stream S crossed the S+1 " +
                    "recovery publication boundary.");
                Assert.AreEqual(1, hookCalls,
                    "A stale stream reached the physical publication seam.");
            }
            finally
            {
                DS4Windows.Program.rootHub = previousHub;
            }
        }

        [TestMethod]
        [DoNotParallelize]
        public void ExitedOwnerLeaseIsRetainedWhileLegacyCallbackIsActive()
        {
            const BindingFlags fields = BindingFlags.Instance |
                BindingFlags.NonPublic;
            ControlService previousHub = DS4Windows.Program.rootHub;
            try
            {
                var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(
                    typeof(HidDevice));
                var device = new DualSenseDevice(hid,
                    "foreground exit callback test");
                typeof(DualSenseDevice).GetField("nativeGameOutputRevision",
                        fields).
                    SetValue(device, 7L);
                var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(
                    typeof(ControlService));
                hub.DS4Controllers = new DS4Device[4];
                hub.DS4Controllers[0] = device;
                DS4Windows.Program.rootHub = hub;

                var output = new ViiperOutDevice(OutContType.None,
                    ViiperVirtualDeviceType.DualSense);
                typeof(ViiperOutDevice).GetField("connected", fields).
                    SetValue(output, true);
                typeof(ViiperOutDevice).GetField(
                        "feedbackDispatchStopRequested", fields).
                    SetValue(output, false);
                typeof(ViiperOutDevice).GetField("streamGeneration", fields).
                    SetValue(output, 3L);
                typeof(ViiperOutDevice).GetMethod(
                        "PublishPhysicalControllerBinding", fields).
                    Invoke(output, new object[] { 0 });

                Process owner = Process.GetCurrentProcess();
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerProcess", fields).
                    SetValue(output, owner);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerProcessId", fields).
                    SetValue(output, owner.Id);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerRevision", fields).
                    SetValue(output, 7L);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerStreamGeneration", fields).
                    SetValue(output, 3L);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerTargetDevice", fields).
                    SetValue(output, device);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerHasVerifiedVisualClaim", fields).
                    SetValue(output, true);
                typeof(ViiperOutDevice).GetField(
                        "lastNativeGameOutputRevision", fields).
                    SetValue(output, 7L);
                typeof(ViiperOutDevice).GetField(
                        "lastNativeGameOutputStreamGeneration", fields).
                    SetValue(output, 3L);
                typeof(ViiperOutDevice).GetField(
                        "lastNativeGameOutputTargetDevice", fields).
                    SetValue(output, device);

                MethodInfo release = typeof(ViiperOutDevice).GetMethod(
                    "TryReleaseExitedForegroundOwnerLedOwnership", fields);
                typeof(ViiperOutDevice).GetField("activeFeedbackCallbacks",
                        fields).
                    SetValue(output, 1);
                Assert.IsFalse((bool)release.Invoke(output, new object[]
                {
                    owner,
                    ViiperOutDevice.NativeGameOwnerProcessLiveness.
                        ConfirmedExited,
                }));
                Assert.AreSame(owner, typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerProcess", fields).
                    GetValue(output),
                    "A transient callback conflict discarded the only retry " +
                    "lease.");

                typeof(ViiperOutDevice).GetField("activeFeedbackCallbacks",
                        fields).
                    SetValue(output, 0);
                int releaseHooks = 0;
                output.NativeGameLedReleaseAdmissionTestHook = () =>
                    releaseHooks++;
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerHasUnverifiedVisualClaim",
                        fields).
                    SetValue(output, true);
                Assert.IsFalse((bool)release.Invoke(output, new object[]
                {
                    owner,
                    ViiperOutDevice.NativeGameOwnerProcessLiveness.
                        ConfirmedExited,
                }), "A permanently fenced dead owner tried to alter LEDs.");
                Assert.AreEqual(0, releaseHooks);
                Assert.IsNull(typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerProcess", fields).
                    GetValue(output),
                    "A permanently stale dead lease was retained forever.");

                Process retryOwner = Process.GetCurrentProcess();
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerProcess", fields).
                    SetValue(output, retryOwner);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerProcessId", fields).
                    SetValue(output, retryOwner.Id);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerRevision", fields).
                    SetValue(output, 7L);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerStreamGeneration", fields).
                    SetValue(output, 3L);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerTargetDevice", fields).
                    SetValue(output, device);
                typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerHasVerifiedVisualClaim", fields).
                    SetValue(output, true);
                Assert.IsTrue((bool)release.Invoke(output, new object[]
                {
                    retryOwner,
                    ViiperOutDevice.NativeGameOwnerProcessLiveness.
                        ConfirmedExited,
                }));
                Assert.AreEqual(1, releaseHooks);
                Assert.IsNull(typeof(ViiperOutDevice).GetField(
                        "nativeGameOutputOwnerProcess", fields).
                    GetValue(output));
            }
            finally
            {
                DS4Windows.Program.rootHub = previousHub;
            }
        }

        [TestMethod]
        [DoNotParallelize]
        public void SameSlotTargetReplacementRequiresExactRepublish()
        {
            const BindingFlags fields = BindingFlags.Instance |
                BindingFlags.NonPublic;
            ControlService previousHub = DS4Windows.Program.rootHub;
            try
            {
                var hidA = (HidDevice)RuntimeHelpers.GetUninitializedObject(
                    typeof(HidDevice));
                var hidB = (HidDevice)RuntimeHelpers.GetUninitializedObject(
                    typeof(HidDevice));
                var deviceA = new DualSenseDevice(hidA, "same slot A");
                var deviceB = new DualSenseDevice(hidB, "same slot B");
                typeof(DualSenseDevice).GetField("nativeGameOutputRevision",
                        fields).
                    SetValue(deviceA, 7L);
                typeof(DualSenseDevice).GetField("nativeGameOutputRevision",
                        fields).
                    SetValue(deviceB, 7L);
                var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(
                    typeof(ControlService));
                hub.DS4Controllers = new DS4Device[4];
                hub.DS4Controllers[0] = deviceA;
                DS4Windows.Program.rootHub = hub;

                var output = new ViiperOutDevice(OutContType.None,
                    ViiperVirtualDeviceType.DualSense);
                typeof(ViiperOutDevice).GetField("connected", fields).
                    SetValue(output, true);
                typeof(ViiperOutDevice).GetField(
                        "feedbackDispatchStopRequested", fields).
                    SetValue(output, false);
                typeof(ViiperOutDevice).GetField("streamGeneration", fields).
                    SetValue(output, 3L);
                MethodInfo publish = typeof(ViiperOutDevice).GetMethod(
                    "PublishPhysicalControllerBinding", fields);
                MethodInfo request = typeof(ViiperOutDevice).GetMethod(
                    "RequestNativeDualSenseLedOwnershipRelease", fields);
                publish.Invoke(output, new object[] { 0 });
                Assert.AreSame(deviceA, typeof(ViiperOutDevice).GetField(
                        "publishedPhysicalControllerTargetDevice", fields).
                    GetValue(output));

                hub.DS4Controllers[0] = deviceB;
                Assert.IsFalse((bool)request.Invoke(output,
                    new object[] { deviceA, 7L, 3L }));
                Assert.IsFalse((bool)request.Invoke(output,
                    new object[] { deviceB, 7L, 3L }));

                publish.Invoke(output, new object[] { 0 });
                Assert.AreSame(deviceB, typeof(ViiperOutDevice).GetField(
                        "publishedPhysicalControllerTargetDevice", fields).
                    GetValue(output),
                    "The unchanged slot index hid an A-to-B identity change.");
                Assert.IsTrue((bool)request.Invoke(output,
                    new object[] { deviceB, 7L, 3L }));
            }
            finally
            {
                DS4Windows.Program.rootHub = previousHub;
            }
        }

        [TestMethod]
        public void ReboundPhysicalTargetFencesForegroundExitRelease()
        {
            object deviceA = new();
            object deviceB = new();

            Assert.IsFalse(ShouldReleaseForegroundExit(
                ownerTargetMatchesLatest: false));
            Assert.IsFalse(ShouldReleaseForegroundExit(
                targetBindingMatches: ViiperOutDevice.
                    NativeOutputTargetBindingMatches(deviceA, deviceB)));
        }

        [TestMethod]
        public void UnrelatedForegroundCannotActAsRetainedOwner()
        {
            Assert.IsFalse(ShouldReleaseForegroundExit(
                retainedOwnerStillCurrent: false),
                "An unrelated foreground process is not the exact retained " +
                "Process lease and cannot trigger a release.");
        }

        [TestMethod]
        public void RealFeedbackEpochNeverExpiresOnQuietInterval()
        {
            Assert.IsFalse(ShouldExpire(realFeedbackEpoch: true));
            Assert.IsFalse(ShouldExpire(exactCandidate: false,
                elapsedTicks: 1000));
        }

        [TestMethod]
        public void NewReportOrStreamGenerationFencesStaleExpiry()
        {
            Assert.IsFalse(ShouldExpire(candidateRevision: 7,
                currentRevision: 8));
            Assert.IsFalse(ShouldExpire(candidateStreamGeneration: 3,
                currentStreamGeneration: 4));
            Assert.IsFalse(DualSenseDevice.
                ShouldApplyNativeGameLedOwnershipRelease(
                    expectedRevision: 7, currentRevision: 8));
            Assert.IsTrue(DualSenseDevice.
                ShouldApplyNativeGameLedOwnershipRelease(
                    expectedRevision: 8, currentRevision: 8));
        }

        [TestMethod]
        public void ReboundPhysicalTargetFencesCoincidentRevision()
        {
            object deviceA = new();
            object deviceB = new();
            Assert.IsTrue(ViiperOutDevice.NativeOutputTargetBindingMatches(
                deviceA, deviceA));
            Assert.IsFalse(ViiperOutDevice.NativeOutputTargetBindingMatches(
                deviceA, deviceB));
            Assert.IsFalse(ShouldExpire(targetBindingMatches: false,
                candidateRevision: 1, currentRevision: 1),
                "Independent devices can have the same numeric revision; " +
                "object identity must fence A-to-B rebinding.");
        }

        [TestMethod]
        public void ReportAdmittedAfterIdleObservationWinsRevisionOrder()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 8, 16, 2);
            long idleRevision = buffer.ControlAdmissionRevision;
            Assert.IsTrue(buffer.TryObserveControlIdle(idleRevision));

            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 0x02, 0x00, 0x04 }, 3,
                generation: 5, deviceIndex: 0));
            Assert.IsFalse(buffer.TryObserveControlIdle(idleRevision));

            // If the visual restore was already published at the idle
            // linearization point, the newly dispatched native report is
            // revision N+1 and is authoritative. If it reaches the physical
            // owner first, the same comparison drops the stale restore.
            Assert.IsFalse(DualSenseDevice.
                ShouldApplyNativeGameLedOwnershipRelease(
                    expectedRevision: 7, currentRevision: 8));
        }

        [TestMethod]
        public void UsbLocalOutputWaitsForNewestAdmittedNativeCommand()
        {
            Assert.IsTrue(DualSenseDevice.
                ShouldDeferUsbLocalOutputForNewerNativeCommand(
                    isUsb: true, nativeCacheAvailable: false,
                    cachedRevision: 0, admittedRevision: 1,
                    nativeCommandPending: true),
                "The first admitted USB command must publish before a local " +
                "generation can establish an unrelated uncached template.");
            Assert.IsTrue(DualSenseDevice.
                ShouldDeferUsbLocalOutputForNewerNativeCommand(
                    isUsb: true, nativeCacheAvailable: true,
                    cachedRevision: 7, admittedRevision: 8,
                    nativeCommandPending: true),
                "The burst cap must not publish a mute/LED generation " +
                "from revision 7 while admitted revision 8 is queued.");

            Assert.IsFalse(DualSenseDevice.
                ShouldDeferUsbLocalOutputForNewerNativeCommand(
                    isUsb: true, nativeCacheAvailable: true,
                    cachedRevision: 8, admittedRevision: 8,
                    nativeCommandPending: true));
            Assert.IsFalse(DualSenseDevice.
                ShouldDeferUsbLocalOutputForNewerNativeCommand(
                    isUsb: true, nativeCacheAvailable: true,
                    cachedRevision: 7, admittedRevision: 8,
                    nativeCommandPending: false));
            Assert.IsFalse(DualSenseDevice.
                ShouldDeferUsbLocalOutputForNewerNativeCommand(
                    isUsb: false, nativeCacheAvailable: true,
                    cachedRevision: 7, admittedRevision: 8,
                    nativeCommandPending: true));
        }

        [TestMethod]
        public void UsbFirstCacheLedReleaseSurvivesLateAdmissionAndRetry()
        {
            const BindingFlags instanceFields = BindingFlags.Instance |
                BindingFlags.NonPublic;
            var hidDevice = (HidDevice)
                RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            var device = new DualSenseDevice(hidDevice,
                "USB native LED lease ordering test");
            typeof(DS4Device).GetField("conType", instanceFields).
                SetValue(device, ConnectionType.USB);
            typeof(DS4Device).GetField("outputReport", instanceFields).
                SetValue(device, new byte[48]);

            FieldInfo stopRequested = typeof(DualSenseDevice).GetField(
                "physicalOutputStopRequested", instanceFields);
            FieldInfo requestedGeneration = typeof(DualSenseDevice).GetField(
                "physicalOutputRequestedGeneration", instanceFields);
            FieldInfo pendingRelease = typeof(DualSenseDevice).GetField(
                "pendingNativeGameLedReleaseRevision", instanceFields);
            FieldInfo cacheAvailable = typeof(DualSenseDevice).GetField(
                "latestUsbNativeGameOutputAvailable", instanceFields);
            FieldInfo cachedRevision = typeof(DualSenseDevice).GetField(
                "latestUsbNativeGameOutputRevision", instanceFields);
            FieldInfo mailboxField = typeof(DualSenseDevice).GetField(
                "physicalOutputStateMailbox", instanceFields);
            MethodInfo applyPendingRelease = typeof(DualSenseDevice).GetMethod(
                "ApplyPendingNativeGameLedOwnershipRelease", instanceFields);
            stopRequested.SetValue(device, 0);

            byte[] raw = new byte[48];
            raw[0] = 0x02;
            raw[1] = 0x04;
            raw[2] = 0x04;
            raw[45] = 9;
            raw[46] = 8;
            raw[47] = 7;
            int writeAttempts = 0;
            device.PhysicalRawOutputWriteTestHook = _ =>
                ++writeAttempts > 1;

            Assert.IsTrue(device.WriteRawOutputReportFromGame(
                raw, 0, raw.Length, out long admittedRevision));
            Assert.IsTrue(device.RequestNativeGameLedOwnershipRelease(
                admittedRevision));
            long requestGeneration =
                (long)requestedGeneration.GetValue(device);

            // Model the narrow worker order: it drained an empty FIFO, then
            // the command and exit release were admitted before Claim runs.
            applyPendingRelease.Invoke(device, null);

            Assert.AreEqual(admittedRevision,
                (long)pendingRelease.GetValue(device),
                "The release must remain behind its exact uncached command.");
            Assert.AreEqual(requestGeneration + 1,
                (long)requestedGeneration.GetValue(device),
                "Retention must schedule a worker pass after the command commits.");
            Assert.IsFalse((bool)cacheAvailable.GetValue(device));

            Assert.AreEqual(
                DualSenseDevice.PhysicalOutputCommandProcessResult.Retry,
                device.ProcessNextPhysicalOutputCommand());
            Assert.AreEqual(admittedRevision,
                (long)pendingRelease.GetValue(device),
                "A failed physical write must not consume the retained release.");
            Assert.IsFalse((bool)cacheAvailable.GetValue(device));

            Assert.AreEqual(
                DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
                device.ProcessNextPhysicalOutputCommand());
            Assert.IsTrue((bool)cacheAvailable.GetValue(device));
            Assert.AreEqual(admittedRevision,
                (long)cachedRevision.GetValue(device));
            var mailbox = (DualSensePhysicalOutputStateMailbox)
                mailboxField.GetValue(device);
            Assert.IsFalse(mailbox.ReadLatest().
                NativeGameLightbarOwnershipReleased,
                "The successful native command must claim the LEDs first.");

            applyPendingRelease.Invoke(device, null);

            Assert.AreEqual(0L, (long)pendingRelease.GetValue(device));
            Assert.IsTrue(mailbox.ReadLatest().
                NativeGameLightbarOwnershipReleased,
                "The retained visual release must apply after the matching " +
                "native template commits.");
            Assert.AreEqual(2, writeAttempts);
            stopRequested.SetValue(device, 1);
        }

        [TestMethod]
        public void OlderLedReleaseCannotReplaceNewerPendingRevision()
        {
            long pendingRevision = 8;

            Assert.IsFalse(DualSenseDevice.
                TryPublishNewestNativeGameLedReleaseRevision(
                    ref pendingRevision, 7));
            Assert.AreEqual(8L, pendingRevision);
            Assert.IsTrue(DualSenseDevice.
                TryPublishNewestNativeGameLedReleaseRevision(
                    ref pendingRevision, 8));
            Assert.AreEqual(8L, pendingRevision);
            Assert.IsTrue(DualSenseDevice.
                TryPublishNewestNativeGameLedReleaseRevision(
                    ref pendingRevision, 9));
            Assert.AreEqual(9L, pendingRevision);
        }

        [TestMethod]
        public void ExactCandidateWaitsForFullGrace()
        {
            Assert.IsFalse(ShouldExpire(elapsedTicks: 999,
                graceTicks: 1000));
            Assert.IsTrue(ShouldExpire(elapsedTicks: 1000,
                graceTicks: 1000));
        }

        [TestMethod]
        public void ExactSignatureIsPolicyTradeoffNotSenderProvenance()
        {
            // SDL's automatic device assignment and its public
            // SDL_SetJoystickPlayerIndex path both reach the same PS5
            // SetDevicePlayerIndex implementation. Byte identity therefore
            // scopes a visual-only recovery policy; it cannot identify the
            // process or prove whether the call was automatic.
            Assert.IsTrue(ViiperOutDevice.
                IsExactSdlDualSenseAutomaticLedInitialization(
                    ObservedSdlPlayerZeroReport, 0));
        }

        [TestMethod]
        public void UsbLocalOverlayDoesNotReplayOneShotNativeCommands()
        {
            byte[] native = new byte[48];
            native[0] = 0x02;
            native[1] = 0x0F;
            native[2] = 0x7C;
            native[3] = 0x91;
            native[4] = 0x72;
            for (int index = 11; index <= 37; index++)
            {
                native[index] = (byte)(0x40 + index);
            }
            native[39] = 0x03;
            native[44] = 0x24;
            native[45] = 0x00;
            native[46] = 0x00;
            native[47] = 0x40;

            DualSensePhysicalOutputSnapshot snapshot =
                DualSensePhysicalOutputSnapshot.Default with
                {
                    MuteLedOverride = true,
                    MuteLedOn = true,
                    MicrophoneMuteOverride = true,
                    MicrophoneMuted = true,
                    SpeakerMuteOverride = true,
                    SpeakerMuted = true,
                    NativeGameLightbarOwnershipReleased = false,
                };

            byte[] exactEmission = new byte[48];
            DualSenseDevice.
                PrepareUsbNativeGameReportWithLocalOverridesInto(
                    native, exactEmission, snapshot);

            Assert.AreEqual((byte)0x0F,
                (byte)(exactEmission[1] & 0x0F),
                "The admitted native rumble/trigger delta must be emitted " +
                "once before its validity is consumed.");
            Assert.AreEqual((byte)0x7C,
                (byte)(exactEmission[2] & 0x7C));
            Assert.AreEqual((byte)0x03, exactEmission[39]);
            CollectionAssert.AreEqual(native[11..38],
                exactEmission[11..38]);

            byte[] quiescentTemplate = (byte[])native.Clone();
            DualSenseDevice.ConsumeNativeGameStateValidity(
                quiescentTemplate, 1);
            byte[] localMuteUpdate = new byte[48];
            DualSenseDevice.
                PrepareUsbNativeGameReportWithLocalOverridesInto(
                    quiescentTemplate, localMuteUpdate, snapshot);

            Assert.AreEqual((byte)0,
                (byte)(localMuteUpdate[1] & 0x0F),
                "A later local update replayed rumble/trigger validity.");
            Assert.AreEqual((byte)0,
                (byte)(localMuteUpdate[2] & 0x7C),
                "A later local update replayed LED release or another " +
                "game-authored validity strobe.");
            Assert.AreEqual((byte)0, localMuteUpdate[39],
                "A later local update replayed flag2 command validity.");
            Assert.AreEqual(native[3], localMuteUpdate[3]);
            Assert.AreEqual(native[4], localMuteUpdate[4]);
            CollectionAssert.AreEqual(native[11..38],
                localMuteUpdate[11..38],
                "Payload may remain cached, but no actuator validity bit " +
                "may make it a second command.");
            Assert.AreEqual((byte)0x01, localMuteUpdate[9]);
            Assert.AreEqual((byte)0x10, localMuteUpdate[10]);
        }

        [TestMethod]
        public void ForegroundExitLedOnlyRestoreChangesNoNativeActuatorOrAudioField()
        {
            Assert.IsTrue(ShouldReleaseForegroundExit());

            byte[] native = new byte[48];
            for (int index = 0; index < native.Length; index++)
            {
                native[index] = (byte)(index * 3 + 1);
            }
            native[0] = 0x02;
            DualSenseDevice.ConsumeNativeGameStateValidity(native, 1);
            DS4LightbarState lightbar = new()
            {
                LightBarColor = new DS4Color(255, 37, 0),
            };
            DualSensePhysicalOutputSnapshot baseSnapshot =
                DualSensePhysicalOutputSnapshot.Default with
                {
                    ActivePlayerLedMask = 0x04,
                    ProfileLightbar = lightbar,
                    NativeGameLightbarOwnershipReleased = false,
                };
            DualSensePhysicalOutputSnapshot releasedSnapshot =
                baseSnapshot with
                {
                    NativeGameLightbarOwnershipReleased = true,
                };

            byte[] beforeRelease = new byte[48];
            byte[] afterRelease = new byte[48];
            DualSenseDevice.
                PrepareUsbNativeGameReportWithLocalOverridesInto(
                    native, beforeRelease, baseSnapshot);
            DualSenseDevice.
                PrepareUsbNativeGameReportWithLocalOverridesInto(
                    native, afterRelease, releasedSnapshot);

            HashSet<int> visualFields =
                new() { 2, 39, 42, 43, 44, 45, 46, 47 };
            for (int index = 0; index < native.Length; index++)
            {
                if (!visualFields.Contains(index))
                {
                    Assert.AreEqual(beforeRelease[index],
                        afterRelease[index],
                        $"LED-only release changed non-visual byte {index}.");
                }
            }
            Assert.AreEqual((byte)0,
                (byte)(afterRelease[1] & 0x0F));
            Assert.AreEqual((byte)0x14,
                (byte)(afterRelease[2] & 0x7C));
            Assert.AreEqual((byte)0x02, afterRelease[39]);
            Assert.AreEqual((byte)0x04, afterRelease[44]);
            Assert.AreEqual((byte)255, afterRelease[45]);
            Assert.AreEqual((byte)37, afterRelease[46]);
            Assert.AreEqual((byte)0, afterRelease[47]);
        }

        private static bool ShouldReleaseForegroundExit(
            ViiperOutDevice.NativeGameOwnerProcessLiveness
                ownerProcessLiveness = ViiperOutDevice.
                    NativeGameOwnerProcessLiveness.ConfirmedExited,
            bool retainedOwnerStillCurrent = true,
            bool ownerTargetMatchesLatest = true,
            bool targetBindingMatches = true,
            bool latestReportControlsVisuals = false,
            bool verifiedVisualClaim = true,
            bool unverifiedVisualClaim = false,
            long ownerStreamGeneration = 3,
            long latestReportStreamGeneration = 3,
            long currentStreamGeneration = 3,
            long ownerRevision = 7,
            long currentRevision = 7)
        {
            return ViiperOutDevice.
                ShouldReleaseForegroundOwnerLedOwnership(
                    ownerProcessLiveness, retainedOwnerStillCurrent,
                    ownerTargetMatchesLatest, targetBindingMatches,
                    latestReportControlsVisuals, verifiedVisualClaim,
                    unverifiedVisualClaim,
                    ownerStreamGeneration, latestReportStreamGeneration,
                    currentStreamGeneration,
                    ownerRevision, currentRevision);
        }

        private static bool ShouldInstallForegroundOwner(
            bool retainedOwnerUnchanged = true,
            bool targetMatchesLatest = true,
            bool targetBindingMatches = true,
            long expectedStreamGeneration = 3,
            long latestReportStreamGeneration = 3,
            long currentStreamGeneration = 3,
            long expectedRevision = 7,
            long latestRevision = 7)
        {
            return ViiperOutDevice.ShouldInstallForegroundOwnerLease(
                retainedOwnerUnchanged, targetMatchesLatest,
                targetBindingMatches, expectedStreamGeneration,
                latestReportStreamGeneration, currentStreamGeneration,
                expectedRevision, latestRevision);
        }

        private static bool ShouldExpire(
            bool exactCandidate = true,
            bool realFeedbackEpoch = false,
            bool foregroundCandidatePresent = false,
            bool targetBindingMatches = true,
            long candidateStreamGeneration = 3,
            long currentStreamGeneration = 3,
            long candidateRevision = 7,
            long currentRevision = 7,
            long elapsedTicks = 1000,
            long graceTicks = 1000)
        {
            return ViiperOutDevice.
                ShouldExpireSdlAutomaticLedInitialization(
                    exactCandidate, realFeedbackEpoch,
                    foregroundCandidatePresent,
                    targetBindingMatches,
                    candidateStreamGeneration, currentStreamGeneration,
                    candidateRevision, currentRevision,
                    elapsedTicks, graceTicks);
        }
    }
}
