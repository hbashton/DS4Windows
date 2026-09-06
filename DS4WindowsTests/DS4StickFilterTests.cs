using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class DS4StickFilterTests
{
    public TestContext TestContext { get; set; }
    private bool captureWholeTest;
    private bool captureWindowInProgress;

    [TestInitialize]
    public void BeginOptionalNativeAllocationCapture()
    {
        captureWindowInProgress = false;
        captureWholeTest = TestContext.TestName == nameof(WarmFilterOwnerAndReducersAllocateNothing) &&
            Environment.GetEnvironmentVariable("DS4W_TRACE_STICK_FILTER_ALLOCATIONS") != "baseline";
        if (captureWholeTest)
            NativeAllocationMeasurement.Begin();
    }

    [TestCleanup]
    public void EndOptionalNativeAllocationCapture()
    {
        if (captureWholeTest || captureWindowInProgress)
            NativeAllocationMeasurement.End(-1);
    }

    [TestMethod]
    [DoNotParallelize]
    public void RepeatedWarmFilterMeasurementsPreserveStrictZeroAllocationGate()
    {
        // Re-run the original assertion, not a substitute loop or a tolerance.
        // This is additional coverage; it cannot clear an unexplained failure
        // from a separate full-suite run.
        for (int measurement = 0; measurement < 100; measurement++)
            WarmFilterOwnerAndReducersAllocateNothing();
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProductionMappingUsesPreciseFiltersOnBothSticksAndFencesReplacement()
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        var savedLeft = Global.store.lsModInfo[slot];
        var savedRight = Global.store.rsModInfo[slot];
        var savedSnapLeft = Global.LSAntiSnapbackInfo[slot];
        var savedSnapRight = Global.RSAntiSnapbackInfo[slot];
        var savedSquare = Global.SquStickInfo[slot];
        double savedLeftRotation = Global.LSRotation[slot], savedRightRotation = Global.RSRotation[slot];
        double savedLeftSensitivity = Global.LSSens[slot], savedRightSensitivity = Global.RSSens[slot];
        int savedLeftCurve = Global.getLsOutCurveMode(slot), savedRightCurve = Global.getRsOutCurveMode(slot);
        sbyte savedLX = Global.LeftStickDriftXAxis[slot], savedLY = Global.LeftStickDriftYAxis[slot];
        sbyte savedRX = Global.RightStickDriftXAxis[slot], savedRY = Global.RightStickDriftYAxis[slot];
        try
        {
            Global.store.lsModInfo[slot] = new StickDeadZoneInfo { fuzz = 1 };
            Global.store.rsModInfo[slot] = new StickDeadZoneInfo { fuzz = 1 };
            Global.LSAntiSnapbackInfo[slot] = new StickAntiSnapbackInfo { enabled = true, delta = 256 };
            Global.RSAntiSnapbackInfo[slot] = new StickAntiSnapbackInfo { enabled = true, delta = 256 };
            Global.SquStickInfo[slot] = new SquareStickInfo();
            Global.LSRotation[slot] = Global.RSRotation[slot] = 0;
            Global.LSSens[slot] = Global.RSSens[slot] = 1;
            Global.setLsOutCurveMode(slot, 0); Global.setRsOutCurveMode(slot, 0);
            Global.LeftStickDriftXAxis[slot] = Global.LeftStickDriftYAxis[slot] = 0;
            Global.RightStickDriftXAxis[slot] = Global.RightStickDriftYAxis[slot] = 0;
            Mapping.ResetStickFilters(slot);
            object owner = new();
            var input = new DS4State {
                LXAxis = Axis(129.01), LYAxis = Axis(128.01),
                RXAxis = Axis(126.99), RYAxis = Axis(127.99) };
            var output = new DS4State();
            Mapping.SetCurveAndDeadzone(slot, input, output, owner);
            Assert.AreEqual(129.01, output.LXAxis.ProfileCoordinate, 1e-12);
            Assert.AreEqual(128.01, output.LYAxis.ProfileCoordinate, 1e-12);
            Assert.AreEqual(126.99, output.RXAxis.ProfileCoordinate, 1e-12);
            Assert.AreEqual(127.99, output.RYAxis.ProfileCoordinate, 1e-12);
            input.LXAxis = Axis(129.1); input.RXAxis = Axis(126.9);
            Mapping.SetCurveAndDeadzone(slot, input, output, owner);
            Assert.AreEqual(129.01, output.LXAxis.ProfileCoordinate, 1e-12);
            Assert.AreEqual(126.99, output.RXAxis.ProfileCoordinate, 1e-12);
            Assert.IsTrue(output.LXAxis.IsHighResolution);
            Assert.IsTrue(output.RXAxis.IsHighResolution);
            input.LXAxis = input.LYAxis = input.RXAxis = input.RYAxis = Axis(128.1);
            Mapping.SetCurveAndDeadzone(slot, input, output, new object());
            Assert.AreEqual(128.0, output.LXAxis.ProfileCoordinate);
            Assert.AreEqual(128.0, output.RXAxis.ProfileCoordinate);
        }
        finally
        {
            Global.store.lsModInfo[slot] = savedLeft; Global.store.rsModInfo[slot] = savedRight;
            Global.LSAntiSnapbackInfo[slot] = savedSnapLeft; Global.RSAntiSnapbackInfo[slot] = savedSnapRight;
            Global.SquStickInfo[slot] = savedSquare;
            Global.LSRotation[slot] = savedLeftRotation; Global.RSRotation[slot] = savedRightRotation;
            Global.LSSens[slot] = savedLeftSensitivity; Global.RSSens[slot] = savedRightSensitivity;
            Global.setLsOutCurveMode(slot, savedLeftCurve); Global.setRsOutCurveMode(slot, savedRightCurve);
            Global.LeftStickDriftXAxis[slot] = savedLX; Global.LeftStickDriftYAxis[slot] = savedLY;
            Global.RightStickDriftXAxis[slot] = savedRX; Global.RightStickDriftYAxis[slot] = savedRY;
            Mapping.ResetStickFilters(slot);
        }
    }

    [DataTestMethod]
    [DataRow(500)]
    [DataRow(1000)]
    [DataRow(8000)]
    public void MaximumWindowCostObservation(int reportsPerSecond)
    {
        // CPU-only observation, not a consumer-latency gate. Exercise the maximum
        // UI window with no early suppression match and report the fixed workload.
        var filter = new DS4StickFilter();
        for (int i = 0; i < reportsPerSecond; i++) Step(i);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = reportsPerSecond; i < reportsPerSecond + 256; i++) Step(i);
        double microseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        TestContext.WriteLine($"Anti-snapback only: rate={reportsPerSecond}/s, window=1000ms, reports=256, elapsed={microseconds:F1}us, mean={microseconds / 256:F2}us/report.");
        Assert.AreEqual(0L, filter.HistoryOverflowCount);
        Assert.IsTrue(filter.HistoryCount <= reportsPerSecond + 8);
        void Step(int i)
        {
            var x = DS4MappedStickAxis.FromSigned((short)(i % 100));
            var y = DS4MappedStickAxis.FromSigned((short)(i % 50));
            filter.ApplySnapback(true, 135, 1000, (long)i * 1000 / reportsPerSecond, ref x, ref y);
        }
    }

    [TestMethod]
    public void LegacySnapbackMatchesOriginalGeometryAndWindow()
    {
        var filter = new DS4StickFilter();
        var history = new Queue<(byte X, byte Y, long Time)>();
        var random = new Random(271828);
        foreach (int timeout in new[] { 0, 1, 50, 1000 })
        foreach (double delta in new[] { 0.0, 0.5, 135.0, 256.0 })
        {
            filter.Reset();
            history.Clear();
            long time = 0;
            for (int i = 0; i < 1024; i++)
            {
                time += random.Next(0, 8);
                byte rawX = (byte)random.Next(256), rawY = (byte)random.Next(256);
                while (history.Count > 0 && history.Peek().Time < time - timeout)
                    history.Dequeue();
                // Deliberately retain the pre-migration formula as an independent
                // integer-input oracle, including its zero-distance/NaN behavior.
                bool suppress = history.Any(old => {
                    double distance = Math.Pow(rawX - old.X, 2) + Math.Pow(rawY - old.Y, 2);
                    if (distance < delta * delta) return false;
                    double t = ((128 - rawX) * (old.X - rawX) +
                        (128 - rawY) * (old.Y - rawY)) / distance;
                    t = Math.Max(0, Math.Min(1, t));
                    return Math.Pow(128 - (rawX + t * (old.X - rawX)), 2) +
                        Math.Pow(128 - (rawY + t * (old.Y - rawY)), 2) <= 15 * 15;
                });
                var x = DS4MappedStickAxis.FromLegacy(rawX);
                var y = DS4MappedStickAxis.FromLegacy(rawY);
                filter.ApplySnapback(true, delta, timeout, time, ref x, ref y);
                Assert.AreEqual(suppress ? (byte)128 : rawX, x.LegacyValue);
                Assert.AreEqual(suppress ? (byte)128 : rawY, y.LegacyValue);
                Assert.IsFalse(x.IsHighResolution);
                Assert.IsFalse(y.IsHighResolution);
                history.Enqueue((rawX, rawY, time));
            }
        }
    }

    [TestMethod]
    public void LegacyFuzzMatchesOriginalThresholdAndEndpointRules()
    {
        var filter = new DS4StickFilter();
        foreach (int delta in new[] { 1, 2, 10, 127, 255 })
        {
            filter.Reset();
            byte lastX = 128, lastY = 128;
            for (int rawX = 0; rawX < 256; rawX++)
            for (int rawY = 0; rawY < 256; rawY++)
            {
                int dx = rawX - lastX, dy = rawY - lastY;
                int magnitude = dx * dx + dy * dy;
                if (rawX == 0 || rawX == 255 || magnitude > delta * delta) lastX = (byte)rawX;
                if (rawY == 0 || rawY == 255 || magnitude > delta * delta) lastY = (byte)rawY;
                var x = DS4MappedStickAxis.FromLegacy((byte)rawX);
                var y = DS4MappedStickAxis.FromLegacy((byte)rawY);
                filter.ApplyFuzz(delta, ref x, ref y);
                Assert.AreEqual(lastX, x.LegacyValue);
                Assert.AreEqual(lastY, y.LegacyValue);
                Assert.IsFalse(x.IsHighResolution);
            }
        }
    }

    [TestMethod]
    public void FractionalFuzzUsesExactThresholdAndRetainsExactHeldValue()
    {
        var filter = new DS4StickFilter();
        var x = Axis(129.01); var y = Axis(128.0);
        filter.ApplyFuzz(1, ref x, ref y);
        Assert.AreEqual(129.01, x.ProfileCoordinate, 1e-12);
        x = Axis(130.0);
        filter.ApplyFuzz(1, ref x, ref y);
        Assert.AreEqual(129.01, x.ProfileCoordinate, 1e-12);
        Assert.IsTrue(x.IsHighResolution);
        x = Axis(130.02);
        filter.ApplyFuzz(1, ref x, ref y);
        Assert.AreEqual(130.02, x.ProfileCoordinate, 1e-12);

        // Values which merely round to an endpoint must not trigger the special
        // physical endpoint bypass before they actually reach that endpoint.
        filter.Reset();
        x = Axis(253.9); y = Axis(128);
        filter.ApplyFuzz(2, ref x, ref y);
        x = Axis(254.99);
        filter.ApplyFuzz(2, ref x, ref y);
        Assert.AreEqual(253.9, x.ProfileCoordinate, 1e-12);
        x = Axis(255);
        filter.ApplyFuzz(2, ref x, ref y);
        Assert.AreEqual(255.0, x.ProfileCoordinate);
    }

    [TestMethod]
    public void SnapbackRetainsFractionsAndSuppressesUsingContinuousGeometry()
    {
        var filter = new DS4StickFilter();
        var x = Axis(195.51); var y = Axis(128.0);
        filter.ApplySnapback(true, 135, 50, 100, ref x, ref y);
        Assert.AreEqual(195.51, x.ProfileCoordinate);
        // Distance134.52 is below135 although byte projections196 and61 would
        // cross the threshold. This is an actual pre-quantization decision.
        x = Axis(60.99);
        filter.ApplySnapback(true, 135, 50, 101, ref x, ref y);
        Assert.AreEqual(60.99, x.ProfileCoordinate, 1e-12);
        x = Axis(195.51);
        filter.ApplySnapback(true, 135, 50, 102, ref x, ref y);
        x = Axis(60.50);
        filter.ApplySnapback(true, 135, 50, 103, ref x, ref y);
        Assert.AreEqual(128.0, x.ProfileCoordinate);
        Assert.AreEqual(128.0, y.ProfileCoordinate);
        Assert.IsTrue(x.IsHighResolution);
    }

    [TestMethod]
    public void HistoryExpiryIsInclusiveAndInvalidPolicyNeverSuppresses()
    {
        var filter = new DS4StickFilter();
        var x = Axis(255); var y = Axis(128);
        filter.ApplySnapback(true, 135, 50, 100, ref x, ref y);
        x = Axis(0);
        filter.ApplySnapback(true, 135, 50, 150, ref x, ref y);
        Assert.AreEqual(128.0, x.ProfileCoordinate);
        x = Axis(255);
        filter.ApplySnapback(true, 135, 50, 201, ref x, ref y);
        Assert.AreEqual(255.0, x.ProfileCoordinate);
        Assert.AreEqual(1, filter.HistoryCount);
        foreach (double delta in new[] { double.NaN, double.PositiveInfinity, -1, 257 })
        {
            x = Axis(0);
            filter.ApplySnapback(true, delta, 50, 202, ref x, ref y);
            Assert.AreEqual(0.0, x.ProfileCoordinate);
            Assert.AreEqual(0, filter.HistoryCount);
        }
    }

    [TestMethod]
    public void OverflowBypassesUntilMissingHistoryExpiresWithoutGrowingStorage()
    {
        var filter = new DS4StickFilter();
        var x = Axis(255); var y = Axis(128);
        for (int i = 0; i < DS4StickFilter.HistoryCapacity; i++)
            filter.ApplySnapback(true, 135, 50, 100, ref x, ref y);
        x = Axis(0);
        filter.ApplySnapback(true, 135, 50, 100, ref x, ref y);
        Assert.AreEqual(0.0, x.ProfileCoordinate, "Overflow must not suppress from incomplete history.");
        Assert.AreEqual(1L, filter.HistoryOverflowCount);
        Assert.AreEqual(1, filter.HistoryCount);
        x = Axis(255);
        filter.ApplySnapback(true, 135, 50, 150, ref x, ref y);
        Assert.AreEqual(255.0, x.ProfileCoordinate);
        Assert.AreEqual(2, filter.HistoryCount);
        x = Axis(0);
        filter.ApplySnapback(true, 135, 50, 151, ref x, ref y);
        Assert.AreEqual(2, filter.HistoryCount);
        Assert.AreEqual(128.0, x.ProfileCoordinate,
            "Recovery must compare against fresh history collected during bypass.");
    }

    [TestMethod]
    public void FilterDisableReenableAndClockRegressionDiscardPriorState()
    {
        var filter = new DS4StickFilter();
        var x = Axis(255); var y = Axis(128);
        filter.ApplySnapback(true, 135, 50, 100, ref x, ref y);
        filter.ApplySnapback(false, 135, 50, 101, ref x, ref y);
        x = Axis(0);
        filter.ApplySnapback(true, 135, 50, 102, ref x, ref y);
        Assert.AreEqual(0.0, x.ProfileCoordinate);
        x = Axis(255);
        filter.ApplySnapback(true, 135, 50, 90, ref x, ref y);
        Assert.AreEqual(255.0, x.ProfileCoordinate);
        filter.ApplyFuzz(10, ref x, ref y);
        filter.ApplyFuzz(0, ref x, ref y);
        x = Axis(129);
        filter.ApplyFuzz(10, ref x, ref y);
        Assert.AreEqual(128.0, x.ProfileCoordinate);
    }

    [TestMethod]
    public void ZeroTimeoutAndExactFuzzBoundaryKeepOriginalSemantics()
    {
        var filter = new DS4StickFilter();
        var x = Axis(255); var y = Axis(128);
        filter.ApplySnapback(true, 135, 0, 100, ref x, ref y);
        x = Axis(0);
        filter.ApplySnapback(true, 135, 0, 100, ref x, ref y);
        Assert.AreEqual(128.0, x.ProfileCoordinate);
        x = Axis(255);
        filter.ApplySnapback(true, 135, 0, 101, ref x, ref y);
        Assert.AreEqual(255.0, x.ProfileCoordinate);
        x = Axis(131); y = Axis(132); // radius5, exactly, must hold
        filter.ApplyFuzz(5, ref x, ref y);
        Assert.AreEqual(128.0, x.ProfileCoordinate);
        Assert.AreEqual(128.0, y.ProfileCoordinate);
        x = Axis(131.01); y = Axis(132);
        filter.ApplyFuzz(5, ref x, ref y);
        Assert.AreEqual(131.01, x.ProfileCoordinate, 1e-12);
    }

    [TestMethod]
    public void SourceProfileGenerationOrientationPresenceAndRotationFenceBothSticks()
    {
        var filters = new DS4StickFilterSet();
        object owner = new();
        var state = new DS4State { Switch2JoyConRawInputStatus = new() {
            IsValid = true, ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
            Mode = Switch2JoyConProfileMode.Joined, PairEpoch = 5,
            LeftPresent = true, RightPresent = true, LeftDeviceGeneration = 6,
            RightDeviceGeneration = 7, LeftTransportGeneration = 8, RightTransportGeneration = 9 } };
        long revision = 0;
        double rotation = 0;
        for (int change = 0; change < 15; change++)
        {
            filters.Prepare(owner, state, revision, rotation, 0);
            var x = Axis(255); var y = Axis(128);
            filters.Left.ApplySnapback(true, 135, 50, 100, ref x, ref y);
            filters.Right.ApplySnapback(true, 135, 50, 100, ref x, ref y);
            filters.Left.ApplyFuzz(10, ref x, ref y);
            filters.Right.ApplyFuzz(10, ref x, ref y);
            // Unchanged observations (including new packet data) retain state.
            state.Switch2JoyConRawInputStatus.LeftDeviceCounterRaw++;
            filters.Prepare(owner, state, revision, rotation, 0);
            Assert.AreEqual(1, filters.Left.HistoryCount);
            switch (change)
            {
                case 0: owner = new(); break;
                case 1: revision++; break;
                case 2: filters.RequestReset(); break;
                case 3: rotation = 0.01; break;
                case 4: state.Switch2JoyConRawInputStatus.PairEpoch++; break;
                case 5: state.Switch2JoyConRawInputStatus.LeftDeviceGeneration++; break;
                case 6: state.Switch2JoyConRawInputStatus.RightTransportGeneration++; break;
                case 7: state.Switch2JoyConRawInputStatus.LeftPresent = false; break;
                case 8: state.Switch2JoyConRawInputStatus.Mode = Switch2JoyConProfileMode.StandaloneHorizontalRight; break;
                case 9: state.Switch2JoyConRawInputStatus.ContractVersion++; break;
                case 10: state.Switch2JoyConRawInputStatus.IsValid = false; break;
                case 11: state.Switch2JoyConRawInputStatus.LeftTransportGeneration++; break;
                case 12: state.Switch2JoyConRawInputStatus.RightDeviceGeneration++; break;
                case 13: state.Switch2JoyConRawInputStatus.RightPresent = false; break;
                case 14: rotation = -0.01; break;
            }
            filters.Prepare(owner, state, revision, rotation, 0);
            Assert.AreEqual(0, filters.Left.HistoryCount);
            Assert.AreEqual(0, filters.Right.HistoryCount);
            x = Axis(129); y = Axis(128);
            filters.Left.ApplyFuzz(10, ref x, ref y);
            Assert.AreEqual(128.0, x.ProfileCoordinate);
            x = Axis(129);
            filters.Right.ApplyFuzz(10, ref x, ref y);
            Assert.AreEqual(128.0, x.ProfileCoordinate);
        }
    }

    [TestMethod]
    public void ProGenerationsAndRightRotationAlsoFenceHistory()
    {
        var filters = new DS4StickFilterSet();
        object owner = new();
        var state = new DS4State { Switch2RawInputStatus = new() {
            IsValid = true, ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
            DeviceGeneration = 1, TransportGeneration = 2 } };
        double rotation = 0;
        for (int change = 0; change < 5; change++)
        {
            filters.Prepare(owner, state, 0, 0, rotation);
            var x = Axis(255); var y = Axis(128);
            filters.Left.ApplySnapback(true, 135, 50, 100, ref x, ref y);
            filters.Right.ApplySnapback(true, 135, 50, 100, ref x, ref y);
            state.Switch2RawInputStatus.CompletionTimestampQpc++;
            filters.Prepare(owner, state, 0, 0, rotation);
            Assert.AreEqual(1, filters.Left.HistoryCount);
            switch (change)
            {
                case 0: state.Switch2RawInputStatus.DeviceGeneration++; break;
                case 1: state.Switch2RawInputStatus.TransportGeneration++; break;
                case 2: state.Switch2RawInputStatus.ContractVersion++; break;
                case 3: state.Switch2RawInputStatus.IsValid = false; break;
                case 4: rotation = 0.01; break;
            }
            filters.Prepare(owner, state, 0, 0, rotation);
            Assert.AreEqual(0, filters.Left.HistoryCount);
            Assert.AreEqual(0, filters.Right.HistoryCount);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void WarmFilterOwnerAndReducersAllocateNothing()
    {
        var filters = new DS4StickFilterSet();
        var state = new DS4State();
        object owner = new();
        double checksum = 0;
        string allocationTraceMode = Environment.GetEnvironmentVariable("DS4W_TRACE_STICK_FILTER_ALLOCATIONS");
        if (allocationTraceMode == "1")
        {
            TraceAllocations();
            return;
        }
        if (allocationTraceMode == "step")
        {
            TraceWholeSteps();
            return;
        }
        if (allocationTraceMode == "boundary")
        {
            TraceAssertionBoundary();
            return;
        }
        if (allocationTraceMode == "baseline")
        {
            TraceIndependentBaseline();
            return;
        }
        for (int i = 0; i < 2000; i++) Step(i);
        long allocated;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 2000; i < 22000; i++) Step(i);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.AreEqual(0L, allocated);
        Assert.IsTrue(checksum > 0);
        Assert.AreEqual(0L, filters.Left.HistoryOverflowCount);
        void Step(int i)
        {
            filters.Prepare(owner, state, 0, 0, 0);
            var x = DS4MappedStickAxis.FromSigned((short)(i % 30000));
            var y = DS4MappedStickAxis.FromSigned((short)(-i % 30000));
            filters.Left.ApplySnapback(true, 135, 50, i, ref x, ref y);
            filters.Left.ApplyFuzz(1, ref x, ref y);
            checksum += x.ProfileCoordinate + y.ProfileCoordinate;
        }

        void TraceIndependentBaseline()
        {
            // Keep an independent, explicitly published copy outside the local
            // frame so a later counter difference can be distinguished from a
            // changed baseline. No profiler/JIT settings or native offsets.
            var preservedCounters = new long[1];
            var collectionEpochs = new int[6];
            int initialThreadId = Environment.CurrentManagedThreadId;
            bool nativeCapture = NativeAllocationMeasurement.IsEnabled;
            Volatile.Write(ref preservedCounters[0], 0L);
            _ = Volatile.Read(ref preservedCounters[0]);
            for (int i = 0; i < 2000; i++) Step(i);

            // These process-wide epochs bracket (rather than occur inside)
            // the counter window. A collection is correlation, not attribution.
            if (nativeCapture)
            {
                NativeAllocationMeasurement.Begin();
                captureWindowInProgress = true;
            }
            collectionEpochs[0] = GC.CollectionCount(0);
            collectionEpochs[1] = GC.CollectionCount(1);
            collectionEpochs[2] = GC.CollectionCount(2);
            long localBefore = GC.GetAllocatedBytesForCurrentThread();
            Volatile.Write(ref preservedCounters[0], localBefore);
            for (int i = 2000; i < 22000; i++) Step(i);
            long rawEndCounter = GC.GetAllocatedBytesForCurrentThread();
            collectionEpochs[3] = GC.CollectionCount(0);
            collectionEpochs[4] = GC.CollectionCount(1);
            collectionEpochs[5] = GC.CollectionCount(2);
            long preservedBefore = Volatile.Read(ref preservedCounters[0]);
            int finalThreadId = Environment.CurrentManagedThreadId;
            long localDelta = rawEndCounter - localBefore;
            long preservedDelta = rawEndCounter - preservedBefore;
            uint nativeObjects = nativeCapture ? NativeAllocationMeasurement.End(preservedDelta) : 0;
            captureWindowInProgress = false;

            // All formatting and assertions follow the final counter. Always
            // retain both baselines, including when the strict assertion fails.
            TestContext?.WriteLine($"Stick filter allocation trace: mode=baseline warm=2000 measured=20000 " +
                $"thread={initialThreadId}->{finalThreadId} localBefore={localBefore} " +
                $"preservedBefore={preservedBefore} rawEnd={rawEndCounter} " +
                $"localDelta={localDelta} preservedDelta={preservedDelta} " +
                $"nativeCapture={nativeCapture} nativeObjects={nativeObjects} " +
                $"gcStart={collectionEpochs[0]},{collectionEpochs[1]},{collectionEpochs[2]} " +
                $"gcEnd={collectionEpochs[3]},{collectionEpochs[4]},{collectionEpochs[5]}");
            Assert.AreEqual(initialThreadId, finalThreadId);
            Assert.AreEqual(preservedBefore, localBefore,
                "The local baseline must match its independently published copy.");
            Assert.AreEqual(0L, localDelta);
            Assert.AreEqual(0L, preservedDelta);
            Assert.IsTrue(checksum > 0);
            Assert.AreEqual(0L, filters.Left.HistoryOverflowCount);
        }

        void TraceAssertionBoundary()
        {
            // No per-iteration probes: compare the raw end-of-loop counter
            // with the unchanged inline assertion's own later counter sample.
            // A difference localizes a window; it does not establish its cause.
            int threadId = Environment.CurrentManagedThreadId;
            for (int i = 0; i < 2000; i++) Step(i);
            long boundaryBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 2000; i < 22000; i++) Step(i);
            long rawEndCounter = GC.GetAllocatedBytesForCurrentThread();
            bool originalAssertionPassed = false;
            try
            {
                Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - boundaryBefore);
                originalAssertionPassed = true;
            }
            finally
            {
                // Even a failed original assertion preserves the independently
                // captured loop counter in TRX output. This log is never inside
                // either allocation-counter window.
                TestContext?.WriteLine($"Stick filter allocation trace: mode=boundary warm=2000 measured=20000 " +
                    $"thread={threadId} rawLoopDelta={rawEndCounter - boundaryBefore} " +
                    $"before={boundaryBefore} rawEnd={rawEndCounter} originalAssertionPassed={originalAssertionPassed}");
            }
            Assert.IsTrue(checksum > 0);
            Assert.AreEqual(0L, filters.Left.HistoryOverflowCount);
        }

        void TraceWholeSteps()
        {
            // Unlike the fine diagnostic below, both warmup and measurement
            // call the ORIGINAL Step. Only whole-call counter windows are
            // added; no alternate reducer path is substituted.
            var records = new (int Iteration, int ThreadId, int Phase,
                long Before, long After)[1024];
            var phaseBytes = new long[3];
            var phaseEvents = new int[3];
            string[] phaseNames = { "BetweenSteps", "WholeStep", "LoopTail" };
            int recordCount = 0, droppedRecords = 0;
            long accountedBytes = 0;
            int initialThreadId = Environment.CurrentManagedThreadId;
            for (int i = 0; i < 2000; i++) Step(i);

            long measuredBefore = GC.GetAllocatedBytesForCurrentThread();
            long lastCounter = measuredBefore;
            for (int i = 2000; i < 22000; i++)
            {
                int threadId = Environment.CurrentManagedThreadId;
                ProbeStep(i, threadId, 0);
                Step(i);
                ProbeStep(i, threadId, 1);
            }
            int finalThreadId = Environment.CurrentManagedThreadId;
            ProbeStep(22000, finalThreadId, 2);
            long totalBytes = lastCounter - measuredBefore;
            long unaccountedBytes = totalBytes - accountedBytes;

            // Reporting and assertions are strictly outside all measured
            // windows. A nonzero WholeStep window still requires stack-level
            // evidence before attributing it to a particular filter method.
            var report = new System.Text.StringBuilder();
            report.Append("Stick filter allocation trace: mode=step warm=2000 measured=20000 total=")
                .Append(totalBytes).Append(" accounted=").Append(accountedBytes)
                .Append(" unaccounted=").Append(unaccountedBytes)
                .Append(" thread=").Append(initialThreadId).Append("->").Append(finalThreadId)
                .Append(" records=").Append(recordCount).Append(" omitted=").Append(droppedRecords)
                .AppendLine();
            for (int phase = 0; phase < phaseNames.Length; phase++)
                report.Append(phaseNames[phase]).Append(": bytes=").Append(phaseBytes[phase])
                    .Append(" counterChanges=").Append(phaseEvents[phase]).AppendLine();
            for (int record = 0; record < recordCount; record++)
            {
                var observation = records[record];
                report.Append("iteration=").Append(observation.Iteration)
                    .Append(" thread=").Append(observation.ThreadId)
                    .Append(" phase=").Append(phaseNames[observation.Phase])
                    .Append(" delta=").Append(observation.After - observation.Before)
                    .Append(" before=").Append(observation.Before)
                    .Append(" after=").Append(observation.After).AppendLine();
            }
            string details = report.ToString();
            TestContext?.WriteLine(details);
            Assert.AreEqual(0L, totalBytes, details);
            Assert.AreEqual(0L, unaccountedBytes, details);
            Assert.AreEqual(initialThreadId, finalThreadId, details);
            Assert.IsTrue(checksum > 0);
            Assert.AreEqual(0L, filters.Left.HistoryOverflowCount);

            void ProbeStep(int iteration, int threadId, int phase)
            {
                long counter = GC.GetAllocatedBytesForCurrentThread();
                long previous = lastCounter;
                lastCounter = counter;
                long delta = counter - previous;
                accountedBytes += delta;
                phaseBytes[phase] += delta;
                if (delta == 0)
                    return;
                phaseEvents[phase]++;
                if (recordCount < records.Length)
                    records[recordCount++] = (iteration, threadId, phase, previous, counter);
                else
                    droppedRecords++;
            }
        }

        void TraceAllocations()
        {
            // These observations identify allocation-counter WINDOWS, not
            // allocation stacks. All recording storage is allocated before
            // warmup. Formatting, TestContext and assertions are after the
            // final measurement; the ordinary Step above stays unchanged.
            const int recordCapacity = 1024;
            var records = new (int Iteration, int ThreadId, int Phase,
                long Before, long After)[recordCapacity];
            var phaseBytes = new long[8];
            var phaseEvents = new int[8];
            string[] phaseNames = { "BetweenSteps", "Prepare", "FromSignedX",
                "FromSignedY", "ApplySnapback", "ApplyFuzz", "Checksum", "LoopTail" };
            int recordCount = 0, droppedRecords = 0;
            bool collecting = false;
            long accountedBytes = 0;
            int initialThreadId = Environment.CurrentManagedThreadId;
            long lastCounter = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++) DiagnosticStep(i);

            collecting = true;
            long measuredBefore = GC.GetAllocatedBytesForCurrentThread();
            lastCounter = measuredBefore;
            for (int i = 2000; i < 22000; i++) DiagnosticStep(i);
            // Include anything between the last Step's checksum sample and
            // loop exit (including our own recording/loop bookkeeping).
            int finalThreadId = Environment.CurrentManagedThreadId;
            Probe(22000, finalThreadId, 7);
            long measuredAfter = lastCounter;
            long totalBytes = measuredAfter - measuredBefore;
            long unaccountedBytes = totalBytes - accountedBytes;

            var report = new System.Text.StringBuilder();
            report.Append("Stick filter allocation trace: warm=2000 measured=20000 total=")
                .Append(totalBytes).Append(" accounted=").Append(accountedBytes)
                .Append(" unaccounted=").Append(unaccountedBytes)
                .Append(" thread=").Append(initialThreadId).Append("->").Append(finalThreadId)
                .Append(" records=").Append(recordCount).Append(" omitted=").Append(droppedRecords)
                .AppendLine();
            for (int phase = 0; phase < phaseNames.Length; phase++)
                report.Append(phaseNames[phase]).Append(": bytes=").Append(phaseBytes[phase])
                    .Append(" counterChanges=").Append(phaseEvents[phase]).AppendLine();
            for (int record = 0; record < recordCount; record++)
            {
                var observation = records[record];
                report.Append("iteration=").Append(observation.Iteration)
                    .Append(" thread=").Append(observation.ThreadId)
                    .Append(" phase=").Append(phaseNames[observation.Phase])
                    .Append(" delta=").Append(observation.After - observation.Before)
                    .Append(" before=").Append(observation.Before)
                    .Append(" after=").Append(observation.After).AppendLine();
            }
            string details = report.ToString();
            TestContext?.WriteLine(details);
            Assert.AreEqual(0L, totalBytes, details);
            Assert.AreEqual(0L, unaccountedBytes, details);
            Assert.AreEqual(initialThreadId, finalThreadId, details);
            Assert.IsTrue(checksum > 0);
            Assert.AreEqual(0L, filters.Left.HistoryOverflowCount);

            void DiagnosticStep(int i)
            {
                int threadId = Environment.CurrentManagedThreadId;
                Probe(i, threadId, 0);
                filters.Prepare(owner, state, 0, 0, 0);
                Probe(i, threadId, 1);
                var x = DS4MappedStickAxis.FromSigned((short)(i % 30000));
                Probe(i, threadId, 2);
                var y = DS4MappedStickAxis.FromSigned((short)(-i % 30000));
                Probe(i, threadId, 3);
                filters.Left.ApplySnapback(true, 135, 50, i, ref x, ref y);
                Probe(i, threadId, 4);
                filters.Left.ApplyFuzz(1, ref x, ref y);
                Probe(i, threadId, 5);
                checksum += x.ProfileCoordinate + y.ProfileCoordinate;
                Probe(i, threadId, 6);
            }

            void Probe(int iteration, int threadId, int phase)
            {
                long counter = GC.GetAllocatedBytesForCurrentThread();
                long previous = lastCounter;
                lastCounter = counter;
                if (!collecting)
                    return;
                long delta = counter - previous;
                accountedBytes += delta;
                phaseBytes[phase] += delta;
                if (delta == 0)
                    return;
                phaseEvents[phase]++;
                if (recordCount < records.Length)
                    records[recordCount++] = (iteration, threadId, phase, previous, counter);
                else
                    droppedRecords++;
            }
        }
    }

    private static DS4MappedStickAxis Axis(double coordinate)
    {
        Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(coordinate, out var axis));
        return axis;
    }
}
