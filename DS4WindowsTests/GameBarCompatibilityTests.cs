using DS4WinWPF.DS4Control.DTOXml;
using System.Xml.Serialization;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class GameBarCompatibilityTests
    {
        [DataTestMethod]
        [DataRow(OutContType.ViiperDualSense)]
        [DataRow(OutContType.ViiperDualSenseEdge)]
        public void UsesTemporaryXInputForNativeDualSenseOutputs(OutContType outputType)
        {
            Assert.IsTrue(ControlService.ShouldUseGameBarControllerCompatibility(
                enabled: true, outputType, dInputOnly: false));
        }

        [DataTestMethod]
        [DataRow(OutContType.None)]
        [DataRow(OutContType.X360)]
        [DataRow(OutContType.DS4)]
        [DataRow(OutContType.ViiperX360)]
        [DataRow(OutContType.ViiperDS4)]
        [DataRow(OutContType.ViiperSwitch2Pro)]
        public void DoesNotCreateCompanionForOtherOutputs(OutContType outputType)
        {
            Assert.IsFalse(ControlService.ShouldUseGameBarControllerCompatibility(
                enabled: true, outputType, dInputOnly: false));
        }

        [TestMethod]
        public void RequiresEnabledVirtualOutputProfile()
        {
            Assert.IsFalse(ControlService.ShouldUseGameBarControllerCompatibility(
                enabled: false, OutContType.ViiperDualSense, dInputOnly: false));
            Assert.IsFalse(ControlService.ShouldUseGameBarControllerCompatibility(
                enabled: true, OutContType.ViiperDualSense, dInputOnly: true));
        }

        [DataTestMethod]
        [DataRow(true, false, false, false, true)]
        [DataRow(true, false, true, false, true)]
        [DataRow(true, true, false, false, true)]
        [DataRow(true, true, true, false, false)]
        [DataRow(false, true, true, true, true)]
        [DataRow(false, true, true, false, false)]
        public void VisibilityLatchChangesOnlyForCompletedSupportedResults(
            bool confirmedVisible, bool probeCompleted, bool supported,
            bool probeVisible, bool expected)
        {
            Assert.AreEqual(expected,
                GameBarIntegration.ResolveGameBarApiVisibility(
                    confirmedVisible, probeCompleted, supported, probeVisible));
        }

        [TestMethod]
        public void ProfileSettingPersistsThroughDtoAndXml()
        {
            var store = new BackingStore();
            var dto = new ProfileDTO
            {
                DeviceIndex = 0,
                GameBarControllerCompatibilityString = bool.TrueString,
            };

            dto.MapTo(store);
            Assert.IsTrue(store.gameBarControllerCompatibility[0]);

            var roundTrip = new ProfileDTO { DeviceIndex = 0 };
            roundTrip.MapFrom(store);
            Assert.AreEqual(bool.TrueString,
                roundTrip.GameBarControllerCompatibilityString);

            var serializer = new XmlSerializer(typeof(ProfileDTO),
                ProfileDTO.GetAttributeOverrides());
            using var writer = new StringWriter();
            serializer.Serialize(writer, roundTrip);
            StringAssert.Contains(writer.ToString(),
                "<GameBarControllerCompatibility>True</GameBarControllerCompatibility>");
        }
    }
}
