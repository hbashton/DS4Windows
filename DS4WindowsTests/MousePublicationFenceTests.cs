using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;
using DS4Windows.DS4Control;

namespace DS4WindowsTests;

/// <summary>
/// Actual mapper publication and replacement boundaries, using memory-only
/// backends. No driver, native injection, controller or live profile is opened.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MousePublicationFenceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly ReaderWriterLockSlim PublicationLock =
        (ReaderWriterLockSlim)typeof(Mapping).GetField("syncStateLock",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [TestMethod]
    public void BackendReplacementWaitsUntilMappedButtonFlushCompletes()
    {
        using var fixture = new Fixture();
        using var flushEntered = new ManualResetEventSlim();
        using var releaseFlush = new ManualResetEventSlim();
        using var replacementEntered = new ManualResetEventSlim();
        var successor = new MemoryBackend();
        bool flushCompleted = false;
        bool flushCompletedBeforeReplacement = false;
        fixture.Original.OnSync = () =>
        {
            flushEntered.Set();
            if (!releaseFlush.Wait(Deadline))
                throw new TimeoutException("Test did not release the buffered flush.");
            flushCompleted = true;
        };

        Mapping.MapClick(0, Mapping.Click.Left);
        Task commit = StartWorker(() => Mapping.Commit(0));
        Task<bool> replacement = null;
        try
        {
            Assert.IsTrue(flushEntered.Wait(Deadline), "Commit must reach the actual backend flush.");
            Assert.AreEqual(1, fixture.Original.DownCount);
            replacement = Task.Factory.StartNew(() => Mapping.ReplaceMouseOutput(() =>
            {
                flushCompletedBeforeReplacement = flushCompleted;
                replacementEntered.Set();
                Global.outputKBMHandler = successor;
                return true;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // Observe a real contending writer, not a sleep-based assumption
            // that the replacement worker has had enough time to run.
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                PublicationLock.WaitingWriteCount > 0 || replacementEntered.IsSet ||
                replacement.IsCompleted, Deadline), "Replacement did not reach the publication boundary.");
            Assert.IsFalse(replacementEntered.IsSet, "Replacement must not reset the backend during its flush.");
            Assert.IsTrue(PublicationLock.WaitingWriteCount > 0);

            releaseFlush.Set();
            Assert.IsTrue(Task.WaitAll(new Task[] { commit, replacement }, Deadline));
            Assert.IsTrue(replacement.Result);
            Assert.IsTrue(flushCompletedBeforeReplacement);
            Assert.AreEqual(1, fixture.Original.SyncCount);
            Assert.AreEqual(1, successor.DownCount, "Replay the held union exactly once on the successor.");
            Assert.AreEqual(1, successor.SyncCount);
            Assert.IsTrue(successor.LeftHeld);

            Mapping.Commit(0); // The next neutral report releases the replayed hold.
            Assert.AreEqual(1, successor.UpCount);
            Assert.IsFalse(successor.LeftHeld);
        }
        finally
        {
            releaseFlush.Set();
            Task[] workers = replacement == null ? new[] { commit } : new Task[] { commit, replacement };
            Assert.IsTrue(Task.WaitAll(workers, Deadline), "Workers must finish before restoring mapper globals.");
        }
    }

    [TestMethod]
    public void ThrowingCommitFlushReleasesPublicationLockForAnotherThread()
    {
        using var fixture = new Fixture();
        var successor = new MemoryBackend();
        fixture.Original.OnSync = () => throw new InvalidOperationException("Synthetic flush failure.");
        try
        {
            Mapping.MapClick(0, Mapping.Click.Left);
            Assert.ThrowsException<InvalidOperationException>(() => Mapping.Commit(0));
            Assert.IsFalse(PublicationLock.IsWriteLockHeld,
                "An exception from the backend must not strand the mapper publication lock.");

            Task<bool> replacement = Task.Factory.StartNew(() => Mapping.ReplaceMouseOutput(() =>
            {
                Global.outputKBMHandler = successor;
                return true;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.IsTrue(replacement.Wait(Deadline), "A different thread must acquire the released boundary.");
            Assert.IsTrue(replacement.Result);
            Assert.AreEqual(1, successor.DownCount);
            Assert.IsTrue(successor.LeftHeld);

            Mapping.Commit(0);
            Assert.AreEqual(1, successor.UpCount);
            Assert.IsFalse(successor.LeftHeld);
            Assert.AreEqual(0, Mapping.globalState.currentClicks.leftCount);
        }
        finally
        {
            // A deliberately regressed implementation can leak the write lock
            // on this test thread. Release only that owned lock so a failing
            // regression does not poison unrelated tests in the same process.
            if (PublicationLock.IsWriteLockHeld) PublicationLock.ExitWriteLock();
        }
    }

    private static Task StartWorker(Action action) => Task.Factory.StartNew(action,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private sealed class Fixture : IDisposable
    {
        private readonly VirtualKBMBase savedHandler = Global.outputKBMHandler;
        private readonly VirtualKBMMapping savedMapping = Global.outputKBMMapping;
        private readonly Mapping.SyntheticState savedGlobal = Mapping.globalState;
        private readonly Mapping.SyntheticState[] savedDevices = Mapping.deviceState;
        internal readonly MemoryBackend Original = new();

        internal Fixture()
        {
            var mapping = new SendInputMapping();
            mapping.PopulateConstants();
            mapping.PopulateMappings();
            Global.outputKBMHandler = Original;
            Global.outputKBMMapping = mapping;
            Mapping.globalState = new Mapping.SyntheticState();
            Mapping.deviceState = Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
                .Select(_ => new Mapping.SyntheticState()).ToArray();
        }

        public void Dispose()
        {
            Global.outputKBMHandler = savedHandler;
            Global.outputKBMMapping = savedMapping;
            Mapping.globalState = savedGlobal;
            Mapping.deviceState = savedDevices;
        }
    }

    private sealed class MemoryBackend : VirtualKBMBase
    {
        internal Action OnSync;
        internal bool LeftHeld;
        internal int DownCount, UpCount, SyncCount;

        public override void PerformMouseButtonEvent(uint button)
        {
            if (button == 2) { LeftHeld = true; DownCount++; }
            else if (button == 4) { LeftHeld = false; UpCount++; }
            else throw new AssertFailedException("Unexpected mouse button in publication fixture.");
        }

        public override void Sync() { SyncCount++; OnSync?.Invoke(); }
        public override bool Connect() => throw new AssertFailedException("No driver connection allowed.");
        public override bool Disconnect() => throw new AssertFailedException("No driver connection allowed.");
        public override void MoveRelativeMouse(int x, int y) => throw new AssertFailedException("Unexpected movement.");
        public override void MoveAbsoluteMouse(double x, double y) => throw new AssertFailedException("Unexpected movement.");
        public override void PerformMouseWheelEvent(int vertical, int horizontal) => throw new AssertFailedException("Unexpected wheel.");
        public override void PerformMouseButtonPress(uint button) => throw new AssertFailedException("Unexpected direct button API.");
        public override void PerformMouseButtonRelease(uint button) => throw new AssertFailedException("Unexpected direct button API.");
        public override void PerformKeyPress(uint key) => throw new AssertFailedException("Unexpected keyboard input.");
        public override void PerformKeyPressAlt(uint key) => throw new AssertFailedException("Unexpected keyboard input.");
        public override void PerformKeyRelease(uint key) => throw new AssertFailedException("Unexpected keyboard input.");
        public override void PerformKeyReleaseAlt(uint key) => throw new AssertFailedException("Unexpected keyboard input.");
        public override string GetDisplayName() => "Memory publication fixture";
        public override string GetFullDisplayName() => GetDisplayName();
        public override string GetIdentifier() => "memory-publication-fixture";
    }
}
