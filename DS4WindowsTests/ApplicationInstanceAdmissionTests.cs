using DS4WinWPF;

namespace DS4WindowsTests;

[TestClass]
public class ApplicationInstanceAdmissionTests
{
    [TestMethod]
    public async Task SimultaneousInstalledLaunchesCanAdmitOnlyOneMapper()
    {
        // Use only a private test event, never the production instance name.
        // Every contender represents a launch whose earlier open-existing
        // check saw nothing. Keep the winner alive until all have attempted.
        string eventName = "DS4Windows-test-admission-" + Guid.NewGuid().ToString("N");
        using var start = new ManualResetEventSlim();
        Task<EventWaitHandle>[] attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            Assert.IsTrue(start.Wait(TimeSpan.FromSeconds(5)));
            return App.CreateSingleAppComEvent(eventName, requireNew: true);
        })).ToArray();
        start.Set();
        EventWaitHandle[] handles = null;
        try
        {
            handles = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, handles.Count(handle => handle != null),
                "A preflight absence check must not let a losing installed launch share mapper ownership.");
            Assert.IsNull(App.CreateSingleAppComEvent(eventName, requireNew: true));
        }
        finally
        {
            // Also retire handles if an assertion/task fails; no app or broker
            // is started and no global production event is opened or signaled.
            foreach (Task<EventWaitHandle> attempt in attempts)
                if (attempt.IsCompletedSuccessfully) attempt.Result?.Dispose();
        }
        using EventWaitHandle next = App.CreateSingleAppComEvent(eventName, requireNew: true);
        Assert.IsNotNull(next, "Once its owner closes, a fresh launch can acquire the event.");
    }
}
