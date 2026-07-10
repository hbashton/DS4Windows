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


using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class MacroParserTests
    {
        private int[] testMacro = new int[]
        {
            87, // W key down
            330, // Wait period 30 ms
            1090220220, // Change lightbar (90, 220, 220)
            83, // S key down
            330, // Wait period 30 ms
            1000000000, // Reset lightbar
            83, // S key up
            330, // Wait period 30 ms
            87, // W key up
        };

        private MacroParser parser;

        public MacroParserTests()
        {
            Setup();
        }

        private void Setup()
        {
            parser = new MacroParser(testMacro);
            parser.LoadMacro();
        }

        [TestMethod]
        public void CheckNumberSteps()
        {
            List<MacroStep> steps = parser.MacroSteps;
            // Make sure parser interpreted all steps
            Assert.AreEqual(testMacro.Length, steps.Count);
        }

        [TestMethod]
        public void CheckStepTypes()
        {
            List<MacroStep> steps = parser.MacroSteps;

            int waitStep = 1;
            Assert.AreEqual(MacroStep.StepType.Wait, steps[waitStep].ActType);

            int changeLightbarStep = 2;
            Assert.AreEqual(MacroStep.StepOutput.Lightbar, steps[changeLightbarStep].OutputType);
            Assert.AreEqual(MacroStep.StepType.ActDown, steps[changeLightbarStep].ActType);

            int resetLightbarStep = 5;
            Assert.AreEqual(MacroStep.StepOutput.Lightbar, steps[resetLightbarStep].OutputType);
            Assert.AreEqual(MacroStep.StepType.ActUp, steps[resetLightbarStep].ActType);

            int lastStep = testMacro.Length - 1;
            Assert.AreEqual(MacroStep.StepType.ActUp, steps[lastStep].ActType);

            return;
        }

        // A macro covering every ParseStep value band, with predictable output names.
        private static readonly int[] BandMacro = new int[]
        {
            800,        // 0: Wait (value - 300 = 500 ms)
            261,        // 1: A Button down (261 > 255 => Button, first press)
            261,        // 2: A Button up (same value again => release)
            1000000,    // 3: Stop Rumble (exactly 1000000 => reset)
            1255255,    // 4: Rumble down (heavy 255, light 255)
            1000000000, // 5: Reset Lightbar (exactly 1000000000 => reset)
            1100200050, // 6: Lightbar Color 100,200,50
            286,        // 7: Touchpad Click down (286 > 255 => Button)
        };

        [TestMethod]
        public void CheckBandParsingTypesAndNames()
        {
            MacroParser bandParser = new MacroParser(BandMacro);
            bandParser.LoadMacro();
            List<MacroStep> steps = bandParser.MacroSteps;

            Assert.AreEqual(BandMacro.Length, steps.Count);

            // 0: Wait band (value >= 300)
            Assert.AreEqual(MacroStep.StepType.Wait, steps[0].ActType);
            Assert.AreEqual(MacroStep.StepOutput.None, steps[0].OutputType);
            Assert.AreEqual("Wait 500 ms", steps[0].Name);

            // 1/2: Button press then release for the same value (keydown tracking)
            Assert.AreEqual(MacroStep.StepOutput.Button, steps[1].OutputType);
            Assert.AreEqual(MacroStep.StepType.ActDown, steps[1].ActType);
            Assert.AreEqual("A Button", steps[1].Name);
            Assert.AreEqual(MacroStep.StepOutput.Button, steps[2].OutputType);
            Assert.AreEqual(MacroStep.StepType.ActUp, steps[2].ActType);
            Assert.AreEqual("A Button", steps[2].Name);

            // 3: Rumble reset (value == 1000000)
            Assert.AreEqual(MacroStep.StepOutput.Rumble, steps[3].OutputType);
            Assert.AreEqual(MacroStep.StepType.ActUp, steps[3].ActType);
            Assert.AreEqual("Stop Rumble", steps[3].Name);

            // 4: Rumble down (heavy/light decoded from the digits)
            Assert.AreEqual(MacroStep.StepOutput.Rumble, steps[4].OutputType);
            Assert.AreEqual(MacroStep.StepType.ActDown, steps[4].ActType);
            Assert.IsTrue(steps[4].Name.StartsWith("Rumble 255, 255"),
                $"Unexpected rumble name: {steps[4].Name}");
            Assert.IsTrue(steps[4].Name.EndsWith("%)"),
                $"Unexpected rumble name: {steps[4].Name}");

            // 5: Lightbar reset (value == 1000000000)
            Assert.AreEqual(MacroStep.StepOutput.Lightbar, steps[5].OutputType);
            Assert.AreEqual(MacroStep.StepType.ActUp, steps[5].ActType);
            Assert.AreEqual("Reset Lightbar", steps[5].Name);

            // 6: Lightbar color (value > 1000000000, RGB decoded from the digits)
            Assert.AreEqual(MacroStep.StepOutput.Lightbar, steps[6].OutputType);
            Assert.AreEqual(MacroStep.StepType.ActDown, steps[6].ActType);
            Assert.AreEqual("Lightbar Color: 100,200,50", steps[6].Name);

            // 7: Button from the named-input table
            Assert.AreEqual(MacroStep.StepOutput.Button, steps[7].OutputType);
            Assert.AreEqual(MacroStep.StepType.ActDown, steps[7].ActType);
            Assert.AreEqual("Touchpad Click", steps[7].Name);
        }

        [TestMethod]
        public void CheckKeyButtonBoundary()
        {
            // 255 is the last Key value; 256 is the first Button value.
            MacroParser boundaryParser = new MacroParser(new int[] { 255, 256 });
            boundaryParser.LoadMacro();
            List<MacroStep> steps = boundaryParser.MacroSteps;

            Assert.AreEqual(MacroStep.StepOutput.Key, steps[0].OutputType);
            Assert.AreEqual(MacroStep.StepOutput.Button, steps[1].OutputType);
            Assert.AreEqual("Left Mouse Button", steps[1].Name);
        }

        [TestMethod]
        public void CheckWaitBoundary()
        {
            // 300 is the exact wait threshold => 0 ms wait.
            MacroParser waitParser = new MacroParser(new int[] { 300 });
            waitParser.LoadMacro();
            List<MacroStep> steps = waitParser.MacroSteps;

            Assert.AreEqual(MacroStep.StepType.Wait, steps[0].ActType);
            Assert.AreEqual("Wait 0 ms", steps[0].Name);
        }

        [TestMethod]
        public void CheckLoadMacroIsIdempotent()
        {
            MacroParser repeatParser = new MacroParser(new int[] { 800 });
            repeatParser.LoadMacro();
            int firstCount = repeatParser.MacroSteps.Count;
            repeatParser.LoadMacro();

            Assert.AreEqual(firstCount, repeatParser.MacroSteps.Count);
        }

        [TestMethod]
        public void CheckGetMacroStringsMatchesSteps()
        {
            MacroParser stringParser = new MacroParser(BandMacro);
            List<string> names = stringParser.GetMacroStrings();
            List<MacroStep> steps = stringParser.MacroSteps;

            Assert.AreEqual(steps.Count, names.Count);
            for (int i = 0; i < steps.Count; i++)
            {
                Assert.AreEqual(steps[i].Name, names[i]);
            }
        }
    }
}