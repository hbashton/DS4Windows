using DS4Windows;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using DS4Windows.DS4Control;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class ProfileMutationBoundaryTests
{
    private const int Slot = Global.MAX_DS4_CONTROLLER_COUNT - 1;

    [TestMethod]
    public void ExplicitUiNameSurvivesAnOlderNamedApplyBetweenSelectionAndEnqueue()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "<DS4Windows config_version=\"5\"><RumbleBoost>42</RumbleBoost><Control><Key><Cross>65</Cross></Key></Control></DS4Windows>");
        fixture.WriteOtherProfile();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        using var mapping = new BlockingSendInputMapping();
        Global.outputKBMMapping = mapping; // Only key translation; never emits input.
        Task<GuardedProfileSwitchResult> named = fixture.LoadNamed();
        var regular = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = false;
        try
        {
            Assert.IsTrue(mapping.Entered.Wait(5000));
            string uiSelection = "Other";
            Global.ProfilePath[Slot] = uiSelection;
            mapping.Release.Set();
            Assert.IsTrue(Await(named).Applied);
            Assert.AreEqual("Candidate", Global.ProfilePath[Slot], "The old apply publishes before UI enqueue.");
            Mapping.RequestRegularProfileReload(Slot, false, fixture.Service,
                loaded => regular.TrySetResult(loaded), profileName: uiSelection);
            enqueued = true;
            Assert.IsTrue(regular.Task.Wait(5000));
            Assert.IsTrue(regular.Task.Result);
            Assert.AreEqual("Other", Global.ProfilePath[Slot]);
            Assert.AreEqual(21, (int)fixture.Store.rumble[Slot]);
        }
        finally
        {
            mapping.Release.Set();
            Await(named);
            if (enqueued) Assert.IsTrue(regular.Task.Wait(5000));
        }
    }

    [TestMethod]
    public void NamedDisplacementWhileWaitingForProfileGateIsSuperseded()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        Assert.IsTrue(fixture.Service.TryCaptureProfileActionTarget(Slot, fixture.Service.DS4Controllers[Slot], out var target));
        long before = Global.ReadProfileSwitchRevision(Slot);
        Assert.IsTrue(GuardedNamedProfileLoad.TryCreate(target, fixture.Service, "Candidate", "Original",
            before, false, () => true, out var request));
        using var checkedTicket = new ManualResetEventSlim();
        int ticket = 1;
        Task<GuardedProfileSwitchResult> pending = null;
        try
        {
            Mapping.ExecuteSerializedProfileMutation(Slot, () =>
            {
                pending = Task.Run(() => request.Execute(() =>
                {
                    bool current = Volatile.Read(ref ticket) == 1;
                    checkedTicket.Set();
                    return current;
                }, (long expected, out long claimed) =>
                {
                    claimed = 0;
                    Assert.Fail("A displaced request must not claim a revision.");
                    return false;
                }));
                Assert.IsTrue(checkedTicket.Wait(5000));
                Volatile.Write(ref ticket, 0);
            });
            Assert.AreEqual(GuardedProfileSwitchStatus.Superseded, Await(pending).Status);
            Assert.AreEqual(before, Global.ReadProfileSwitchRevision(Slot));
            Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        }
        finally { if (pending != null) Await(pending); }
    }

    [TestMethod]
    public void RegularWorkerLoadsItsEnqueuedNameInsteadOfAMutatedGlobalName()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.WriteOtherProfile();
        fixture.AttachSource();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Mapping.ExecuteSerializedProfileMutation(Slot, () =>
            {
                Mapping.RequestRegularProfileReload(Slot, false, fixture.Service,
                    loaded => completed.TrySetResult(loaded));
                Global.ProfilePath[Slot] = "Other";
            });
            Assert.IsTrue(completed.Task.Wait(5000));
            Assert.IsTrue(completed.Task.Result);
            Assert.AreEqual(42, (int)fixture.Store.rumble[Slot]);
            Assert.AreEqual("Candidate", Global.ProfilePath[Slot]);
        }
        finally { Assert.IsTrue(completed.Task.Wait(5000)); }
    }

    [TestMethod]
    public void BaseLegacyQueueCannotConsumePostLoadBeforeOuterActionLeaseReleases()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        DS4Device source = fixture.AttachLegacy();
        Assert.IsTrue(fixture.Service.TryCaptureProfileActionTarget(Slot, source, out var target));
        Assert.IsTrue(PreparedProfileLoad.TryPrepare(fixture.Path, Slot, out var prepared, out _, out _));
        var queue = (Queue<Action>)typeof(DS4Device).GetField("eventQueue",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source);
        object queueGate = typeof(DS4Device).GetField("eventQueueLock",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source);
        long revision = Global.BeginProfileSwitchRevision(Slot);
        source.ReadWaitEv.Set();
        Assert.IsTrue(source.TryHaltReportingRunAction(() =>
        {
            Assert.IsTrue(target.TryAcquire(out var lease));
            using (lease)
            {
                Assert.IsTrue(fixture.Store.ApplyPreparedProfileNew(prepared, false, fixture.Service,
                    out _, xinputChange: false, transitionRevision: revision,
                    completeColdSideEffects: false, actionTarget: target));
                // Exercise the real base pause semantics, but only simulate
                // its queue-drain stage: never start an actual HID input loop.
                Task<int> drain = Task.Run(() =>
                {
                    Assert.IsTrue(source.ReadWaitEv.Wait(1000));
                    Assert.IsFalse(source.FireReport);
                    lock (queueGate)
                    {
                        int count = queue.Count;
                        for (int i = 0; i < count; i++) queue.Dequeue()();
                        return count;
                    }
                });
                Assert.IsTrue(drain.Wait(2000));
                Assert.AreEqual(0, drain.Result,
                    "Legacy queues run while FireReport=false; queuing here loses the guarded transition.");
            }
        }));
        Assert.IsTrue(source.FireReport);
        Assert.IsTrue(fixture.Store.CompletePreparedProfileLoad(prepared, false));
        Action transition = fixture.DequeueOutput(source);
        object outputGate = new object();
        typeof(ControlService).GetField("gameBarCompatibilityOutputLock",
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(fixture.Service, outputGate);
        typeof(ControlService).GetField("gameBarCompatibilityRoutingActive",
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(fixture.Service, new int[Global.MAX_DS4_CONTROLLER_COUNT]);
        Task output = null;
        try
        {
            lock (outputGate)
            {
                // Run the actual queued closure. Stop it at the first benign
                // service gate so we can observe its exact action admission;
                // no virtual output, audio or physical HID is initialized.
                output = Task.Run(transition);
                Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Table.GetSnapshot()[Slot].ActionActive, 2000));
                // The existing stale-revision fence skips CheckProfileOptions
                // after the gate, keeping the rest of this test hardware-free.
                Global.BeginProfileSwitchRevision(Slot);
            }
            Assert.IsTrue(output.Wait(2000));
            Assert.IsFalse(fixture.Table.GetSnapshot()[Slot].ActionActive);
        }
        finally
        {
            // Also fence the callback if an assertion failed before superseding.
            Global.BeginProfileSwitchRevision(Slot);
            if (output != null) Assert.IsTrue(output.Wait(2000));
        }
        Assert.IsTrue(target.TryAcquire(out var available));
        available.Dispose();
        Assert.IsTrue(fixture.Store.CompletePreparedProfileLoad(prepared, false));
        Assert.AreEqual(0, queue.Count, "Cold completion must not enqueue the transition twice.");
    }

    [TestMethod]
    public void NamedProfileResolvesKeyAliasesUnderStableBackend()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "<DS4Windows config_version=\"5\"><Control><Key><Cross>65</Cross></Key></Control></DS4Windows>");
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        var result = Await(fixture.LoadNamed());
        Assert.IsTrue(result.Applied, result.Error);
        Assert.AreEqual(65U, fixture.Store.ds4settings[Slot].Single(
            setting => setting.control == DS4Controls.Cross).action.actionAlias);
    }

    [TestMethod]
    public void AppliedProfileWithMigrationSaveFailureIsNotReportedAsRolledBack()
    {
        using var fixture = new Fixture();
        fixture.MakeValid("4");
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        FileAttributes attributes = File.GetAttributes(fixture.Path);
        File.SetAttributes(fixture.Path, attributes | FileAttributes.ReadOnly);
        try
        {
            var result = Await(fixture.LoadNamed());
            Assert.AreEqual(GuardedProfileSwitchStatus.AppliedWithCompletionError, result.Status, result.Error);
            Assert.IsTrue(result.Applied);
            Assert.AreEqual("Candidate", Global.ProfilePath[Slot]);
            Assert.AreEqual(42, (int)fixture.Store.rumble[Slot]);
            StringAssert.Contains(File.ReadAllText(fixture.Path), "config_version=\"4\"");
        }
        finally { File.SetAttributes(fixture.Path, attributes); }
    }

    [TestMethod]
    public void ThrowingContextCompletesRequestAndDoesNotStrandWorker()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        long before = Global.ReadProfileSwitchRevision(Slot);
        var rejected = Await(fixture.LoadNamed(() => throw new InvalidOperationException("Synthetic context failure")));
        Assert.AreEqual(GuardedProfileSwitchStatus.ApplyFailed, rejected.Status);
        Assert.AreEqual(0L, rejected.Revision);
        Assert.AreEqual(before, Global.ReadProfileSwitchRevision(Slot));
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        Assert.IsTrue(Await(fixture.LoadNamed()).Applied);
    }

    [DataTestMethod]
    [DataRow("../Candidate")]
    [DataRow("..\\Candidate")]
    [DataRow("Candidate.")]
    [DataRow("Candidate ")]
    [DataRow("C:\\Candidate")]
    [DataRow("")]
    public void NamedSelectionRejectsNonCatalogFileNames(string name)
    {
        using var fixture = new Fixture();
        var source = fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        Assert.IsTrue(fixture.Service.TryCaptureProfileActionTarget(Slot, source, out var target));
        long before = Global.ReadProfileSwitchRevision(Slot);
        var result = Await(Mapping.RequestNamedRegularProfileLoad(target, name, "Original", before,
            false, fixture.Service, () => true));
        Assert.AreEqual(GuardedProfileSwitchStatus.InvalidRequest, result.Status);
        Assert.AreEqual(before, Global.ReadProfileSwitchRevision(Slot));
    }

    [TestMethod]
    public void GuardedRevisionCasRejectsStaleAndOverflowWithoutMutatingCurrent()
    {
        using var fixture = new Fixture();
        long before = Global.ReadProfileSwitchRevision(Slot);
        Assert.IsTrue(Global.TryBeginProfileSwitchRevision(Slot, before, out long claimed));
        Assert.AreEqual(before + 1, claimed);
        Assert.IsFalse(Global.TryBeginProfileSwitchRevision(Slot, before, out long stale));
        Assert.AreEqual(0L, stale);
        Assert.IsFalse(Global.TryBeginProfileSwitchRevision(Slot, long.MaxValue, out _));
        Assert.IsFalse(Global.TryBeginProfileSwitchRevision(Slot, -1, out _));
        Assert.IsFalse(Global.TryBeginProfileSwitchRevision(-1, claimed, out _));
        Assert.AreEqual(claimed, Global.ReadProfileSwitchRevision(Slot));
    }

    [TestMethod]
    public void NamedWorkerPreparesThenClaimsOneRevisionAndPublishesRegularName()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        long before = Global.ReadProfileSwitchRevision(Slot);
        bool sawStableBackend = false;
        var result = Await(fixture.LoadNamed(() =>
        {
            if (Monitor.IsEntered(fixture.KbmGate))
            {
                sawStableBackend = true;
                Assert.IsTrue(fixture.Table.GetSnapshot()[Slot].ActionActive);
            }
            return true;
        }));
        Assert.IsTrue(result.Applied, result.Error);
        Assert.AreEqual(before + 1, result.Revision);
        Assert.AreEqual(result.Revision, Global.ReadProfileSwitchRevision(Slot));
        Assert.AreEqual("Candidate", Global.ProfilePath[Slot]);
        Assert.AreEqual(42, (int)fixture.Store.rumble[Slot]);
        Assert.IsFalse(Global.useTempProfile[Slot]);
        Assert.IsTrue(sawStableBackend);
        Assert.IsFalse(fixture.Table.GetSnapshot()[Slot].ActionActive);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedNamedPreparationDoesNotClaimRevisionOrChangeName(bool missing)
    {
        using var fixture = new Fixture();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        if (missing) File.Delete(fixture.Path);
        long before = Global.ReadProfileSwitchRevision(Slot);
        var result = Await(fixture.LoadNamed());
        Assert.AreEqual(GuardedProfileSwitchStatus.PreparationFailed, result.Status);
        Assert.AreEqual(before, Global.ReadProfileSwitchRevision(Slot));
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NamedWorkerRechecksContextAndDoesNotOverrideTemporaryProfile(bool temporary)
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        Global.useTempProfile[Slot] = temporary;
        long before = Global.ReadProfileSwitchRevision(Slot);
        int guardCalls = 0;
        var result = Await(fixture.LoadNamed(() => ++guardCalls == 1));
        Assert.AreEqual(GuardedProfileSwitchStatus.ContextChanged, result.Status);
        Assert.AreEqual(before, Global.ReadProfileSwitchRevision(Slot));
        Assert.AreEqual(temporary, Global.useTempProfile[Slot]);
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
    }

    [TestMethod]
    public void NamedCasCannotBumpPastAnExternalRevisionDuringFinalGuard()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        long external = 0;
        var result = Await(fixture.LoadNamed(() =>
        {
            if (Monitor.IsEntered(fixture.KbmGate))
                external = Global.BeginProfileSwitchRevision(Slot);
            return true;
        }));
        Assert.AreEqual(GuardedProfileSwitchStatus.Superseded, result.Status);
        Assert.AreEqual(external, Global.ReadProfileSwitchRevision(Slot));
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
    }

    [TestMethod]
    public void AllCoalescedNamedRequestsCompleteAndOnlyLatestApplies()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        long before = Global.ReadProfileSwitchRevision(Slot);
        var pending = new List<Task<GuardedProfileSwitchResult>>();
        try
        {
            Mapping.ExecuteSerializedProfileMutation(Slot, () =>
            {
                for (int i = 0; i < 3; i++) pending.Add(fixture.LoadNamed());
                Assert.AreEqual(before, Global.ReadProfileSwitchRevision(Slot),
                    "Enqueue must not claim the live profile revision.");
            });
            var results = pending.Select(Await).ToArray();
            Assert.AreEqual(GuardedProfileSwitchStatus.Superseded, results[0].Status);
            Assert.AreEqual(GuardedProfileSwitchStatus.Superseded, results[1].Status);
            Assert.IsTrue(results[2].Applied, results[2].Error);
            Assert.AreEqual(before + 1, Global.ReadProfileSwitchRevision(Slot));
        }
        finally { foreach (var task in pending) Await(task); }
    }

    [TestMethod]
    public void ExplicitTemporaryRequestSupersedesNamedWithoutLosingItsCompletion()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        Task<GuardedProfileSwitchResult> named = null;
        var ordinary = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Mapping.ExecuteSerializedProfileMutation(Slot, () =>
            {
                named = fixture.LoadNamed();
                Mapping.RequestTemporaryProfileLoad(Slot, "Candidate", false, fixture.Service,
                    loaded => ordinary.TrySetResult(loaded));
            });
            Assert.IsFalse(Await(named).Applied);
            Assert.IsTrue(ordinary.Task.Wait(5000));
            Assert.IsTrue(ordinary.Task.Result);
            Assert.IsTrue(Global.useTempProfile[Slot]);
            Assert.AreEqual("Original", Global.ProfilePath[Slot]);
        }
        finally
        {
            if (named != null) Await(named);
            Assert.IsTrue(ordinary.Task.Wait(5000));
        }
    }

    [TestMethod]
    public void NamedRequestCannotApplyToReusedLifetimeAfterEnqueue()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        var source = fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        long before = Global.ReadProfileSwitchRevision(Slot);
        Task<GuardedProfileSwitchResult> task = null;
        try
        {
            Mapping.ExecuteSerializedProfileMutation(Slot, () =>
            {
                task = fixture.LoadNamed();
                fixture.RetireAndReattachSameSource(source);
            });
            Assert.AreEqual(GuardedProfileSwitchStatus.SourceUnavailable, Await(task).Status);
            Assert.AreEqual(before, Global.ReadProfileSwitchRevision(Slot));
            Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        }
        finally { if (task != null) Await(task); }
    }

    [TestMethod]
    public void NamedApplicationRejectsBackendContentionWithoutWaitingOrClaiming()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.AttachSwitch2();
        fixture.EnableNamedSwitching();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        long before = Global.ReadProfileSwitchRevision(Slot);
        Task holder = Task.Run(() =>
        {
            lock (fixture.KbmGate)
            {
                entered.Set();
                Assert.IsTrue(release.Wait(5000));
            }
        });
        try
        {
            Assert.IsTrue(entered.Wait(2000));
            Assert.AreEqual(GuardedProfileSwitchStatus.AdmissionBusy, Await(fixture.LoadNamed()).Status);
            Assert.AreEqual(before, Global.ReadProfileSwitchRevision(Slot));
            Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        }
        finally { release.Set(); Assert.IsTrue(holder.Wait(2000)); }
        Assert.IsTrue(Await(fixture.LoadNamed()).Applied);
    }

    [TestMethod]
    public void ConsumedCandidateIsRejectedBeforeAnyLiveReset()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        fixture.AttachSource();
        Assert.IsTrue(PreparedProfileLoad.TryPrepare(fixture.Path, Slot, out var prepared, out _, out _));
        prepared.ApplyTo(new BackingStore());
        bool changed = true;
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Store.ApplyPreparedProfileNew(
            prepared, false, fixture.Service, out changed));
        Assert.IsFalse(changed);
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
    }

    private static GuardedProfileSwitchResult Await(Task<GuardedProfileSwitchResult> task)
    {
        Assert.IsTrue(task.Wait(5000), "Every guarded request must complete, including rejection/coalescing.");
        return task.GetAwaiter().GetResult();
    }

    [TestMethod]
    public void RegisteredAutoLoadAppliesAndReleasesItsLeaseBeforeQueuedOutput()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        var source = fixture.AttachSwitch2();
        Assert.IsTrue(fixture.Service.TryCaptureProfileActionTarget(Slot, source, out var captured));
        Assert.IsTrue(Global.TryLoadAutoProfile(Slot, source, "Candidate", false, fixture.Service));
        Assert.AreEqual(42, (int)fixture.Store.rumble[Slot]);
        Assert.IsTrue(Global.useTempProfile[Slot]);
        Assert.IsFalse(fixture.Table.GetSnapshot()[Slot].ActionActive);
        Assert.IsFalse(fixture.Table.GetSnapshot()[Slot].ActionPending);
        Assert.IsTrue(fixture.Table.TryAcquireReportLease(fixture.Token, source, out var report, out _));
        report.Dispose();

        Action queued = fixture.DequeueOutput(source);
        fixture.RetireAndReattachSameSource(source);
        Assert.IsFalse(source.IsRemoving);
        // Deliberately retain the source reference and profile revision. Only
        // the captured slot generation can reject this old output callback.
        Assert.IsFalse(captured.TryAcquire(out var staleLease));
        Assert.IsFalse(staleLease.IsValid);
        queued();
        Assert.IsFalse(fixture.Table.GetSnapshot()[Slot].ActionActive);
    }

    [TestMethod]
    public void AutoLoadCannotRecaptureANewerLifetimeAfterPreparation()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        var source = fixture.AttachSwitch2();
        Task<bool> pending = null;
        long before = Global.ReadProfileSwitchRevision(Slot);
        try
        {
            Mapping.ExecuteSerializedProfileMutation(Slot, () =>
            {
                pending = Task.Run(() => Global.TryLoadAutoProfile(Slot, source,
                    "Candidate", false, fixture.Service));
                Assert.IsTrue(SpinWait.SpinUntil(() => Global.ReadProfileSwitchRevision(Slot) != before, 2000));
                Assert.IsFalse(fixture.Table.GetSnapshot()[Slot].ActionActive,
                    "Cold preparation/contention must not retain action admission.");
                fixture.RetireAndReattachSameSource(source);
            });
            Assert.IsTrue(pending.Wait(5000));
            Assert.IsFalse(pending.Result);
            Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        }
        finally
        {
            if (pending != null)
                Assert.IsTrue(pending.Wait(5000));
        }
    }

    [TestMethod]
    public void RegisteredSourceNeverFallsBackWhenItsTableIsMissingOrClosed()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        var source = fixture.AttachSwitch2(register: false);
        Assert.IsFalse(Global.TryLoadAutoProfile(Slot, source, "Candidate", false, fixture.Service));
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        fixture.RegisterSwitch2(source);
        Assert.IsTrue(fixture.Table.TryClose(1, out _, out _));
        Assert.IsFalse(Global.TryLoadAutoProfile(Slot, source, "Candidate", false, fixture.Service));
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
    }

    [TestMethod]
    public void BusyActionAdmissionPreservesProfileAndAllowsAFreshRetry()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        var source = fixture.AttachSwitch2();
        Assert.IsTrue(fixture.Table.TryAcquireReportLease(fixture.Token, source, out var report, out _));
        try
        {
            Assert.IsFalse(Global.TryLoadAutoProfile(Slot, source, "Candidate", false, fixture.Service));
            Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
            Assert.IsFalse(fixture.Table.GetSnapshot()[Slot].ActionPending);
        }
        finally { report.Dispose(); }
        Assert.IsTrue(Global.TryLoadAutoProfile(Slot, source, "Candidate", false, fixture.Service));
        Assert.AreEqual(42, (int)fixture.Store.rumble[Slot]);
    }

    [TestMethod]
    public void AutoLoadWaitsBeforePausingAndAppliesThePreparedSnapshot()
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        var source = fixture.AttachSource();
        source.BeforeAction = () => File.WriteAllText(fixture.Path, "File replaced after prepare.");
        Task<bool> pending = null;
        long before = Global.ReadProfileSwitchRevision(Slot);
        try
        {
            Mapping.ExecuteSerializedProfileMutation(Slot, () =>
            {
                pending = Task.Run(() => Global.TryLoadAutoProfile(Slot, source,
                    "Candidate", false, fixture.Service));
                Assert.IsTrue(SpinWait.SpinUntil(() => Global.ReadProfileSwitchRevision(Slot) != before, 2000));
                Assert.IsFalse(source.PauseEntered.Wait(150),
                    "Writer contention must not begin report suppression.");
            });
            Assert.IsTrue(pending.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(pending.Result);
            Assert.AreEqual(42, (int)fixture.Store.rumble[Slot]);
            Assert.AreEqual("Candidate", Global.tempprofilename[Slot]);
            Assert.IsTrue(source.FireReport);
            Assert.AreEqual("File replaced after prepare.", File.ReadAllText(fixture.Path));
        }
        finally
        {
            if (pending != null)
                Assert.IsTrue(pending.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public void AutoLoadRejectsInvalidFileBeforePausing()
    {
        using var fixture = new Fixture();
        var source = fixture.AttachSource();
        Assert.IsFalse(Global.TryLoadAutoProfile(Slot, source, "Candidate", false, fixture.Service));
        Assert.IsFalse(source.PauseEntered.IsSet);
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AutoLoadRechecksSourceAndRevisionInsidePause(bool replaceSource)
    {
        using var fixture = new Fixture();
        fixture.MakeValid();
        var source = fixture.AttachSource();
        source.BeforeAction = () =>
        {
            if (replaceSource)
                fixture.Service.DS4Controllers[Slot] = null;
            else
                Global.BeginProfileSwitchRevision(Slot);
        };
        Assert.IsFalse(Global.TryLoadAutoProfile(Slot, source, "Candidate", false, fixture.Service));
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        Assert.IsTrue(source.FireReport);
    }

    [TestMethod]
    public void AutoLoadSavesMigrationOnlyAfterReportsResume()
    {
        using var fixture = new Fixture();
        fixture.MakeValid("4");
        string original = File.ReadAllText(fixture.Path);
        var source = fixture.AttachSource();
        source.BeforeResume = () => Assert.AreEqual(original, File.ReadAllText(fixture.Path));
        Assert.IsTrue(Global.TryLoadAutoProfile(Slot, source, "Candidate", false, fixture.Service));
        Assert.IsTrue(source.FireReport);
        Assert.AreNotEqual(original, File.ReadAllText(fixture.Path));
        StringAssert.Contains(File.ReadAllText(fixture.Path), "config_version=\"5\"");
    }

    [TestMethod]
    public void RejectedPauseDoesNotApplyOrSaveCandidate()
    {
        using var fixture = new Fixture();
        fixture.MakeValid("4");
        string original = File.ReadAllText(fixture.Path);
        var source = fixture.AttachSource();
        source.AllowPause = false;
        Assert.IsFalse(Global.TryLoadAutoProfile(Slot, source, "Candidate", false, fixture.Service));
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        Assert.AreEqual(original, File.ReadAllText(fixture.Path));
    }

    [TestMethod]
    public void BasePauseRestoresReportingAfterAnException()
    {
        using var source = new FakeSource { UseBasePause = true };
        source.ReadWaitEv.Set();
        Assert.ThrowsException<InvalidOperationException>(() =>
            source.TryHaltReportingRunAction(() =>
            {
                Assert.IsFalse(source.FireReport);
                throw new InvalidOperationException("Synthetic failed apply");
            }));
        Assert.IsTrue(source.FireReport);
        source.FireReport = false;
        Assert.IsTrue(source.TryHaltReportingRunAction(() => { }));
        Assert.IsFalse(source.FireReport, "Preserve an already-paused caller's state.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DirectLoadWaitsForTheSameMutationBoundaryAsWorkerAndUi(bool temporary)
    {
        using var fixture = new Fixture();
        using var starting = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Task pending = null;
        try
        {
            Mapping.ExecuteSerializedProfileMutation(Slot, () =>
            {
                pending = Task.Run(() =>
                {
                    starting.Set();
                    try { fixture.Load(temporary); }
                    finally { finished.Set(); }
                });
                Assert.IsTrue(starting.Wait(TimeSpan.FromSeconds(2)));
                Assert.IsFalse(finished.Wait(TimeSpan.FromMilliseconds(150)),
                    "A direct profile load bypassed the worker/UI mutation boundary.");
            });
        }
        finally
        {
            if (pending != null)
            {
                Assert.IsTrue(pending.Wait(TimeSpan.FromSeconds(5)));
                pending.GetAwaiter().GetResult();
            }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SupersededDirectLoadDoesNotReachMissingFileReset(bool temporary)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Path);
        long oldRevision = Global.BeginProfileSwitchRevision(Slot);
        long currentRevision = Global.BeginProfileSwitchRevision(Slot);
        Exception failure = null;
        bool loaded = true;
        try { loaded = fixture.Load(temporary, oldRevision); }
        catch (Exception ex) { failure = ex; }
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        Assert.IsNull(failure, failure?.ToString());
        Assert.IsFalse(loaded);
        Assert.AreEqual(currentRevision, Global.ReadProfileSwitchRevision(Slot));
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly FieldInfo StoreField = typeof(Global).GetField("m_Config",
            BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly long[] Revisions = (long[])typeof(Global).GetField("profileSwitchRevisions",
            BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        private readonly object oldStore = StoreField.GetValue(null);
        private readonly string oldRoot = Global.appdatapath;
        private readonly long oldRevision = Global.ReadProfileSwitchRevision(Slot);
        private readonly string oldTemp = Global.tempprofilename[Slot];
        private readonly bool oldUseTemp = Global.useTempProfile[Slot];
        private readonly bool oldDistance = Global.tempprofileDistance[Slot];
        private readonly bool oldTouch = Global.TouchActive[Slot];
        private readonly bool oldForceLight = DS4LightBar.forcelight[Slot];
        private readonly byte oldFlash = DS4LightBar.forcedFlash[Slot];
        private readonly VirtualKBMMapping oldKbmMapping = Global.outputKBMMapping;
        private readonly string directory;
        private readonly string profiles;
        private string otherPath;
        internal readonly BackingStore Store = new();
        internal readonly string Path;
        internal ControlService Service;
        private FakeSource source;
        private Switch2RuntimeInputDevice switch2Source;
        private DS4Device legacySource;
        internal InputControllerRegistrationTable Table;
        internal InputControllerSlotToken Token;
        internal object KbmGate;

        internal void EnableNamedSwitching()
        {
            Store.profilePath[Slot] = "Original";
            Global.useTempProfile[Slot] = false;
            KbmGate = new object();
            typeof(ControlService).GetField("outputKbmHandlerLock", BindingFlags.Instance |
                BindingFlags.NonPublic).SetValue(Service, KbmGate);
            Global.outputKBMMapping = new SendInputMapping(); // Key aliases only; no input injection.
        }

        internal Task<GuardedProfileSwitchResult> LoadNamed(Func<bool> guard = null)
        {
            Assert.IsTrue(Service.TryCaptureProfileActionTarget(Slot, switch2Source, out var target));
            return Mapping.RequestNamedRegularProfileLoad(target, "Candidate", "Original",
                Global.ReadProfileSwitchRevision(Slot), false, Service, guard ?? (() => true));
        }

        internal void MakeValid(string version = "5") => File.WriteAllText(Path,
            $"<DS4Windows config_version=\"{version}\"><RumbleBoost>42</RumbleBoost></DS4Windows>");

        internal void WriteOtherProfile()
        {
            otherPath = System.IO.Path.Combine(profiles, "Other.xml");
            File.WriteAllText(otherPath, "<DS4Windows config_version=\"5\"><RumbleBoost>21</RumbleBoost></DS4Windows>");
        }

        internal FakeSource AttachSource()
        {
            source = new FakeSource();
            InitializeService(source);
            return source;
        }

        private void InitializeService(DS4Device device)
        {
            Service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            Service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
            Service.DS4Controllers[Slot] = device;
            Service.touchPad = new Mouse[Global.MAX_DS4_CONTROLLER_COUNT];
            Service.touchreleased = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        }

        internal Switch2RuntimeInputDevice AttachSwitch2(bool register = true)
        {
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(94_001, 94_002,
                Switch2Transport.Usb, out switch2Source, out _));
            InitializeService(switch2Source);
            switch2Source.StartUpdate(); // Synthetic runtime only, no HID/transport/worker.
            if (register)
                RegisterSwitch2(switch2Source);
            return switch2Source;
        }

        internal DS4Device AttachLegacy()
        {
            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            legacySource = new DS4Device(hid, "Profile legacy queue test");
            // Model an attached, initialized legacy source without PostInit
            // or any real HID access; post-load queues require a synced pad.
            typeof(DS4Device).GetField("synced", BindingFlags.Instance |
                BindingFlags.NonPublic).SetValue(legacySource, true);
            InitializeService(legacySource);
            Table = new InputControllerRegistrationTable(Global.MAX_DS4_CONTROLLER_COUNT);
            Assert.IsTrue(Table.TryOpen(1, out _));
            typeof(ControlService).GetField("inputRegistrationTable", BindingFlags.NonPublic |
                BindingFlags.Instance).SetValue(Service, Table);
            var owner = new LegacyTestOwner(legacySource);
            Assert.IsTrue(InputControllerRegistration.TryCreate(legacySource, 95_001,
                InputControllerOwnershipKind.LegacyHid, true, false, owner, out var registration, out _));
            Assert.IsTrue(Table.TryReserveAndBindExactSlot(Slot, registration, out Token, out _, out _));
            Assert.IsTrue(Table.TryActivate(Token, out _));
            return legacySource;
        }

        internal void RegisterSwitch2(Switch2RuntimeInputDevice device)
        {
            if (Table == null)
            {
                Table = new InputControllerRegistrationTable(Global.MAX_DS4_CONTROLLER_COUNT);
                Assert.IsTrue(Table.TryOpen(1, out _));
                typeof(ControlService).GetField("inputRegistrationTable",
                    BindingFlags.NonPublic | BindingFlags.Instance).SetValue(Service, Table);
            }
            var owner = new TestOwner(device);
            Assert.IsTrue(InputControllerRegistration.TryCreate(device, device.RuntimeGeneration,
                InputControllerOwnershipKind.Switch2Runtime, false, false, owner,
                out var registration, out _));
            Assert.IsTrue(Table.TryReserveAndBindExactSlot(Slot, registration, out Token, out _, out _));
            Assert.IsTrue(Table.TryActivate(Token, out _));
        }

        internal void RetireAndReattachSameSource(Switch2RuntimeInputDevice device)
        {
            Assert.IsTrue(Table.TryBeginRetire(Token, out var claim, out _));
            Assert.IsTrue(Table.TryAcquireTerminalReportLease(claim, device, out var terminal, out _));
            Assert.IsTrue(terminal.TryAcknowledgeTerminalNeutral(out _));
            terminal.Dispose();
            Assert.IsTrue(Table.TryMarkQuiesced(claim, out _));
            Assert.IsTrue(Table.TryCompleteRemoval(claim, out _));
            RegisterSwitch2(device);
        }

        internal Action DequeueOutput(DS4Device device)
        {
            var queue = (Queue<Action>)typeof(DS4Device).GetField("eventQueue",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(device);
            Assert.AreEqual(1, queue.Count);
            return queue.Dequeue();
        }

        internal Fixture()
        {
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"ds4w-profile-boundary-{Guid.NewGuid():N}");
            profiles = System.IO.Path.Combine(directory, "Profiles");
            Directory.CreateDirectory(profiles);
            Path = System.IO.Path.Combine(profiles, "Candidate.xml");
            File.WriteAllText(Path, "<DS4Windows>");
            Store.profilePath[Slot] = "Candidate";
            Store.rumble[Slot] = 77;
            StoreField.SetValue(null, Store);
            Global.appdatapath = directory;
        }

        internal bool Load(bool temporary, long revision = 0) => temporary ?
            Global.LoadTempProfile(Slot, "Candidate", false, null, transitionRevision: revision) :
            Global.LoadProfile(Slot, false, null, transitionRevision: revision);

        public void Dispose()
        {
            StoreField.SetValue(null, oldStore);
            Global.appdatapath = oldRoot;
            Global.tempprofilename[Slot] = oldTemp;
            Global.useTempProfile[Slot] = oldUseTemp;
            Global.tempprofileDistance[Slot] = oldDistance;
            Global.TouchActive[Slot] = oldTouch;
            DS4LightBar.forcelight[Slot] = oldForceLight;
            DS4LightBar.forcedFlash[Slot] = oldFlash;
            Global.outputKBMMapping = oldKbmMapping;
            Interlocked.Exchange(ref Revisions[Slot], oldRevision);
            source?.Dispose();
            switch2Source?.ReadWaitEv.Dispose();
            legacySource?.ReadWaitEv.Dispose();
            File.Delete(Path);
            if (otherPath != null) File.Delete(otherPath);
            Directory.Delete(profiles);
            Directory.Delete(directory);
        }
    }

    private sealed class BlockingSendInputMapping : SendInputMapping, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        private int visited;

        public override uint GetRealEventKey(uint winVkKey)
        {
            if (Interlocked.Exchange(ref visited, 1) == 0)
            {
                Entered.Set();
                Assert.IsTrue(Release.Wait(5000));
            }
            return base.GetRealEventKey(winVkKey);
        }

        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class TestOwner(Switch2RuntimeInputDevice device) : IInputControllerRegistrationOwner
    {
        public InputControllerOwnershipKind Kind => InputControllerOwnershipKind.Switch2Runtime;
        public bool Authenticates(DS4Device candidate, ulong generation) =>
            ReferenceEquals(device, candidate) && generation == device.RuntimeGeneration;
        public bool TryStopAndQuiesce(DS4Device candidate, ulong generation, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        {
            failure = InputControllerOwnerOperationFailure.StopRejected;
            return false; // These tests only exercise table authority, not an owner stop.
        }
        public bool TryRemove(DS4Device candidate, ulong generation,
            out InputControllerOwnerOperationFailure failure)
        {
            failure = InputControllerOwnerOperationFailure.RemoveRejected;
            return false;
        }
    }

    private sealed class LegacyTestOwner(DS4Device device) : IInputControllerRegistrationOwner
    {
        public InputControllerOwnershipKind Kind => InputControllerOwnershipKind.LegacyHid;
        public bool Authenticates(DS4Device candidate, ulong generation) => ReferenceEquals(device, candidate) && generation == 95_001;
        public bool TryStopAndQuiesce(DS4Device candidate, ulong generation, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        {
            failure = InputControllerOwnerOperationFailure.StopRejected;
            return false;
        }
        public bool TryRemove(DS4Device candidate, ulong generation, out InputControllerOwnerOperationFailure failure)
        {
            failure = InputControllerOwnerOperationFailure.RemoveRejected;
            return false;
        }
    }

    private sealed class FakeSource : DS4Device, IDisposable
    {
        internal FakeSource() : base("Profile pause test", InputDeviceType.DS4, ConnectionType.USB)
        {
            synced = false; // No virtual output transition or queued hardware action.
        }

        internal readonly ManualResetEventSlim PauseEntered = new();
        internal bool UseBasePause;
        internal bool AllowPause = true;
        internal Action BeforeAction;
        internal Action BeforeResume;

        public override bool TryHaltReportingRunAction(Action action)
        {
            if (UseBasePause)
                return base.TryHaltReportingRunAction(action);
            PauseEntered.Set();
            if (!AllowPause)
                return false;
            bool previous = FireReport;
            FireReport = false;
            try
            {
                BeforeAction?.Invoke();
                action();
                BeforeResume?.Invoke();
                return true;
            }
            finally { FireReport = previous; }
        }

        public void Dispose()
        {
            PauseEntered.Dispose();
            ReadWaitEv.Dispose();
        }
    }
}
