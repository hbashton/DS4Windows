using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2HighRateMousePresentationTests
{
    [TestMethod]
    public async Task CancelledCalibrationFenceDoesNotWaitForStuckExternalMouseOutput()
    {
        using var moved = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var presenter = new Switch2HighRateMousePresenter((_, _) =>
        {
            moved.Set();
            if (!release.Wait(5000)) throw new TimeoutException();
        });
        try
        {
            Assert.IsTrue(presenter.TrySetSource(Switch2ContinuousMouseSource.Gyro, true, 100_000, 0, 0));
            Assert.IsTrue(moved.Wait(1000));
            presenter.ClearSources();
            var fencing = Task.Run(() => presenter.FencePresentation(cancellation.Token));
            Assert.IsFalse(fencing.Wait(50));
            cancellation.Cancel();
            Assert.IsFalse(await fencing.WaitAsync(TimeSpan.FromSeconds(1)),
                "Cancellation must release Begin without claiming a successful output fence.");
        }
        finally { release.Set(); presenter.Stop(); }
    }

    [TestMethod]
    public void MixerCombinesOnlyFreshActiveSources()
    {
        Switch2HighRateMouseSourceMixer mixer = default;
        Assert.IsTrue(mixer.TryUpdate(Switch2ContinuousMouseSource.Gyro,
            active: true, 120.0, -40.0, timestampQpc: 1_000,
            profileRevision: 7));
        Assert.IsTrue(mixer.TryUpdate(Switch2ContinuousMouseSource.Ir,
            active: true, -20.0, 10.0, timestampQpc: 1_040,
            profileRevision: 7));
        Assert.IsTrue(mixer.TryUpdate(
            Switch2ContinuousMouseSource.StickAssist,
            active: false, 999.0, 999.0, timestampQpc: 1_045,
            profileRevision: 7));
        Assert.IsTrue(mixer.TryUpdate(
            Switch2ContinuousMouseSource.MappedStick,
            active: true, 30.0, 15.0, timestampQpc: 1_046,
            profileRevision: 7));

        Assert.IsTrue(mixer.TrySnapshot(nowQpc: 1_050,
            qpcFrequency: 1_000, out double x, out double y));
        Assert.AreEqual(130.0, x, 0.000001);
        Assert.AreEqual(-15.0, y, 0.000001);

        Assert.IsFalse(mixer.TrySnapshot(nowQpc: 1_147,
            qpcFrequency: 1_000, out x, out y));
        Assert.AreEqual(0.0, x);
        Assert.AreEqual(0.0, y);
    }

    [TestMethod]
    public void ProfileRevisionAtomicallyDropsEarlierSources()
    {
        Switch2HighRateMouseSourceMixer mixer = default;
        Assert.IsTrue(mixer.TryUpdate(Switch2ContinuousMouseSource.Gyro,
            active: true, 500.0, 400.0, timestampQpc: 1_000,
            profileRevision: 3));
        Assert.IsTrue(mixer.TryUpdate(Switch2ContinuousMouseSource.Ir,
            active: true, 10.0, 20.0, timestampQpc: 1_001,
            profileRevision: 4));

        Assert.IsTrue(mixer.TrySnapshot(nowQpc: 1_002,
            qpcFrequency: 1_000, out double x, out double y));
        Assert.AreEqual(10.0, x);
        Assert.AreEqual(20.0, y);
        Assert.IsFalse(mixer.Gyro.Active);
        Assert.IsFalse(mixer.MappedStick.Active);
    }

    [TestMethod]
    public void MixerRejectsInvalidAndUnboundedState()
    {
        Switch2HighRateMouseSourceMixer mixer = default;
        Assert.IsFalse(mixer.TryUpdate(
            (Switch2ContinuousMouseSource)99, true, 1.0, 1.0,
            timestampQpc: 1, profileRevision: 0));
        Assert.IsFalse(mixer.TryUpdate(Switch2ContinuousMouseSource.Gyro,
            true, double.NaN, 1.0, timestampQpc: 1,
            profileRevision: 0));
        Assert.IsFalse(mixer.TryUpdate(Switch2ContinuousMouseSource.Gyro,
            true,
            Switch2HighRateMousePresenter.MaximumVelocityPixelsPerSecond + 1,
            1.0, timestampQpc: 1, profileRevision: 0));
        Assert.IsFalse(mixer.TrySnapshot(nowQpc: 0, qpcFrequency: 1_000,
            out _, out _));
    }

    [TestMethod]
    public void MappingSourceBatchCommitsAtomicallyWithOneTimestamp()
    {
        Switch2HighRateMouseSourceMixer mixer = default;
        Assert.IsTrue(mixer.TryUpdate(Switch2ContinuousMouseSource.Gyro,
            active: true, 5.0, 6.0, timestampQpc: 950,
            profileRevision: 4));

        Assert.IsFalse(mixer.TryUpdateMappingSources(
            stickAssistActive: true, 10.0, 20.0,
            irActive: true, double.NaN, 30.0,
            mappedStickActive: true, 40.0, 50.0,
            timestampQpc: 1_000, profileRevision: 4));
        Assert.IsFalse(mixer.StickAssist.Active);
        Assert.IsFalse(mixer.Ir.Active);
        Assert.IsFalse(mixer.MappedStick.Active);
        Assert.IsTrue(mixer.Gyro.Active);

        Assert.IsTrue(mixer.TryUpdateMappingSources(
            stickAssistActive: true, 10.0, 20.0,
            irActive: true, 20.0, 30.0,
            mappedStickActive: true, 40.0, 50.0,
            timestampQpc: 1_001, profileRevision: 4));
        Assert.AreEqual(1_001, mixer.StickAssist.TimestampQpc);
        Assert.AreEqual(1_001, mixer.Ir.TimestampQpc);
        Assert.AreEqual(1_001, mixer.MappedStick.TimestampQpc);
        Assert.IsTrue(mixer.TrySnapshot(nowQpc: 1_002,
            qpcFrequency: 1_000, out double x, out double y));
        Assert.AreEqual(75.0, x, 0.000001);
        Assert.AreEqual(106.0, y, 0.000001);
    }

    [TestMethod]
    public void IntegratorScalesElapsedTimeAndCarriesFractions()
    {
        Switch2HighRateMouseIntegrator integrator = default;
        Assert.IsTrue(integrator.TryStep(500.0, -500.0, 0.001,
            out int x, out int y));
        Assert.AreEqual(0, x);
        Assert.AreEqual(0, y);
        Assert.AreEqual(0.5, integrator.ResidualX, 0.000001);
        Assert.AreEqual(-0.5, integrator.ResidualY, 0.000001);

        Assert.IsTrue(integrator.TryStep(500.0, -500.0, 0.001,
            out x, out y));
        Assert.AreEqual(1, x);
        Assert.AreEqual(-1, y);
        Assert.AreEqual(0.0, integrator.ResidualX, 0.000001);
        Assert.AreEqual(0.0, integrator.ResidualY, 0.000001);
    }

    [TestMethod]
    public void IntegratorUsesBoundedFallbackAfterSchedulerStall()
    {
        Switch2HighRateMouseIntegrator integrator = default;
        Assert.IsTrue(integrator.TryStep(100.0, 0.0, 0.500,
            out int x, out int y));
        Assert.AreEqual(1, x);
        Assert.AreEqual(0, y);
        Assert.AreEqual(0.5, integrator.ResidualX, 0.000001);

        Assert.IsFalse(integrator.TryStep(double.PositiveInfinity, 0.0,
            0.001, out _, out _));
        Assert.AreEqual(0.0, integrator.ResidualX);
    }

    [TestMethod]
    public void WarmMixerAndIntegratorPathsDoNotAllocate()
    {
        Switch2HighRateMouseSourceMixer mixer = default;
        Switch2HighRateMouseIntegrator integrator = default;
        mixer.TryUpdate(Switch2ContinuousMouseSource.Gyro, true,
            100.0, -50.0, 1, 1);
        mixer.TrySnapshot(1, 1_000, out _, out _);
        integrator.TryStep(100.0, -50.0, 0.001, out _, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int index = 0; index < 20_000; index++)
        {
            long timestamp = index + 2;
            succeeded &= mixer.TryUpdate(
                Switch2ContinuousMouseSource.Gyro, true,
                100.0, -50.0, timestamp, 1);
            succeeded &= mixer.TrySnapshot(timestamp, 1_000,
                out double x, out double y);
            succeeded &= integrator.TryStep(x, y, 0.001,
                out _, out _);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, after - before);
    }

    [TestMethod]
    public void PresenterStopsSynchronouslyAndRejectsResurrection()
    {
        using ManualResetEventSlim moved = new(false);
        int presentationCount = 0;
        Switch2HighRateMousePresenter presenter = new((_, _) =>
        {
            Interlocked.Increment(ref presentationCount);
            moved.Set();
        });

        try
        {
            Assert.IsTrue(presenter.TrySetSource(
                Switch2ContinuousMouseSource.Gyro, active: true,
                100_000.0, 0.0, profileRevision: 1));
            Assert.IsTrue(moved.Wait(TimeSpan.FromSeconds(2)));

            presenter.Stop();
            int stoppedCount = Volatile.Read(ref presentationCount);
            Thread.Sleep(30);
            Assert.AreEqual(stoppedCount,
                Volatile.Read(ref presentationCount));
            Assert.IsFalse(presenter.TrySetSource(
                Switch2ContinuousMouseSource.Gyro, active: true,
                100_000.0, 0.0, profileRevision: 1));
        }
        finally
        {
            presenter.Stop();
        }
    }

    [TestMethod]
    public void ProfileDefaultsOnAndRoundTripsExplicitOptOut()
    {
        BackingStore store = new();
        ProfileDTO profile = new() { DeviceIndex = 0 };
        Assert.IsTrue(profile.Switch2HighRateMousePresentation);
        profile.MapTo(store);
        Assert.IsTrue(store.switch2HighRateMousePresentation[0]);

        profile.Switch2HighRateMousePresentation = false;
        profile.MapTo(store);
        Assert.IsFalse(store.switch2HighRateMousePresentation[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.IsFalse(output.Switch2HighRateMousePresentation);
        output.SerializeAppAttrs = false;
        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2HighRateMousePresentation>false</Switch2HighRateMousePresentation>");

        using var reader = new StringReader(
            "<DS4Windows config_version=\"5\" />");
        var legacy = (ProfileDTO)serializer.Deserialize(reader);
        Assert.IsTrue(legacy.Switch2HighRateMousePresentation);
    }
}
