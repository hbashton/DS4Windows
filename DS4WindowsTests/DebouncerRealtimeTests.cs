using DS4Windows;
using DS4WinWPF.DS4Control;

namespace DS4WindowsTests
{
    [TestClass]
    public class DebouncerRealtimeTests
    {
        [TestMethod]
        public void EnabledDebouncerPreservesTypedBehaviorAndAllocatesZero()
        {
            Debouncer debouncer = new(TimeSpan.FromMilliseconds(10));
            debouncer.AddDebouncer(nameof(DS4State.Cross));
            debouncer.AddDebouncer(nameof(DS4State.R2Btn));
            DS4State source = new()
            {
                Cross = true,
                R2Btn = true,
                R2 = 211,
                LX = 17,
                ReportTimeStamp = new DateTime(10_000_000),
            };

            DS4State pressed = debouncer.ProcessInput(source);
            Assert.IsTrue(pressed.Cross);
            Assert.IsTrue(pressed.R2Btn);
            Assert.AreEqual(211, pressed.R2);
            Assert.AreEqual(17, pressed.LX);
            source.Cross = false;
            source.R2Btn = false;
            source.ReportTimeStamp = source.ReportTimeStamp.AddMilliseconds(5);
            DS4State held = debouncer.ProcessInput(source);
            Assert.IsTrue(held.Cross);
            Assert.IsTrue(held.R2Btn);
            source.ReportTimeStamp = source.ReportTimeStamp.AddMilliseconds(6);
            DS4State released = debouncer.ProcessInput(source);
            Assert.IsFalse(released.Cross);
            Assert.IsFalse(released.R2Btn);

            for (int index = 0; index < 512; index++)
            {
                source.ReportTimeStamp = source.ReportTimeStamp.AddTicks(1);
                _ = debouncer.ProcessInput(source);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                source.ReportTimeStamp = source.ReportTimeStamp.AddTicks(1);
                _ = debouncer.ProcessInput(source);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
        }
    }
}
