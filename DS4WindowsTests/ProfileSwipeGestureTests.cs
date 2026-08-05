using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class ProfileSwipeGestureTests
    {
        private static Touch TouchAt(int x, int y, byte id) =>
            new Touch(x, y, id);

        [TestMethod]
        public void TwoFingerCentroidDetectsHorizontalSwipe()
        {
            Touch[] touches =
            {
                TouchAt(730, 330, 1),
                TouchAt(1130, 350, 2),
            };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                700, 300, touches, out int direction);

            Assert.IsTrue(detected);
            Assert.AreEqual(1, direction);
        }

        [TestMethod]
        public void NaturalVerticalDriftDoesNotRejectHorizontalSwipe()
        {
            Touch[] touches =
            {
                TouchAt(810, 390, 1),
                TouchAt(1210, 410, 2),
            };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                750, 300, touches, out int direction);

            Assert.IsTrue(detected);
            Assert.AreEqual(1, direction);
        }

        [TestMethod]
        public void PredominantlyVerticalGestureIsNotProfileSwipe()
        {
            Touch[] touches =
            {
                TouchAt(590, 690, 1),
                TouchAt(990, 710, 2),
            };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                700, 300, touches, out int direction);

            Assert.IsFalse(detected);
            Assert.AreEqual(0, direction);
        }

        [TestMethod]
        public void BothContactsContributeToDirection()
        {
            Touch[] touches =
            {
                TouchAt(300, 300, 9),
                TouchAt(700, 300, 3),
            };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                750, 300, touches, out int direction);

            Assert.IsTrue(detected);
            Assert.AreEqual(-1, direction);
        }

        [TestMethod]
        public void OneFingerMotionCannotChangeProfile()
        {
            Touch[] touches = { TouchAt(1200, 300, 1) };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                700, 300, touches, out int direction);

            Assert.IsFalse(detected);
            Assert.AreEqual(0, direction);
        }
    }
}
