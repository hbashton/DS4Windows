using DS4Windows;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests
{
    [TestClass]
    public class OutputControllerMigrationTests
    {
        [TestMethod]
        public void RetiredVigemDualShock4AlwaysNormalizesToViiper()
        {
            Assert.AreEqual(OutContType.ViiperDS4,
                OutContType.DS4.Normalize());
            Assert.AreEqual(OutContType.ViiperDS4,
                OutContType.ViiperDS4.Normalize());
        }

        [DataTestMethod]
        [DataRow("DS4")]
        [DataRow("DualShock 4")]
        [DataRow("DualShock4")]
        public void LegacyOutputSlotNamesLoadAsViiperDualShock4(string value)
        {
            var slot = new OutputSlotSerializer
            {
                DeviceTypeString = value,
            };

            Assert.AreEqual(OutContType.ViiperDS4, slot.DeviceType);
            Assert.AreEqual("ViiperDS4", slot.DeviceTypeString);
        }

        [TestMethod]
        public void RetiredVigemValueIsNeverSerializedAgain()
        {
            var slot = new OutputSlotSerializer
            {
                DeviceType = OutContType.DS4,
            };

            Assert.AreEqual("ViiperDS4", slot.DeviceTypeString);
        }
    }
}
