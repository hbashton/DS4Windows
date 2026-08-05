using DS4Windows.InputDevices;
using DS4WinWPF;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests
{
    [TestClass]
    public class AutoProfileTests
    {
        [TestMethod]
        public void ExactRuleMatchingIsCaseAndSlashInsensitive()
        {
            AutoProfileEntity rule = new(
                @"C:\Games\Slay the Spire 2\SlayTheSpire2.exe",
                "Slay the Spire 2");

            Assert.IsTrue(rule.IsMatch(
                "c:/games/slay the spire 2/slaythespire2.EXE",
                "SLAY THE SPIRE 2"));
        }

        [TestMethod]
        public void PartialRuleMarkersMatchExpectedBoundaries()
        {
            AutoProfileEntity startsWith = new(
                @"^C:\Games\", "^Ghost of");
            AutoProfileEntity contains = new(
                "*Tsushima.exe", "*Director's Cut");
            AutoProfileEntity endsWith = new(
                "Tsushima.exe$", "Gameplay$");

            const string path =
                @"C:\Games\Ghost of Tsushima\Tsushima.exe";
            Assert.IsTrue(startsWith.IsMatch(path,
                "Ghost of Tsushima - Gameplay"));
            Assert.IsTrue(contains.IsMatch(path,
                "Ghost of Tsushima Director's Cut"));
            Assert.IsTrue(endsWith.IsMatch(path,
                "Ghost of Tsushima - Gameplay"));
        }

        [TestMethod]
        public void BlankRuleNeverMatchesEveryApplication()
        {
            AutoProfileEntity rule = new(string.Empty, string.Empty);

            Assert.IsFalse(rule.IsMatch(
                @"C:\Windows\explorer.exe", "Desktop"));
        }

        [TestMethod]
        public void ApplyToAllControllersUsesFirstConfiguredProfile()
        {
            AutoProfileEntity rule = new("*game.exe", string.Empty)
            {
                ApplyToAllControllers = true,
                ProfileNames = new[] { "Game", "Second" },
            };

            Assert.AreEqual("Game", rule.GetProfileNameForController(0));
            Assert.AreEqual("Game", rule.GetProfileNameForController(7));
        }

        [TestMethod]
        public void DeviceSpecificRuleDoesNotLeakAcrossControllerTypes()
        {
            AutoProfileEntity rule = new("*game.exe", string.Empty)
            {
                DeviceOption = AutoProfileDeviceOption.DualSense,
            };

            Assert.IsTrue(rule.IsDeviceMatch(InputDeviceType.DualSense));
            Assert.IsFalse(rule.IsDeviceMatch(InputDeviceType.DS4));
            Assert.IsFalse(rule.IsDeviceMatch(InputDeviceType.SwitchPro));
        }
    }
}
