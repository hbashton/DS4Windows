using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DS4Windows;
using DS4WinWPF;
using DS4WinWPF.DS4Forms;
using DS4WinWPF.DS4Forms.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public class LogViewModelTests
{
    [TestMethod]
    public void RetainsNewestThousandRowsIncludingTemporaryMessagesInOrder()
    {
        RunSta(() =>
        {
            var model = new LogViewModel();
            int largestNotifiedCount = 0;
            model.LogItems.CollectionChanged += (_, _) =>
                largestNotifiedCount = Math.Max(largestNotifiedCount, model.LogItems.Count);

            for (int i = 0; i < 1250; i++)
                model.AddLogMessage(null, new DebugEventArgs($"Message {i}",
                    warn: i % 3 == 0, temporary: i % 2 == 0));

            Assert.AreEqual(1000, model.LogItems.Count);
            Assert.IsTrue(largestNotifiedCount <= 1000,
                "Even collection observers must never see more than the retained limit.");
            CollectionAssert.AreEqual(Enumerable.Range(250, 1000)
                .Select(i => $"Message {i}").ToArray(),
                model.LogItems.Select(item => item.Message).ToArray());
            Assert.IsTrue(model.LogItems[2].Warning);
        });
    }

    [TestMethod]
    public void ThrowingCollectionSubscriberDoesNotLeaveTheWriteLockHeld()
    {
        RunSta(() =>
        {
            var model = new LogViewModel();
            NotifyCollectionChangedEventHandler throwingHandler = (_, _) =>
                throw new InvalidOperationException("Subscriber failure");
            model.LogItems.CollectionChanged += throwingHandler;
            try
            {
                Assert.ThrowsException<InvalidOperationException>(() =>
                    model.AddLogMessage(null, new DebugEventArgs("First", false)));
                Assert.IsFalse(model.LogListLocker.IsWriteLockHeld,
                    "A failed UI subscriber must not permanently block later logging.");
                model.LogItems.CollectionChanged -= throwingHandler;
                model.AddLogMessage(null, new DebugEventArgs("Second", false));
                Assert.AreEqual("Second", model.LogItems.Last().Message);
            }
            finally
            {
                model.LogItems.CollectionChanged -= throwingHandler;
                // Also clean up the deliberately failing pre-fix implementation.
                if (model.LogListLocker.IsWriteLockHeld)
                    model.LogListLocker.ExitWriteLock();
            }
        });
    }

    [TestMethod]
    public void SnapshotStaysStableAfterClearAndNewMessages()
    {
        RunSta(() =>
        {
            var model = new LogViewModel();
            var original = new DebugEventArgs("Original warning", true, temporary: true);
            model.AddLogMessage(null, original);
            var snapshot = model.Snapshot();

            model.Clear();
            model.AddLogMessage(null, new DebugEventArgs("New message", false));

            Assert.AreEqual(1, snapshot.Count);
            Assert.AreEqual(original.Data, snapshot[0].Message);
            Assert.AreEqual(original.Time, snapshot[0].Datetime);
            Assert.IsTrue(snapshot[0].Warning);
            Assert.AreEqual("New message", model.Snapshot().Single().Message);
        });
    }

    [TestMethod]
    public void BoundCollectionCanBeTrimmedClearedAndRefilledOnUiThread()
    {
        RunSta(() =>
        {
            var model = new LogViewModel();
            var view = CollectionViewSource.GetDefaultView(model.LogItems);
            for (int i = 0; i < 1250; i++)
                model.AddLogMessage(null, new DebugEventArgs($"Message {i}", false));

            Assert.AreEqual(1000, view.Cast<object>().Count());
            model.Clear();
            Assert.IsTrue(view.IsEmpty);
            model.AddLogMessage(null, new DebugEventArgs("After clear", false));
            Assert.AreEqual(1, view.Cast<object>().Count());
            Assert.AreEqual("After clear", model.Snapshot().Single().Message);
        });
    }

    [TestMethod]
    public void BackgroundLogBurstsAndUiClearReachTheBoundViewBeforeVisibleTailScroll()
    {
        RunSta(() =>
        {
            var model = new LogViewModel();
            var list = new ListView { ItemsSource = model.LogItems };
            var scrollCalls = new List<LogItem>();
            var dispatcherFailures = new List<Exception>();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            bool visible = false;
            DispatcherUnhandledExceptionEventHandler captureFailure = (_, args) =>
            {
                dispatcherFailures.Add(args.Exception);
                args.Handled = true;
            };
            dispatcher.UnhandledException += captureFailure;
            // Use the real WPF collection view and dispatcher without opening a
            // window. Visibility/scroll delegates isolate scheduling from layout.
            using var scroller = new LogViewAutoScroller(list, () => visible, item =>
            {
                Assert.IsTrue(dispatcher.CheckAccess());
                scrollCalls.Add((LogItem)item);
            });
            try
            {
                AddBackgroundBurst(model, "Before clear", 10000);
                Assert.AreEqual(1000, model.Snapshot().Count);
                Assert.AreEqual(0, list.Items.Count,
                    "The blocked STA must leave the worker notifications queued in WPF.");
                DrainDispatcher();
                CollectionAssert.AreEqual(model.Snapshot().ToArray(),
                    list.Items.Cast<LogItem>().ToArray());
                Assert.AreEqual(0, scrollCalls.Count);

                // Clear on the UI thread while another worker burst is still
                // queued; old change records must not reappear after the reset.
                AddBackgroundBurst(model, "Stale queued", 250);
                model.Clear();
                Assert.AreEqual(0, model.Snapshot().Count);
                AddBackgroundBurst(model, "After clear", 1500);
                DrainDispatcher();
                LogItem[] retained = model.Snapshot().ToArray();
                Assert.AreEqual(1000, retained.Length);
                Assert.AreEqual("After clear 500", retained[0].Message);
                Assert.AreEqual("After clear 1499", retained[^1].Message);
                CollectionAssert.AreEqual(retained, list.Items.Cast<LogItem>().ToArray());
                Assert.AreEqual(0, scrollCalls.Count,
                    "Producer changes and UI resets must never scroll the hidden log.");

                visible = true;
                for (int i = 0; i < 100; i++) scroller.RequestScroll();
                DrainDispatcher();
                Assert.AreEqual(1, scrollCalls.Count,
                    "Repeated requests before dispatch must coalesce to one visible scroll.");
                Assert.AreSame(retained[^1], scrollCalls.Single());
                Assert.AreSame(retained[^1], list.Items[list.Items.Count - 1]);
                Assert.AreEqual(0, dispatcherFailures.Count,
                    string.Join(Environment.NewLine, dispatcherFailures));
            }
            finally
            {
                scroller.Dispose();
                list.ItemsSource = null;
                dispatcher.UnhandledException -= captureFailure;
            }
        });
    }

    private static void AddBackgroundBurst(LogViewModel model, string prefix, int count)
    {
        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
                model.AddLogMessage(null, new DebugEventArgs($"{prefix} {i}",
                    false, temporary: i % 2 == 0));
        });
        Assert.IsTrue(producer.Wait(TimeSpan.FromSeconds(5)),
            "A background producer must finish while the UI dispatcher is blocked.");
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        // WPF can split collection synchronization into several ContextIdle
        // batches. A lower-priority sentinel also drains their continuations.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [DataTestMethod]
    [DataRow(NotifyCollectionChangedAction.Remove)]
    [DataRow(NotifyCollectionChangedAction.Reset)]
    public void ThrowingEvictionOrClearSubscriberReleasesTheWriteLock(
        NotifyCollectionChangedAction action)
    {
        RunSta(() =>
        {
            var model = new LogViewModel();
            for (int i = 0; i < 1000; i++)
                model.AddLogMessage(null, new DebugEventArgs($"Message {i}", false));
            NotifyCollectionChangedEventHandler throwingHandler = (_, args) =>
            {
                if (args.Action == action)
                    throw new InvalidOperationException("Subscriber failure");
            };
            model.LogItems.CollectionChanged += throwingHandler;
            try
            {
                Assert.ThrowsException<InvalidOperationException>(() =>
                {
                    if (action == NotifyCollectionChangedAction.Reset)
                        model.Clear();
                    else
                        model.AddLogMessage(null, new DebugEventArgs("Evicts oldest", false));
                });
                Assert.IsFalse(model.LogListLocker.IsWriteLockHeld);
            }
            finally
            {
                model.LogItems.CollectionChanged -= throwingHandler;
            }

            model.AddLogMessage(null, new DebugEventArgs("Still works", false));
            Assert.AreEqual("Still works", model.Snapshot().Last().Message);
        });
    }

    [TestMethod]
    public void ConcurrentLoggingSnapshotsAndClearsKeepBoundedOrderedRows()
    {
        RunSta(() =>
        {
            var model = new LogViewModel();
            using var start = new ManualResetEventSlim();
            var workers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 2000; i++)
                    model.AddLogMessage(null, new DebugEventArgs($"{writer}:{i}",
                        false, temporary: i % 2 == 0));
            })).ToList();
            workers.Add(Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 1000; i++)
                {
                    var snapshot = model.Snapshot();
                    Assert.IsTrue(snapshot.Count <= 1000);
                    var previousIndices = new Dictionary<int, int>();
                    foreach (var item in snapshot)
                    {
                        string[] parts = item.Message.Split(':');
                        int writer = int.Parse(parts[0]);
                        int index = int.Parse(parts[1]);
                        if (previousIndices.TryGetValue(writer, out int previous))
                            Assert.IsTrue(index > previous, "A writer's rows were reordered or duplicated.");
                        previousIndices[writer] = index;
                    }
                    if (i % 31 == 0) model.Clear();
                }
            }));
            start.Set();
            Assert.IsTrue(Task.WhenAll(workers).Wait(TimeSpan.FromSeconds(10)),
                "Concurrent logging did not finish.");
            Assert.IsTrue(model.Snapshot().Count <= 1000);
            model.Clear();
            Assert.AreEqual(0, model.Snapshot().Count);
            model.AddLogMessage(null, new DebugEventArgs("After concurrent work", false));
            Assert.AreEqual("After concurrent work", model.Snapshot().Single().Message);
        });
    }

    private static void RunSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "Log test did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
