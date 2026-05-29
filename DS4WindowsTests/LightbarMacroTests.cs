using System.Collections.ObjectModel;
using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
public class LightbarMacroTests
{
    [TestMethod]
    public void CheckCompile()
    {
        var macro = new LightbarMacro(
            true,
            [
                new LightbarMacroElement(new DS4Color(255, 255, 255), 100),
                new LightbarMacroElement(new DS4Color(255, 0, 0), 50),
                new LightbarMacroElement(new DS4Color(0, 128, 0), 1000),
                new LightbarMacroElement(new DS4Color(0, 0, 0), 5)
            ],
            LightbarMacroTrigger.Press,
            true);
        var output = macro.Compile();
        var expectedStr = "True/255,255,255:100;255,0,0:50;0,128,0:1000;0,0,0:5/Press/True";
        Assert.AreEqual(output, expectedStr);

        macro.Active = false;
        output = macro.Compile();
        expectedStr = "False/255,255,255:100;255,0,0:50;0,128,0:1000;0,0,0:5/Press/True";
        Assert.AreEqual(output, expectedStr);

        // shouldn't really happen, if macro array is empty, it will not be serialised
        // if one is detected, the macro will be reset to default the next time it's loaded anyway
        macro.Macro = [];
        output = macro.Compile();
        expectedStr = "False//Press/True";
        Assert.AreEqual(output, expectedStr);

        macro.Trigger = LightbarMacroTrigger.Release;
        output = macro.Compile();
        expectedStr = "False//Release/True";
        Assert.AreEqual(output, expectedStr);

        macro.CancelCurrent = false;
        output = macro.Compile();
        expectedStr = "False//Release/False";
        Assert.AreEqual(output, expectedStr);
    }

    [TestMethod]
    public void CheckParse()
    {
        var incomingString = "True/255,255,255:100;255,0,0:50;0,128,0:1000;0,0,0:5/Press/True";
        var macro = new LightbarMacro(incomingString);

        Assert.AreEqual(macro.Active, true);
        Assert.AreEqual(macro.Trigger, LightbarMacroTrigger.Press);
        var expectedArray = new LightbarMacroElement[]
        {
            new(new DS4Color(255, 255, 255), 100),
            new(new DS4Color(255, 0, 0), 50),
            new(new DS4Color(0, 128, 0), 1000),
            new(new DS4Color(0, 0, 0), 5)
        };
        CollectionAssert.AreEqual(macro.Macro, expectedArray);
        Assert.AreEqual(macro.CancelCurrent, true);

        incomingString = "False/255,0,0:100/Release/False";
        macro.Parse(incomingString);
        Assert.AreEqual(macro.Active, false);
        Assert.AreEqual(macro.Trigger, LightbarMacroTrigger.Release);
        DS4Color color = default;
        _ = DS4Color.TryParse("255,0,0", ref color);
        expectedArray = [new LightbarMacroElement(color, 100)];
        CollectionAssert.AreEqual(macro.Macro, expectedArray);
        Assert.AreEqual(macro.CancelCurrent, false);

        // test pre-3.9.2 strings that didn't have the cancellation parameter (should always have it on)
        incomingString = "False/255,0,0:100/Release";
        macro.Parse(incomingString);
        Assert.AreEqual(macro.Active, false);
        Assert.AreEqual(macro.Trigger, LightbarMacroTrigger.Release);
        CollectionAssert.AreEqual(macro.Macro, expectedArray);
        Assert.AreEqual(macro.CancelCurrent, true);

        // string with no macro elements should always parse to the same output, regardless of other parameters
        incomingString = "True//Release/True";
        macro.Parse(incomingString);
        Assert.AreEqual(macro.Active, false);
        Assert.AreEqual(macro.Trigger, LightbarMacroTrigger.Press);
        expectedArray = [];
        CollectionAssert.AreEqual(macro.Macro, expectedArray);
        Assert.AreEqual(macro.CancelCurrent, false);


    }
}