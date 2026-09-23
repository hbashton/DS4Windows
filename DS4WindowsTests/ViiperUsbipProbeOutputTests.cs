using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class ViiperUsbipProbeOutputTests
{
    [TestMethod]
    public void CompletedOutputPreservesBothStreamsForTheExistingAbiGate()
    {
        Assert.IsTrue(ViiperSetupManager.TryCompleteUsbipProbeOutput(
            Task.FromResult("Imported USB devices\r\n"), Task.FromResult("ABI mismatch"), 1000,
            out string output, out string failure));
        Assert.IsNull(failure);
        StringAssert.Contains(output, "Imported USB devices");
        StringAssert.Contains(output, "ABI mismatch");
        Assert.IsFalse(ViiperSetupManager.IsSuccessfulUsbipPortProbe(0, output),
            "A successful process exit must not override an error in either stream.");
        Assert.IsFalse(ViiperSetupManager.IsSuccessfulUsbipPortProbe(1, string.Empty));
    }

    [TestMethod]
    public void CompletedSuccessfulOutputRemainsEligibleForReadiness()
    {
        Assert.IsTrue(ViiperSetupManager.TryCompleteUsbipProbeOutput(
            Task.FromResult("Imported USB devices\r\n"), Task.FromResult(string.Empty), 1000,
            out string output, out string failure));
        Assert.IsNull(failure);
        Assert.IsTrue(ViiperSetupManager.IsSuccessfulUsbipPortProbe(0, output));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AStalledReaderCannotAdmitPartialOutputOrWaitBeyondTheRemainingBudget(bool stallError)
    {
        TaskCompletionSource<string> stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> finished = Task.FromResult("Imported USB devices");
        Assert.IsFalse(ViiperSetupManager.TryCompleteUsbipProbeOutput(
            stallError ? finished : stalled.Task, stallError ? stalled.Task : finished, 1,
            out string output, out string failure));
        Assert.IsNull(output);
        StringAssert.Contains(failure, "diagnostic output timed out");
        Assert.IsFalse(stalled.Task.IsCompleted, "The seam must not pretend that a retained pipe reached EOF.");
        // Production cancels/closes its own pipe; completion after the bounded
        // waiter has returned must not resurrect the rejected readiness check.
        stalled.SetException(new IOException("late pipe close"));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void ExpiredOverallBudgetCannotBeResetByAlreadyCompletedReaders(int remaining)
    {
        Assert.IsFalse(ViiperSetupManager.TryCompleteUsbipProbeOutput(
            Task.FromResult(string.Empty), Task.FromResult(string.Empty), remaining,
            out string output, out string failure));
        Assert.IsNull(output);
        StringAssert.Contains(failure, "timed out");
    }

    [TestMethod]
    public void FaultedReadersReturnAFixedPhaseWithoutLeakingTheirException()
    {
        const string secret = "private connection key or driver text must not escape";
        Assert.IsFalse(ViiperSetupManager.TryCompleteUsbipProbeOutput(
            Task.FromException<string>(new IOException(secret)), Task.FromResult(string.Empty), 1000,
            out string output, out string failure));
        Assert.IsNull(output);
        StringAssert.Contains(failure, "diagnostic output could not be read");
        Assert.IsFalse(failure.Contains(secret));
    }

    [TestMethod]
    public void CanceledReadersAreNotSuccessfulEmptyOutput()
    {
        Assert.IsFalse(ViiperSetupManager.TryCompleteUsbipProbeOutput(
            Task.FromCanceled<string>(new CancellationToken(canceled: true)), Task.FromResult(string.Empty), 1000,
            out string output, out string failure));
        Assert.IsNull(output);
        StringAssert.Contains(failure, "could not be read");
    }

    [TestMethod]
    public void PipeDrainBudgetStaysSmallComparedWithTheColdStartupBudget()
    {
        Assert.AreEqual(250, ViiperSetupManager.UsbipProbePipeDrainMilliseconds);
        Assert.IsTrue(ViiperSetupManager.UsbipProbePipeDrainMilliseconds < ViiperStartupReadiness.BudgetMilliseconds);
    }
}
