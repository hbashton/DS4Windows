using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public class RumbleConcurrencyTests
{
    [TestMethod]
    public void ConcurrentRumblePublicationNeverProducesTornMotorPair()
    {
        DS4Device device = (DS4Device)RuntimeHelpers.GetUninitializedObject(
            typeof(DS4Device));

        FieldInfo mailboxLock = typeof(DS4Device).GetField(
            "rumbleStateLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        mailboxLock.SetValue(device, new object());

        MethodInfo mergeStates = typeof(DS4Device).GetMethod(
            "MergeStates",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo currentHaptics = typeof(DS4Device).GetField(
            "currentHap",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        using ManualResetEventSlim start = new(false);
        const int Iterations = 250_000;
        Exception failure = null;

        Task publisher = Task.Run(() =>
        {
            start.Wait();
            for (int i = 1; i <= Iterations; i++)
            {
                byte fast = (byte)((i % 254) + 1);
                device.setRumble(fast, (byte)(255 - fast));
            }
        });

        Task consumer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < Iterations; i++)
                {
                    mergeStates.Invoke(device, null);
                    DS4HapticState state =
                        (DS4HapticState)currentHaptics.GetValue(device)!;
                    byte fast = state.rumbleState.
                        RumbleMotorStrengthRightLightFast;
                    byte slow = state.rumbleState.
                        RumbleMotorStrengthLeftHeavySlow;

                    if ((fast != 0 || slow != 0) && fast + slow != 255)
                    {
                        throw new AssertFailedException(
                            $"Observed torn rumble pair {fast}/{slow}.");
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        start.Set();
        Task.WaitAll(publisher, consumer);

        if (failure != null)
        {
            throw failure;
        }
    }

    [TestMethod]
    public void ConsumedMotorPairAndGenerationAreOneAtomicSnapshot()
    {
        DS4Device device = CreateUninitializedDevice();
        MethodInfo mergeAndGetGeneration = typeof(DS4Device).GetMethod(
            "MergeStatesAndGetRumbleGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo currentHaptics = GetCurrentHaptics();

        using ManualResetEventSlim start = new(false);
        const int Iterations = 100_000;
        Exception failure = null;

        Task publisher = Task.Run(() =>
        {
            start.Wait();
            for (int generation = 1; generation <= Iterations; generation++)
            {
                byte fast = (byte)(((generation - 1) % 254) + 1);
                device.setRumble(fast, (byte)(255 - fast));
            }
        });

        Task consumer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int iteration = 0; iteration < Iterations; iteration++)
                {
                    long generation = (long)mergeAndGetGeneration.Invoke(
                        device, null)!;
                    if (generation == 0)
                    {
                        continue;
                    }

                    DS4HapticState state =
                        (DS4HapticState)currentHaptics.GetValue(device)!;
                    byte expectedFast = (byte)(
                        ((generation - 1) % 254) + 1);
                    Assert.AreEqual(expectedFast, state.rumbleState.
                        RumbleMotorStrengthRightLightFast,
                        $"Generation {generation} acknowledged a different motor pair.");
                    Assert.AreEqual((byte)(255 - expectedFast),
                        state.rumbleState.RumbleMotorStrengthLeftHeavySlow,
                        $"Generation {generation} acknowledged a torn motor pair.");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        start.Set();
        Task.WaitAll(publisher, consumer);

        if (failure != null)
        {
            throw failure;
        }
    }

    [TestMethod]
    public void PreviewRumbleSurvivesConcurrentNeutralFeedbackUntilReleased()
    {
        DS4Device device = CreateUninitializedDevice();
        MethodInfo mergeStates = GetMergeStates();
        FieldInfo currentHaptics = GetCurrentHaptics();

        device.SetRumblePreview(lightMotorActive: true,
            lightMotorStrength: 211, heavyMotorActive: false,
            heavyMotorStrength: 0);

        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            device.setRumble(0, 0);
            mergeStates.Invoke(device, null);
            DS4HapticState state =
                (DS4HapticState)currentHaptics.GetValue(device)!;
            Assert.AreEqual((byte)211, state.rumbleState.
                RumbleMotorStrengthRightLightFast);
            Assert.AreEqual((byte)0, state.rumbleState.
                RumbleMotorStrengthLeftHeavySlow);
        }

        device.ClearRumblePreview();
        mergeStates.Invoke(device, null);
        DS4HapticState released =
            (DS4HapticState)currentHaptics.GetValue(device)!;
        Assert.AreEqual((byte)0, released.rumbleState.
            RumbleMotorStrengthRightLightFast);
        Assert.AreEqual((byte)0, released.rumbleState.
            RumbleMotorStrengthLeftHeavySlow);
        Assert.IsTrue(released.dirty);
    }

    [TestMethod]
    public void IndependentPreviewMotorsComposeAsOneAtomicPair()
    {
        DS4Device device = CreateUninitializedDevice();
        MethodInfo mergeStates = GetMergeStates();
        FieldInfo currentHaptics = GetCurrentHaptics();

        device.SetRumblePreview(lightMotorActive: true,
            lightMotorStrength: 101, heavyMotorActive: true,
            heavyMotorStrength: 202);
        mergeStates.Invoke(device, null);

        DS4HapticState state =
            (DS4HapticState)currentHaptics.GetValue(device)!;
        Assert.AreEqual((byte)101, state.rumbleState.
            RumbleMotorStrengthRightLightFast);
        Assert.AreEqual((byte)202, state.rumbleState.
            RumbleMotorStrengthLeftHeavySlow);
        Assert.IsFalse(state.rumbleState.RumbleMotorsExplicitlyOff);
    }

    [TestMethod]
    public void StoppingPreviewPublishesExplicitZeroMotorPair()
    {
        DS4Device device = CreateUninitializedDevice();
        MethodInfo mergeStates = GetMergeStates();
        FieldInfo currentHaptics = GetCurrentHaptics();

        device.SetRumblePreview(lightMotorActive: false,
            lightMotorStrength: 255, heavyMotorActive: true,
            heavyMotorStrength: 255);
        mergeStates.Invoke(device, null);

        device.SetRumblePreview(lightMotorActive: false,
            lightMotorStrength: 255, heavyMotorActive: false,
            heavyMotorStrength: 255);
        mergeStates.Invoke(device, null);

        DS4HapticState stopped =
            (DS4HapticState)currentHaptics.GetValue(device)!;
        Assert.AreEqual((byte)0, stopped.rumbleState.
            RumbleMotorStrengthRightLightFast);
        Assert.AreEqual((byte)0, stopped.rumbleState.
            RumbleMotorStrengthLeftHeavySlow);
        Assert.IsTrue(stopped.dirty);
    }

    private static DS4Device CreateUninitializedDevice()
    {
        DS4Device device = (DS4Device)RuntimeHelpers.GetUninitializedObject(
            typeof(DS4Device));
        typeof(DS4Device).GetField("rumbleStateLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(device, new object());
        return device;
    }

    private static MethodInfo GetMergeStates() =>
        typeof(DS4Device).GetMethod("MergeStates",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FieldInfo GetCurrentHaptics() =>
        typeof(DS4Device).GetField("currentHap",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
}
