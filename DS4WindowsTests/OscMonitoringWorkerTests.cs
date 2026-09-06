using System.Diagnostics;
using System.Threading;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class OscMonitoringWorkerTests
    {
        [TestMethod]
        public void BlockingSenderNeverBlocksPublisherAndPendingStateIsLatest()
        {
            using ManualResetEvent callbackEntered = new(false);
            using ManualResetEvent releaseCallback = new(false);
            using ManualResetEvent latestObserved = new(false);
            int callbackCount = 0;
            byte lastValue = 0;
            using OscMonitoringWorker worker = new(1,
                (index, previous, current) =>
                {
                    int count = Interlocked.Increment(ref callbackCount);
                    if (count == 1)
                    {
                        callbackEntered.Set();
                        releaseCallback.WaitOne();
                    }
                    lastValue = current.LX;
                    if (current.LX == 200)
                    {
                        latestObserved.Set();
                    }
                });
            worker.Resume();

            DS4State previous = new();
            DS4State current = new() { LX = 129 };
            Assert.IsTrue(worker.Publish(0, previous, current));
            Assert.IsTrue(callbackEntered.WaitOne(1000));

            Stopwatch publishTime = Stopwatch.StartNew();
            for (byte value = 130; value <= 200; value++)
            {
                current.LX = value;
                Assert.IsTrue(worker.Publish(0, previous, current));
            }
            publishTime.Stop();
            Assert.IsTrue(publishTime.ElapsedMilliseconds < 100,
                $"OSC publication blocked for {publishTime.ElapsedMilliseconds}ms");
            Assert.IsTrue(worker.ReplacementCount > 0);

            releaseCallback.Set();
            Assert.IsTrue(latestObserved.WaitOne(1000));
            worker.Pause();
            Assert.AreEqual(200, lastValue);
            Assert.AreEqual(2, callbackCount,
                "A blocked callback must collapse pending monitoring state to one latest snapshot.");
        }

        [TestMethod]
        public void AsyncMonitoringOwnersDisposeIdempotently()
        {
            OscMonitoringWorker osc = new(1, (_, _, _) => { });
            osc.Resume();
            osc.Dispose();
            osc.Dispose();
            Assert.IsFalse(osc.IsAlive);

            ReportDiagnosticsWorker diagnostics = new(1, _ => { });
            diagnostics.Resume();
            diagnostics.Dispose();
            diagnostics.Dispose();
            Assert.IsTrue(SpinWait.SpinUntil(() => !diagnostics.IsAlive, 2_000),
                "Nonblocking diagnostics disposal must eventually retire its worker.");
        }
    }
}
