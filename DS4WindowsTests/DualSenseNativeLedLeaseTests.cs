using DS4Windows;
using DS4Windows.InputDevices;

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
        public void UsbLedOnlyRestoreChangesNoNativeActuatorOrAudioField()
        {
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
