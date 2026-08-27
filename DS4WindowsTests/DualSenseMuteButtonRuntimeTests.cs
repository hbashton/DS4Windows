using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Forms.ViewModels;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseMuteButtonRuntimeTests
    {
        [TestMethod]
        public void InputOutputModeWinsOverStaleProfileSwitchConfiguration()
        {
            DualSenseMuteButtonRuntimePolicy policy =
                DualSenseMuteButtonRuntimePolicy.Resolve(
                    inputOutputModeEnabled: true,
                    microphoneTargetEnabled: true,
                    speakerTargetEnabled: true,
                    profileSwitchingEnabled: true,
                    legacyMuteLedEnabled: false);

            Assert.IsTrue(policy.InputOutputModeEnabled);
            Assert.IsTrue(policy.MutesMicrophone);
            Assert.IsTrue(policy.MutesSpeaker);
            Assert.IsFalse(policy.SwitchesProfiles);
            Assert.IsTrue(policy.OverridesMuteLed);
            Assert.IsTrue(policy.HandlesButton);
        }

        [TestMethod]
        public void GuardedProfileSwitchIsRejectedBeforeProfileMutation()
        {
            int device = Global.MAX_DS4_CONTROLLER_COUNT - 1;
            long revision = Global.BeginProfileSwitchRevision(device);
            bool guardEvaluated = false;
            bool loadInvoked = false;

            bool loaded = Mapping.TryExecuteCurrentProfileSwitchRequest(
                device, revision,
                loadGuard: () =>
                {
                    guardEvaluated = true;
                    return false;
                },
                load: () =>
                {
                    loadInvoked = true;
                    return true;
                }, out bool requestAccepted);

            Assert.IsFalse(loaded);
            Assert.IsFalse(requestAccepted);
            Assert.IsTrue(guardEvaluated);
            Assert.IsFalse(loadInvoked,
                "A master-mode change must fence the queued profile load before ResetProfile/MapTo can run.");
        }

        [TestMethod]
        public void MuteProfileRequestEpochRejectsMasterModeAba()
        {
            int device = Global.MAX_DS4_CONTROLLER_COUNT - 1;
            bool originalMaster =
                Global.DualSenseMuteButtonMutesInputOutput[device];
            bool originalSwitch =
                Global.DualSenseMuteButtonSwitchesProfiles[device];
            var viewModel = (ProfileSettingsViewModel)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(ProfileSettingsViewModel));
            typeof(ProfileSettingsViewModel).GetField("device",
                    BindingFlags.Instance | BindingFlags.NonPublic).
                SetValue(viewModel, device);

            try
            {
                Mapping.ExecuteSerializedProfileMutation(device, () =>
                {
                    Global.DualSenseMuteButtonMutesInputOutput[device] =
                        false;
                    Global.DualSenseMuteButtonSwitchesProfiles[device] =
                        true;
                    Global.AdvanceDualSenseMuteButtonModeEpoch(device);
                });
                long queuedModeEpoch =
                    Global.ReadDualSenseMuteButtonModeEpoch(device);
                Assert.IsTrue(ControlService.
                    IsCurrentDualSenseMuteProfileRequest(
                        device, queuedModeEpoch));

                // Return the visible settings to their original values after
                // passing through master mode. A boolean-only guard would
                // accept this stale request; the epoch must reject the ABA.
                viewModel.DualSenseMuteButtonMutesInputOutput = true;
                viewModel.DualSenseMuteButtonMutesInputOutput = false;
                viewModel.DualSenseMuteButtonSwitchesProfiles = true;

                Assert.IsFalse(
                    Global.DualSenseMuteButtonMutesInputOutput[device]);
                Assert.IsTrue(
                    Global.DualSenseMuteButtonSwitchesProfiles[device]);
                Assert.AreNotEqual(queuedModeEpoch,
                    Global.ReadDualSenseMuteButtonModeEpoch(device));
                Assert.IsFalse(ControlService.
                    IsCurrentDualSenseMuteProfileRequest(
                        device, queuedModeEpoch));

                bool loadInvoked = false;
                long profileRevision =
                    Global.BeginProfileSwitchRevision(device);
                bool loaded = Mapping.TryExecuteCurrentProfileSwitchRequest(
                    device, profileRevision,
                    loadGuard: () => ControlService.
                        IsCurrentDualSenseMuteProfileRequest(
                            device, queuedModeEpoch),
                    load: () =>
                    {
                        loadInvoked = true;
                        return true;
                    }, out bool requestAccepted);

                Assert.IsFalse(loaded);
                Assert.IsFalse(requestAccepted);
                Assert.IsFalse(loadInvoked,
                    "A stale mute request must not reach ResetProfile even when profile-switch mode is visibly re-enabled.");
            }
            finally
            {
                Mapping.ExecuteSerializedProfileMutation(device, () =>
                {
                    Global.DualSenseMuteButtonMutesInputOutput[device] =
                        originalMaster;
                    Global.DualSenseMuteButtonSwitchesProfiles[device] =
                        originalSwitch;
                    Global.AdvanceDualSenseMuteButtonModeEpoch(device);
                });
            }
        }

        [TestMethod]
        public void ViewModelMasterAndProfileSwitchRemainMutuallyExclusive()
        {
            int device = Global.MAX_DS4_CONTROLLER_COUNT - 1;
            bool originalMaster =
                Global.DualSenseMuteButtonMutesInputOutput[device];
            bool originalSwitch =
                Global.DualSenseMuteButtonSwitchesProfiles[device];
            var viewModel = (ProfileSettingsViewModel)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(ProfileSettingsViewModel));
            typeof(ProfileSettingsViewModel).GetField("device",
                    BindingFlags.Instance | BindingFlags.NonPublic).
                SetValue(viewModel, device);

            try
            {
                Mapping.ExecuteSerializedProfileMutation(device, () =>
                {
                    Global.DualSenseMuteButtonMutesInputOutput[device] =
                        false;
                    Global.DualSenseMuteButtonSwitchesProfiles[device] =
                        true;
                    Global.AdvanceDualSenseMuteButtonModeEpoch(device);
                });
                int masterChanges = 0;
                int switchChanges = 0;
                viewModel.DualSenseMuteButtonMutesInputOutputChanged +=
                    (_, _) => masterChanges++;
                viewModel.DualSenseMuteButtonSwitchesProfilesChanged +=
                    (_, _) => switchChanges++;

                viewModel.DualSenseMuteButtonMutesInputOutput = true;

                Assert.IsTrue(
                    Global.DualSenseMuteButtonMutesInputOutput[device]);
                Assert.IsFalse(
                    Global.DualSenseMuteButtonSwitchesProfiles[device]);
                Assert.AreEqual(1, masterChanges);
                Assert.AreEqual(1, switchChanges,
                    "The bound switch checkbox must be notified when the master unchecks it.");

                viewModel.DualSenseMuteButtonSwitchesProfiles = true;
                Assert.IsFalse(
                    Global.DualSenseMuteButtonSwitchesProfiles[device],
                    "A disabled UI control must not be able to restore stale profile-switch mode through its setter.");

                viewModel.DualSenseMuteButtonMutesInputOutput = false;
                viewModel.DualSenseMuteButtonSwitchesProfiles = true;
                Assert.IsTrue(
                    Global.DualSenseMuteButtonSwitchesProfiles[device]);
            }
            finally
            {
                Mapping.ExecuteSerializedProfileMutation(device, () =>
                {
                    Global.DualSenseMuteButtonMutesInputOutput[device] =
                        originalMaster;
                    Global.DualSenseMuteButtonSwitchesProfiles[device] =
                        originalSwitch;
                    Global.AdvanceDualSenseMuteButtonModeEpoch(device);
                });
            }
        }

        [TestMethod]
        public void PerTargetFlagsCannotMuteWithoutMasterMode()
        {
            DualSenseMuteButtonRuntimePolicy policy =
                DualSenseMuteButtonRuntimePolicy.Resolve(
                    inputOutputModeEnabled: false,
                    microphoneTargetEnabled: true,
                    speakerTargetEnabled: true,
                    profileSwitchingEnabled: false,
                    legacyMuteLedEnabled: false);

            Assert.IsFalse(policy.MutesMicrophone);
            Assert.IsFalse(policy.MutesSpeaker);
            Assert.IsFalse(policy.OverridesMuteLed);
            Assert.IsFalse(policy.HandlesButton);
            Assert.AreEqual((byte)197,
                policy.ResolveSpeakerVolume(197, muteLatched: true));
        }

        [TestMethod]
        public void SpeakerTargetZerosAndRestoresOnlyProfileSpeakerGain()
        {
            DualSenseMuteButtonRuntimePolicy policy =
                DualSenseMuteButtonRuntimePolicy.Resolve(
                    inputOutputModeEnabled: true,
                    microphoneTargetEnabled: false,
                    speakerTargetEnabled: true,
                    profileSwitchingEnabled: false,
                    legacyMuteLedEnabled: false);

            Assert.AreEqual((byte)0,
                policy.ResolveSpeakerVolume(211, muteLatched: true));
            Assert.AreEqual((byte)211,
                policy.ResolveSpeakerVolume(211, muteLatched: false));
            Assert.IsTrue(policy.CanMuteBuiltInSpeaker(
                controllerAudioEnabled: true, headsetOnly: false));
            Assert.IsFalse(policy.CanMuteBuiltInSpeaker(
                controllerAudioEnabled: true, headsetOnly: true),
                "The speaker target must not mute AUX/headphone audio.");
            Assert.IsFalse(policy.CanMuteBuiltInSpeaker(
                controllerAudioEnabled: false, headsetOnly: false));
        }

        [TestMethod]
        public void MutedSpeakerKeepsConfiguredSharedTransportGain()
        {
            DualSenseMuteButtonRuntimePolicy policy =
                DualSenseMuteButtonRuntimePolicy.Resolve(
                    inputOutputModeEnabled: true,
                    microphoneTargetEnabled: false,
                    speakerTargetEnabled: true,
                    profileSwitchingEnabled: false,
                    legacyMuteLedEnabled: false);

            DualSenseSpeakerTransportState builtIn =
                DualSenseSpeakerTransportState.Resolve(
                    speakerEnabled: true, headsetOnly: false,
                    configuredSpeakerVolume: 203, muteLatched: true,
                    policy);
            Assert.AreEqual((byte)203, builtIn.TransportVolume,
                "Mute must not bake silence into the capture/encoder carrier.");
            Assert.AreEqual((byte)0, builtIn.PhysicalSpeakerVolume,
                "Only the physical built-in speaker gain is muted.");

            DualSenseSpeakerTransportState headsetOnly =
                DualSenseSpeakerTransportState.Resolve(
                    speakerEnabled: true, headsetOnly: true,
                    configuredSpeakerVolume: 203, muteLatched: true,
                    policy);
            Assert.AreEqual((byte)203, headsetOnly.TransportVolume);
            Assert.AreEqual((byte)203, headsetOnly.PhysicalSpeakerVolume,
                "The built-in-speaker target must never zero AUX/headphones during reconnect or route changes.");

            DualSenseSpeakerTransportState silentCarrier =
                DualSenseSpeakerTransportState.Resolve(
                    speakerEnabled: false, headsetOnly: false,
                    configuredSpeakerVolume: 203, muteLatched: true,
                    policy);
            Assert.AreEqual((byte)203, silentCarrier.TransportVolume,
                "A Bluetooth replacement carrier retains configured encoder gain across mode changes.");
            Assert.AreEqual((byte)0,
                silentCarrier.PhysicalSpeakerVolume);
        }

        [TestMethod]
        public void TransientNoHandlerProfileResetPreservesMuteOffFallback()
        {
            const string sourceProfile = "Remembered Source";
            DualSenseMuteButtonRuntimePolicy transientResetPolicy =
                DualSenseMuteButtonRuntimePolicy.Resolve(
                    inputOutputModeEnabled: false,
                    microphoneTargetEnabled: false,
                    speakerTargetEnabled: false,
                    profileSwitchingEnabled: false,
                    legacyMuteLedEnabled: false);
            Assert.IsFalse(transientResetPolicy.HandlesButton);

            string afterTransientReset = ControlService.
                UpdateDualSenseRememberedOffProfileName(
                    sourceProfile, controllerConnected: true,
                    inputOutputModeEnabled:
                        transientResetPolicy.InputOutputModeEnabled);
            Assert.AreEqual(sourceProfile, afterTransientReset,
                "ResetProfile's temporary no-handler globals must not erase the return target before MapTo.");
            Assert.AreEqual(sourceProfile, ControlService.
                ResolveDualSenseMuteOffProfileName(
                    configuredOffProfileName: string.Empty,
                    rememberedOffProfileName: afterTransientReset));

            Assert.AreEqual(string.Empty, ControlService.
                UpdateDualSenseRememberedOffProfileName(
                    sourceProfile, controllerConnected: false,
                    inputOutputModeEnabled: false));
            Assert.AreEqual(string.Empty, ControlService.
                UpdateDualSenseRememberedOffProfileName(
                    sourceProfile, controllerConnected: true,
                    inputOutputModeEnabled: true));
        }

        [TestMethod]
        public void CompoundMutePublicationPreservesCarrierAndAuxState()
        {
            DualSensePhysicalOutputStateMailbox mailbox = new();
            Assert.IsTrue(mailbox.Publish(
                DualSensePhysicalOutputSnapshot.Default with
                {
                    EnableSpeakerOutput = true,
                    SpeakerVolume = 207,
                    HeadphoneVolume = 173,
                }));

            Assert.IsTrue(mailbox.SetProfileMuteButtonState(
                muteLedOverride: true,
                muteLedOn: true,
                microphoneMuteOverride: true,
                microphoneMuted: true,
                speakerMuteOverride: true,
                speakerMuted: true,
                speakerVolume: 0));

            DualSensePhysicalOutputSnapshot muted = mailbox.ReadLatest();
            Assert.IsTrue(muted.MuteLedOverride);
            Assert.IsTrue(muted.MuteLedOn);
            Assert.IsTrue(muted.MicrophoneMuteOverride);
            Assert.IsTrue(muted.MicrophoneMuted);
            Assert.IsTrue(muted.SpeakerMuteOverride);
            Assert.IsTrue(muted.SpeakerMuted);
            Assert.AreEqual((byte)0, muted.SpeakerVolume);
            Assert.AreEqual((byte)173, muted.HeadphoneVolume,
                "The mute-button speaker target must not mute AUX/headphones.");
            Assert.IsTrue(muted.EnableSpeakerOutput,
                "The live media carrier must survive a speaker mute.");

            Assert.IsTrue(mailbox.SetProfileMuteButtonState(
                muteLedOverride: true,
                muteLedOn: false,
                microphoneMuteOverride: true,
                microphoneMuted: false,
                speakerMuteOverride: true,
                speakerMuted: false,
                speakerVolume: 207));

            DualSensePhysicalOutputSnapshot restored = mailbox.ReadLatest();
            Assert.IsFalse(restored.MuteLedOn);
            Assert.IsFalse(restored.MicrophoneMuted);
            Assert.AreEqual((byte)207, restored.SpeakerVolume);
            Assert.AreEqual((byte)173, restored.HeadphoneVolume);
            Assert.IsTrue(restored.EnableSpeakerOutput);
        }

        [TestMethod]
        public void ExternalCompoundPublicationInvalidatesCachedMuteState()
        {
            var device = (DualSenseDevice)
                RuntimeHelpers.GetUninitializedObject(typeof(DualSenseDevice));
            const int authoritativeSignature = 0x4455;
            int cachedSignature = authoritativeSignature;

            Assert.IsTrue(ControlService.
                IsCurrentDualSenseMuteOutputPublication(
                    device, cachedSignature, device,
                    authoritativeSignature));

            ControlService.InvalidateDualSenseMuteOutputSignature(
                ref cachedSignature);

            Assert.AreEqual(-1, cachedSignature);
            Assert.IsFalse(ControlService.
                IsCurrentDualSenseMuteOutputPublication(
                    device, cachedSignature, device,
                    authoritativeSignature),
                "An out-of-band profile publication must force the next input report to reapply the authoritative latch.");
        }

        [TestMethod]
        public void DisablingModeClearsOverridesInOnePublication()
        {
            DualSensePhysicalOutputStateMailbox mailbox = new();
            mailbox.SetProfileMuteButtonState(true, true, true, true,
                true, true, 0);

            Assert.IsTrue(mailbox.SetProfileMuteButtonState(
                muteLedOverride: false,
                muteLedOn: true,
                microphoneMuteOverride: false,
                microphoneMuted: true,
                speakerMuteOverride: false,
                speakerMuted: true,
                speakerVolume: 144));

            DualSensePhysicalOutputSnapshot restored = mailbox.ReadLatest();
            Assert.IsFalse(restored.MuteLedOverride);
            Assert.IsFalse(restored.MuteLedOn);
            Assert.IsFalse(restored.MicrophoneMuteOverride);
            Assert.IsFalse(restored.MicrophoneMuted);
            Assert.IsFalse(restored.SpeakerMuteOverride);
            Assert.IsFalse(restored.SpeakerMuted);
            Assert.AreEqual((byte)144, restored.SpeakerVolume);
        }

        [TestMethod]
        public void LatchedReconnectPublishesCarrierAndZeroGainAtomically()
        {
            DualSensePhysicalOutputStateMailbox mailbox = new();
            long claimedVersion = 0;
            Assert.IsTrue(mailbox.TryClaim(ref claimedVersion, out _));

            Assert.IsTrue(mailbox.SetProfileAudioAndMuteButtonState(
                enableSpeakerOutput: true,
                speakerVolume: 0,
                headphoneVolume: 193,
                headsetOnlyAudio: false,
                muteLedOverride: true,
                muteLedOn: true,
                microphoneMuteOverride: true,
                microphoneMuted: true,
                speakerMuteOverride: true,
                speakerMuted: true));

            Assert.IsTrue(mailbox.TryClaim(ref claimedVersion,
                out DualSensePhysicalOutputSnapshot reconnect));
            Assert.IsTrue(reconnect.EnableSpeakerOutput);
            Assert.AreEqual((byte)0, reconnect.SpeakerVolume,
                "The carrier must never be observable with the default gain while latched mute is active.");
            Assert.AreEqual((byte)193, reconnect.HeadphoneVolume);
            Assert.IsFalse(reconnect.HeadsetOnlyAudio);
            Assert.IsTrue(reconnect.MuteLedOn);
            Assert.IsTrue(reconnect.MicrophoneMuted);
            Assert.IsTrue(reconnect.SpeakerMuted);
            Assert.IsFalse(mailbox.TryClaim(ref claimedVersion, out _),
                "Profile apply must produce one complete publication, not intermediate audio states.");
        }

        [TestMethod]
        public void MuteButtonPublicationAllocatesZeroAfterWarmup()
        {
            const int iterations = 20_000;
            DualSensePhysicalOutputStateMailbox mailbox = new();
            long claimedVersion = 0;

            for (int index = 0; index < 1_000; index++)
            {
                bool muted = (index & 1) != 0;
                mailbox.SetProfileMuteButtonState(true, muted, true,
                    muted, true, muted, muted ? (byte)0 : (byte)177);
                mailbox.TryClaim(ref claimedVersion, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < iterations; index++)
            {
                bool muted = (index & 1) != 0;
                mailbox.SetProfileMuteButtonState(true, muted, true,
                    muted, true, muted, muted ? (byte)0 : (byte)177);
                mailbox.TryClaim(ref claimedVersion, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                $"Mute-button publication allocated {allocated} bytes after warmup.");
        }

        [TestMethod]
        public void BluetoothCarrierOverlaysSelectedMuteFieldsOnly()
        {
            const int stateOffset = 13;
            byte[] report = new byte[398];
            report[stateOffset + 1] = 0x80;
            report[stateOffset + 8] = 0x02;
            report[stateOffset + 9] = 0x40;
            DualSensePhysicalOutputSnapshot muted =
                DualSensePhysicalOutputSnapshot.Default with
                {
                    MuteLedOverride = true,
                    MuteLedOn = true,
                    MicrophoneMuteOverride = true,
                    MicrophoneMuted = true,
                };

            DualSenseDevice.ApplyProfileMuteButtonStateToNativeReport(report,
                stateOffset, muted);

            Assert.AreEqual((byte)0x83, report[stateOffset + 1]);
            Assert.AreEqual((byte)0x01, report[stateOffset + 8]);
            Assert.AreEqual((byte)0x50, report[stateOffset + 9]);

            DualSensePhysicalOutputSnapshot unmuted = muted with
            {
                MuteLedOn = false,
                MicrophoneMuted = false,
            };
            DualSenseDevice.ApplyProfileMuteButtonStateToNativeReport(report,
                stateOffset, unmuted);

            Assert.AreEqual((byte)0x00, report[stateOffset + 8]);
            Assert.AreEqual((byte)0x40, report[stateOffset + 9],
                "Unmuting the microphone must preserve a native audio-mute bit.");
        }

        [TestMethod]
        public void BluetoothSpeakerOnlyModeLeavesNativeMicrophoneStateAlone()
        {
            const int stateOffset = 13;
            byte[] report = new byte[398];
            report[stateOffset + 1] = 0x82;
            report[stateOffset + 8] = 0x02;
            report[stateOffset + 9] = 0x50;
            DualSensePhysicalOutputSnapshot speakerOnly =
                DualSensePhysicalOutputSnapshot.Default with
                {
                    MuteLedOverride = true,
                    MuteLedOn = true,
                    MicrophoneMuteOverride = false,
                    MicrophoneMuted = false,
                    SpeakerMuteOverride = true,
                    SpeakerMuted = true,
                    SpeakerVolume = 0,
                };

            DualSenseDevice.ApplyProfileMuteButtonStateToNativeReport(report,
                stateOffset, speakerOnly);

            Assert.AreEqual((byte)0x83, report[stateOffset + 1]);
            Assert.AreEqual((byte)0x01, report[stateOffset + 8]);
            Assert.AreEqual((byte)0x50, report[stateOffset + 9],
                "A speaker-only target must not change game-owned microphone state.");
            Assert.AreEqual((byte)0x00, report[stateOffset + 5]);
        }

        [TestMethod]
        public void ProfileMicrophoneEventPublishesEachTriStateEdgeAfterState()
        {
            var hidDevice = (HidDevice)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(HidDevice));
            var device = new DualSenseDevice(hidDevice,
                "mute event test");
            var observed = new List<bool>();
            device.ProfileMicrophoneMuteStateChanged += (_, _) =>
                observed.Add(device.IsProfileMicrophoneMuted);

            device.SetProfileMicrophoneMuteState(
                enabled: true, muted: false);
            device.SetProfileMicrophoneMuteState(
                enabled: true, muted: false);
            device.SetProfileMuteButtonState(
                muteLedOverride: true, muteLedOn: true,
                microphoneMuteOverride: true, microphoneMuted: true,
                speakerMuteOverride: false, speakerMuted: false,
                speakerVolume: 128);
            device.SetProfileAudioAndMuteButtonState(
                enableSpeakerOutput: true, speakerVolume: 128,
                headphoneVolume: 128, headsetOnlyAudio: false,
                muteLedOverride: false, muteLedOn: false,
                microphoneMuteOverride: false, microphoneMuted: false,
                speakerMuteOverride: false, speakerMuted: false);

            CollectionAssert.AreEqual(
                new[] { false, true, false }, observed,
                "Disabled, enabled-live, and enabled-muted are distinct publication states; duplicate publications must not wake VIIPER.");
        }

        [TestMethod]
        public void AttachedViiperObservesProfileMuteWithoutMonitorPolling()
        {
            var hidDevice = (HidDevice)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(HidDevice));
            var device = new DualSenseDevice(hidDevice,
                "VIIPER mute event test");
            var viiper = new ViiperOutDevice(
                OutContType.ViiperDualSense,
                ViiperVirtualDeviceType.DualSense);
            const BindingFlags instanceFields = BindingFlags.Instance |
                BindingFlags.NonPublic;
            typeof(OutputDevice).GetField("connected", instanceFields).
                SetValue(viiper, true);
            typeof(ViiperOutDevice).GetField("microphoneSourceDevice",
                    instanceFields).
                SetValue(viiper, device);
            MethodInfo callbackMethod = typeof(ViiperOutDevice).GetMethod(
                "ProfileMicrophoneMuteStateChanged", instanceFields);
            var callback = (EventHandler)Delegate.CreateDelegate(
                typeof(EventHandler), viiper, callbackMethod);
            device.ProfileMicrophoneMuteStateChanged += callback;

            try
            {
                device.SetProfileMicrophoneMuteState(
                    enabled: true, muted: true);
                Assert.AreEqual(1, (int)typeof(ViiperOutDevice).GetField(
                    "microphoneMuted", instanceFields).GetValue(viiper));

                device.SetProfileMicrophoneMuteState(
                    enabled: true, muted: false);
                Assert.AreEqual(0, (int)typeof(ViiperOutDevice).GetField(
                    "microphoneMuted", instanceFields).GetValue(viiper));
            }
            finally
            {
                device.ProfileMicrophoneMuteStateChanged -= callback;
                typeof(OutputDevice).GetField("connected", instanceFields).
                    SetValue(viiper, false);
            }
        }

        [TestMethod]
        public void FinalMicrophoneWriterSilencesAlreadyPreparedPayload()
        {
            byte[] payload = { 1, 2, 3, 4, 5, 6 };

            ViiperOutDevice.ApplyFinalMicrophoneMuteInPlace(
                payload, payloadLength: 4, muted: true);

            CollectionAssert.AreEqual(
                new byte[] { 0, 0, 0, 0, 5, 6 }, payload,
                "Mute must be enforced after the prepared queue, without corrupting storage beyond the current frame.");

            byte[] live = { 7, 8, 9 };
            ViiperOutDevice.ApplyFinalMicrophoneMuteInPlace(
                live, live.Length, muted: false);
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, live);
        }

        [TestMethod]
        public void FallingProfileOwnershipStagesAllThreeReleaseStrobes()
        {
            DualSensePhysicalOutputSnapshot current =
                DualSensePhysicalOutputSnapshot.Default with
                {
                    MuteLedByte = 0x02,
                    SpeakerVolume = 203,
                    HeadphoneVolume = 177,
                    HeadsetOnlyAudio = true,
                };
            DualSensePhysicalOutputSnapshot previous = current with
            {
                MuteLedOverride = true,
                MuteLedOn = true,
                MicrophoneMuteOverride = true,
                MicrophoneMuted = true,
                SpeakerMuteOverride = true,
                SpeakerMuted = true,
            };

            byte strobes = DualSenseDevice.GetProfileMuteReleaseStrobes(
                previous, current);

            Assert.AreEqual((byte)(
                DualSenseDevice.ProfileMuteReleaseLed |
                DualSenseDevice.ProfileMuteReleaseMicrophone |
                DualSenseDevice.ProfileMuteReleaseSpeaker), strobes);
        }

        [TestMethod]
        public void ReleaseReportRestoresMicLedAndSpeakerWithoutAuxMutation()
        {
            const int stateOffset = 3;
            byte[] report = new byte[64];
            report[stateOffset] = 0x08;
            report[stateOffset + 1] = 0x40;
            report[stateOffset + 4] = 0x33;
            report[stateOffset + 5] = 0x00;
            report[stateOffset + 7] = 0x99;
            report[stateOffset + 8] = 0x01;
            report[stateOffset + 9] = 0x50;
            DualSensePhysicalOutputSnapshot current =
                DualSensePhysicalOutputSnapshot.Default with
                {
                    MuteLedByte = 0x02,
                    SpeakerVolume = 203,
                    HeadphoneVolume = 177,
                    HeadsetOnlyAudio = true,
                };

            DualSenseDevice.ApplyProfileMuteReleaseStrobesToNativeReport(
                report, stateOffset, current,
                (byte)(DualSenseDevice.ProfileMuteReleaseLed |
                    DualSenseDevice.ProfileMuteReleaseMicrophone |
                    DualSenseDevice.ProfileMuteReleaseSpeaker));

            Assert.AreEqual((byte)0x28, report[stateOffset]);
            Assert.AreEqual((byte)0x43, report[stateOffset + 1]);
            Assert.AreEqual((byte)0x02, report[stateOffset + 8]);
            Assert.AreEqual((byte)0x40, report[stateOffset + 9],
                "Microphone release must clear only its power-save bit.");
            Assert.AreNotEqual((byte)0, report[stateOffset + 5]);
            Assert.AreEqual((byte)0x33, report[stateOffset + 4],
                "Built-in speaker release must not alter AUX gain.");
            Assert.AreEqual((byte)0x99, report[stateOffset + 7],
                "Built-in speaker release must not alter AUX routing.");
        }

        [TestMethod]
        public void ConsumedUsbTemplateCarriesOneShotMicrophoneRelease()
        {
            byte[] consumedNativeTemplate = new byte[48];
            byte[] destination = new byte[48];
            consumedNativeTemplate[0] = 0x02;
            DualSensePhysicalOutputSnapshot current =
                DualSensePhysicalOutputSnapshot.Default with
                {
                    MuteLedByte = 0,
                    SpeakerVolume = 190,
                };

            DualSenseDevice.PrepareUsbNativeGameReportWithLocalOverridesInto(
                consumedNativeTemplate, destination, current,
                DualSenseDevice.ProfileMuteReleaseMicrophone);

            Assert.AreEqual((byte)0x02,
                (byte)(destination[2] & 0x02),
                "A cached native template must still make the live microphone restore valid exactly once.");
            Assert.AreEqual((byte)0x00,
                (byte)(destination[10] & 0x10));
        }

        [TestMethod]
        public void ReleaseAcknowledgementRequiresSuccessfulPublication()
        {
            byte all = (byte)(DualSenseDevice.ProfileMuteReleaseLed |
                DualSenseDevice.ProfileMuteReleaseMicrophone |
                DualSenseDevice.ProfileMuteReleaseSpeaker);
            byte prepared = (byte)(DualSenseDevice.ProfileMuteReleaseLed |
                DualSenseDevice.ProfileMuteReleaseMicrophone);

            Assert.AreEqual(all,
                DualSenseDevice.AcknowledgeProfileMuteReleaseStrobes(
                    all, prepared, published: false));
            Assert.AreEqual(DualSenseDevice.ProfileMuteReleaseSpeaker,
                DualSenseDevice.AcknowledgeProfileMuteReleaseStrobes(
                    all, prepared, published: true),
                "A successful retry may acknowledge only the release bits encoded into that report.");
        }

        [TestMethod]
        public void UsbNativeCommandFailureRetainsFifoAndCommitsExactlyOnce()
        {
            var hidDevice = (HidDevice)
                RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            var device = new DualSenseDevice(hidDevice,
                "USB native retry test");
            const BindingFlags instanceFields = BindingFlags.Instance |
                BindingFlags.NonPublic;
            typeof(DS4Device).GetField("conType", instanceFields).
                SetValue(device, ConnectionType.USB);
            typeof(DS4Device).GetField("outputReport", instanceFields).
                SetValue(device, new byte[48]);

            byte[] raw = new byte[48];
            raw[0] = 0x02;
            raw[1] = 0x04;
            raw[2] = 0x04;
            raw[45] = 9;
            raw[46] = 8;
            raw[47] = 7;
            int attempts = 0;
            int successfulWrites = 0;
            var attemptedLightbars = new List<byte[]>();
            device.PhysicalRawOutputWriteTestHook = attempted =>
            {
                attempts++;
                attemptedLightbars.Add(
                    new[] { attempted[45], attempted[46], attempted[47] });
                if (attempts == 1)
                {
                    return false;
                }

                successfulWrites++;
                return true;
            };

            Assert.IsTrue(device.WriteRawOutputReportFromGame(
                raw, 0, raw.Length, out long admittedRevision));
            Assert.IsTrue(admittedRevision > 0);
            Assert.AreEqual(
                DualSenseDevice.PhysicalOutputCommandProcessResult.Retry,
                device.ProcessNextPhysicalOutputCommand());

            FieldInfo cacheAvailableField = typeof(DualSenseDevice).GetField(
                "latestUsbNativeGameOutputAvailable", instanceFields);
            FieldInfo cachedRevisionField = typeof(DualSenseDevice).GetField(
                "latestUsbNativeGameOutputRevision", instanceFields);
            FieldInfo cachedReportField = typeof(DualSenseDevice).GetField(
                "latestUsbNativeGameOutputReport", instanceFields);
            FieldInfo mailboxField = typeof(DualSenseDevice).GetField(
                "physicalOutputStateMailbox", instanceFields);
            Assert.IsFalse((bool)cacheAvailableField.GetValue(device));
            Assert.AreEqual(0L,
                (long)cachedRevisionField.GetValue(device));
            var mailbox = (DualSensePhysicalOutputStateMailbox)
                mailboxField.GetValue(device);
            Assert.IsTrue(mailbox.ReadLatest().
                NativeGameLightbarOwnershipReleased,
                "A failed LED claim must not publish native ownership.");

            Assert.AreEqual(
                DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
                device.ProcessNextPhysicalOutputCommand());
            Assert.AreEqual(2, attempts);
            Assert.AreEqual(1, successfulWrites,
                "The admitted native delta must have exactly one successful physical emission.");
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 },
                attemptedLightbars[0],
                "The first LED claim attempt must compose against its prospective native ownership, not overwrite it with the currently released profile lightbar.");
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 },
                attemptedLightbars[1]);
            Assert.IsTrue((bool)cacheAvailableField.GetValue(device));
            Assert.AreEqual(admittedRevision,
                (long)cachedRevisionField.GetValue(device));
            Assert.IsFalse(mailbox.ReadLatest().
                NativeGameLightbarOwnershipReleased);
            byte[] cachedReport =
                (byte[])cachedReportField.GetValue(device);
            Assert.AreEqual((byte)0,
                (byte)(cachedReport[1] & 0x0F),
                "A successfully emitted raw command must cache only its consumed steady-state template.");
            Assert.AreEqual((byte)0,
                (byte)(cachedReport[2] & 0x7C));

            Assert.AreEqual(
                DualSenseDevice.PhysicalOutputCommandProcessResult.None,
                device.ProcessNextPhysicalOutputCommand(),
                "The FIFO head must be committed once and only once after success.");
        }

        [TestMethod]
        public void BluetoothRawFailureRetriesOnlyBeforeTransactionalAdmission()
        {
            var hidDevice = (HidDevice)
                RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            var device = new DualSenseDevice(hidDevice,
                "Bluetooth raw admission test");
            const BindingFlags instanceFields = BindingFlags.Instance |
                BindingFlags.NonPublic;
            MethodInfo updateCache = typeof(DualSenseDevice).GetMethod(
                "UpdateCachedBluetoothCombinedState", instanceFields);
            FieldInfo cacheAvailable = typeof(DualSenseDevice).GetField(
                "bluetoothCombinedSpeakerReportAvailable", instanceFields);
            FieldInfo mailboxField = typeof(DualSenseDevice).GetField(
                "physicalOutputStateMailbox", instanceFields);
            var mailbox = (DualSensePhysicalOutputStateMailbox)
                mailboxField.GetValue(device);
            byte[] rawLedClaim = new byte[48];
            rawLedClaim[0] = 0x02;
            rawLedClaim[2] = 0x04;
            rawLedClaim[45] = 21;
            rawLedClaim[46] = 22;
            rawLedClaim[47] = 23;

            Assert.IsFalse((bool)updateCache.Invoke(device,
                new object[] { rawLedClaim, 0 }));
            Assert.IsTrue(mailbox.ReadLatest().
                NativeGameLightbarOwnershipReleased,
                "A not-admitted Bluetooth delta must leave LED ownership transactional and safe to retry.");
            Assert.AreEqual(
                DualSenseDevice.BluetoothRawCommandOutcome.NotAdmitted,
                DualSenseDevice.ResolveBluetoothRawCommandOutcome(
                    cacheAdmitted: false, published: false));

            cacheAvailable.SetValue(device, true);
            Assert.IsTrue((bool)updateCache.Invoke(device,
                new object[] { rawLedClaim, 0 }));
            Assert.IsFalse(mailbox.ReadLatest().
                NativeGameLightbarOwnershipReleased,
                "LED ownership commits with the successful native cache merge.");
            Assert.AreEqual(
                DualSenseDevice.BluetoothRawCommandOutcome.CacheAdmitted,
                DualSenseDevice.ResolveBluetoothRawCommandOutcome(
                    cacheAdmitted: true, published: false),
                "Immediate publication failure must consume the FIFO head; unified recovery owns the already-admitted exact delta.");
            Assert.AreEqual(
                DualSenseDevice.BluetoothRawCommandOutcome.Published,
                DualSenseDevice.ResolveBluetoothRawCommandOutcome(
                    cacheAdmitted: true, published: true));
        }
    }
}
