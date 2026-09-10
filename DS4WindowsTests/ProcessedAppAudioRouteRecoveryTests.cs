using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class ProcessedAppAudioRouteRecoveryTests
{
    private static long Threshold => Stopwatch.Frequency *
        ProcessLoopbackWaveCapture.ProcessedRouteStallMilliseconds / 1000;

    [TestMethod]
    public void ActualSnapshotAndRecoveryKeepSameAudibleSonarEndpointWithoutReopening()
    {
        var recovery = new ProcessedAppAudioRouteRecovery();
        for (int poll = 1; poll <= 20; poll++)
        {
            var endpoint = new FakeEndpoint("sonar-gaming", 0.2f);
            var observation = Observe("sonar-gaming", endpoint);
            Assert.IsTrue(observation.CurrentRouteKnown);
            Assert.IsTrue(observation.CurrentRouteAudible);
            Assert.AreEqual("sonar-gaming", observation.AudibleExclusiveEndpointId);
            Assert.AreEqual(1, endpoint.DisposeCount);
            long now = poll * Threshold;
            Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.None,
                recovery.Observe("sonar-gaming", observation, now, now));
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualSnapshotDoesNotTurnCurrentQueryFailureIntoRelocation(bool failIdentity)
    {
        var current = new FakeEndpoint("sonar-gaming", 0.2f)
        {
            FailIdentity = failIdentity,
            FailSessions = !failIdentity,
        };
        var other = new FakeEndpoint("sonar-chat", 0.3f);
        var observation = Observe("sonar-gaming", current, other);
        Assert.IsFalse(observation.CurrentRouteKnown);
        Assert.AreEqual("sonar-chat", observation.AudibleExclusiveEndpointId);
        Assert.AreEqual(1, current.DisposeCount);
        Assert.AreEqual(1, other.DisposeCount);
        var recovery = new ProcessedAppAudioRouteRecovery();
        Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.None,
            recovery.Observe("sonar-gaming", observation, 100, 100));
        Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.None,
            recovery.Observe("sonar-gaming", observation, 100, 100 + Threshold * 2));
    }

    [TestMethod]
    public void ActualSnapshotExcludesLouderMixedEndpointAndRecordsLossOfCurrentIsolation()
    {
        var current = new FakeEndpoint("sonar-gaming", 0.1f) { Unrelated = true };
        var mixed = new FakeEndpoint("sonar-chat", 0.9f) { Unrelated = true };
        var exclusive = new FakeEndpoint("sonar-media", 0.3f);
        var observation = Observe("sonar-gaming", current, mixed, exclusive);
        Assert.IsTrue(observation.CurrentRouteHasUnrelatedSession);
        Assert.AreEqual("sonar-media", observation.AudibleExclusiveEndpointId);
        foreach (var endpoint in new[] { current, mixed, exclusive }) Assert.AreEqual(1, endpoint.DisposeCount);
    }

    [TestMethod]
    public void ExclusiveSelectionEnumeratesOnceAndDisposesActualRejectedWrappers()
    {
        var quiet = new FakeEndpoint("quiet", 0.0f);
        var failed = new FakeEndpoint("failed", 1.0f) { FailSessions = true };
        var selected = new FakeEndpoint("selected", 0.3f);
        var mixed = new FakeEndpoint("mixed", 0.9f) { Unrelated = true };
        int enumerations = 0;
        IEnumerable<FakeEndpoint> Endpoints()
        {
            Assert.AreEqual(1, ++enumerations, "MMDeviceCollection recreates wrappers on a second enumeration.");
            yield return quiet;
            yield return failed;
            yield return selected;
            yield return mixed;
        }
        var result = ProcessedAppAudioRouteResolver.FindExclusiveEndpoint(Endpoints(), endpoint =>
        {
            var state = endpoint.Read();
            return state.UnrelatedActive ? null : (float?)state.TargetPeak;
        });
        Assert.AreSame(selected, result);
        Assert.AreEqual(1, quiet.DisposeCount);
        Assert.AreEqual(1, failed.DisposeCount);
        Assert.AreEqual(1, mixed.DisposeCount);
        Assert.AreEqual(0, selected.DisposeCount, "Ownership transfers to the route caller.");
        result.Dispose();
        Assert.AreEqual(1, selected.DisposeCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RouteQueryDoesNotDisposeTheEndpointsBorrowedSessionManager(bool exclusiveQuery)
    {
        // Real NAudio wrappers, without their COM constructors or any endpoint
        // activation. The intentionally missing native enumerator exercises
        // the exception path that must leave borrowed ownership untouched.
        using var endpoint = (MMDevice)RuntimeHelpers.GetUninitializedObject(typeof(MMDevice));
        var manager = (AudioSessionManager)RuntimeHelpers.GetUninitializedObject(typeof(AudioSessionManager));
        var sessions = (SessionCollection)RuntimeHelpers.GetUninitializedObject(typeof(SessionCollection));
        typeof(MMDevice).GetField("audioSessionManager", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(endpoint, manager);
        typeof(AudioSessionManager).GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(manager, sessions);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (exclusiveQuery)
            {
                var exception = Assert.ThrowsException<TargetInvocationException>(() =>
                    typeof(ProcessedAppAudioRouteResolver).GetMethod("TryGetExclusiveTargetPeak",
                        BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                        new object[] { endpoint, Environment.ProcessId, 0.0f }));
                Assert.IsInstanceOfType(exception.InnerException, typeof(NullReferenceException));
            }
            else
            {
                Assert.ThrowsException<NullReferenceException>(() =>
                    ProcessedAppAudioRouteResolver.IsTargetRouteAudiblyActive(endpoint, Environment.ProcessId));
            }
            Assert.AreSame(manager, endpoint.AudioSessionManager);
            Assert.AreSame(sessions, manager.Sessions,
                "Disposing the borrowed manager nulls Sessions on the MMDevice's cached instance and poisons every later poll.");
        }

        endpoint.Dispose();
        Assert.IsNull(manager.Sessions, "The endpoint owner remains responsible for terminal manager disposal.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SameSonarEndpointNeverBecomesRelocationEvidence(bool currentAudible)
    {
        var recovery = new ProcessedAppAudioRouteRecovery();
        var observation = new ProcessedAppAudioRouteObservation(true, currentAudible, false, "SONAR-GAMING");
        for (int poll = 1; poll <= 30; poll++)
        {
            long now = poll * Threshold;
            Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.None,
                recovery.Observe("sonar-gaming", observation, now, now));
        }
    }

    [TestMethod]
    public void GenuineDifferentExclusiveRouteRequiresStableIdentityForTheFullDebounce()
    {
        var recovery = new ProcessedAppAudioRouteRecovery();
        var moved = new ProcessedAppAudioRouteObservation(true, false, false, "sonar-chat");
        Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.None,
            recovery.Observe("sonar-gaming", moved, 100, 100));
        Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.None,
            recovery.Observe("sonar-gaming", moved, 100 + Threshold - 1, 100 + Threshold - 1));
        Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.Relocated,
            recovery.Observe("sonar-gaming", moved, 100 + Threshold, 100 + Threshold));
    }

    [DataTestMethod]
    [DataRow("unknown")]
    [DataRow("silence")]
    [DataRow("current-route")]
    [DataRow("another-candidate")]
    public void InterruptedOrChangingRelocationEvidenceCannotAccumulate(string interruption)
    {
        var recovery = new ProcessedAppAudioRouteRecovery();
        var moved = new ProcessedAppAudioRouteObservation(true, false, false, "sonar-chat");
        Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.None,
            recovery.Observe("sonar-gaming", moved, 100, 100));
        var interrupted = interruption switch
        {
            "unknown" => new ProcessedAppAudioRouteObservation(false, false, false, "sonar-chat"),
            "silence" => new ProcessedAppAudioRouteObservation(true, false, false, ""),
            "current-route" => new ProcessedAppAudioRouteObservation(true, true, false, "sonar-chat"),
            _ => new ProcessedAppAudioRouteObservation(true, false, false, "sonar-media"),
        };
        Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.None,
            recovery.Observe("sonar-gaming", interrupted, 100 + Threshold, 100 + Threshold));
        Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.None,
            recovery.Observe("sonar-gaming", moved, 100 + Threshold * 2, 100 + Threshold * 2));
        Assert.AreEqual(ProcessedAppAudioRouteRecoveryReason.Relocated,
            recovery.Observe("sonar-gaming", moved, 100 + Threshold * 3, 100 + Threshold * 3));
    }

    [DataTestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    public void GenuineCallbackStallStillRecoversOnlyWithKnownAudibleCurrentRoute(
        bool known, bool audible, bool expectedRecovery)
    {
        var recovery = new ProcessedAppAudioRouteRecovery();
        var observation = new ProcessedAppAudioRouteObservation(known, audible, false, "sonar-gaming");
        Assert.AreEqual(expectedRecovery ? ProcessedAppAudioRouteRecoveryReason.CallbackStalled :
            ProcessedAppAudioRouteRecoveryReason.None,
            recovery.Observe("sonar-gaming", observation, 100, 100 + Threshold));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PositivelyObservedUnrelatedApplicationRetiresEndpointMix(bool known)
    {
        var recovery = new ProcessedAppAudioRouteRecovery();
        var observation = new ProcessedAppAudioRouteObservation(known, true, true, "");
        Assert.AreEqual(known ? ProcessedAppAudioRouteRecoveryReason.NoLongerExclusive :
            ProcessedAppAudioRouteRecoveryReason.None,
            recovery.Observe("sonar-gaming", observation, 100, 100));
    }

    [TestMethod]
    public void CompatibleEndpointSelectionPreservesFormatAndAcceptsNewRouteIdentity()
    {
        WaveFormat expected = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        Assert.AreEqual("sonar-gaming", ProcessedAppAudioRouteRecovery.SelectCompatibleEndpoint(
            "sonar-gaming", expected, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        Assert.AreEqual("sonar-chat", ProcessedAppAudioRouteRecovery.SelectCompatibleEndpoint(
            "sonar-chat", expected, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
    }

    [DataTestMethod]
    [DataRow("same", true)]
    [DataRow("subformat", false)]
    [DataRow("channel-mask", false)]
    [DataRow("valid-bits", false)]
    public void ExtensibleFormatIdentityIncludesSubtypeChannelMaskAndValidBits(string difference, bool matches)
    {
        var expected = new WaveFormatExtensible(48000, 32, 2);
        var actual = new WaveFormatExtensible(48000, 32, 2);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        // Names come from the pinned NAudio2.2.1 public-serializer source;
        // production never reads private fields or guesses native offsets.
        if (difference == "subformat")
            typeof(WaveFormatExtensible).GetField("subFormat", fields).SetValue(actual,
                NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_PCM);
        else if (difference == "channel-mask")
            typeof(WaveFormatExtensible).GetField("dwChannelMask", fields).SetValue(actual, 0x0C);
        else if (difference == "valid-bits")
            typeof(WaveFormatExtensible).GetField("wValidBitsPerSample", fields).SetValue(actual, (short)24);

        Assert.AreEqual(expected.Encoding, actual.Encoding);
        Assert.AreEqual(expected.Channels, actual.Channels);
        Assert.AreEqual(expected.BitsPerSample, actual.BitsPerSample);
        Assert.AreEqual(matches, ProcessedAppAudioRouteRecovery.FormatsMatch(expected, actual));
        Assert.AreEqual(matches ? "sonar-chat" : string.Empty,
            ProcessedAppAudioRouteRecovery.SelectCompatibleEndpoint("sonar-chat", expected, actual));
        Type registry = typeof(ProcessLoopbackWaveCapture).GetNestedType("ProcessCaptureRegistry", BindingFlags.NonPublic);
        MethodInfo buildKey = registry.GetMethod("BuildKey", BindingFlags.Static | BindingFlags.NonPublic);
        string oldKey = (string)buildKey.Invoke(null, new object[] { 42, expected, "sonar-chat" });
        string newKey = (string)buildKey.Invoke(null, new object[] { 42, actual, "sonar-chat" });
        Assert.AreEqual(matches, string.Equals(oldKey, newKey, StringComparison.Ordinal),
            "A format-incompatible lease must not reuse a registered capture with the same coarse format fields.");
    }

    [DataTestMethod]
    [DataRow("sample-rate")]
    [DataRow("channels")]
    [DataRow("encoding")]
    [DataRow("sample-size")]
    [DataRow("unknown-format")]
    [DataRow("no-exclusive-route")]
    public void IncompatibleOrMissingExclusiveRouteKeepsFormatStableProcessLoopback(string reason)
    {
        WaveFormat expected = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        WaveFormat actual = reason switch
        {
            "sample-rate" => WaveFormat.CreateIeeeFloatWaveFormat(44100, 2),
            "channels" => WaveFormat.CreateIeeeFloatWaveFormat(48000, 1),
            "encoding" => new WaveFormat(48000, 32, 2),
            "sample-size" => new WaveFormat(48000, 16, 2),
            "unknown-format" => null,
            _ => expected,
        };
        Assert.AreEqual(string.Empty, ProcessedAppAudioRouteRecovery.SelectCompatibleEndpoint(
            reason == "no-exclusive-route" ? string.Empty : "sonar-chat", expected, actual));
        Assert.AreEqual(48000, expected.SampleRate);
        Assert.AreEqual(2, expected.Channels);
        Assert.AreEqual(WaveFormatEncoding.IeeeFloat, expected.Encoding);
    }

    private static ProcessedAppAudioRouteObservation Observe(string current,
        params FakeEndpoint[] endpoints) => ProcessedAppAudioRouteResolver.ObserveEndpoints(
            endpoints, current, endpoint => endpoint.GetId(), endpoint => endpoint.Read());

    private sealed class FakeEndpoint : IDisposable
    {
        private readonly string id;
        private readonly float peak;
        internal bool FailIdentity;
        internal bool FailSessions;
        internal bool Unrelated;
        internal int DisposeCount;

        internal FakeEndpoint(string id, float peak)
        {
            this.id = id;
            this.peak = peak;
        }

        internal string GetId() => FailIdentity ? throw new InvalidOperationException("endpoint disappeared") : id;
        internal (bool TargetActive, float TargetPeak, bool UnrelatedActive) Read() =>
            FailSessions ? throw new InvalidOperationException("session graph rebuilding") : (true, peak, Unrelated);
        public void Dispose() => DisposeCount++;
    }
}
