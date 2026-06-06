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
    public class StickAxisInvertTests
    {
        [DataTestMethod]
        [DataRow(0, 255)]
        [DataRow(1, 255)]
        [DataRow(127, 129)]
        [DataRow(128, 128)]
        [DataRow(129, 127)]
        [DataRow(255, 1)]
        public void InvertStickAxisPreservesNeutralCenter(int axisValue, int expectedValue)
        {
            Assert.AreEqual((byte)expectedValue, Mapping.InvertStickAxis((byte)axisValue));
        }
    }
}
