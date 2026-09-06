using DS4Windows;
using DS4WinWPF.DS4Control;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class OutputSlotAtomicBindingTests
{
    [TestMethod]
    public void CompetingInputSlotsCannotAdoptTheSameUnboundOutput()
    {
        using var fixture = new Fixture();
        using var ready = new Barrier(2);
        var accepted = new bool[2];
        var produced = new OutputDevice[2];
        Task[] attempts = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            Assert.IsTrue(ready.SignalAndWait(TimeSpan.FromSeconds(5)));
            accepted[index] = fixture.Manager.TryBindExistingUnboundOutput(
                fixture.Candidate, fixture.BoundOutputs, index, $"Input {index}",
                OutContType.ViiperX360, out produced[index]);
        })).ToArray();
        Assert.IsTrue(Task.WaitAll(attempts, TimeSpan.FromSeconds(10)));

        Assert.AreEqual(1, accepted.Count(value => value));
        int winner = accepted[0] ? 0 : 1;
        int loser = 1 - winner;
        Assert.AreSame(fixture.Output, produced[winner]);
        Assert.AreSame(fixture.Output, fixture.BoundOutputs[winner]);
        Assert.IsNull(produced[loser]);
        Assert.IsNull(fixture.BoundOutputs[loser]);
        Assert.AreEqual(winner, fixture.Candidate.InputIndex);
        Assert.AreEqual($"Input {winner}", fixture.Candidate.InputDisplayString);
        Assert.IsTrue(fixture.Manager.IsExactBoundOutput(fixture.Output, winner));
        Assert.IsFalse(fixture.Manager.IsExactBoundOutput(fixture.Output, loser));
        Assert.AreEqual(1, fixture.Output.ConnectCount);
        Assert.AreEqual(0, fixture.Output.DisconnectCount);
    }

    [TestMethod]
    public void BindingObserversSeeCompleteInputAssociation()
    {
        using var fixture = new Fixture();
        bool observed = false;
        fixture.Candidate.CurrentInputBoundChanged += (_, _) =>
        {
            if (fixture.Candidate.CurrentInputBound != OutSlotDevice.InputBound.Bound)
                return;
            observed = true;
            Assert.AreEqual(1, fixture.Candidate.InputIndex);
            Assert.AreEqual("Second input", fixture.Candidate.InputDisplayString);
            Assert.AreSame(fixture.Output, fixture.BoundOutputs[1]);
        };
        Assert.IsTrue(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
            fixture.BoundOutputs, 1, "Second input", OutContType.ViiperX360, out var produced));
        Assert.IsTrue(observed);
        Assert.AreSame(fixture.Output, produced);
    }

    [TestMethod]
    public void WrongTypeForeignSlotAndOccupiedDestinationDoNotMutateBinding()
    {
        using var fixture = new Fixture();
        Assert.IsFalse(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
            fixture.BoundOutputs, 0, "Wrong type", OutContType.ViiperXboxOne, out var produced));
        Assert.IsNull(produced);
        var foreign = new OutSlotDevice(fixture.Candidate.Index);
        foreign.AttachedDevice(fixture.Output, OutContType.ViiperX360, -1, "");
        Assert.IsFalse(fixture.Manager.TryBindExistingUnboundOutput(foreign,
            fixture.BoundOutputs, 0, "Foreign slot", OutContType.ViiperX360, out produced));
        Assert.IsNull(produced);
        var occupant = new FakeOutput();
        fixture.BoundOutputs[0] = occupant;
        Assert.IsFalse(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
            fixture.BoundOutputs, 0, "Occupied", OutContType.ViiperX360, out produced));
        Assert.IsNull(produced);
        Assert.AreSame(occupant, fixture.BoundOutputs[0]);
        Assert.AreEqual(OutSlotDevice.InputBound.Unbound, fixture.Candidate.CurrentInputBound);
        Assert.AreEqual(-1, fixture.Candidate.InputIndex);
        Assert.IsFalse(fixture.Manager.IsExactBoundOutput(fixture.Output, 0));
    }

    [TestMethod]
    public void RetiredOutputCannotBeReboundThroughItsOldCandidate()
    {
        using var fixture = new Fixture();
        fixture.Manager.DeferredRemoval(fixture.Output, -1, fixture.BoundOutputs, true);
        Assert.IsFalse(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
            fixture.BoundOutputs, 0, "Retired", OutContType.ViiperX360, out var produced));
        Assert.IsNull(produced);
        Assert.IsNull(fixture.BoundOutputs[0]);
        Assert.IsFalse(fixture.Manager.IsExactBoundOutput(fixture.Output, 0));
        Assert.AreEqual(1, fixture.Output.DisconnectCount);
    }

    [TestMethod]
    public void InvalidArgumentsRejectWithoutPublishingAnOutput()
    {
        using var fixture = new Fixture();
        Assert.IsFalse(fixture.Manager.TryBindExistingUnboundOutput(null,
            fixture.BoundOutputs, 0, "", OutContType.ViiperX360, out var produced));
        Assert.IsNull(produced);
        Assert.IsFalse(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
            null, 0, "", OutContType.ViiperX360, out produced));
        Assert.IsNull(produced);
        foreach (int index in new[] { -1, fixture.BoundOutputs.Length })
        {
            Assert.IsFalse(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
                fixture.BoundOutputs, index, "", OutContType.ViiperX360, out produced));
            Assert.IsNull(produced);
        }
        Assert.IsFalse(fixture.Manager.IsExactBoundOutput(null, 0));
        Assert.IsFalse(fixture.Manager.IsExactBoundOutput(fixture.Output, -1));
    }

    [TestMethod]
    public void RebindingWaitsUntilPreviousResetAndFeedbackWithdrawalComplete()
    {
        using var fixture = new Fixture();
        Assert.IsTrue(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
            fixture.BoundOutputs, 0, "First", OutContType.ViiperX360, out _));
        using var resetEntered = new ManualResetEventSlim(false);
        using var finishReset = new ManualResetEventSlim(false);
        using var bindStarted = new ManualResetEventSlim(false);
        fixture.Output.OnReset = () =>
        {
            resetEntered.Set();
            Assert.IsTrue(finishReset.Wait(TimeSpan.FromSeconds(5)));
        };
        Task<bool> release = Task.Run(() => fixture.Manager.TryReleaseBoundOutput(
            fixture.Output, fixture.BoundOutputs, 0));
        Task<bool> rebind = null;
        OutputDevice produced = null;
        try
        {
            Assert.IsTrue(resetEntered.Wait(TimeSpan.FromSeconds(5)));
            rebind = Task.Run(() =>
            {
                bindStarted.Set();
                return fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
                    fixture.BoundOutputs, 1, "Second", OutContType.ViiperX360, out produced);
            });
            Assert.IsTrue(bindStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(rebind.IsCompleted);
            Assert.AreEqual(OutSlotDevice.InputBound.Bound, fixture.Candidate.CurrentInputBound);
            Assert.AreSame(fixture.Output, fixture.BoundOutputs[0]);
            Assert.IsNull(fixture.BoundOutputs[1]);
        }
        finally
        {
            finishReset.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
            if (rebind != null) Assert.IsTrue(rebind.Wait(TimeSpan.FromSeconds(5)));
        }
        Assert.IsTrue(release.Result);
        Assert.IsTrue(rebind.Result);
        Assert.AreEqual(1, fixture.Output.ResetCount);
        Assert.AreEqual(1, fixture.Output.FeedbackClearCount);
        Assert.AreSame(fixture.Output, produced);
        Assert.IsNull(fixture.BoundOutputs[0]);
        Assert.AreSame(fixture.Output, fixture.BoundOutputs[1]);
        Assert.IsTrue(fixture.Manager.IsExactBoundOutput(fixture.Output, 1));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedReleaseKeepsPreviousBindingUnavailableForAdoption(bool failFeedbackWithdrawal)
    {
        using var fixture = new Fixture();
        Assert.IsTrue(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
            fixture.BoundOutputs, 0, "First", OutContType.ViiperX360, out _));
        if (failFeedbackWithdrawal)
            fixture.Output.OnFeedbackClear = () => throw new InvalidOperationException("Synthetic feedback failure");
        else
            fixture.Output.OnReset = () => throw new InvalidOperationException("Synthetic reset failure");
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.Manager.TryReleaseBoundOutput(fixture.Output, fixture.BoundOutputs, 0));
            Assert.AreSame(fixture.Output, fixture.BoundOutputs[0]);
            Assert.AreEqual("First", fixture.Candidate.InputDisplayString);
            Assert.IsTrue(fixture.Manager.IsExactBoundOutput(fixture.Output, 0));
            Assert.IsFalse(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
                fixture.BoundOutputs, 1, "Second", OutContType.ViiperX360, out var produced));
            Assert.IsNull(produced);
            Assert.IsNull(fixture.BoundOutputs[1]);
        }
        finally
        {
            fixture.Output.OnReset = null;
            fixture.Output.OnFeedbackClear = null;
        }
    }

    [TestMethod]
    public void WrongInputCannotReleaseAnotherInputsBinding()
    {
        using var fixture = new Fixture();
        Assert.IsTrue(fixture.Manager.TryBindExistingUnboundOutput(fixture.Candidate,
            fixture.BoundOutputs, 0, "First", OutContType.ViiperX360, out _));
        Assert.IsFalse(fixture.Manager.TryReleaseBoundOutput(fixture.Output, fixture.BoundOutputs, 1));
        Assert.AreEqual(0, fixture.Output.ResetCount);
        Assert.AreEqual(0, fixture.Output.FeedbackClearCount);
        Assert.AreSame(fixture.Output, fixture.BoundOutputs[0]);
        Assert.IsTrue(fixture.Manager.IsExactBoundOutput(fixture.Output, 0));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly OutputSlotManager Manager = new();
        internal readonly OutputDevice[] BoundOutputs = new OutputDevice[2];
        internal readonly FakeOutput Output = new();
        internal readonly OutSlotDevice Candidate;

        internal Fixture()
        {
            // Fake output methods never use VIIPER, a driver, or hardware.
            Manager.DeferredPlugin(Output, -1, "", BoundOutputs, OutContType.ViiperX360);
            Candidate = Manager.GetOutSlotDevice(Output);
            Assert.IsNotNull(Candidate);
        }

        public void Dispose()
        {
            if (Manager.GetOutSlotDevice(Output) != null)
                Manager.DeferredRemoval(Output, -1, BoundOutputs, true);
        }
    }

    private sealed class FakeOutput : OutputDevice
    {
        internal int ConnectCount { get; private set; }
        internal int DisconnectCount { get; private set; }
        internal int ResetCount { get; private set; }
        internal int FeedbackClearCount { get; private set; }
        internal Action OnReset { get; set; }
        internal Action OnFeedbackClear { get; set; }
        public override void Connect() { ConnectCount++; connected = true; }
        public override void Disconnect() { DisconnectCount++; connected = false; }
        public override string GetDeviceType() => OutContType.ViiperX360.ToString();
        public override void ConvertandSendReport(DS4State state, int device) { }
        public override void ResetState(bool submit = true) { ResetCount++; OnReset?.Invoke(); }
        public override void RemoveFeedbacks() { FeedbackClearCount++; OnFeedbackClear?.Invoke(); }
        public override void RemoveFeedback(int inIdx) { }
    }
}
