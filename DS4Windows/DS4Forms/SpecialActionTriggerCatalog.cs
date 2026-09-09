using System;
using System.Collections.Generic;
using DS4Windows;

namespace DS4WinWPF.DS4Forms;

/// <summary>
/// The regular and unload checklists share these entries. Labels may improve,
/// but tags are the established Actions.xml vocabulary, not display aliases.
/// Profiles are universal: unavailable physical controls simply stay released.
/// </summary>
internal static class SpecialActionTriggerCatalog
{
    internal sealed record Entry(string Tag, DS4Controls Control, string Label,
        string Group, string Detail = null);

    internal static IReadOnlyList<Entry> Entries { get; } = Array.AsReadOnly(new[]
    {
        new Entry("Switch 2 C", DS4Controls.Switch2C, "C button", "Extra buttons", "Switch 2 Pro / Joy-Con 2"),
        new Entry("Bottom Left Paddle", DS4Controls.BLP, "GL / Left back paddle", "Extra buttons", "Switch 2 Pro / DualSense Edge"),
        new Entry("Bottom Right Paddle", DS4Controls.BRP, "GR / Right back paddle", "Extra buttons", "Switch 2 Pro / DualSense Edge"),
        new Entry("Function Left", DS4Controls.FnL, "Fn left", "Extra buttons", "DualSense Edge"),
        new Entry("Function Right", DS4Controls.FnR, "Fn right", "Extra buttons", "DualSense Edge"),
        new Entry("Mute", DS4Controls.Mute, "Mute button", "Extra buttons", "DualSense / DualSense Edge"),
        new Entry("Capture", DS4Controls.Capture, "Capture button", "Extra buttons", "Nintendo controllers"),
        new Entry("Switch 2 Joy-Con Left SL", DS4Controls.Switch2JoyConLeftSL, "Left Joy-Con · SL", "Joy-Con rail buttons", "Joy-Con / Joy-Con 2"),
        new Entry("Switch 2 Joy-Con Left SR", DS4Controls.Switch2JoyConLeftSR, "Left Joy-Con · SR", "Joy-Con rail buttons", "Joy-Con / Joy-Con 2"),
        new Entry("Switch 2 Joy-Con Right SL", DS4Controls.Switch2JoyConRightSL, "Right Joy-Con · SL", "Joy-Con rail buttons", "Joy-Con / Joy-Con 2"),
        new Entry("Switch 2 Joy-Con Right SR", DS4Controls.Switch2JoyConRightSR, "Right Joy-Con · SR", "Joy-Con rail buttons", "Joy-Con / Joy-Con 2"),
        new Entry("Cross", DS4Controls.Cross, "Cross", "Standard buttons"),
        new Entry("Circle", DS4Controls.Circle, "Circle", "Standard buttons"),
        new Entry("Square", DS4Controls.Square, "Square", "Standard buttons"),
        new Entry("Triangle", DS4Controls.Triangle, "Triangle", "Standard buttons"),
        new Entry("Options", DS4Controls.Options, "Options / Menu / +", "Standard buttons"),
        new Entry("Share", DS4Controls.Share, "Share / View / −", "Standard buttons"),
        new Entry("Up", DS4Controls.DpadUp, "D-pad up", "Standard buttons"),
        new Entry("Down", DS4Controls.DpadDown, "D-pad down", "Standard buttons"),
        new Entry("Left", DS4Controls.DpadLeft, "D-pad left", "Standard buttons"),
        new Entry("Right", DS4Controls.DpadRight, "D-pad right", "Standard buttons"),
        new Entry("PS", DS4Controls.PS, "PS / Guide / Home", "Standard buttons"),
        new Entry("L1", DS4Controls.L1, "L1 / LB / L", "Standard buttons"),
        new Entry("R1", DS4Controls.R1, "R1 / RB / R", "Standard buttons"),
        new Entry("L2", DS4Controls.L2, "L2 / LT / ZL", "Standard buttons"),
        new Entry("L2 Full Pull", DS4Controls.L2FullPull, "L2 / LT / ZL full pull", "Standard buttons"),
        new Entry("R2", DS4Controls.R2, "R2 / RT / ZR", "Standard buttons"),
        new Entry("R2 Full Pull", DS4Controls.R2FullPull, "R2 / RT / ZR full pull", "Standard buttons"),
        new Entry("L3", DS4Controls.L3, "Left stick click (L3)", "Standard buttons"),
        new Entry("R3", DS4Controls.R3, "Right stick click (R3)", "Standard buttons"),
        new Entry("SideL", DS4Controls.SideL, "Side L", "Other extra controls"),
        new Entry("SideR", DS4Controls.SideR, "Side R", "Other extra controls"),
        new Entry("Switch 2 Joy-Con Left Paddle 1", DS4Controls.Switch2JoyConLeftPaddle1,
            "Left Joy-Con · L (sideways)", "Other extra controls", "Joy-Con / Joy-Con 2"),
        new Entry("Switch 2 Joy-Con Left Paddle 2", DS4Controls.Switch2JoyConLeftPaddle2,
            "Left Joy-Con · ZL (sideways)", "Other extra controls", "Joy-Con / Joy-Con 2"),
        new Entry("Switch 2 Joy-Con Right Paddle 1", DS4Controls.Switch2JoyConRightPaddle1,
            "Right Joy-Con · R (sideways)", "Other extra controls", "Joy-Con / Joy-Con 2"),
        new Entry("Switch 2 Joy-Con Right Paddle 2", DS4Controls.Switch2JoyConRightPaddle2,
            "Right Joy-Con · ZR (sideways)", "Other extra controls", "Joy-Con / Joy-Con 2"),
        new Entry("Switch 2 Joy-Con Left IR Sensor", DS4Controls.Switch2JoyConLeftIrSensor,
            "Left Joy-Con 2 · sensor contact", "Other extra controls"),
        new Entry("Switch 2 Joy-Con Right IR Sensor", DS4Controls.Switch2JoyConRightIrSensor,
            "Right Joy-Con 2 · sensor contact", "Other extra controls"),
        new Entry("Left Touch", DS4Controls.TouchLeft, "Left touch", "Touchpad"),
        new Entry("Upper Touch", DS4Controls.TouchUpper, "Upper touch", "Touchpad"),
        new Entry("Multitouch", DS4Controls.TouchMulti, "Multitouch", "Touchpad"),
        new Entry("Right Touch", DS4Controls.TouchRight, "Right touch", "Touchpad"),
        new Entry("Touch Started", DS4Controls.TouchStarted, "Touch started", "Touchpad"),
        new Entry("Touch Ended", DS4Controls.TouchEnded, "Touch ended", "Touchpad"),
        new Entry("Left Stick Up", DS4Controls.LYNeg, "Left stick up", "Stick directions"),
        new Entry("Left Stick Down", DS4Controls.LYPos, "Left stick down", "Stick directions"),
        new Entry("Left Stick Left", DS4Controls.LXNeg, "Left stick left", "Stick directions"),
        new Entry("Left Stick Right", DS4Controls.LXPos, "Left stick right", "Stick directions"),
        new Entry("Right Stick Up", DS4Controls.RYNeg, "Right stick up", "Stick directions"),
        new Entry("Right Stick Down", DS4Controls.RYPos, "Right stick down", "Stick directions"),
        new Entry("Right Stick Left", DS4Controls.RXNeg, "Right stick left", "Stick directions"),
        new Entry("Right Stick Right", DS4Controls.RXPos, "Right stick right", "Stick directions"),
        new Entry("Swipe Up", DS4Controls.SwipeUp, "Swipe up", "Swipe and tilt"),
        new Entry("Swipe Down", DS4Controls.SwipeDown, "Swipe down", "Swipe and tilt"),
        new Entry("Swipe Left", DS4Controls.SwipeLeft, "Swipe left", "Swipe and tilt"),
        new Entry("Swipe Right", DS4Controls.SwipeRight, "Swipe right", "Swipe and tilt"),
        new Entry("Tilt Up", DS4Controls.GyroZNeg, "Tilt up", "Swipe and tilt"),
        new Entry("Tilt Down", DS4Controls.GyroZPos, "Tilt down", "Swipe and tilt"),
        new Entry("Tilt Left", DS4Controls.GyroXPos, "Tilt left", "Swipe and tilt"),
        new Entry("Tilt Right", DS4Controls.GyroXNeg, "Tilt right", "Swipe and tilt"),
    });
}
