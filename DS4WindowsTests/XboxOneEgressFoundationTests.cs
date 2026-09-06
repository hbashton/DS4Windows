using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class XboxOneEgressFoundationTests
    {
        [TestMethod]
        public void SemanticFrameMatchesViiperGoldenVector()
        {
            XboxOneEgressState state = new(
                XboxOneEgressState.MenuButton |
                XboxOneEgressState.AButton |
                XboxOneEgressState.YButton |
                XboxOneEgressState.DpadUpButton |
                XboxOneEgressState.DpadRightButton |
                XboxOneEgressState.RightBumperButton |
                XboxOneEgressState.LeftStickButton |
                XboxOneEgressState.GuideButton |
                XboxOneEgressState.ShareButton,
                0x0234, 0x03CD, 0x0102, -2, short.MinValue,
                short.MaxValue);
            byte[] wire = new byte[XboxOneEgressState.WireSize];
            Array.Fill(wire, (byte)0xA5);

            state.BuildInto(wire);

            CollectionAssert.AreEqual(new byte[]
            {
                0x01, 0x00, 0x18, 0x00,
                0x65, 0xDA, 0x00, 0x00,
                0x34, 0x02, 0xCD, 0x03,
                0x02, 0x01, 0xFE, 0xFF,
                0x00, 0x80, 0xFF, 0x7F,
                0x00, 0x00, 0x00, 0x00,
            }, wire);
        }

        [TestMethod]
        public void EverySemanticButtonOccupiesExactlyOneBit()
        {
            uint[] buttons =
            {
                XboxOneEgressState.MenuButton,
                XboxOneEgressState.ViewButton,
                XboxOneEgressState.AButton,
                XboxOneEgressState.BButton,
                XboxOneEgressState.XButton,
                XboxOneEgressState.YButton,
                XboxOneEgressState.DpadUpButton,
                XboxOneEgressState.DpadDownButton,
                XboxOneEgressState.DpadLeftButton,
                XboxOneEgressState.DpadRightButton,
                XboxOneEgressState.LeftBumperButton,
                XboxOneEgressState.RightBumperButton,
                XboxOneEgressState.LeftStickButton,
                XboxOneEgressState.RightStickButton,
                XboxOneEgressState.GuideButton,
                XboxOneEgressState.ShareButton,
            };
            uint combined = 0;
            Span<byte> wire = stackalloc byte[XboxOneEgressState.WireSize];
            foreach (uint button in buttons)
            {
                Assert.AreEqual(0u, combined & button,
                    $"Button bit 0x{button:X8} was reused.");
                Assert.AreEqual(1, System.Numerics.BitOperations.PopCount(
                    button));
                combined |= button;

                XboxOneEgressState state = State(buttons: button);
                state.BuildInto(wire);
                Assert.AreEqual(button,
                    BinaryPrimitives.ReadUInt32LittleEndian(wire.Slice(4, 4)));
            }
            Assert.AreEqual(XboxOneEgressState.ValidButtonsMask, combined);
        }

        [TestMethod]
        public void LegacyProjectionPreservesExistingMappingAndDedicatedShare()
        {
            DS4State source = new()
            {
                Options = true,
                Share = true,
                Cross = true,
                Triangle = true,
                DpadUp = true,
                DpadRight = true,
                L1 = true,
                R1 = true,
                L3 = true,
                R3 = true,
                PS = true,
                Capture = true,
                L2 = 1,
                R2 = 255,
                LX = 0,
                LY = 255,
                RX = 128,
                RY = 64,
            };

            XboxOneEgressState projected = XboxOneEgressState.
                FromLegacyMappedState(source, -1);

            uint wantButtons = XboxOneEgressState.MenuButton |
                XboxOneEgressState.ViewButton |
                XboxOneEgressState.AButton |
                XboxOneEgressState.YButton |
                XboxOneEgressState.DpadUpButton |
                XboxOneEgressState.DpadRightButton |
                XboxOneEgressState.LeftBumperButton |
                XboxOneEgressState.RightBumperButton |
                XboxOneEgressState.LeftStickButton |
                XboxOneEgressState.RightStickButton |
                XboxOneEgressState.GuideButton |
                XboxOneEgressState.ShareButton;
            Assert.AreEqual(wantButtons, projected.Buttons);
            Assert.AreEqual((ushort)4, projected.LeftTrigger,
                "The eight-bit compatibility value must expand monotonically.");
            Assert.AreEqual((ushort)1023, projected.RightTrigger);
            Assert.AreEqual(short.MinValue, projected.LeftStickX);
            Assert.AreEqual(short.MinValue, projected.LeftStickY);
            Assert.AreEqual((short)0, projected.RightStickX);
            Assert.AreEqual((short)16383, projected.RightStickY);
        }

        [TestMethod]
        public void ValidationAndExactStorageFailClosed()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                State(buttons: 1u << 16));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                State(leftTrigger: 1024));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                State(rightTrigger: ushort.MaxValue));
            Assert.ThrowsException<ArgumentException>(() =>
                XboxOneEgressState.Neutral.BuildInto(new byte[23]));
            Assert.ThrowsException<ArgumentException>(() =>
                XboxOneEgressState.Neutral.BuildInto(new byte[25]));
        }

        [TestMethod]
        public void GuideShareAndTriggerEdgesAreOrderedButAxesAreContinuous()
        {
            XboxOneEgressState neutral = XboxOneEgressState.Neutral;
            Assert.IsTrue(neutral.HasOrderedTransitionTo(
                State(buttons: XboxOneEgressState.GuideButton)));
            Assert.IsTrue(neutral.HasOrderedTransitionTo(
                State(buttons: XboxOneEgressState.ShareButton)));
            Assert.IsTrue(neutral.HasOrderedTransitionTo(
                State(leftTrigger: 1)));
            Assert.IsTrue(State(leftTrigger: 1).HasOrderedTransitionTo(
                neutral));
            Assert.IsFalse(State(leftTrigger: 1).HasOrderedTransitionTo(
                State(leftTrigger: 1023, lx: short.MaxValue)));
            Assert.IsFalse(neutral.HasOrderedTransitionTo(
                State(lx: short.MinValue, ry: short.MaxValue)));
        }

        [TestMethod]
        public void SchedulerPreservesGuidePressReleaseAndFinalAdmission()
        {
            var scheduler = new XboxOneEgressScheduler(
                maximumOrderedAge: 1_000_000);
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            XboxOneEgressState press = State(
                buttons: XboxOneEgressState.GuideButton);

            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch, press, 10));
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch, XboxOneEgressState.Neutral, 11));
            Assert.IsTrue(scheduler.TryClaim(11,
                out OrderedEgressClaim<XboxOneEgressState> pressClaim));
            Assert.AreEqual(press, pressClaim.State);
            Assert.IsTrue(scheduler.TryAdmit(pressClaim, 11));
            Assert.IsFalse(scheduler.TryAdmit(pressClaim, 11),
                "Final admission must be single-use.");
            Assert.IsTrue(scheduler.Complete(pressClaim,
                OrderedEgressCompletion.Commit));

            Assert.IsTrue(scheduler.TryClaim(11,
                out OrderedEgressClaim<XboxOneEgressState> releaseClaim));
            Assert.IsTrue(releaseClaim.State.IsNeutral);
            Assert.IsTrue(scheduler.TryAdmit(releaseClaim, 11));
            Assert.IsTrue(scheduler.Complete(releaseClaim,
                OrderedEgressCompletion.Commit));
        }

        [TestMethod]
        public void StateIsImmutableAndSerializationAllocatesNothing()
        {
            Type type = typeof(XboxOneEgressState);
            Assert.IsTrue(type.IsValueType);
            Assert.IsTrue(type.IsDefined(typeof(IsReadOnlyAttribute), false));
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.IsTrue(field.IsInitOnly, field.Name);
                Assert.IsFalse(field.FieldType.IsArray, field.Name);
                Assert.IsFalse(field.FieldType.IsClass, field.Name);
            }

            XboxOneEgressState state = State(
                XboxOneEgressState.AButton, 1, 1023, 1, -2, 3, -4);
            Span<byte> wire = stackalloc byte[XboxOneEgressState.WireSize];
            state.BuildInto(wire);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                state.BuildInto(wire);
            }
            Assert.AreEqual(0L,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }

        private static XboxOneEgressState State(uint buttons = 0,
            ushort leftTrigger = 0, ushort rightTrigger = 0, short lx = 0,
            short ly = 0, short rx = 0, short ry = 0) =>
            new(buttons, leftTrigger, rightTrigger, lx, ly, rx, ry);
    }
}
