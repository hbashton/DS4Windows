using System;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests
{
    [TestClass]
    public class ControllerUiCapabilityPolicyTests
    {
        [TestMethod]
        public void DualShock4UsesDs4ArtworkAndHidesDualSenseHardware()
        {
            ControllerUiCapabilities capabilities =
                ControllerUiCapabilities.For(InputDeviceType.DS4);

            Assert.AreEqual("DualShock 4 Controller.png",
                capabilities.ImageResourceName);
            Assert.AreEqual("Rumble strength", capabilities.FeedbackLabel);
            Assert.AreEqual("DualShock 4 audio", capabilities.AudioHeader);
            Assert.AreEqual("Enable headset microphone input",
                capabilities.MicrophoneToggleLabel);
            Assert.IsTrue(capabilities.ShowControllerAudioSettings);
            Assert.IsFalse(capabilities.ShowDualSenseHardwareControls);
            Assert.IsFalse(capabilities.SupportsAdaptiveTriggers);
            Assert.IsFalse(capabilities.SupportsMuteButton);
        }

        [TestMethod]
        public void DualSenseUsesDualSenseArtworkAndHardwareControls()
        {
            ControllerUiCapabilities capabilities =
                ControllerUiCapabilities.For(InputDeviceType.DualSense);

            Assert.AreEqual("DualSense Controller.png",
                capabilities.ImageResourceName);
            Assert.AreEqual("Haptic feedback strength",
                capabilities.FeedbackLabel);
            Assert.AreEqual("DualSense audio", capabilities.AudioHeader);
            Assert.IsTrue(capabilities.ShowControllerAudioSettings);
            Assert.IsTrue(capabilities.ShowDualSenseHardwareControls);
            Assert.IsTrue(capabilities.SupportsAdaptiveTriggers);
            Assert.IsTrue(capabilities.SupportsMuteButton);
        }

        [TestMethod]
        public void OfflineProfileEditingKeepsCompleteBackendSurfaceAvailable()
        {
            ControllerUiCapabilities capabilities =
                ControllerUiCapabilities.For(null);

            Assert.IsTrue(capabilities.ShowControllerAudioSettings);
            Assert.IsTrue(capabilities.ShowDualSenseHardwareControls);
            Assert.IsTrue(capabilities.SupportsAdaptiveTriggers);
            Assert.IsTrue(capabilities.SupportsMuteButton);
            Assert.IsTrue(capabilities.ShowSwitch2Controls);
        }

        [TestMethod]
        public void ExactSwitch2RuntimeShowsControllerControlsAndSourceButtons()
        {
            ControllerUiCapabilities capabilities =
                ControllerUiCapabilities.For(
                    InputDeviceType.Switch2JoyConJoined);

            Assert.IsTrue(capabilities.IsSwitch2);
            Assert.IsTrue(capabilities.ShowSwitch2Controls);
            Assert.AreEqual("Joy-Con 2 (Joined)",
                capabilities.ControllerName);
            Assert.IsTrue(capabilities.IsMappingControlAvailable(
                DS4Controls.Switch2C, isDualSenseEdge: false));
            Assert.IsTrue(capabilities.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConLeftPaddle1,
                isDualSenseEdge: false));
            Assert.IsTrue(capabilities.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConLeftIrSensor,
                isDualSenseEdge: false));
            Assert.IsTrue(capabilities.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConRightIrSensor,
                isDualSenseEdge: false));
        }

        [TestMethod]
        public void StandaloneHoldControlAppearsOnlyForApplicableJoyCons()
        {
            Assert.IsTrue(ControllerUiCapabilities.For(null).
                ShowSwitch2StandaloneJoyConControls);
            Assert.IsTrue(ControllerUiCapabilities.For(
                InputDeviceType.Switch2JoyConLeft).
                ShowSwitch2StandaloneJoyConControls);
            Assert.IsTrue(ControllerUiCapabilities.For(
                InputDeviceType.Switch2JoyConRight).
                ShowSwitch2StandaloneJoyConControls);
            Assert.IsFalse(ControllerUiCapabilities.For(
                InputDeviceType.Switch2JoyConJoined).
                ShowSwitch2StandaloneJoyConControls);
            Assert.IsFalse(ControllerUiCapabilities.For(
                InputDeviceType.Switch2Pro).
                ShowSwitch2StandaloneJoyConControls);
            Assert.IsFalse(ControllerUiCapabilities.For(
                InputDeviceType.DualSense).
                ShowSwitch2StandaloneJoyConControls);
        }

        [TestMethod]
        public void Switch2SourceControlsFollowExactPhysicalModelAndSide()
        {
            ControllerUiCapabilities pro = ControllerUiCapabilities.For(
                InputDeviceType.Switch2Pro);
            Assert.IsTrue(pro.IsMappingControlAvailable(
                DS4Controls.Switch2C, false));
            Assert.IsFalse(pro.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConLeftPaddle1, false));
            Assert.IsFalse(pro.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConRightIrSensor, false));

            ControllerUiCapabilities left = ControllerUiCapabilities.For(
                InputDeviceType.Switch2JoyConLeft);
            Assert.IsFalse(left.IsMappingControlAvailable(
                DS4Controls.Switch2C, false));
            Assert.IsTrue(left.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConLeftPaddle2, false));
            Assert.IsTrue(left.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConLeftIrSensor, false));
            Assert.IsFalse(left.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConRightPaddle1, false));

            ControllerUiCapabilities right = ControllerUiCapabilities.For(
                InputDeviceType.Switch2JoyConRight);
            Assert.IsTrue(right.IsMappingControlAvailable(
                DS4Controls.Switch2C, false));
            Assert.IsTrue(right.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConRightPaddle2, false));
            Assert.IsTrue(right.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConRightIrSensor, false));
            Assert.IsFalse(right.IsMappingControlAvailable(
                DS4Controls.Switch2JoyConLeftPaddle1, false));

            ControllerUiCapabilities joined = ControllerUiCapabilities.For(
                InputDeviceType.Switch2JoyConJoined);
            foreach (DS4Controls control in new[]
            {
                DS4Controls.Switch2C,
                DS4Controls.Switch2JoyConLeftPaddle1,
                DS4Controls.Switch2JoyConLeftPaddle2,
                DS4Controls.Switch2JoyConRightPaddle1,
                DS4Controls.Switch2JoyConRightPaddle2,
                DS4Controls.Switch2JoyConLeftIrSensor,
                DS4Controls.Switch2JoyConRightIrSensor,
            })
            {
                Assert.IsTrue(joined.IsMappingControlAvailable(control,
                    false), control.ToString());
            }
        }

        [TestMethod]
        public void EveryPhysicalFamilyRetainsEveryVirtualPadChoice()
        {
            // Physical capabilities may filter hardware-only profile panels,
            // but they never filter the shared virtual-output selection.
            OutContType[] expected =
            {
                OutContType.ViiperDS4,
                OutContType.ViiperX360,
                OutContType.ViiperDualSense,
                OutContType.ViiperDualSenseEdge,
                OutContType.ViiperSwitch2Pro,
                OutContType.ViiperXboxOne,
            };

            foreach (InputDeviceType physicalDeviceType in
                Enum.GetValues<InputDeviceType>())
            {
                _ = ControllerUiCapabilities.For(physicalDeviceType);
                for (int index = 0; index < expected.Length; index++)
                {
                    Assert.AreEqual(expected[index],
                        ProfileSettingsViewModel.GetOutputControllerType(index),
                        $"Physical type {physicalDeviceType} lost virtual " +
                        $"output {expected[index]}.");
                }
            }
        }

        [TestMethod]
        public void NonPlayStationControllerHidesPlayStationSpecificPanels()
        {
            ControllerUiCapabilities capabilities =
                ControllerUiCapabilities.For(InputDeviceType.SwitchPro);

            Assert.IsFalse(capabilities.ShowPlayStationControllerSettings);
            Assert.IsFalse(capabilities.ShowControllerAudioSettings);
            Assert.IsFalse(capabilities.ShowDualSenseHardwareControls);
        }

        [TestMethod]
        public void MappingAvailabilityMatchesPhysicalPlayStationControls()
        {
            ControllerUiCapabilities dualShock4 =
                ControllerUiCapabilities.For(InputDeviceType.DS4);
            ControllerUiCapabilities dualSense =
                ControllerUiCapabilities.For(InputDeviceType.DualSense);

            Assert.IsTrue(dualShock4.IsMappingControlAvailable(
                DS4Controls.Cross, isDualSenseEdge: false));
            Assert.IsFalse(dualShock4.IsMappingControlAvailable(
                DS4Controls.Mute, isDualSenseEdge: false));
            Assert.IsFalse(dualShock4.IsMappingControlAvailable(
                DS4Controls.FnL, isDualSenseEdge: false));

            Assert.IsTrue(dualSense.IsMappingControlAvailable(
                DS4Controls.Mute, isDualSenseEdge: false));
            Assert.IsFalse(dualSense.IsMappingControlAvailable(
                DS4Controls.FnL, isDualSenseEdge: false));
            Assert.IsTrue(dualSense.IsMappingControlAvailable(
                DS4Controls.FnL, isDualSenseEdge: true));
            Assert.IsFalse(dualSense.IsMappingControlAvailable(
                DS4Controls.Capture, isDualSenseEdge: true));

            Assert.IsTrue(dualSense.IsControllerMapListOnlyControl(
                DS4Controls.FnL, isDualSenseEdge: true));
            Assert.IsTrue(dualSense.IsControllerMapListOnlyControl(
                DS4Controls.BRP, isDualSenseEdge: true));
            Assert.IsFalse(dualSense.IsControllerMapListOnlyControl(
                DS4Controls.Cross, isDualSenseEdge: true));
            Assert.IsFalse(dualSense.IsControllerMapListOnlyControl(
                DS4Controls.FnL, isDualSenseEdge: false));
            Assert.IsFalse(dualShock4.IsControllerMapListOnlyControl(
                DS4Controls.FnL, isDualSenseEdge: true));
        }

        [TestMethod]
        public void DualSenseEdgeExtrasStayRemappableAndAreMarkedListOnly()
        {
            MappingListViewModel mappings = new MappingListViewModel(
                Global.TEST_PROFILE_INDEX, OutContType.ViiperDualSenseEdge,
                InputDeviceType.DualSense,
                physicalControllerIsDualSenseEdge: true);

            MappedControl functionLeft = mappings.ControlMap[DS4Controls.FnL];
            MappedControl bottomRightPaddle =
                mappings.ControlMap[DS4Controls.BRP];
            MappedControl cross = mappings.ControlMap[DS4Controls.Cross];

            Assert.IsTrue(functionLeft.IsAvailableOnPhysicalController);
            Assert.IsTrue(functionLeft.IsControllerMapListOnly);
            Assert.IsTrue(functionLeft.PhysicalControllerAvailabilityHint
                .Contains("fully remappable"));
            Assert.IsTrue(bottomRightPaddle.IsAvailableOnPhysicalController);
            Assert.IsTrue(bottomRightPaddle.IsControllerMapListOnly);
            Assert.IsFalse(cross.IsControllerMapListOnly);
        }

        [DataTestMethod]
        [DataRow((int)OutContType.ViiperXboxOne)]
        [DataRow((int)OutContType.ViiperDualSense)]
        public void ProRearLabelsKeepCanonicalKeysAcrossVirtualOutputs(int output)
        {
            var pro = new MappingListViewModel(Global.TEST_PROFILE_INDEX,
                (OutContType)output, InputDeviceType.Switch2Pro);
            Assert.AreEqual("GL (Rear Left)", pro.ControlMap[DS4Controls.BLP].ControlName);
            Assert.AreEqual("GR (Rear Right)", pro.ControlMap[DS4Controls.BRP].ControlName);
            Assert.AreEqual(DS4Controls.BLP, pro.ControlMap[DS4Controls.BLP].Setting.control);
            Assert.AreEqual(DS4Controls.BRP, pro.ControlMap[DS4Controls.BRP].Setting.control);
            Assert.IsTrue(pro.ControlMap[DS4Controls.BLP].IsAvailableOnPhysicalController);
            Assert.IsTrue(pro.ControlMap[DS4Controls.BRP].IsAvailableOnPhysicalController);

            var edge = new MappingListViewModel(Global.TEST_PROFILE_INDEX,
                (OutContType)output, InputDeviceType.DualSense, true);
            Assert.AreEqual("Bottom Left Paddle", edge.ControlMap[DS4Controls.BLP].ControlName);
            Assert.AreEqual("Bottom Right Paddle", edge.ControlMap[DS4Controls.BRP].ControlName);
        }

        [DataTestMethod]
        [DataRow((int)InputDeviceType.DS4, (int)ConnectionType.BT,
            0x054C, 0x09CC, true)]
        [DataRow((int)InputDeviceType.DS4, (int)ConnectionType.USB,
            0x054C, 0x09CC, false)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.BT,
            0x054C, 0x0CE6, true)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.USB,
            0x054C, 0x0CE6, true)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.BT,
            0x054C, 0x0DF2, true)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.BT,
            0x1234, 0x0CE6, false)]
        [DataRow((int)InputDeviceType.DS4, (int)ConnectionType.BT,
            0x054C, 0xFFFF, false)]
        public void PhysicalAudioCapabilityMatchesConnectionAndSonyIdentity(
            int deviceType, int connectionType, int vendorId, int productId,
            bool expected)
        {
            ControllerUiCapabilities capabilities =
                ControllerUiCapabilities.For((InputDeviceType)deviceType,
                    (ConnectionType)connectionType, vendorId, productId);

            Assert.AreEqual(expected, capabilities.SupportsControllerAudio);
        }

        [TestMethod]
        public void UsbSpeakerAndLegacyMicSelectorsOnlyAppearForUsbDualSense()
        {
            ControllerUiCapabilities offlineProfile =
                ControllerUiCapabilities.For(null);
            ControllerUiCapabilities usbDualSense =
                ControllerUiCapabilities.For(InputDeviceType.DualSense,
                    ConnectionType.USB, 0x054C, 0x0CE6);
            ControllerUiCapabilities bluetoothDualSense =
                ControllerUiCapabilities.For(InputDeviceType.DualSense,
                    ConnectionType.BT, 0x054C, 0x0CE6);
            ControllerUiCapabilities bluetoothDualShock4 =
                ControllerUiCapabilities.For(InputDeviceType.DS4,
                    ConnectionType.BT, 0x054C, 0x09CC);

            Assert.IsFalse(offlineProfile.ShowUsbDualSenseSpeakerSelector);
            Assert.IsTrue(offlineProfile.ShowLegacyMicrophoneRouting,
                "Offline editing keeps the legacy endpoint settings available under Advanced audio.");
            Assert.IsTrue(usbDualSense.ShowUsbDualSenseSpeakerSelector);
            Assert.IsTrue(usbDualSense.ShowLegacyMicrophoneRouting);
            Assert.IsFalse(bluetoothDualSense.ShowUsbDualSenseSpeakerSelector);
            Assert.IsFalse(bluetoothDualSense.ShowLegacyMicrophoneRouting);
            Assert.IsFalse(bluetoothDualShock4.ShowUsbDualSenseSpeakerSelector);
            Assert.IsFalse(bluetoothDualShock4.ShowLegacyMicrophoneRouting);
        }

        [DataTestMethod]
        [DataRow((int)InputDeviceType.DS4, (int)ConnectionType.BT,
            0x054C, 0x09CC, (int)OutContType.ViiperDS4, true,
            (int)ControllerMicrophoneUiStatus.Ready, true)]
        [DataRow((int)InputDeviceType.DS4, (int)ConnectionType.BT,
            0x054C, 0x09CC, (int)OutContType.ViiperDualSense, true,
            (int)ControllerMicrophoneUiStatus.Ready, true)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.BT,
            0x054C, 0x0CE6, (int)OutContType.ViiperDS4, true,
            (int)ControllerMicrophoneUiStatus.Ready, true)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.BT,
            0x054C, 0x0CE6, (int)OutContType.ViiperDualSense, true,
            (int)ControllerMicrophoneUiStatus.Ready, true)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.BT,
            0x054C, 0x0DF2, (int)OutContType.ViiperDualSenseEdge, true,
            (int)ControllerMicrophoneUiStatus.Ready, true)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.BT,
            0x054C, 0x0CE6, (int)OutContType.X360, true,
            (int)ControllerMicrophoneUiStatus.Ready, true)]
        [DataRow((int)InputDeviceType.DS4, (int)ConnectionType.BT,
            0x054C, 0x09CC, (int)OutContType.ViiperSwitch2Pro, true,
            (int)ControllerMicrophoneUiStatus.Ready, true)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.BT,
            0x054C, 0x0CE6, (int)OutContType.ViiperDualSense, false,
            (int)ControllerMicrophoneUiStatus.OutputStarting, false)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.USB,
            0x054C, 0x0CE6, (int)OutContType.X360, false,
            (int)ControllerMicrophoneUiStatus.UsbLegacyRoute, true)]
        [DataRow((int)InputDeviceType.DS4, (int)ConnectionType.USB,
            0x054C, 0x09CC, (int)OutContType.ViiperDS4, true,
            (int)ControllerMicrophoneUiStatus.RequiresBluetooth, false)]
        [DataRow((int)InputDeviceType.DualSense, (int)ConnectionType.BT,
            0x1234, 0x0CE6, (int)OutContType.ViiperDualSense, true,
            (int)ControllerMicrophoneUiStatus.RequiresCompatibleController,
            false)]
        public void MicrophoneUiStateExplainsPhysicalPersonaAndStreamReadiness(
            int deviceType, int connectionType, int vendorId, int productId,
            int outputType, bool activeStreamSupportsMicrophone,
            int expectedStatus, bool expectedCanEnable)
        {
            ControllerUiCapabilities capabilities =
                ControllerUiCapabilities.For((InputDeviceType)deviceType,
                    (ConnectionType)connectionType, vendorId, productId);

            ControllerMicrophoneUiState state =
                capabilities.GetMicrophoneUiState((OutContType)outputType,
                    activeStreamSupportsMicrophone,
                    requireActiveStream: true);

            Assert.AreEqual((ControllerMicrophoneUiStatus)expectedStatus,
                state.Status);
            Assert.AreEqual(expectedCanEnable, state.CanEnable);
            Assert.AreEqual(state.Status != ControllerMicrophoneUiStatus.Ready,
                state.ShowMessage);

            if (!expectedCanEnable)
            {
                Assert.IsTrue(state.CanChange(currentlyEnabled: true),
                    "An unsupported stale setting must remain switch-off-able.");
                Assert.IsFalse(state.CanAdjustLevel(currentlyEnabled: true));
            }
        }

        [TestMethod]
        public void ProfileOutputSelectionPreservesCustomAudioProcessing()
        {
            int device = Global.TEST_PROFILE_INDEX;
            OutContType previousOutput = Global.OutContType[device];
            OutContType previousTemporaryOutput = Global.outDevTypeTemp[device];
            byte previousCompression = Global.DualSenseSpeakerCompression[device];
            byte previousBassBoost = Global.DualSenseSpeakerBassBoost[device];
            byte previousHeadphoneVolume = Global.DualSenseHeadphoneVolume[device];

            try
            {
                Global.OutContType[device] = OutContType.ViiperDS4;
                Global.DualSenseSpeakerCompression[device] =
                    (byte)DualSenseSpeakerCompression.Strong;
                Global.DualSenseSpeakerBassBoost[device] = 6;
                Global.DualSenseHeadphoneVolume[device] = 173;

                int selectedIndex = 1;
                Assert.IsTrue(ProfileSettingsViewModel
                    .ApplyTemporaryOutputControllerSelection(device,
                        ref selectedIndex, 2));

                Assert.AreEqual(OutContType.ViiperDualSense,
                    ProfileSettingsViewModel.GetOutputControllerType(
                        selectedIndex));
                Assert.AreEqual((byte)DualSenseSpeakerCompression.Strong,
                    Global.DualSenseSpeakerCompression[device]);
                Assert.AreEqual((byte)6,
                    Global.DualSenseSpeakerBassBoost[device]);
                Assert.AreEqual((byte)173,
                    Global.DualSenseHeadphoneVolume[device]);
            }
            finally
            {
                Global.OutContType[device] = previousOutput;
                Global.outDevTypeTemp[device] = previousTemporaryOutput;
                Global.DualSenseSpeakerCompression[device] = previousCompression;
                Global.DualSenseSpeakerBassBoost[device] = previousBassBoost;
                Global.DualSenseHeadphoneVolume[device] =
                    previousHeadphoneVolume;
            }
        }
    }
}
