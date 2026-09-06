using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;
using Fixture = DS4WindowsTests.Switch2ProductionGyroMappingIntegrationTests.Fixture;
using RawSticks = DS4WindowsTests.Switch2ProductionGyroMappingIntegrationTests.RawSticks;

namespace DS4WindowsTests;

// Decoder -> registered runtime/slot -> curve/deadzone and custom mapper ->
// exact broker payload. OS transports, profile persistence and virtual transport
// are not exercised. Expected bit fields below are the broker contracts, not
// output derived by a second invocation of the production encoder.
[TestClass]
[DoNotParallelize]
public sealed class Switch2ProductionInputMatrixTests
{
    private static readonly ViiperVirtualDeviceType[] Targets =
    {
        ViiperVirtualDeviceType.Xbox360, ViiperVirtualDeviceType.XboxOne,
        ViiperVirtualDeviceType.DualShock4, ViiperVirtualDeviceType.DualSense,
        ViiperVirtualDeviceType.DualSenseEdge, ViiperVirtualDeviceType.Switch2Pro
    };

    public static IEnumerable<object[]> Routes()
    {
        // 0 USB Pro; 1 BLE Pro; 2 joined; 3/4 vertical L/R; 5/6 horizontal L/R.
        for (int source = 0; source <= 6; source++)
            foreach (var target in Targets) yield return new object[] { source, target };
    }

    private readonly record struct Button(string Name, uint Left, uint Right,
        uint Xbox360, uint XboxOne, uint Sony, uint Switch, byte Dpad = 0, int Trigger = 0);

    private static readonly Button[] Standard =
    {
        new("West", 0, 1u << 0, 0x4000, 0x10, 0x10, 4),
        new("North", 0, 1u << 1, 0x8000, 0x20, 0x80, 8),
        new("South", 0, 1u << 2, 0x1000, 4, 0x20, 1),
        new("East", 0, 1u << 3, 0x2000, 8, 0x40, 2),
        new("L", 1u << 22, 0, 0x100, 0x400, 0x100, 0x1000),
        new("R", 0, 1u << 6, 0x200, 0x800, 0x200, 0x10),
        new("ZL", 1u << 23, 0, 0, 0, 0x400, 0x2000, Trigger: 1),
        new("ZR", 0, 1u << 7, 0, 0, 0x800, 0x20, Trigger: 2),
        new("Minus", 1u << 8, 0, 0x20, 2, 0x1000, 0x4000),
        new("Plus", 0, 1u << 9, 0x10, 1, 0x2000, 0x40),
        new("Left click", 1u << 11, 0, 0x40, 0x1000, 0x4000, 0x8000),
        new("Right click", 0, 1u << 10, 0x80, 0x2000, 0x8000, 0x80),
        new("Home", 0, 1u << 12, 0x400, 0x4000, 0x10000, 0x10000),
        new("Capture", 1u << 13, 0, 0, 0x8000, 0, 0x20000),
        new("Down", 1u << 16, 0, 2, 0x80, 0, 0x100, 2),
        new("Up", 1u << 17, 0, 1, 0x40, 0, 0x800, 1),
        new("Right", 1u << 18, 0, 8, 0x200, 0, 0x200, 8),
        new("Left", 1u << 19, 0, 4, 0x100, 0, 0x400, 4),
    };

    private static IEnumerable<Button> ButtonsFor(int source)
    {
        if (source < 5)
        {
            foreach (var button in Standard)
                if (source != 3 && source != 4 || source == 3 && button.Left != 0 || source == 4 && button.Right != 0)
                    yield return button;
            yield break;
        }
        bool left = source == 5;
        foreach (var button in Standard)
        {
            int bit = button.Name switch
            {
                "West" => left ? 16 : 0, "North" => left ? 17 : 1,
                "South" => left ? 18 : 2, "East" => left ? 19 : 3,
                "L" => left ? 21 : 5, "R" => left ? 20 : 4,
                "Plus" => left ? 8 : 9, "Left click" => left ? 11 : 10,
                "Home" => left ? 13 : 12, _ => -1
            };
            if (bit >= 0) yield return button with { Left = left ? 1u << bit : 0, Right = left ? 0 : 1u << bit };
        }
    }

    [DataTestMethod]
    [DynamicData(nameof(Routes), DynamicDataSourceType.Method)]
    public void DecodedRegisteredButtonsAndTerminalNeutralEncodeForEveryTarget(int source, ViiperVirtualDeviceType target)
    {
        using var fixture = new Fixture(source, controllerOnly: true);
        foreach (var button in ButtonsFor(source))
        {
            foreach (bool pressed in new[] { false, true, true, false, true, false })
            {
                fixture.PublishSides(pressed ? button.Left : 0, pressed ? button.Right : 0);
                byte[] packet = ViiperStatePacketBuilder.Build(target, fixture.Last.Mapped, -1);
                AssertButtons(packet, target, pressed ? button : default,
                    $"source={source}, target={target}, {button.Name}, pressed={pressed}");
            }
        }
        var last = ButtonsFor(source).Last();
        fixture.PublishSides(last.Left, last.Right);
        fixture.Remove();
        Assert.AreEqual(Switch2RuntimeReportKind.TerminalNeutral, fixture.Last.Kind);
        AssertButtons(ViiperStatePacketBuilder.Build(target, fixture.Last.Mapped, -1), target, default, "terminal");
        Assert.AreEqual(0, fixture.SystemInputCalls);
        Assert.AreEqual(1, fixture.TerminalCalls);
        Assert.AreEqual((byte)128, fixture.Last.Mapped.LX);
        Assert.AreEqual((byte)128, fixture.Last.Mapped.LY);
        Assert.AreEqual((byte)128, fixture.Last.Mapped.RX);
        Assert.AreEqual((byte)128, fixture.Last.Mapped.RY);
    }

    public static IEnumerable<object[]> ExtraRoutes()
    {
        var extras = new (int Source, DS4Controls Control, uint Left, uint Right)[]
        {
            (0, DS4Controls.BLP, 1u << 25, 0), (0, DS4Controls.BRP, 0, 1u << 24),
            (1, DS4Controls.BLP, 1u << 25, 0), (1, DS4Controls.BRP, 0, 1u << 24),
            (0, DS4Controls.Capture, 1u << 13, 0), (1, DS4Controls.Capture, 1u << 13, 0),
            (2, DS4Controls.Capture, 1u << 13, 0), (3, DS4Controls.Capture, 1u << 13, 0),
            (0, DS4Controls.Switch2C, 0, 1u << 14), (1, DS4Controls.Switch2C, 0, 1u << 14),
            (2, DS4Controls.Switch2C, 0, 1u << 14), (4, DS4Controls.Switch2C, 0, 1u << 14),
            (6, DS4Controls.Switch2C, 0, 1u << 14),
            (5, DS4Controls.Switch2JoyConLeftPaddle1, 1u << 22, 0),
            (5, DS4Controls.Switch2JoyConLeftPaddle2, 1u << 23, 0),
            (6, DS4Controls.Switch2JoyConRightPaddle1, 0, 1u << 6),
            (6, DS4Controls.Switch2JoyConRightPaddle2, 0, 1u << 7),
            (2, DS4Controls.Switch2JoyConLeftSL, 1u << 21, 0),
            (2, DS4Controls.Switch2JoyConLeftSR, 1u << 20, 0),
            (2, DS4Controls.Switch2JoyConRightSL, 0, 1u << 5),
            (2, DS4Controls.Switch2JoyConRightSR, 0, 1u << 4),
        };
        foreach (var item in extras) yield return new object[] { item.Source, item.Control, item.Left, item.Right };
    }

    [DataTestMethod]
    [DynamicData(nameof(Routes), DynamicDataSourceType.Method)]
    public void RawStickPrecisionOrientationAndUnusedAxesSurviveMapping(int source, ViiperVirtualDeviceType target)
    {
        using var fixture = new Fixture(source, controllerOnly: true);
        for (int physicalAxis = 0; physicalAxis < 4; physicalAxis++)
        {
            foreach (ushort raw in new ushort[] { 0, 1, 2047, 2048, 2049, 4094, 4095 })
            {
                var input = new RawSticks(2048, 2048, 2048, 2048);
                input = physicalAxis switch
                {
                    0 => input with { LX = raw }, 1 => input with { LY = raw },
                    2 => input with { RX = raw }, _ => input with { RY = raw }
                };
                fixture.PublishSides(0, 0, sticks: input);
                byte[] packet = ViiperStatePacketBuilder.Build(target, fixture.Last.Mapped, -1);
                int mappedAxis = physicalAxis;
                bool invert = (physicalAxis & 1) == 1;
                if ((source is 3 or 5) && physicalAxis >= 2 || (source is 4 or 6) && physicalAxis < 2)
                    mappedAxis = -1;
                else if (source >= 5)
                {
                    mappedAxis = (physicalAxis & 1) == 0 ? 1 : 0;
                    invert = source == 5;
                }
                double unit = (raw - 2048.0) / (raw < 2048 ? 2048 : 2047);
                if (invert) unit = -unit;
                int signed = Round(unit * (unit < 0 ? 32768 : 32767));
                // Calibration's signed-16 projection precedes final wire quantization.
                unit = signed / (signed < 0 ? 32768.0 : 32767.0);
                for (int axis = 0; axis < 4; axis++)
                {
                    double expectedUnit = axis == mappedAxis ? unit : 0;
                    string context = $"source={source}, {target}, raw axis={physicalAxis}, raw={raw}, wire axis={axis}";
                    int expected, actual;
                    if (target is ViiperVirtualDeviceType.Xbox360 or ViiperVirtualDeviceType.XboxOne)
                    {
                        if ((axis & 1) == 1) expectedUnit = -expectedUnit;
                        expected = Round(expectedUnit * (expectedUnit < 0 ? 32768 : 32767));
                        int offset = (target == ViiperVirtualDeviceType.Xbox360 ? 6 : 12) + axis * 2;
                        actual = BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(offset));
                    }
                    else if (target == ViiperVirtualDeviceType.Switch2Pro)
                    {
                        expected = 2048 + Round(expectedUnit * (expectedUnit < 0 ? 2048 : 2047));
                        actual = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4 + axis * 2));
                    }
                    else
                    {
                        expected = Round(expectedUnit * (expectedUnit < 0 ? 128 : 127));
                        actual = unchecked((sbyte)packet[axis]);
                    }
                    Assert.AreEqual(expected, actual, context);
                }
            }
        }
        fixture.Remove();
        Assert.AreEqual(0, fixture.SystemInputCalls);
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    [DataTestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public void ProOnlyRearBitsCannotManufactureJoyConButtons(int source)
    {
        // SDL's combined handler includes these bits, but the separately pinned
        // Switch2Connect btn_states admits physical GL/GR only on Pro. Preserve
        // the existing conservative Joy-Con policy, not guessed extra hardware.
        using var fixture = new Fixture(source, controllerOnly: true);
        foreach (uint raw in new[] { 1u << 24, 1u << 25, 3u << 24, 0u })
        {
            fixture.PublishSides(raw, raw);
            foreach (var target in Targets)
                AssertButtons(ViiperStatePacketBuilder.Build(target, fixture.Last.Mapped, -1),
                    target, default, $"source={source}, {target}, unknown rear bits={raw:X}");
        }
        Assert.AreEqual(0, fixture.SystemInputCalls);
    }

    [DataTestMethod]
    [DynamicData(nameof(ExtraRoutes), DynamicDataSourceType.Method)]
    public void ExtraButtonBindingDoesNotLeakUnmappedNativeButton(int source, DS4Controls control, uint left, uint right)
    {
        using var fixture = new Fixture(source, extraControl: control, controllerOnly: true);
        foreach (bool pressed in new[] { false, true, true, false, true, false })
        {
            fixture.PublishSides(pressed ? left : 0, pressed ? right : 0);
            foreach (var target in Targets)
                AssertButtons(ViiperStatePacketBuilder.Build(target, fixture.Last.Mapped, -1), target,
                    pressed ? Standard[2] : default, $"source={source}, {control}, {target}, pressed={pressed}");
        }
        fixture.PublishSides(left, right);
        fixture.Remove();
        foreach (var target in Targets)
            AssertButtons(ViiperStatePacketBuilder.Build(target, fixture.Last.Mapped, -1), target, default, "terminal");
        Assert.AreEqual(0, fixture.SystemInputCalls);
    }

    private static void AssertButtons(byte[] packet, ViiperVirtualDeviceType target, Button expected, string context)
    {
        uint buttons;
        uint expectedButtons;
        int left = 0, right = 0, maxTrigger = 255;
        switch (target)
        {
            case ViiperVirtualDeviceType.Xbox360:
                buttons = BinaryPrimitives.ReadUInt32LittleEndian(packet);
                expectedButtons = expected.Xbox360;
                left = packet[4]; right = packet[5];
                Assert.AreEqual(20, packet.Length, context);
                break;
            case ViiperVirtualDeviceType.XboxOne:
                Assert.AreEqual((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(packet), context);
                Assert.AreEqual((ushort)24, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)), context);
                buttons = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
                expectedButtons = expected.XboxOne;
                left = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8));
                right = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10));
                maxTrigger = 1023;
                Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20)), context);
                Assert.AreEqual(24, packet.Length, context);
                break;
            case ViiperVirtualDeviceType.Switch2Pro:
                buttons = BinaryPrimitives.ReadUInt32LittleEndian(packet);
                expectedButtons = expected.Switch;
                Assert.AreEqual(24, packet.Length, context);
                break;
            default:
                bool ds4 = target == ViiperVirtualDeviceType.DualShock4;
                buttons = ds4 ? BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)) :
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
                expectedButtons = ds4 && expected.Sony == 0x10000 ? 1u : expected.Sony;
                Assert.AreEqual(expected.Dpad, packet[ds4 ? 6 : 8], context + " D-pad");
                left = packet[ds4 ? 7 : 9]; right = packet[ds4 ? 8 : 10];
                Assert.AreEqual(ds4 ? 31 : 33, packet.Length, context);
                break;
        }
        Assert.AreEqual(expectedButtons, buttons, context + " exact buttons");
        if (target != ViiperVirtualDeviceType.Switch2Pro)
        {
            Assert.AreEqual(expected.Trigger == 1 ? maxTrigger : 0, left, context + " LT");
            Assert.AreEqual(expected.Trigger == 2 ? maxTrigger : 0, right, context + " RT");
        }
    }
}
