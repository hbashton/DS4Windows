using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2StickDirectionTapMappingTests
{
    [TestMethod]
    public void ActivePulseUsesCanonicalButtonTriggerAndAxisQueueValues()
    {
        Switch2StickDirectionTapFrame frame = Frame(
            leftTap: Switch2StickScrollSector.Up,
            leftActive: Switch2StickScrollSector.Up);

        Mapping.ControlToXInput button = Mapping.CreateControlToXInput(
            in frame, DS4Controls.LYNeg, DS4Controls.Cross,
            tapEligible: true);
        Assert.IsTrue(button.hasActiveOverride);
        Assert.IsTrue(Mapping.ResolveControlToXInputButtonValue(in button,
            fallback: false));
        Assert.AreEqual(byte.MaxValue,
            Mapping.ResolveControlToXInputTriggerValue(in button,
                fallback: 17));
        Assert.AreEqual((byte)0,
            Mapping.ResolveControlToXInputAxisValue(in button,
                (int)DS4Controls.LXNeg, fallback: 91));
        Assert.AreEqual(byte.MaxValue,
            Mapping.ResolveControlToXInputAxisValue(in button,
                (int)DS4Controls.LXPos, fallback: 91));
    }

    [TestMethod]
    public void InactivePulsePresentsReleasedAndNeutralCanonicalValues()
    {
        Switch2StickDirectionTapFrame frame = Frame(
            leftTap: Switch2StickScrollSector.Up,
            leftActive: Switch2StickScrollSector.None);
        Mapping.ControlToXInput mapping = Mapping.CreateControlToXInput(
            in frame, DS4Controls.LYNeg, DS4Controls.Cross,
            tapEligible: true);

        Assert.IsTrue(mapping.hasActiveOverride);
        Assert.IsFalse(mapping.activeOverride);
        Assert.IsFalse(Mapping.ResolveControlToXInputButtonValue(in mapping,
            fallback: true));
        Assert.AreEqual((byte)0,
            Mapping.ResolveControlToXInputTriggerValue(in mapping,
                fallback: 99));
        Assert.AreEqual((byte)128,
            Mapping.ResolveControlToXInputAxisValue(in mapping,
                (int)DS4Controls.RXPos, fallback: 99));
    }

    [TestMethod]
    public void IneligibleOrHoldDirectionsPreserveExistingMapperValues()
    {
        Switch2StickDirectionTapFrame tapFrame = Frame(
            leftTap: Switch2StickScrollSector.Up,
            leftActive: Switch2StickScrollSector.Up);
        Mapping.ControlToXInput ineligible = Mapping.CreateControlToXInput(
            in tapFrame, DS4Controls.LYNeg, DS4Controls.Cross,
            tapEligible: false);
        Assert.IsFalse(ineligible.hasActiveOverride);
        Assert.IsTrue(Mapping.ResolveControlToXInputButtonValue(in ineligible,
            fallback: true));
        Assert.AreEqual((byte)37,
            Mapping.ResolveControlToXInputTriggerValue(in ineligible,
                fallback: 37));
        Assert.AreEqual((byte)93,
            Mapping.ResolveControlToXInputAxisValue(in ineligible,
                (int)DS4Controls.LXNeg, fallback: 93));

        Switch2StickDirectionTapFrame holdFrame = Frame(
            leftTap: Switch2StickScrollSector.None,
            leftActive: Switch2StickScrollSector.Up);
        Mapping.ControlToXInput hold = Mapping.CreateControlToXInput(
            in holdFrame, DS4Controls.LYNeg, DS4Controls.Cross,
            tapEligible: true);
        Assert.IsFalse(hold.hasActiveOverride);
    }

    private static Switch2StickDirectionTapFrame Frame(
        Switch2StickScrollSector leftTap,
        Switch2StickScrollSector leftActive) => new(
            isValid: true,
            leftTapMask: leftTap,
            rightTapMask: Switch2StickScrollSector.None,
            leftActive: leftActive,
            rightActive: Switch2StickScrollSector.None);
}
