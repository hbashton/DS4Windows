/*
DS4Windows
Copyright (C) 2026  Travis Nickles

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
    public class TriggerFullPullTests
    {
        [DataTestMethod]
        [DataRow(0, false)]
        [DataRow(249, false)]
        [DataRow(250, true)]
        [DataRow(255, true)]
        public void FullPullThresholdAllowsSmallRawInputTolerance(int rawTriggerValue, bool expectedFullPull)
        {
            Assert.AreEqual(expectedFullPull, DS4StateFieldMapping.IsTriggerFullPull((byte)rawTriggerValue));
        }

        [TestMethod]
        public void FieldMappingUsesFullPullThresholdForTriggerButtons()
        {
            DS4State state = new DS4State
            {
                L2Raw = DS4StateFieldMapping.TRIGGER_FULL_PULL_THRESHOLD,
                R2Raw = DS4StateFieldMapping.TRIGGER_FULL_PULL_THRESHOLD - 1
            };

            DS4StateFieldMapping mapping = new DS4StateFieldMapping(
                state,
                new DS4StateExposed(state),
                tp: null);

            Assert.IsTrue(mapping.buttons[(int)DS4Controls.L2FullPull]);
            Assert.IsFalse(mapping.buttons[(int)DS4Controls.R2FullPull]);
        }
    }
}
