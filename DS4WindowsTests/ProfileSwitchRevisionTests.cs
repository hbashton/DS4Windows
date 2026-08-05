using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class ProfileSwitchRevisionTests
    {
        [TestMethod]
        public void NewProfileRequestInvalidatesOlderTransition()
        {
            long first = Global.BeginProfileSwitchRevision(0);
            long second = Global.BeginProfileSwitchRevision(0);

            Assert.IsFalse(Global.IsCurrentProfileSwitchRevision(0, first));
            Assert.IsTrue(Global.IsCurrentProfileSwitchRevision(0, second));
        }

        [TestMethod]
        public void ControllerRevisionsAreIndependent()
        {
            long controllerOne = Global.BeginProfileSwitchRevision(0);
            long controllerTwo = Global.BeginProfileSwitchRevision(1);
            Global.BeginProfileSwitchRevision(0);

            Assert.IsFalse(Global.IsCurrentProfileSwitchRevision(
                0, controllerOne));
            Assert.IsTrue(Global.IsCurrentProfileSwitchRevision(
                1, controllerTwo));
        }

        [TestMethod]
        public void InvalidControllerCannotOwnTransition()
        {
            Assert.AreEqual(0, Global.BeginProfileSwitchRevision(-1));
            Assert.IsFalse(Global.IsCurrentProfileSwitchRevision(-1, 1));
            Assert.AreEqual(0, Global.BeginProfileSwitchRevision(
                Global.MAX_DS4_CONTROLLER_COUNT));
        }
    }
}
