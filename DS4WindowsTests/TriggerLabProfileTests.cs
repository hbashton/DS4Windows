using System.IO;
using System.Xml.Serialization;
using DS4Windows;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests
{
    [TestClass]
    public class TriggerLabProfileTests
    {
        [TestMethod]
        public void DefaultWeaponEffectMatchesDualSenseProtocol()
        {
            TriggerLabEffectEncoder.Effect effect = TriggerLabEffectEncoder.Encode(
                new TriggerLabEffectSettings(), true);

            Assert.AreEqual(0x25, effect.Mode);
            Assert.AreEqual(0x44, effect.ZoneMaskLow);
            Assert.AreEqual(0x00, effect.ZoneMaskHigh);
            Assert.AreEqual(0x06, effect.Data0);
            Assert.AreEqual(0x00, effect.Frequency);
        }

        [TestMethod]
        public void InactiveEffectWritesACompleteOffBlock()
        {
            byte[] report = Enumerable.Repeat((byte)0xFF, 32).ToArray();

            TriggerLabEffectEncoder.WriteNativeBlock(report, 7,
                new TriggerLabEffectSettings(), false);

            Assert.AreEqual(0x05, report[7]);
            CollectionAssert.AreEqual(new byte[10], report.Skip(8).Take(10).ToArray());
            Assert.AreEqual(0xFF, report[6]);
            Assert.AreEqual(0xFF, report[18]);
        }

        [TestMethod]
        public void TriggerLabStateRoundTripsInsideProfileXml()
        {
            ProfileDTO original = new ProfileDTO
            {
                TriggerLabSettings = new TriggerLabProfileSettings
                {
                    Enabled = true,
                    Linked = false,
                    LeftActive = true,
                    RightActive = true,
                    LeftGameRumbleVibration = true,
                    RightGameRumbleVibration = false,
                    HasSplitState = true,
                    SplitLeftActive = true,
                    SplitRightActive = false,
                    Left = new TriggerLabEffectSettings
                    {
                        ProfileId = "custom-left",
                        Mode = TriggerLabMode.Feedback,
                        StartPercent = 35,
                        WallPercent = 55,
                        ForcePercent = 70,
                    },
                    Right = new TriggerLabEffectSettings
                    {
                        ProfileId = "custom-right",
                        Mode = TriggerLabMode.Vibration,
                        StartPercent = 15,
                        WallPercent = 80,
                        ForcePercent = 90,
                    },
                    SplitLeft = new TriggerLabEffectSettings
                    {
                        ProfileId = "custom-left",
                        Mode = TriggerLabMode.Feedback,
                        StartPercent = 35,
                        WallPercent = 55,
                        ForcePercent = 70,
                    },
                    SplitRight = new TriggerLabEffectSettings
                    {
                        ProfileId = "custom-right",
                        Mode = TriggerLabMode.Vibration,
                        StartPercent = 15,
                        WallPercent = 80,
                        ForcePercent = 90,
                    },
                    CustomProfiles =
                    {
                        new TriggerLabCustomProfile
                        {
                            Id = "custom-left",
                            Name = "Left wall",
                            Mode = TriggerLabMode.Feedback,
                            StartPercent = 35,
                            WallPercent = 55,
                            ForcePercent = 70,
                            Active = true,
                        },
                        new TriggerLabCustomProfile
                        {
                            Id = "custom-right",
                            Name = "Right pulse",
                            Mode = TriggerLabMode.Vibration,
                            StartPercent = 15,
                            WallPercent = 80,
                            ForcePercent = 90,
                            Active = true,
                        },
                    },
                },
            };
            XmlSerializer serializer = new XmlSerializer(typeof(ProfileDTO),
                ProfileDTO.GetAttributeOverrides());

            string xml;
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, original);
                xml = writer.ToString();
            }

            ProfileDTO restored;
            using (StringReader reader = new StringReader(xml))
            {
                restored = (ProfileDTO)serializer.Deserialize(reader);
            }

            Assert.IsTrue(xml.Contains("<TriggerLab>"));
            Assert.IsTrue(restored.TriggerLabSettings.Enabled);
            Assert.IsFalse(restored.TriggerLabSettings.Linked);
            Assert.IsTrue(restored.TriggerLabSettings.LeftGameRumbleVibration);
            Assert.IsFalse(restored.TriggerLabSettings.RightGameRumbleVibration);
            Assert.AreEqual(TriggerLabMode.Feedback,
                restored.TriggerLabSettings.Left.Mode);
            Assert.AreEqual(TriggerLabMode.Vibration,
                restored.TriggerLabSettings.Right.Mode);
            Assert.IsTrue(restored.TriggerLabSettings.HasSplitState);
            Assert.IsTrue(restored.TriggerLabSettings.SplitLeftActive);
            Assert.IsFalse(restored.TriggerLabSettings.SplitRightActive);
            Assert.AreEqual(TriggerLabMode.Vibration,
                restored.TriggerLabSettings.SplitRight.Mode);
            Assert.AreEqual(2, restored.TriggerLabSettings.CustomProfiles.Count);
            Assert.AreEqual("Right pulse",
                restored.TriggerLabSettings.CustomProfiles[1].Name);
        }

        [TestMethod]
        public void CloningPreservesRememberedSplitDesign()
        {
            TriggerLabProfileSettings original = new TriggerLabProfileSettings
            {
                Linked = true,
                HasSplitState = true,
                SplitLeftActive = true,
                SplitRightActive = false,
                SplitLeft = new TriggerLabEffectSettings
                {
                    ProfileId = "custom",
                    Mode = TriggerLabMode.Feedback,
                    ForcePercent = 65,
                },
                SplitRight = new TriggerLabEffectSettings
                {
                    ProfileId = "custom-right",
                    Mode = TriggerLabMode.Vibration,
                    ForcePercent = 45,
                },
                CustomProfiles =
                {
                    new TriggerLabCustomProfile
                    {
                        Id = "custom",
                        Name = "Custom",
                    },
                    new TriggerLabCustomProfile
                    {
                        Id = "custom-right",
                        Name = "Right",
                    },
                },
            };

            TriggerLabProfileSettings clone = original.Clone();

            Assert.IsTrue(clone.HasSplitState);
            Assert.IsTrue(clone.SplitLeftActive);
            Assert.IsFalse(clone.SplitRightActive);
            Assert.AreEqual(TriggerLabMode.Vibration, clone.SplitRight.Mode);
            Assert.AreNotSame(original.SplitRight, clone.SplitRight);
        }

        [TestMethod]
        public void GameRumbleVibrationKeepsLabEnabledWithoutPersistentEffect()
        {
            TriggerLabProfileSettings settings = new TriggerLabProfileSettings
            {
                Enabled = true,
                LeftActive = false,
                RightActive = false,
                LeftGameRumbleVibration = true,
            };

            settings.Normalize();
            TriggerLabProfileSettings clone = settings.Clone();

            Assert.IsTrue(settings.Enabled);
            Assert.IsTrue(settings.HasGameRumbleVibration);
            Assert.IsFalse(settings.HasActiveOverride);
            Assert.IsTrue(clone.LeftGameRumbleVibration);
            Assert.IsFalse(clone.RightGameRumbleVibration);
        }

        [TestMethod]
        public void GameRumbleMagnitudeMapsAcrossCompleteTriggerRange()
        {
            Assert.AreEqual(0,
                TriggerLabEffectEncoder.MagnitudeToPercent(0));
            Assert.AreEqual(1,
                TriggerLabEffectEncoder.MagnitudeToPercent(1));
            Assert.AreEqual(50,
                TriggerLabEffectEncoder.MagnitudeToPercent(128));
            Assert.AreEqual(100,
                TriggerLabEffectEncoder.MagnitudeToPercent(byte.MaxValue));

            TriggerLabEffectSettings effect =
                TriggerLabEffectEncoder.CreateGameRumbleVibration(
                    TriggerLabPresetCatalog.Presets[0].CreateEffect(), 128);
            Assert.AreEqual(TriggerLabMode.Vibration, effect.Mode);
            Assert.AreEqual(50, effect.ForcePercent);
        }

        [TestMethod]
        public void XboxSeriesImpulseUsesNativeAdaptiveTriggerVibration()
        {
            TriggerLabEffectEncoder.Effect active =
                TriggerLabEffectEncoder.EncodeImpulseTriggerVibration(173);
            TriggerLabEffectEncoder.Effect stopped =
                TriggerLabEffectEncoder.EncodeImpulseTriggerVibration(0);

            Assert.AreEqual(0x06, active.Mode);
            Assert.AreEqual(15, active.ZoneMaskLow,
                "Mode 0x06 byte 1 is the vibration frequency.");
            Assert.AreEqual(173, active.ZoneMaskHigh,
                "Mode 0x06 byte 2 must preserve the game's magnitude.");
            Assert.AreEqual(0, active.Data0,
                "Impulse vibration starts across the complete trigger pull.");
            Assert.AreEqual(0x05, stopped.Mode);
            Assert.IsTrue(stopped.IsOff);
        }

        [TestMethod]
        public void LinkedEffectsKeepPerTriggerActivationIndependent()
        {
            TriggerLabProfileSettings settings = new TriggerLabProfileSettings
            {
                Enabled = true,
                Linked = true,
                LeftActive = true,
                RightActive = false,
                Left = TriggerLabPresetCatalog.Presets[3].CreateEffect(),
                Right = TriggerLabPresetCatalog.Presets[1].CreateEffect(),
            };

            settings.Normalize();

            Assert.IsTrue(settings.LeftActive);
            Assert.IsFalse(settings.RightActive);
            Assert.AreEqual(settings.Left.ProfileId, settings.Right.ProfileId,
                "Linked should mirror the effect shape.");
        }

        [TestMethod]
        public void LinkedAndSplitModesRoundTripIndependentEffects()
        {
            TriggerLabProfileSettings settings = new TriggerLabProfileSettings
            {
                Enabled = true,
                Linked = false,
                LeftActive = true,
                RightActive = false,
                Left = TriggerLabPresetCatalog.Presets[1].CreateEffect(),
                Right = TriggerLabPresetCatalog.Presets[4].CreateEffect(),
            };
            string leftProfile = settings.Left.ProfileId;
            string rightProfile = settings.Right.ProfileId;

            settings.SetLinkedMode(true);

            Assert.IsTrue(settings.Linked);
            Assert.AreEqual(leftProfile, settings.Left.ProfileId);
            Assert.AreEqual(leftProfile, settings.Right.ProfileId);
            Assert.IsTrue(settings.LeftActive);
            Assert.IsFalse(settings.RightActive,
                "Linking effect design must not enable the other trigger.");

            settings.SetLinkedMode(false);

            Assert.IsFalse(settings.Linked);
            Assert.AreEqual(leftProfile, settings.Left.ProfileId);
            Assert.AreEqual(rightProfile, settings.Right.ProfileId,
                "Split should restore the last independent right-trigger design.");
            Assert.IsTrue(settings.LeftActive);
            Assert.IsFalse(settings.RightActive);
        }

        [TestMethod]
        public void SelectingCurrentLinkedModeDoesNotToggleItOff()
        {
            TriggerLabProfileSettings settings = new TriggerLabProfileSettings
            {
                Linked = true,
                Left = TriggerLabPresetCatalog.Presets[2].CreateEffect(),
            };
            settings.Normalize();

            settings.SetLinkedMode(true);

            Assert.IsTrue(settings.Linked);
            Assert.AreEqual(settings.Left.ProfileId, settings.Right.ProfileId);
            Assert.IsFalse(settings.HasSplitState,
                "Selecting an already-selected mode should be a no-op.");
        }

        [TestMethod]
        public void PresetCatalogProvidesReadyMadeEffectsForEveryMode()
        {
            Assert.IsTrue(TriggerLabPresetCatalog.Presets.Count >= 6);
            Assert.AreEqual(TriggerLabPresetCatalog.Presets.Count,
                TriggerLabPresetCatalog.Presets.Select(preset => preset.Id)
                    .Distinct(StringComparer.Ordinal).Count());

            foreach (TriggerLabMode mode in Enum.GetValues<TriggerLabMode>())
            {
                Assert.IsTrue(TriggerLabPresetCatalog.Presets.Any(
                    preset => preset.Mode == mode),
                    $"No ready-made {mode} effect was provided.");
            }

            foreach (TriggerLabPreset preset in TriggerLabPresetCatalog.Presets)
            {
                Assert.IsTrue(TriggerLabPresetCatalog.TryCreateEffect(
                    preset.Id, out TriggerLabEffectSettings effect));
                Assert.AreEqual(preset.Id, effect.ProfileId);
                Assert.IsTrue(effect.ForcePercent > 0);
            }
        }
    }
}
