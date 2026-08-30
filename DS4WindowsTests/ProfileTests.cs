/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System.Text;
using System.Xml;
using System.Xml.Serialization;
using DS4Windows;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests
{
    [TestClass]
    public class ProfileTests
    {
        private string defaultProfileXml = string.Empty;

        public ProfileTests()
        {
            #region ProfileXMLString
            //<!-- DS4Windows Configuration Data. 11/30/2023 00:16:38 -->
            //< !--Made with DS4Windows version 3.2.20-- >
            //app_version=""3.2.20"" config_version=""5""
            defaultProfileXml = @"<?xml version=""1.0"" encoding=""utf-8""?>

<DS4Windows>
  <touchToggle>True</touchToggle>
  <idleDisconnectTimeout>0</idleDisconnectTimeout>
  <outputDataToDS4>True</outputDataToDS4>
  <Color>0,0,255</Color>
  <RumbleBoost>100</RumbleBoost>
  <RumbleAutostopTime>0</RumbleAutostopTime>
  <LightbarMode>DS4Win</LightbarMode>
  <ledAsBatteryIndicator>False</ledAsBatteryIndicator>
  <FlashType>0</FlashType>
  <flashBatteryAt>0</flashBatteryAt>
  <touchSensitivity>100</touchSensitivity>
  <LowColor>0,0,0</LowColor>
  <ChargingColor>0,0,0</ChargingColor>
  <FlashColor>0,0,0</FlashColor>
  <touchpadJitterCompensation>True</touchpadJitterCompensation>
  <lowerRCOn>False</lowerRCOn>
  <tapSensitivity>0</tapSensitivity>
  <doubleTap>False</doubleTap>
  <scrollSensitivity>0</scrollSensitivity>
  <LeftTriggerMiddle>0</LeftTriggerMiddle>
  <RightTriggerMiddle>0</RightTriggerMiddle>
  <TouchpadInvert>0</TouchpadInvert>
  <TouchpadClickPassthru>False</TouchpadClickPassthru>
  <L2AntiDeadZone>0</L2AntiDeadZone>
  <R2AntiDeadZone>0</R2AntiDeadZone>
  <L2MaxZone>100</L2MaxZone>
  <R2MaxZone>100</R2MaxZone>
  <L2MaxOutput>100</L2MaxOutput>
  <R2MaxOutput>100</R2MaxOutput>
  <ButtonMouseSensitivity>25</ButtonMouseSensitivity>
  <ButtonMouseOffset>0.008</ButtonMouseOffset>
  <Rainbow>0</Rainbow>
  <MaxSatRainbow>100</MaxSatRainbow>
  <LSDeadZone>10</LSDeadZone>
  <RSDeadZone>10</RSDeadZone>
  <LSAntiDeadZone>20</LSAntiDeadZone>
  <RSAntiDeadZone>20</RSAntiDeadZone>
  <LSMaxZone>100</LSMaxZone>
  <RSMaxZone>100</RSMaxZone>
  <LSVerticalScale>100</LSVerticalScale>
  <RSVerticalScale>100</RSVerticalScale>
  <LSMaxOutput>100</LSMaxOutput>
  <RSMaxOutput>100</RSMaxOutput>
  <LSMaxOutputForce>False</LSMaxOutputForce>
  <RSMaxOutputForce>False</RSMaxOutputForce>
  <LSDeadZoneType>Radial</LSDeadZoneType>
  <RSDeadZoneType>Radial</RSDeadZoneType>
  <LSAxialDeadOptions>
    <DeadZoneX>10</DeadZoneX>
    <DeadZoneY>10</DeadZoneY>
    <MaxZoneX>100</MaxZoneX>
    <MaxZoneY>100</MaxZoneY>
    <AntiDeadZoneX>20</AntiDeadZoneX>
    <AntiDeadZoneY>20</AntiDeadZoneY>
    <MaxOutputX>100</MaxOutputX>
    <MaxOutputY>100</MaxOutputY>
  </LSAxialDeadOptions>
  <RSAxialDeadOptions>
    <DeadZoneX>10</DeadZoneX>
    <DeadZoneY>10</DeadZoneY>
    <MaxZoneX>100</MaxZoneX>
    <MaxZoneY>100</MaxZoneY>
    <AntiDeadZoneX>20</AntiDeadZoneX>
    <AntiDeadZoneY>20</AntiDeadZoneY>
    <MaxOutputX>100</MaxOutputX>
    <MaxOutputY>100</MaxOutputY>
  </RSAxialDeadOptions>
  <LSRotation>8</LSRotation>
  <RSRotation>0</RSRotation>
  <LSFuzz>0</LSFuzz>
  <RSFuzz>0</RSFuzz>
  <LSOuterBindDead>75</LSOuterBindDead>
  <RSOuterBindDead>75</RSOuterBindDead>
  <LSOuterBindInvert>False</LSOuterBindInvert>
  <RSOuterBindInvert>False</RSOuterBindInvert>
  <LSDeltaAccelSettings>
    <Enabled>False</Enabled>
    <Multiplier>4</Multiplier>
    <MaxTravel>0.2</MaxTravel>
    <MinTravel>0.01</MinTravel>
    <EasingDuration>0.2</EasingDuration>
    <MinFactor>1</MinFactor>
  </LSDeltaAccelSettings>
  <RSDeltaAccelSettings>
    <Enabled>False</Enabled>
    <Multiplier>4</Multiplier>
    <MaxTravel>0.2</MaxTravel>
    <MinTravel>0.01</MinTravel>
    <EasingDuration>0.2</EasingDuration>
    <MinFactor>1</MinFactor>
  </RSDeltaAccelSettings>
  <SXDeadZone>0.25</SXDeadZone>
  <SZDeadZone>0.25</SZDeadZone>
  <SXMaxZone>100</SXMaxZone>
  <SZMaxZone>100</SZMaxZone>
  <SXAntiDeadZone>0</SXAntiDeadZone>
  <SZAntiDeadZone>0</SZAntiDeadZone>
  <Sensitivity>1|1|1|1|1|1</Sensitivity>
  <ChargingType>0</ChargingType>
  <MouseAcceleration>False</MouseAcceleration>
  <ButtonMouseVerticalScale>100</ButtonMouseVerticalScale>
  <LaunchProgram />
  <DinputOnly>False</DinputOnly>
  <StartTouchpadOff>False</StartTouchpadOff>
  <TouchpadOutputMode>Controls</TouchpadOutputMode>
  <SATriggers>-1</SATriggers>
  <SATriggerCond>and</SATriggerCond>
  <SASteeringWheelEmulationAxis>None</SASteeringWheelEmulationAxis>
  <SASteeringWheelEmulationRange>360</SASteeringWheelEmulationRange>
  <SASteeringWheelFuzz>0</SASteeringWheelFuzz>
  <SASteeringWheelSmoothingOptions>
    <SASteeringWheelUseSmoothing>False</SASteeringWheelUseSmoothing>
    <SASteeringWheelSmoothMinCutoff>0.1</SASteeringWheelSmoothMinCutoff>
    <SASteeringWheelSmoothBeta>0.1</SASteeringWheelSmoothBeta>
  </SASteeringWheelSmoothingOptions>
  <TouchDisInvTriggers>-1</TouchDisInvTriggers>
  <GyroSensitivity>100</GyroSensitivity>
  <GyroSensVerticalScale>100</GyroSensVerticalScale>
  <GyroInvert>0</GyroInvert>
  <GyroTriggerTurns>True</GyroTriggerTurns>
  <GyroControlsSettings>
    <Triggers>-1</Triggers>
    <TriggerCond>and</TriggerCond>
    <TriggerTurns>True</TriggerTurns>
    <Toggle>False</Toggle>
  </GyroControlsSettings>
  <GyroMouseSmoothingSettings>
    <UseSmoothing>False</UseSmoothing>
    <SmoothingMethod>none</SmoothingMethod>
    <SmoothingWeight>50</SmoothingWeight>
    <SmoothingMinCutoff>1</SmoothingMinCutoff>
    <SmoothingBeta>0.7</SmoothingBeta>
  </GyroMouseSmoothingSettings>
  <GyroMouseHAxis>0</GyroMouseHAxis>
  <GyroMouseDeadZone>10</GyroMouseDeadZone>
  <GyroMouseMinThreshold>1</GyroMouseMinThreshold>
  <GyroMouseToggle>False</GyroMouseToggle>
  <GyroMouseJitterCompensation>True</GyroMouseJitterCompensation>
  <GyroOutputMode>Controls</GyroOutputMode>
  <GyroMouseStickTriggers>-1</GyroMouseStickTriggers>
  <GyroMouseStickTriggerCond>and</GyroMouseStickTriggerCond>
  <GyroMouseStickTriggerTurns>True</GyroMouseStickTriggerTurns>
  <GyroMouseStickHAxis>0</GyroMouseStickHAxis>
  <GyroMouseStickDeadZone>30</GyroMouseStickDeadZone>
  <GyroMouseStickMaxZone>830</GyroMouseStickMaxZone>
  <GyroMouseStickOutputStick>RightStick</GyroMouseStickOutputStick>
  <GyroMouseStickOutputStickAxes>XY</GyroMouseStickOutputStickAxes>
  <GyroMouseStickAntiDeadX>0.4</GyroMouseStickAntiDeadX>
  <GyroMouseStickAntiDeadY>0.4</GyroMouseStickAntiDeadY>
  <GyroMouseStickInvert>0</GyroMouseStickInvert>
  <GyroMouseStickToggle>False</GyroMouseStickToggle>
  <GyroMouseStickMaxOutput>100</GyroMouseStickMaxOutput>
  <GyroMouseStickMaxOutputEnabled>False</GyroMouseStickMaxOutputEnabled>
  <GyroMouseStickVerticalScale>100</GyroMouseStickVerticalScale>
  <GyroMouseStickJitterCompensation>False</GyroMouseStickJitterCompensation>
  <GyroMouseStickSmoothingSettings>
    <UseSmoothing>False</UseSmoothing>
    <SmoothingMethod>none</SmoothingMethod>
    <SmoothingWeight>50</SmoothingWeight>
    <SmoothingMinCutoff>0.4</SmoothingMinCutoff>
    <SmoothingBeta>0.7</SmoothingBeta>
  </GyroMouseStickSmoothingSettings>
  <GyroSwipeSettings>
    <DeadZoneX>80</DeadZoneX>
    <DeadZoneY>80</DeadZoneY>
    <Triggers>-1</Triggers>
    <TriggerCond>and</TriggerCond>
    <TriggerTurns>True</TriggerTurns>
    <XAxis>Yaw</XAxis>
    <DelayTime>0</DelayTime>
  </GyroSwipeSettings>
  <BTPollRate>4</BTPollRate>
  <LSOutputCurveMode>linear</LSOutputCurveMode>
  <LSOutputCurveCustom />
  <RSOutputCurveMode>linear</RSOutputCurveMode>
  <RSOutputCurveCustom />
  <LSSquareStick>False</LSSquareStick>
  <RSSquareStick>False</RSSquareStick>
  <SquareStickRoundness>5</SquareStickRoundness>
  <SquareRStickRoundness>5</SquareRStickRoundness>
  <LSAntiSnapback>False</LSAntiSnapback>
  <RSAntiSnapback>False</RSAntiSnapback>
  <LSAntiSnapbackDelta>135</LSAntiSnapbackDelta>
  <RSAntiSnapbackDelta>135</RSAntiSnapbackDelta>
  <LSAntiSnapbackTimeout>50</LSAntiSnapbackTimeout>
  <RSAntiSnapbackTimeout>50</RSAntiSnapbackTimeout>
  <LSOutputMode>Controls</LSOutputMode>
  <RSOutputMode>Controls</RSOutputMode>
  <LSOutputSettings>
    <FlickStickSettings>
      <RealWorldCalibration>5.3</RealWorldCalibration>
      <FlickThreshold>0.9</FlickThreshold>
      <FlickTime>0.1</FlickTime>
      <MinAngleThreshold>0</MinAngleThreshold>
    </FlickStickSettings>
  </LSOutputSettings>
  <RSOutputSettings>
    <FlickStickSettings>
      <RealWorldCalibration>5.3</RealWorldCalibration>
      <FlickThreshold>0.9</FlickThreshold>
      <FlickTime>0.1</FlickTime>
      <MinAngleThreshold>0</MinAngleThreshold>
    </FlickStickSettings>
  </RSOutputSettings>
  <DualSenseControllerSettings>
    <RumbleSettings>
      <EmulationMode>Accurate</EmulationMode>
      <EnableGenericRumbleRescale>False</EnableGenericRumbleRescale>
      <HapticPowerLevel>0</HapticPowerLevel>
    </RumbleSettings>
  </DualSenseControllerSettings>
  <L2OutputCurveMode>linear</L2OutputCurveMode>
  <L2OutputCurveCustom />
  <L2TwoStageMode>Disabled</L2TwoStageMode>
  <R2TwoStageMode>Disabled</R2TwoStageMode>
  <L2HipFireTime>100</L2HipFireTime>
  <R2HipFireTime>100</R2HipFireTime>
  <L2TriggerEffect>None</L2TriggerEffect>
  <R2TriggerEffect>None</R2TriggerEffect>
  <R2OutputCurveMode>linear</R2OutputCurveMode>
  <R2OutputCurveCustom />
  <SXOutputCurveMode>linear</SXOutputCurveMode>
  <SXOutputCurveCustom />
  <SZOutputCurveMode>linear</SZOutputCurveMode>
  <SZOutputCurveCustom />
  <TrackballMode>False</TrackballMode>
  <TrackballFriction>10</TrackballFriction>
  <TouchRelMouseRotation>0</TouchRelMouseRotation>
  <TouchRelMouseMinThreshold>1</TouchRelMouseMinThreshold>
  <TouchpadAbsMouseSettings>
    <MaxZoneX>90</MaxZoneX>
    <MaxZoneY>90</MaxZoneY>
    <SnapToCenter>False</SnapToCenter>
  </TouchpadAbsMouseSettings>
  <TouchpadMouseStick>
    <DeadZone>0</DeadZone>
    <MaxZone>8</MaxZone>
    <OutputStick>RightStick</OutputStick>
    <OutputStickAxes>XY</OutputStickAxes>
    <AntiDeadX>0.4</AntiDeadX>
    <AntiDeadY>0.4</AntiDeadY>
    <Invert>0</Invert>
    <MaxOutput>100</MaxOutput>
    <MaxOutputEnabled>False</MaxOutputEnabled>
    <VerticalScale>100</VerticalScale>
    <OutputCurve>Linear</OutputCurve>
    <Rotation>0</Rotation>
    <SmoothingSettings>
      <SmoothingMethod>None</SmoothingMethod>
      <SmoothingMinCutoff>0.8</SmoothingMinCutoff>
      <SmoothingBeta>0.7</SmoothingBeta>
    </SmoothingSettings>
  </TouchpadMouseStick>
  <TouchpadButtonMode>Click</TouchpadButtonMode>
  <AbsMouseRegionSettings>
    <AbsWidth>1</AbsWidth>
    <AbsHeight>1</AbsHeight>
    <AbsXCenter>0.5</AbsXCenter>
    <AbsYCenter>0.5</AbsYCenter>
    <AntiRadius>0</AntiRadius>
    <SnapToCenter>True</SnapToCenter>
  </AbsMouseRegionSettings>
  <OutputContDevice>X360</OutputContDevice>
  <ProfileActions>Disconnect Controller</ProfileActions>
  <Control />
  <ShiftControl />
</DS4Windows>";
            #endregion
        }


        [TestMethod]
        public void CheckReadProfile()
        {
            // Test profile reading. Will fail if an XML exception is thrown
            XmlSerializer serializer = new XmlSerializer(typeof(ProfileDTO),
                   ProfileDTO.GetAttributeOverrides());
            using StringReader sr = new StringReader(defaultProfileXml);
            BackingStore tempStore = new BackingStore();
            ProfileDTO dto = serializer.Deserialize(sr) as ProfileDTO;
            dto.DeviceIndex = 0; // Use default slot
            dto.MapTo(tempStore);

            // Check settings
            Assert.AreEqual(OutContType.ViiperX360, dto.OutputContDevice);
            Assert.AreEqual(OutContType.ViiperX360, tempStore.outputDevType[0]);
        }

        [TestMethod]
        public void CheckViiperOutputDeviceTypesRoundTrip()
        {
            OutContType[] outputTypes =
            {
                OutContType.ViiperX360,
                OutContType.ViiperDS4,
                OutContType.ViiperDualSense,
                OutContType.ViiperDualSenseEdge,
                OutContType.ViiperSwitch2Pro,
            };

            foreach (OutContType outputType in outputTypes)
            {
                ProfileDTO source = new ProfileDTO
                {
                    OutputContDevice = outputType,
                };

                ProfileDTO target = new ProfileDTO
                {
                    OutputContDeviceString = source.OutputContDeviceString,
                };

                Assert.AreEqual(outputType, target.OutputContDevice, $"{outputType} should round-trip through profile XML.");
            }
        }

        [TestMethod]
        public void CheckMicrophoneNoiseSuppressionDefaultsAndRoundTrip()
        {
            var store = new BackingStore();
            var dto = new ProfileDTO
            {
                DeviceIndex = 0,
            };

            dto.MapTo(store);
            Assert.AreEqual((byte)DualSenseMicrophoneNoiseSuppression.Balanced,
                store.dualSenseMicrophoneNoiseSuppression[0]);

            store.dualSenseMicrophoneNoiseSuppression[0] =
                (byte)DualSenseMicrophoneNoiseSuppression.Strong;
            dto.MapFrom(store);

            Assert.AreEqual((byte)DualSenseMicrophoneNoiseSuppression.Strong,
                dto.DualSenseControllerSettings.AudioSettingsGroup.MicrophoneNoiseSuppression);

            store.dualSenseMicrophoneNoiseSuppression[0] =
                (byte)DualSenseMicrophoneNoiseSuppression.NvidiaAi;
            dto.MapFrom(store);
            Assert.AreEqual((byte)DualSenseMicrophoneNoiseSuppression.NvidiaAi,
                dto.DualSenseControllerSettings.AudioSettingsGroup.MicrophoneNoiseSuppression);
        }

        [TestMethod]
        public void CheckSpeakerProcessingDefaultsAndRoundTrip()
        {
            var store = new BackingStore();
            var dto = new ProfileDTO
            {
                DeviceIndex = 0,
            };

            dto.MapTo(store);
            Assert.AreEqual((byte)DualSenseSpeakerCompression.Off,
                store.dualSenseSpeakerCompression[0]);
            Assert.AreEqual((byte)0, store.dualSenseSpeakerBassBoost[0]);

            store.dualSenseSpeakerCompression[0] =
                (byte)DualSenseSpeakerCompression.Strong;
            store.dualSenseSpeakerBassBoost[0] = 5;
            dto.MapFrom(store);

            Assert.AreEqual((byte)DualSenseSpeakerCompression.Strong,
                dto.DualSenseControllerSettings.AudioSettingsGroup.SpeakerCompression);
            Assert.AreEqual((byte)5,
                dto.DualSenseControllerSettings.AudioSettingsGroup.SpeakerBassBoost);
        }

        [TestMethod]
        public void CheckMuteButtonInputOutputModeDefaultsAndRoundTrip()
        {
            var store = new BackingStore();
            var dto = new ProfileDTO
            {
                DeviceIndex = 0,
            };

            dto.MapTo(store);
            Assert.IsFalse(store.dualSenseMuteButtonMutesInputOutput[0]);
            Assert.IsFalse(store.dualSenseMuteButtonMutesMicrophone[0]);
            Assert.IsFalse(store.dualSenseMuteButtonMutesSpeaker[0]);
            Assert.IsFalse(store.dualSenseMuteButtonSwitchesProfiles[0]);

            dto.DualSenseMuteButtonMutesInputOutputString = bool.TrueString;
            dto.DualSenseMuteButtonMutesMicrophoneString = bool.TrueString;
            dto.DualSenseMuteButtonMutesSpeakerString = bool.TrueString;
            dto.DualSenseMuteButtonSwitchesProfilesString = bool.TrueString;
            dto.MapTo(store);
            Assert.IsTrue(store.dualSenseMuteButtonMutesInputOutput[0]);
            Assert.IsTrue(store.dualSenseMuteButtonMutesMicrophone[0]);
            Assert.IsTrue(store.dualSenseMuteButtonMutesSpeaker[0]);
            Assert.IsFalse(store.dualSenseMuteButtonSwitchesProfiles[0],
                "Input/output muting must take precedence over profile switching.");

            var roundTrip = new ProfileDTO
            {
                DeviceIndex = 0,
            };
            roundTrip.MapFrom(store);
            Assert.AreEqual(bool.TrueString,
                roundTrip.DualSenseMuteButtonMutesInputOutputString);
            Assert.AreEqual(bool.TrueString,
                roundTrip.DualSenseMuteButtonMutesMicrophoneString);
            Assert.AreEqual(bool.TrueString,
                roundTrip.DualSenseMuteButtonMutesSpeakerString);
            Assert.AreEqual(bool.FalseString,
                roundTrip.DualSenseMuteButtonSwitchesProfilesString);

            var serializer = new XmlSerializer(typeof(ProfileDTO),
                ProfileDTO.GetAttributeOverrides());
            using var writer = new StringWriter();
            serializer.Serialize(writer, roundTrip);
            StringAssert.Contains(writer.ToString(),
                "<DualSenseMuteButtonMutesInputOutput>True</DualSenseMuteButtonMutesInputOutput>");
            StringAssert.Contains(writer.ToString(),
                "<DualSenseMuteButtonMutesMicrophone>True</DualSenseMuteButtonMutesMicrophone>");
            StringAssert.Contains(writer.ToString(),
                "<DualSenseMuteButtonMutesSpeaker>True</DualSenseMuteButtonMutesSpeaker>");
            StringAssert.Contains(writer.ToString(),
                "<DualSenseMuteButtonSwitchesProfiles>False</DualSenseMuteButtonSwitchesProfiles>");
        }

        [TestMethod]
        public void LegacyMuteButtonModesMigrateWithoutLosingProfileChoices()
        {
            var store = new BackingStore();
            var serializer = new XmlSerializer(typeof(ProfileDTO),
                ProfileDTO.GetAttributeOverrides());
            const string legacyMicrophoneXml =
                "<DS4Windows>" +
                "<DualSenseMuteButtonMutesMicrophone>True</DualSenseMuteButtonMutesMicrophone>" +
                "<DualSenseMuteOnProfileName>Muted</DualSenseMuteOnProfileName>" +
                "<DualSenseMuteOffProfileName>Live</DualSenseMuteOffProfileName>" +
                "</DS4Windows>";
            using var legacyMicrophoneReader = new StringReader(
                legacyMicrophoneXml);
            var legacyMicrophoneMode = (ProfileDTO)serializer.Deserialize(
                legacyMicrophoneReader);
            legacyMicrophoneMode.DeviceIndex = 0;

            legacyMicrophoneMode.MapTo(store);

            Assert.IsTrue(store.dualSenseMuteButtonMutesInputOutput[0],
                "The old microphone mode represented the master and mic target together.");
            Assert.IsTrue(store.dualSenseMuteButtonMutesMicrophone[0]);
            Assert.IsFalse(store.dualSenseMuteButtonMutesSpeaker[0]);
            Assert.IsFalse(store.dualSenseMuteButtonSwitchesProfiles[0]);
            Assert.AreEqual("Muted", store.dualSenseMuteOnProfileName[0]);
            Assert.AreEqual("Live", store.dualSenseMuteOffProfileName[0]);

            const string legacySwitchXml =
                "<DS4Windows>" +
                "<DualSenseMuteButtonLightEnabled>True</DualSenseMuteButtonLightEnabled>" +
                "<DualSenseMuteOnProfileName>Muted</DualSenseMuteOnProfileName>" +
                "<DualSenseMuteOffProfileName>Live</DualSenseMuteOffProfileName>" +
                "</DS4Windows>";
            using var legacySwitchReader = new StringReader(legacySwitchXml);
            var legacyProfileSwitch = (ProfileDTO)serializer.Deserialize(
                legacySwitchReader);
            legacyProfileSwitch.DeviceIndex = 0;

            legacyProfileSwitch.MapTo(store);

            Assert.IsFalse(store.dualSenseMuteButtonMutesInputOutput[0]);
            Assert.IsTrue(store.dualSenseMuteButtonSwitchesProfiles[0],
                "Old profile names should opt into the explicit profile-switch mode.");

            legacyProfileSwitch.DualSenseMuteButtonMutesInputOutputString =
                bool.FalseString;
            legacyProfileSwitch.DualSenseMuteButtonSwitchesProfilesString =
                bool.FalseString;
            legacyProfileSwitch.MapTo(store);

            Assert.IsFalse(store.dualSenseMuteButtonSwitchesProfiles[0],
                "An explicit modern off value must not be re-inferred from saved names.");

            var preparedMicrophoneTarget = new ProfileDTO
            {
                DeviceIndex = 0,
                DualSenseMuteButtonMutesInputOutputString = bool.FalseString,
                DualSenseMuteButtonMutesMicrophoneString = bool.TrueString,
            };

            preparedMicrophoneTarget.MapTo(store);

            Assert.IsFalse(store.dualSenseMuteButtonMutesInputOutput[0],
                "A modern profile may prepare a target while its master remains off.");
            Assert.IsTrue(store.dualSenseMuteButtonMutesMicrophone[0]);
        }

        [TestMethod]
        public void LegacyInactiveMuteButtonNamesRemainDisabled()
        {
            var store = new BackingStore();
            var serializer = new XmlSerializer(typeof(ProfileDTO),
                ProfileDTO.GetAttributeOverrides());
            const string legacyInactiveXml =
                "<DS4Windows>" +
                "<DualSenseMuteButtonLightEnabled>False</DualSenseMuteButtonLightEnabled>" +
                "<DualSenseMuteButtonMutesMicrophone>False</DualSenseMuteButtonMutesMicrophone>" +
                "<DualSenseMuteOnProfileName>Muted</DualSenseMuteOnProfileName>" +
                "<DualSenseMuteOffProfileName>Live</DualSenseMuteOffProfileName>" +
                "</DS4Windows>";
            using var reader = new StringReader(legacyInactiveXml);
            var legacyInactive = (ProfileDTO)serializer.Deserialize(reader);
            legacyInactive.DeviceIndex = 0;

            legacyInactive.MapTo(store);

            Assert.IsFalse(store.dualSenseMuteButtonMutesInputOutput[0]);
            Assert.IsFalse(store.dualSenseMuteButtonSwitchesProfiles[0],
                "Saved names from a disabled legacy mode must remain inert after upgrade.");
            Assert.AreEqual("Muted", store.dualSenseMuteOnProfileName[0]);
            Assert.AreEqual("Live", store.dualSenseMuteOffProfileName[0]);
        }

        [TestMethod]
        public void LegacyHeadsetPluggedInEnablesHeadsetOnlyAudio()
        {
            var serializer = new XmlSerializer(
                typeof(DualSenseControllerSettings.AudioSettings));
            const string legacyXml =
                "<AudioSettings><HeadsetPluggedIn>true</HeadsetPluggedIn></AudioSettings>";

            using var reader = new StringReader(legacyXml);
            var settings = (DualSenseControllerSettings.AudioSettings)
                serializer.Deserialize(reader);
            Assert.IsTrue(settings.HeadsetOnlyAudio);

            using var writer = new StringWriter();
            serializer.Serialize(writer, settings);
            StringAssert.Contains(writer.ToString(),
                "<HeadsetOnlyAudio>true</HeadsetOnlyAudio>");
            Assert.IsFalse(writer.ToString().Contains("HeadsetPluggedIn"));
        }

        [TestMethod]
        public void CheckWriteProfile()
        {
            BackingStore tempStore = new BackingStore();
            // Test profile reading. Will fail if an XML exception is thrown
            XmlSerializer serializer = new XmlSerializer(typeof(ProfileDTO),
                   ProfileDTO.GetAttributeOverrides());
            using (StringReader sr = new StringReader(defaultProfileXml))
            {
                ProfileDTO dto = serializer.Deserialize(sr) as ProfileDTO;
                dto.DeviceIndex = 0; // Use default slot
                dto.MapTo(tempStore);
            }

            string testStr = string.Empty;
            serializer = new XmlSerializer(typeof(ProfileDTO),
                ProfileDTO.GetAttributeOverrides());
            using (Utf8StringWriter strWriter = new Utf8StringWriter())
            {
                using XmlWriter xmlWriter = XmlWriter.Create(strWriter,
                    new XmlWriterSettings()
                    {
                        Encoding = Encoding.UTF8,
                        Indent = true,
                    });

                // Write header explicitly
                //xmlWriter.WriteStartDocument();
                //xmlWriter.WriteComment(string.Format(" DS4Windows Configuration Data. {0} ", DateTime.Now));
                //xmlWriter.WriteComment(string.Format(" Made with DS4Windows version {0} ", Global.exeversion));
                xmlWriter.WriteWhitespace("\r\n");
                xmlWriter.WriteWhitespace("\r\n");

                // Write root element and children
                ProfileDTO dto = new ProfileDTO();
                dto.DeviceIndex = 0; // Use default slot
                dto.SerializeAppAttrs = false;
                dto.MapFrom(tempStore);
                // Omit xmlns:xsi and xmlns:xsd from output
                serializer.Serialize(xmlWriter, dto,
                    new XmlSerializerNamespaces(new[] { XmlQualifiedName.Empty }));
                xmlWriter.Flush();
                xmlWriter.Close();

                testStr = strWriter.ToString();
                //Trace.WriteLine("TEST OUTPUT");
                //Trace.WriteLine(testStr);
            }

            Assert.AreEqual(true, !string.IsNullOrEmpty(testStr));
            // The fixture is a legacy X360 profile. Serialization is expected
            // to add newly introduced defaults and migrate its retired output
            // type instead of reproducing the old XML byte-for-byte.
            StringAssert.Contains(testStr,
                "<OutputContDevice>ViiperX360</OutputContDevice>");
            StringAssert.Contains(testStr,
                "<AudioSettings>");
            using StringReader roundTripReader = new StringReader(testStr);
            ProfileDTO roundTrip = (ProfileDTO)serializer.Deserialize(
                roundTripReader);
            roundTrip.DeviceIndex = 0;
            BackingStore roundTripStore = new BackingStore();
            roundTrip.MapTo(roundTripStore);
            Assert.AreEqual(OutContType.ViiperX360,
                roundTripStore.outputDevType[0]);
        }

        [TestMethod]
        public void CheckLegacyPerChannelColorRead()
        {
            // Old profiles store the lightbar colour as separate <Red>/<Green>/<Blue>
            // elements rather than a combined <Color>. Each channel must be parsed
            // from its own element.
            string legacyColorProfileXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<DS4Windows config_version=""5"">
  <Red>10</Red>
  <Green>20</Green>
  <Blue>30</Blue>
</DS4Windows>";

            XmlSerializer serializer = new XmlSerializer(typeof(ProfileDTO),
                ProfileDTO.GetAttributeOverrides());
            using StringReader sr = new StringReader(legacyColorProfileXml);
            ProfileDTO dto = serializer.Deserialize(sr) as ProfileDTO;
            dto.DeviceIndex = 0;
            BackingStore tempStore = new BackingStore();
            dto.MapTo(tempStore);

            DS4Color led = tempStore.lightbarSettingInfo[0].ds4winSettings.m_Led;
            Assert.AreEqual((byte)10, led.red);
            Assert.AreEqual((byte)20, led.green);
            Assert.AreEqual((byte)30, led.blue);
        }
    }
}
