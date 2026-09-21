using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.DS4Control;
using FakerInputWrapper;

namespace DS4WindowsTests;

/// <summary>
/// Real mapper commit/macro entry points, with a logical backend sink only.
/// No Connect, native injection, device, thread, window, or live profile is used.
/// The mapped lane is the same per-frame MapClick/Commit lane used by R2 and
/// touchpad mappings; full R2 action dispatch is covered by the mapping tests.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MacroMouseOwnershipTests
{
    private delegate bool MacroStep(int device, bool[] controls, DS4KeyType type,
        int code, bool[] down);
    private delegate void MacroTask(int device, bool[] controls, string text,
        List<int> list, int[] codes, DS4Controls control, DS4KeyType type,
        SpecialAction action, Mapping.ActionState done);
    private delegate void EpochMacroTask(int device, bool[] controls, string text,
        List<int> list, int[] codes, DS4Controls control, DS4KeyType type,
        SpecialAction action, Mapping.ActionState done, int mouseEpoch);
    private delegate void RetainMacro(int device, bool[] down);

    private static readonly MacroStep Step = typeof(Mapping).GetMethod(
        "PlayMacroCodeValue", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<MacroStep>();
    private static readonly MacroTask TaskBody = typeof(Mapping).GetMethod(
        "PlayMacroTask", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<MacroTask>();
    private static readonly EpochMacroTask EpochTaskBody = typeof(Mapping).GetMethod(
        "PlayMacroTaskInEpoch", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<EpochMacroTask>();
    private static readonly RetainMacro Retain = typeof(Mapping).GetMethod(
        "RetainMacroMouseButtons", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<RetainMacro>();
    private static readonly RetainMacro Register = typeof(Mapping).GetMethod(
        "RegisterMacroMouseOwner", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<RetainMacro>();

    public static IEnumerable<object[]> ButtonCases()
    {
        foreach (bool faker in new[] { false, true })
            for (int button = 0; button < 5; button++)
                yield return new object[] { faker, button };
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void MappedHoldSurvivesMacroPressAndRelease(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] macro = new bool[512];
        fixture.MappedFrame(button);
        fixture.AssertHeld(button);
        fixture.MacroEdge(button, macro);
        fixture.AssertHeld(button);
        fixture.MappedFrame(button);
        fixture.MacroEdge(button, macro);
        fixture.AssertHeld(button);
        fixture.MappedFrame(button); // No artificial re-press may be required.
        fixture.AssertHeld(button);
        Mapping.Commit(0);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void MacroHoldSurvivesMappedPressAndRelease(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] macro = new bool[512];
        fixture.MacroEdge(button, macro);
        fixture.MappedFrame(button);
        fixture.AssertHeld(button);
        Mapping.Commit(0); // Release only the mapped owner.
        fixture.AssertHeld(button);
        fixture.MacroEdge(button, macro);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void OverlappingMacroOwnersReleaseOnlyTheirOwnHold(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] first = new bool[512], second = new bool[512];
        fixture.MacroEdge(button, first, device: 0);
        fixture.MacroEdge(button, second, device: 1);
        fixture.AssertHeld(button);
        fixture.MacroEdge(button, first, device: 0);
        fixture.AssertHeld(button);
        fixture.MacroEdge(button, second, device: 1);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void ActualMacroCompletionCleanupCannotReleaseMappedOwner(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.MappedFrame(button);
        fixture.RunMacroWithUnreleasedLastButton(button);
        fixture.AssertHeld(button);
        fixture.MappedFrame(button);
        fixture.AssertHeld(button);
        Mapping.Commit(0);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void ActualMacroCompletionCleanupCannotReleaseAnotherMacro(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] retained = new bool[512];
        fixture.MacroEdge(button, retained);
        fixture.RunMacroWithUnreleasedLastButton(button);
        fixture.AssertHeld(button);
        fixture.MacroEdge(button, retained);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void ActualMacroCompletionReleasesItsUnsharedButtonExactlyOnce(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.RunMacroWithUnreleasedLastButton(button);
        fixture.AssertReleasedOnce();
        Mapping.Commit(0);
        fixture.AssertReleasedOnce(); // No stale ownership replays an edge.
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void KeepKeyStateIsClaimedByNextMacroAndCanBeFullyReleased(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.RunKeptMacro(button);
        fixture.AssertHeld(button);
        fixture.AssertMacroOwnership(button, count: 1, retainedSlots: 1);
        fixture.RunReleasePair(button);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
        Mapping.Commit(0);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void RepeatedKeptMacrosDoNotAccumulateUnreachableOwners(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        for (int index = 0; index < 4; index++)
        {
            fixture.RunKeptMacro(button);
            fixture.AssertHeld(button);
            fixture.AssertMacroOwnership(button, count: 1, retainedSlots: 1);
        }
        fixture.RunReleasePair(button);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void OverlappingKeptCompletionsCoalesceOnlyTheirRetainedOwner(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] first = new bool[512];
        fixture.MacroEdge(button, first);
        fixture.RunKeptMacro(button); // Second invocation finishes before first.
        fixture.AssertMacroOwnership(button, count: 2, retainedSlots: 1);
        Retain(0, first); // The actual completion helper for the first invocation.
        fixture.AssertHeld(button);
        fixture.AssertMacroOwnership(button, count: 1, retainedSlots: 1);
        fixture.RunReleasePair(button);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void ClaimingRetainedHoldDoesNotStealAnotherActiveMacroOwner(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] first = new bool[512], successor = new bool[512];
        fixture.MacroEdge(button, first);
        fixture.RunKeptMacro(button);
        fixture.AssertMacroOwnership(button, count: 2, retainedSlots: 1);
        fixture.MacroEdge(button, successor); // Claims only the retained owner.
        fixture.AssertMacroOwnership(button, count: 2, retainedSlots: 0);
        fixture.MacroEdge(button, first);
        fixture.AssertHeld(button);
        fixture.AssertMacroOwnership(button, count: 1, retainedSlots: 0);
        fixture.MacroEdge(button, successor);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void RetainedOwnersFromDifferentDevicesAreReleasedIndependently(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.RunKeptMacro(button, device: 0);
        fixture.RunKeptMacro(button, device: 1);
        fixture.AssertHeld(button);
        fixture.AssertMacroOwnership(button, count: 2, retainedSlots: 3);
        fixture.RunReleasePair(button, device: 0);
        fixture.AssertHeld(button);
        fixture.AssertMacroOwnership(button, count: 1, retainedSlots: 2);
        fixture.RunReleasePair(button, device: 1);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void RetainedMacroSurvivesOrdinaryBindingRelease(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.MappedFrame(button);
        fixture.RunKeptMacro(button);
        fixture.AssertHeld(button);
        Mapping.Commit(0);
        fixture.AssertHeld(button);
        fixture.RunReleasePair(button);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void OrdinaryBindingSurvivesRetainedMacroClaimAndRelease(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.RunKeptMacro(button);
        fixture.MappedFrame(button);
        fixture.RunReleasePair(button);
        fixture.AssertHeld(button);
        fixture.MappedFrame(button);
        fixture.AssertHeld(button);
        Mapping.Commit(0);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void NeutralRetiresActiveMacroAndRejectsItsDelayedSteps(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] stale = new bool[512];
        fixture.MacroEdge(button, stale);
        Mapping.CommitNeutral(0);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
        fixture.MacroEdge(button, stale); // Delayed cleanup must not subtract a new owner.
        fixture.MacroEdge(button, stale); // Nor may an old invocation press again.
        Retain(0, stale);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
        bool[] current = new bool[512];
        fixture.MacroEdge(button, current);
        Assert.AreEqual(1 << button, fixture.Sink.HeldMask);
        fixture.MacroEdge(button, stale);
        Assert.AreEqual(1 << button, fixture.Sink.HeldMask);
        fixture.AssertMacroOwnership(button, count: 1, retainedSlots: 0);
        fixture.MacroEdge(button, current);
        Assert.AreEqual(0, fixture.Sink.HeldMask);
        Assert.AreEqual(4, fixture.Sink.Events);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void NeutralRetiresMacroRegisteredBeforeItsFirstMouseStep(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] waiting = new bool[512];
        Register(0, waiting); // Actual executor registration precedes macro delays.
        Mapping.CommitNeutral(0);
        fixture.MacroEdge(button, waiting);
        Retain(0, waiting);
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
        Assert.AreEqual(0, fixture.Sink.Events);
        Assert.AreEqual(0, fixture.Sink.HeldMask);
        fixture.MappedFrame(button);
        fixture.AssertHeld(button);
        Mapping.Commit(0);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void QueuedMacroFromRetiredEpochCannotClaimSuccessorRetainedHold(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        int queuedEpoch = Mapping.globalState.macroEpochs[0];
        Mapping.CommitNeutral(0);
        fixture.RunMacroInEpoch(button, queuedEpoch, keep: false);
        fixture.RunMacroInEpoch(button, queuedEpoch, keep: true);
        Assert.AreEqual(0, fixture.Sink.Events);
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
        fixture.RunKeptMacro(button);
        fixture.AssertHeld(button);
        fixture.RunMacroInEpoch(button, queuedEpoch, keep: false);
        fixture.RunMacroInEpoch(button, queuedEpoch, keep: true);
        fixture.AssertHeld(button);
        fixture.AssertMacroOwnership(button, count: 1, retainedSlots: 1);
        fixture.RunReleasePair(button);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void NeutralClearsRetainedMacroWithoutSuppressingNextOrdinaryPress(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.RunKeptMacro(button);
        Mapping.CommitNeutral(0);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
        Mapping.CommitNeutral(0); // Repeated retirement is idempotent.
        fixture.AssertReleasedOnce();
        fixture.MappedFrame(button);
        Assert.AreEqual(1 << button, fixture.Sink.HeldMask);
        Assert.AreEqual(3, fixture.Sink.Events);
        Mapping.Commit(0);
        Assert.AreEqual(0, fixture.Sink.HeldMask);
        Assert.AreEqual(4, fixture.Sink.Events);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void NeutralPreservesOtherDeviceMacroAndOrdinaryOwners(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] first = new bool[512], other = new bool[512];
        fixture.MacroEdge(button, first, device: 0);
        fixture.MacroEdge(button, other, device: 1);
        fixture.MappedFrame(button, device: 1);
        Mapping.CommitNeutral(0);
        fixture.AssertHeld(button);
        fixture.AssertMacroOwnership(button, count: 1, retainedSlots: 0);
        fixture.MacroEdge(button, first);
        fixture.MacroEdge(button, other, device: 1);
        fixture.AssertHeld(button);
        Mapping.CommitNeutral(1);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void NeutralClearsRetainedOwnersOnlyForItsDevice(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.RunKeptMacro(button, device: 0);
        fixture.RunKeptMacro(button, device: 1);
        Mapping.CommitNeutral(0);
        fixture.AssertHeld(button);
        fixture.AssertMacroOwnership(button, count: 1, retainedSlots: 2);
        fixture.RunReleasePair(button, device: 1);
        fixture.AssertReleasedOnce();
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void MacroPairPublishesDownBeforeUpToBufferedBackend(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.Sink.CaptureSync = true;
        fixture.RunReleasePair(button);
        fixture.AssertReleasedOnce();
        Assert.IsTrue(fixture.Sink.SyncedMasks.Count >= 2,
            "A completed macro pair must not collapse to an all-up report.");
        Assert.AreEqual(1 << button, fixture.Sink.SyncedMasks[0]);
        Assert.AreEqual(0, fixture.Sink.SyncedMasks[^1]);
        Assert.AreEqual(1, fixture.Sink.SyncedMasks.Count(mask => mask == 1 << button));
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void BackendReplacementReprojectsMappedHoldAndLaterRelease(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.MappedFrame(button);
        RecordingHandler successor = ReplaceBackend(!faker);
        Assert.AreEqual(1 << button, successor.HeldMask);
        Assert.AreEqual(1, successor.Events);
        CollectionAssert.AreEqual(new[] { 1 << button }, successor.SyncedMasks);
        fixture.MappedFrame(button);
        Assert.AreEqual(1, successor.Events, "A stable held binding must not duplicate the projection.");
        Mapping.Commit(0);
        Assert.AreEqual(0, successor.HeldMask);
        Assert.AreEqual(2, successor.Events);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void BackendReplacementPreservesUnionOfActiveRetainedAndMappedOwners(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        bool[] active = new bool[512];
        fixture.MacroEdge(button, active);
        fixture.RunKeptMacro(button);
        fixture.MappedFrame(button);
        RecordingHandler successor = ReplaceBackend(!faker);
        Assert.AreEqual(1 << button, successor.HeldMask);
        Assert.AreEqual(1, successor.Events);
        fixture.MacroEdge(button, active);
        fixture.RunReleasePair(button);
        Assert.AreEqual(1 << button, successor.HeldMask);
        Assert.AreEqual(1, successor.Events);
        Mapping.Commit(0);
        Assert.AreEqual(0, successor.HeldMask);
        Assert.AreEqual(2, successor.Events);
        fixture.AssertMacroOwnership(button, count: 0, retainedSlots: 0);
    }

    [DataTestMethod]
    [DynamicData(nameof(ButtonCases), DynamicDataSourceType.Method)]
    public void BackendReplacementRollbackDoesNotReplayToggleOnOriginalBackend(bool faker, int button)
    {
        using var fixture = new Fixture(faker);
        fixture.RunKeptMacro(button);
        VirtualKBMBase original = Global.outputKBMHandler;
        VirtualKBMMapping originalMapping = Global.outputKBMMapping;
        Mapping.ReplaceMouseOutput(() =>
        {
            // A failed replacement restores the original connected backend.
            // Replaying its Faker toggle event here would invert the hold.
            Global.outputKBMHandler = new RecordingHandler(faker, originalMapping);
            Global.outputKBMHandler = original;
            Global.outputKBMMapping = originalMapping;
            return false;
        });
        fixture.AssertHeld(button);
        fixture.RunReleasePair(button);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BackendReplacementReprojectsAllFiveHeldButtonsInOneSync(bool faker)
    {
        using var fixture = new Fixture(faker);
        for (int button = 0; button < 5; button++) fixture.RunKeptMacro(button);
        RecordingHandler successor = ReplaceBackend(!faker);
        Assert.AreEqual(31, successor.HeldMask);
        Assert.AreEqual(5, successor.Events);
        CollectionAssert.AreEqual(new[] { 31 }, successor.SyncedMasks);
        Mapping.CommitNeutral(0);
        Assert.AreEqual(0, successor.HeldMask);
        Assert.AreEqual(10, successor.Events);
    }

    private static RecordingHandler ReplaceBackend(bool faker)
    {
        VirtualKBMMapping mapping = faker ? new FakerInputMapping() : new SendInputMapping();
        mapping.PopulateConstants();
        mapping.PopulateMappings();
        var successor = new RecordingHandler(faker, mapping) { CaptureSync = true };
        Mapping.ReplaceMouseOutput(() =>
        {
            Global.outputKBMHandler = successor;
            Global.outputKBMMapping = mapping;
            return true;
        });
        return successor;
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AllFiveButtonsHaveIndependentOwners(bool faker)
    {
        using var fixture = new Fixture(faker);
        bool[][] macros = Enumerable.Range(0, 5).Select(_ => new bool[512]).ToArray();
        for (int button = 0; button < 5; button++) fixture.MacroEdge(button, macros[button]);
        Assert.AreEqual(31, fixture.Sink.HeldMask);
        Assert.AreEqual(5, fixture.Sink.Events);
        for (int button = 0; button < 5; button++)
        {
            fixture.MacroEdge(button, macros[button]);
            Assert.AreEqual(31 & ~((1 << (button + 1)) - 1), fixture.Sink.HeldMask);
        }
        Assert.AreEqual(10, fixture.Sink.Events);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StableMappedOwnerAndMacroOverlapDoNotAllocatePerEdge(bool faker)
    {
        using var fixture = new Fixture(faker);
        bool[] macro = new bool[512];
        fixture.MappedFrame(0);
        for (int index = 0; index < 2000; index++)
        {
            fixture.MappedFrame(0);
            fixture.MacroEdgeUnchecked(0, macro);
            fixture.MacroEdgeUnchecked(0, macro);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 6000; index++)
        {
            fixture.MappedFrame(0);
            fixture.MacroEdgeUnchecked(0, macro);
            fixture.MacroEdgeUnchecked(0, macro);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated, "Cached typed delegates avoid reflection-invoke allocations in the measured path.");
        fixture.AssertHeld(0);
        Mapping.Commit(0);
        fixture.AssertReleasedOnce();
    }

    [DataTestMethod]
    [DataRow(1, MouseButton.XButton1)]
    [DataRow(2, MouseButton.XButton2)]
    public void ActualFakerExtendedEventUsesWrapperButtonAndPreservesHeldLeft(int type, MouseButton expected)
    {
        using var fixture = new FakerStateFixture();
        fixture.Report.ButtonDown(MouseButton.LeftButton);
        fixture.Report.MouseX = 17;
        fixture.Report.MouseY = -9;
        fixture.Report.WheelPosition = 3;
        fixture.Handler.PerformMouseButtonEventAlt(0, type);
        Assert.AreEqual(MouseButton.LeftButton | expected, fixture.Report.Buttons);
        Assert.IsTrue(fixture.Report.HeldButtons.Contains(MouseButton.LeftButton));
        Assert.IsTrue(fixture.Report.HeldButtons.Contains(expected));
        Assert.AreEqual((short)17, fixture.Report.MouseX);
        Assert.AreEqual((short)-9, fixture.Report.MouseY);
        Assert.AreEqual((byte)3, fixture.Report.WheelPosition);
        Assert.IsTrue(fixture.PendingRelativeSync);
        fixture.Handler.PerformMouseButtonEventAlt(0, type);
        Assert.AreEqual(MouseButton.LeftButton, fixture.Report.Buttons);
        Assert.IsFalse(fixture.Report.HeldButtons.Contains(expected));
    }

    [TestMethod]
    public void ActualFakerBothExtendedButtonsRemainIndependentOfEachOtherAndLeft()
    {
        using var fixture = new FakerStateFixture();
        fixture.Report.ButtonDown(MouseButton.LeftButton);
        fixture.Handler.PerformMouseButtonEventAlt(0, 1);
        fixture.Handler.PerformMouseButtonEventAlt(0, 2);
        Assert.AreEqual((MouseButton)25, fixture.Report.Buttons);
        fixture.Handler.PerformMouseButtonEventAlt(0, 1);
        Assert.AreEqual((MouseButton)17, fixture.Report.Buttons);
        fixture.Handler.PerformMouseButtonEventAlt(0, 2);
        Assert.AreEqual(MouseButton.LeftButton, fixture.Report.Buttons);
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(int.MaxValue)]
    public void ActualFakerUnknownExtendedButtonDoesNotMutateState(int type)
    {
        using var fixture = new FakerStateFixture();
        fixture.Report.ButtonDown(MouseButton.LeftButton);
        fixture.Handler.PerformMouseButtonEventAlt(0, type);
        Assert.AreEqual(MouseButton.LeftButton, fixture.Report.Buttons);
        Assert.AreEqual(1, fixture.Report.HeldButtons.Count);
        Assert.IsFalse(fixture.PendingRelativeSync);
    }

    [TestMethod]
    public void ActualFakerExtendedButtonEdgesAllocateNothingAfterWarmup()
    {
        using var fixture = new FakerStateFixture();
        fixture.Report.ButtonDown(MouseButton.LeftButton);
        for (int index = 0; index < 2000; index++) fixture.Handler.PerformMouseButtonEventAlt(0, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10000; index++) fixture.Handler.PerformMouseButtonEventAlt(0, 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(MouseButton.LeftButton, fixture.Report.Buttons);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly VirtualKBMBase savedHandler = Global.outputKBMHandler;
        private readonly VirtualKBMMapping savedMapping = Global.outputKBMMapping;
        private readonly Mapping.SyntheticState savedGlobal = Mapping.globalState;
        private readonly Mapping.SyntheticState[] savedDevices = Mapping.deviceState;
        private readonly bool[] savedLights = (bool[])DS4LightBar.forcelight.Clone();
        private readonly byte[] savedFlashes = (byte[])DS4LightBar.forcedFlash.Clone();
        private readonly bool[] controls = new bool[25];
        internal readonly RecordingHandler Sink;

        internal Fixture(bool faker)
        {
            VirtualKBMMapping mapping = faker ? new FakerInputMapping() : new SendInputMapping();
            mapping.PopulateConstants();
            mapping.PopulateMappings();
            Sink = new RecordingHandler(faker, mapping);
            Global.outputKBMHandler = Sink;
            Global.outputKBMMapping = mapping;
            Mapping.globalState = new Mapping.SyntheticState();
            Mapping.deviceState = Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
                .Select(_ => new Mapping.SyntheticState()).ToArray();
        }

        internal void MappedFrame(int button, int device = 0)
        {
            Mapping.MapClick(device, button switch
            {
                0 => Mapping.Click.Left, 1 => Mapping.Click.Right,
                2 => Mapping.Click.Middle, 3 => Mapping.Click.Fourth,
                4 => Mapping.Click.Fifth, _ => throw new ArgumentOutOfRangeException(nameof(button))
            });
            Mapping.Commit(device);
        }

        internal void MacroEdgeUnchecked(int button, bool[] down) =>
            Step(0, controls, DS4KeyType.None, 256 + button, down);

        internal void MacroEdge(int button, bool[] down, int device = 0) =>
            Assert.IsFalse(Step(device, controls, DS4KeyType.None, 256 + button, down));

        internal void RunMacroWithUnreleasedLastButton(int button) =>
            TaskBody(0, controls, string.Empty, null, new[] { 256 + button },
                DS4Controls.None, DS4KeyType.None, null, null);

        internal void RunKeptMacro(int button, int device = 0)
        {
            // Only the macro executor's keepKeyState contract is relevant;
            // do not parse a live special action or launch its background task.
            var action = (SpecialAction)RuntimeHelpers.GetUninitializedObject(typeof(SpecialAction));
            action.keepKeyState = true;
            TaskBody(device, controls, string.Empty, null, new[] { 256 + button },
                DS4Controls.None, DS4KeyType.None, action, null);
        }

        internal void RunReleasePair(int button, int device = 0) =>
            TaskBody(device, controls, string.Empty, null,
                new[] { 256 + button, 256 + button },
                DS4Controls.None, DS4KeyType.None, null, null);

        internal void RunMacroInEpoch(int button, int epoch, bool keep)
        {
            var action = (SpecialAction)RuntimeHelpers.GetUninitializedObject(typeof(SpecialAction));
            action.keepKeyState = keep;
            EpochTaskBody(0, controls, string.Empty, null, new[] { 256 + button },
                DS4Controls.None, DS4KeyType.None, action, null, epoch);
        }

        internal void AssertMacroOwnership(int button, int count, int retainedSlots)
        {
            Mapping.Click target = button switch
            {
                0 => Mapping.Click.Left, 1 => Mapping.Click.Right,
                2 => Mapping.Click.Middle, 3 => Mapping.Click.Fourth,
                _ => Mapping.Click.Fifth,
            };
            Assert.AreEqual(count, Mapping.globalState.macroClicks.ButtonCount(target));
            for (int device = 0; device < Global.MAX_DS4_CONTROLLER_COUNT; device++)
                Assert.AreEqual((retainedSlots & (1 << device)) != 0,
                    (Mapping.globalState.retainedMacroButtons[device] & (1 << button)) != 0,
                    $"Retained owner mismatch for device {device}.");
        }

        internal void AssertHeld(int button)
        {
            Assert.AreEqual(1 << button, Sink.HeldMask, "Another producer cannot release this owner.");
            Assert.AreEqual(1, Sink.Events, "Union ownership must emit only its first down edge.");
        }

        internal void AssertReleasedOnce()
        {
            Assert.AreEqual(0, Sink.HeldMask);
            Assert.AreEqual(2, Sink.Events, "One physical/logical down and one final up, not one pair per producer.");
            Assert.AreEqual(1, Sink.DownEvents);
            Assert.AreEqual(1, Sink.UpEvents);
        }

        public void Dispose()
        {
            // No cleanup dispatch into the real backend. Restore every shared
            // owner even when a pre-fix regression assertion fails.
            Global.outputKBMHandler = savedHandler;
            Global.outputKBMMapping = savedMapping;
            Mapping.globalState = savedGlobal;
            Mapping.deviceState = savedDevices;
            Array.Copy(savedLights, DS4LightBar.forcelight, savedLights.Length);
            Array.Copy(savedFlashes, DS4LightBar.forcedFlash, savedFlashes.Length);
        }
    }

    // Logical event boundary: FakerInput uses toggle events, SendInput uses
    // explicit edge flags. Extended type 1/2 is decoded here independently;
    // the actual bundled Faker handler translation is separately tested above.
    private sealed class RecordingHandler(bool faker, VirtualKBMMapping mapping) : VirtualKBMBase
    {
        internal int HeldMask, Events, DownEvents, UpEvents;
        internal bool CaptureSync;
        internal readonly List<int> SyncedMasks = new();
        public override void Sync()
        {
            if (CaptureSync) SyncedMasks.Add(HeldMask);
        }
        private void Emit(int index, bool pressed)
        {
            Events++;
            if (pressed) { DownEvents++; HeldMask |= 1 << index; }
            else { UpEvents++; HeldMask &= ~(1 << index); }
        }
        private void Event(int index, bool requestedDown) =>
            Emit(index, faker ? (HeldMask & (1 << index)) == 0 : requestedDown);
        public override void PerformMouseButtonEvent(uint code)
        {
            if (code == mapping.MOUSEEVENTF_LEFTDOWN || code == mapping.MOUSEEVENTF_LEFTUP)
                Event(0, code == mapping.MOUSEEVENTF_LEFTDOWN);
            else if (code == mapping.MOUSEEVENTF_RIGHTDOWN || code == mapping.MOUSEEVENTF_RIGHTUP)
                Event(1, code == mapping.MOUSEEVENTF_RIGHTDOWN);
            else if (code == mapping.MOUSEEVENTF_MIDDLEDOWN || code == mapping.MOUSEEVENTF_MIDDLEUP)
                Event(2, code == mapping.MOUSEEVENTF_MIDDLEDOWN);
            else throw new AssertFailedException("Unknown basic mouse event.");
        }
        public override void PerformMouseButtonEventAlt(uint code, int type)
        {
            if (type is not 1 and not 2) throw new AssertFailedException("Unknown extended mouse type.");
            Event(type + 2, code == mapping.MOUSEEVENTF_XBUTTONDOWN);
        }
        public override bool Connect() => throw new AssertFailedException("No driver connection allowed.");
        public override bool Disconnect() => throw new AssertFailedException("No driver connection allowed.");
        public override void MoveRelativeMouse(int x, int y) { }
        public override void MoveAbsoluteMouse(double x, double y) { }
        public override void PerformMouseWheelEvent(int vertical, int horizontal) { }
        public override void PerformMouseButtonPress(uint code) => throw new AssertFailedException("Unexpected direct press API.");
        public override void PerformMouseButtonRelease(uint code) => throw new AssertFailedException("Unexpected direct release API.");
        public override void PerformKeyPress(uint key) { }
        public override void PerformKeyPressAlt(uint key) { }
        public override void PerformKeyRelease(uint key) { }
        public override void PerformKeyReleaseAlt(uint key) { }
        public override string GetDisplayName() => "no-driver mouse ownership sink";
        public override string GetIdentifier() => faker ? FakerInputHandler.IDENTIFIER : SendInputHandler.IDENTIFIER;
        public override string GetFullDisplayName() => GetDisplayName();
    }

    private sealed class FakerStateFixture : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly ReaderWriterLockSlim gate = new();
        internal readonly RelativeMouseReport Report = new();
        internal readonly FakerInputHandler Handler = (FakerInputHandler)
            RuntimeHelpers.GetUninitializedObject(typeof(FakerInputHandler));
        internal FakerStateFixture()
        {
            typeof(FakerInputHandler).GetField("eventLock", Fields)!.SetValue(Handler, gate);
            typeof(FakerInputHandler).GetField("mouseReport", Fields)!.SetValue(Handler, Report);
        }
        internal bool PendingRelativeSync => (bool)typeof(FakerInputHandler)
            .GetField("syncRelativeMouse", Fields)!.GetValue(Handler)!;
        public void Dispose() => gate.Dispose();
    }
}
