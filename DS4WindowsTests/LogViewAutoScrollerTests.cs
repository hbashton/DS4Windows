using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using DS4WinWPF.DS4Forms;

namespace DS4WindowsTests;

[TestClass]
public sealed class LogViewAutoScrollerTests
{
    [TestMethod]
    public void HiddenLogDoesNotScheduleScrollsForProducerBursts()
    {
        OnSta(() =>
        {
            var list = new ListView();
            var calls = new List<object>();
            bool visible = false;
            using var scroller = new LogViewAutoScroller(list, () => visible, calls.Add);
            for (int i = 0; i < 2000; i++) list.Items.Add(i);
            Drain();
            Assert.AreEqual(0, calls.Count);
            visible = true;
            scroller.RequestScroll();
            Drain();
            CollectionAssert.AreEqual(new object[] { 1999 }, calls);
        });
    }

    [TestMethod]
    public void VisibleBurstCoalescesAndUsesTheCurrentViewTail()
    {
        OnSta(() =>
        {
            var list = new ListView();
            var calls = new List<object>();
            using var scroller = new LogViewAutoScroller(list, () => true, calls.Add);
            for (int i = 0; i < 1000; i++) list.Items.Add(i);
            list.Items.Clear();
            list.Items.Add("after clear");
            Drain();
            CollectionAssert.AreEqual(new object[] { "after clear" }, calls);
        });
    }

    [TestMethod]
    public void ClearBeforeDeferredScrollDoesNotIndexAnEmptyView()
    {
        OnSta(() =>
        {
            var list = new ListView();
            var calls = new List<object>();
            using var scroller = new LogViewAutoScroller(list, () => true, calls.Add);
            list.Items.Add("pending");
            list.Items.Clear();
            Drain();
            Assert.AreEqual(0, calls.Count);
        });
    }

    [TestMethod]
    public void HidingBeforeDispatchCancelsPendingScroll()
    {
        OnSta(() =>
        {
            var list = new ListView();
            var calls = new List<object>();
            bool visible = true;
            using var scroller = new LogViewAutoScroller(list, () => visible, calls.Add);
            list.Items.Add("pending");
            visible = false;
            scroller.RequestScroll();
            Drain();
            Assert.AreEqual(0, calls.Count);
        });
    }

    [TestMethod]
    public void DisposeCancelsAndUnsubscribesWithoutTouchingLogItems()
    {
        OnSta(() =>
        {
            var list = new ListView();
            var calls = new List<object>();
            var scroller = new LogViewAutoScroller(list, () => true, calls.Add);
            list.Items.Add("before");
            scroller.Dispose();
            scroller.Dispose();
            list.Items.Add("after");
            Drain();
            Assert.AreEqual(0, calls.Count);
            Assert.AreEqual(2, list.Items.Count);
        });
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void OnSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Log scrolling did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
