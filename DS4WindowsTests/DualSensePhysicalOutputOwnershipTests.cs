using System.Threading;
using System.Reflection;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSensePhysicalOutputOwnershipTests
    {
        [TestMethod]
        public void CompoundHapticPublicationClaimsOneCoherentSnapshot()
        {
            DualSensePhysicalOutputStateMailbox mailbox = new();
            long claimedVersion = 0;
            Assert.IsTrue(mailbox.TryClaim(ref claimedVersion, out _));

            DS4LightbarState lightbar = new()
            {
                LightBarColor = new DS4Color(17, 31, 47),
                LightBarFlashDurationOn = 5,
                LightBarFlashDurationOff = 9,
            };
            DS4ForceFeedbackState rumble = new()
            {
                RumbleMotorStrengthRightLightFast = 73,
                RumbleMotorStrengthLeftHeavySlow = 191,
            };

            Assert.IsTrue(mailbox.SetHapticState(lightbar, rumble, out _));
            Assert.IsTrue(mailbox.TryClaim(ref claimedVersion,
                out DualSensePhysicalOutputSnapshot claimed));
            Assert.AreEqual(lightbar, claimed.ProfileLightbar);
            Assert.AreEqual(rumble, claimed.RumbleState);
            Assert.AreEqual(1L, claimed.RumbleGeneration);
            Assert.IsFalse(mailbox.TryClaim(ref claimedVersion, out _));
        }

        [TestMethod]
        public void ConcurrentMotorChannelUpdatesPreserveBothChannels()
        {
            for (int iteration = 0; iteration < 256; iteration++)
            {
                DualSensePhysicalOutputStateMailbox mailbox = new();
                using Barrier barrier = new(3);
                Thread right = new(() =>
                {
                    barrier.SignalAndWait();
                    mailbox.SetRumbleChannel(rightLightFast: true, 0xA5,
                        out _);
                });
                Thread left = new(() =>
                {
                    barrier.SignalAndWait();
                    mailbox.SetRumbleChannel(rightLightFast: false, 0x5A,
                        out _);
                });
                right.Start();
                left.Start();
                barrier.SignalAndWait();
                Assert.IsTrue(right.Join(1000));
                Assert.IsTrue(left.Join(1000));

                DualSensePhysicalOutputSnapshot snapshot =
                    mailbox.ReadLatest();
                Assert.AreEqual((byte)0xA5, snapshot.RumbleState.
                    RumbleMotorStrengthRightLightFast);
                Assert.AreEqual((byte)0x5A, snapshot.RumbleState.
                    RumbleMotorStrengthLeftHeavySlow);
                Assert.AreEqual(2L, snapshot.RumbleGeneration);
            }
        }

        [TestMethod]
        public void ConcurrentCompletePublicationsNeverExposeHybridState()
        {
            DualSensePhysicalOutputStateMailbox mailbox = new();
            DualSensePhysicalOutputSnapshot first =
                DualSensePhysicalOutputSnapshot.Default with
                {
                    HapticPowerLevel = 1,
                    SpeakerVolume = 11,
                    HeadphoneVolume = 12,
                    MicrophoneVolume = 13,
                    LeftTrigger = Trigger(21),
                    RightTrigger = Trigger(22),
                    MuteLedByte = 23,
                    ActivePlayerLedMask = 24,
                    RumbleState = Rumble(25, 26),
                    RumbleGeneration = 27,
                };
            DualSensePhysicalOutputSnapshot second =
                DualSensePhysicalOutputSnapshot.Default with
                {
                    HapticPowerLevel = 101,
                    SpeakerVolume = 111,
                    HeadphoneVolume = 112,
                    MicrophoneVolume = 113,
                    LeftTrigger = Trigger(121),
                    RightTrigger = Trigger(122),
                    MuteLedByte = 123,
                    ActivePlayerLedMask = 124,
                    RumbleState = Rumble(125, 126),
                    RumbleGeneration = 127,
                };
            mailbox.Publish(first);

            using ManualResetEvent start = new(false);
            Thread firstProducer = new(() =>
            {
                start.WaitOne();
                for (int index = 0; index < 20_000; index++)
                {
                    mailbox.Publish(first);
                }
            });
            Thread secondProducer = new(() =>
            {
                start.WaitOne();
                for (int index = 0; index < 20_000; index++)
                {
                    mailbox.Publish(second);
                }
            });
            firstProducer.Start();
            secondProducer.Start();
            start.Set();

            while (firstProducer.IsAlive || secondProducer.IsAlive)
            {
                DualSensePhysicalOutputSnapshot observed =
                    mailbox.ReadLatest();
                Assert.IsTrue(observed.Equals(first) || observed.Equals(second),
                    "The compositor observed fields from different publications.");
            }
            firstProducer.Join();
            secondProducer.Join();
        }

        [TestMethod]
        public void CompoundCompositorPublishAndClaimAllocateZeroAfterWarmup()
        {
            const int iterations = 20_000;
            DualSensePhysicalOutputStateMailbox mailbox = new();
            long claimedVersion = 0;
            DS4LightbarState firstLightbar = new()
            {
                LightBarColor = new DS4Color(1, 2, 3),
            };
            DS4LightbarState secondLightbar = new()
            {
                LightBarColor = new DS4Color(101, 102, 103),
            };
            DS4ForceFeedbackState firstRumble = Rumble(7, 11);
            DS4ForceFeedbackState secondRumble = Rumble(107, 111);

            for (int index = 0; index < 1_000; index++)
            {
                bool first = (index & 1) == 0;
                mailbox.SetHapticState(
                    first ? firstLightbar : secondLightbar,
                    first ? firstRumble : secondRumble, out _);
                mailbox.TryClaim(ref claimedVersion, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < iterations; index++)
            {
                bool first = (index & 1) == 0;
                mailbox.SetHapticState(
                    first ? firstLightbar : secondLightbar,
                    first ? firstRumble : secondRumble, out _);
                mailbox.TryClaim(ref claimedVersion, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated,
                $"Compositor publish/claim allocated {allocated} bytes after warmup.");
        }

        [TestMethod]
        public void NativeOutputBuildIntoAndCombinedCopyAllocateZeroAfterWarmup()
        {
            const int nativeOffset = 28;
            const int nativeLength = 48;
            const int iterations = 20_000;
            byte[] feedback = new byte[nativeOffset + nativeLength];
            byte[] nativeScratch = new byte[nativeLength];
            byte[] combinedCarrier = new byte[512];
            for (int index = 0; index < nativeLength; index++)
            {
                feedback[nativeOffset + index] = (byte)(index + 1);
            }
            feedback[nativeOffset] = 0x02;

            for (int index = 0; index < 1_000; index++)
            {
                ViiperOutDevice.PrepareNativeDualSenseOutputReportForProfileInto(
                    feedback, -1, nativeScratch);
                ViiperOutDevice.CopyPreparedNativeDualSenseStateIntoCombinedCarrier(
                    nativeScratch, combinedCarrier, 33);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < iterations; index++)
            {
                ViiperOutDevice.PrepareNativeDualSenseOutputReportForProfileInto(
                    feedback, -1, nativeScratch);
                ViiperOutDevice.CopyPreparedNativeDualSenseStateIntoCombinedCarrier(
                    nativeScratch, combinedCarrier, 33);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                $"Native output build/copy allocated {allocated} bytes after warmup.");
            CollectionAssert.AreEqual(feedback.Skip(nativeOffset).
                Take(nativeLength).ToArray(), nativeScratch);
            CollectionAssert.AreEqual(nativeScratch.Skip(1).ToArray(),
                combinedCarrier.Skip(33).Take(nativeLength - 1).ToArray());
        }

        [TestMethod]
        public void FeedbackCallbackAdmissionReleasesMonitorAndLifecycleOwnsWait()
        {
            ViiperOutDevice device = new(OutContType.ViiperDualSense,
                ViiperVirtualDeviceType.DualSense);
            SetField(device, "connected", true);
            SetField(device, "feedbackDispatchStopRequested", false);
            SetField(device, "feedbackDispatchGeneration", 7L);
            SetField(device, "streamGeneration", 11L);
            SetField(device, "lastInputDeviceIndex", 2);

            Assert.AreEqual(true, Invoke(device,
                "TryBeginFeedbackDispatchCallback", 7L, 11L, 2, true));
            object admissionLock = GetField(device,
                "feedbackCallbackAdmissionLock");
            bool monitorClaimed = Monitor.TryEnter(admissionLock);
            Assert.IsTrue(monitorClaimed,
                "A callback claim retained the generation admission monitor.");
            if (monitorClaimed)
            {
                Monitor.Exit(admissionLock);
            }

            using ManualResetEvent lifecycleCompleted = new(false);
            Thread lifecycle = new(() =>
            {
                Invoke(device, "WaitForFeedbackDispatchCallbacks");
                lifecycleCompleted.Set();
            });
            lifecycle.Start();
            Assert.IsFalse(lifecycleCompleted.WaitOne(50),
                "Lifecycle retirement did not wait for the active callback.");
            Invoke(device, "EndFeedbackCallback");
            Assert.IsTrue(lifecycleCompleted.WaitOne(1000));
            Assert.IsTrue(lifecycle.Join(1000));
        }

        private static DualSenseDevice.TriggerEffectData Trigger(byte seed) =>
            new()
            {
                triggerMotorMode = seed,
                triggerStartResistance = (byte)(seed + 1),
                triggerEffectForce = (byte)(seed + 2),
                triggerRangeForce = (byte)(seed + 3),
                triggerNearReleaseStrength = (byte)(seed + 4),
                triggerNearMiddleStrength = (byte)(seed + 5),
                triggerPressedStrength = (byte)(seed + 6),
                triggerActuationFrequency = (byte)(seed + 7),
            };

        private static DS4ForceFeedbackState Rumble(byte right, byte left) =>
            new()
            {
                RumbleMotorStrengthRightLightFast = right,
                RumbleMotorStrengthLeftHeavySlow = left,
            };

        private static object Invoke(object target, string name,
            params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, name);
            return method.Invoke(target, arguments);
        }

        private static object GetField(object target, string name)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic |
                    BindingFlags.Public);
                if (field != null)
                {
                    return field.GetValue(target);
                }
                type = type.BaseType;
            }
            Assert.Fail(name);
            return null;
        }

        private static void SetField(object target, string name, object value)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic |
                    BindingFlags.Public);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }
                type = type.BaseType;
            }
            Assert.Fail(name);
        }
    }
}
