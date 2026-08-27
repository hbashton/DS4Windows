using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class HidHideLifecycleTests
    {
        private const string OldHid = @"HID\VID_054C&PID_05C4&MI_03\OLD";
        private const string NewHid = @"HID\VID_054C&PID_05C4&MI_03\NEW";

        [TestMethod]
        public void WiredIdentityChangeReleasesOnlyOwnedOldGeneration()
        {
            HidHideManagedDeviceRegistry<object> registry = new();
            object oldDevice = new();
            HidHideConnectionClaim<object> oldClaim =
                registry.BeginConnection(oldDevice, new[] { OldHid });
            registry.CompleteConnection(oldClaim, Array.Empty<string>(),
                new[] { OldHid });

            HidHideDisconnectPlan disconnect = registry.Disconnect(oldDevice);
            CollectionAssert.AreEquivalent(new[] { OldHid },
                disconnect.PersistentReleaseIds.ToArray());
            Assert.AreEqual(0,
                registry.CompletePersistentRelease(
                    disconnect.PersistentReleaseIds).Count);

            object newDevice = new();
            HidHideConnectionClaim<object> newClaim =
                registry.BeginConnection(newDevice, new[] { NewHid });
            CollectionAssert.AreEquivalent(new[] { NewHid },
                registry.GetUncoveredIds(newClaim,
                    Array.Empty<string>()).ToArray());
            Assert.AreEqual(0, registry.PersistentOwnedIds.Count,
                "The old PnP identity remained owned after its disconnect cleanup.");
        }

        [TestMethod]
        public void PreexistingUserEntryIsProtectionButNeverOurOwnership()
        {
            HidHideManagedDeviceRegistry<object> registry = new();
            object device = new();
            HidHideConnectionClaim<object> claim =
                registry.BeginConnection(device, new[] { OldHid });

            Assert.AreEqual(0,
                registry.GetUncoveredIds(claim, new[] { OldHid }).Count);
            registry.CompleteConnection(claim, Array.Empty<string>(),
                Array.Empty<string>());

            HidHideDisconnectPlan disconnect = registry.Disconnect(device);
            Assert.AreEqual(0, disconnect.PersistentReleaseIds.Count);
            Assert.AreEqual(0, registry.PersistentOwnedIds.Count);
        }

        [TestMethod]
        public void SharedIdentityStaysHiddenUntilLastGenerationDisconnects()
        {
            HidHideManagedDeviceRegistry<object> registry = new();
            object first = new();
            object second = new();
            HidHideConnectionClaim<object> firstClaim =
                registry.BeginConnection(first, new[] { OldHid });
            registry.CompleteConnection(firstClaim, Array.Empty<string>(),
                new[] { OldHid });

            HidHideConnectionClaim<object> secondClaim =
                registry.BeginConnection(second, new[] { OldHid });
            registry.CompleteConnection(secondClaim, Array.Empty<string>(),
                Array.Empty<string>());

            Assert.AreEqual(0,
                registry.Disconnect(first).PersistentReleaseIds.Count);
            CollectionAssert.AreEquivalent(new[] { OldHid },
                registry.Disconnect(second).PersistentReleaseIds.ToArray());
        }

        [TestMethod]
        public void ReconnectDuringRemovalRequestsSameLockWindowReassert()
        {
            HidHideManagedDeviceRegistry<object> registry = new();
            object oldDevice = new();
            HidHideConnectionClaim<object> claim =
                registry.BeginConnection(oldDevice, new[] { OldHid });
            registry.CompleteConnection(claim, Array.Empty<string>(),
                new[] { OldHid });

            IReadOnlyList<string> removal =
                registry.Disconnect(oldDevice).PersistentReleaseIds;
            CollectionAssert.AreEquivalent(new[] { OldHid },
                registry.RevalidatePersistentRelease(removal).ToArray());

            object reconnectedDevice = new();
            registry.BeginConnection(reconnectedDevice, new[] { OldHid });
            CollectionAssert.AreEquivalent(new[] { OldHid },
                registry.CompletePersistentRelease(removal).ToArray(),
                "A generation that arrived during the driver write was not reasserted.");
            CollectionAssert.AreEquivalent(new[] { OldHid },
                registry.PersistentOwnedIds.ToArray());
        }

        [TestMethod]
        public void DisconnectDuringAddRollsBackNewPersistentEntry()
        {
            HidHideManagedDeviceRegistry<object> registry = new();
            object device = new();
            HidHideConnectionClaim<object> pending =
                registry.BeginConnection(device, new[] { OldHid });

            HidHideDisconnectPlan disconnect = registry.Disconnect(device);
            Assert.AreEqual(0, disconnect.PersistentReleaseIds.Count,
                "The driver write had not completed when removal was observed.");

            CollectionAssert.AreEquivalent(new[] { OldHid },
                registry.CompleteConnection(pending, Array.Empty<string>(),
                    new[] { OldHid }).ToArray(),
                "A persistent rule added after its generation disappeared was leaked.");
        }

        [TestMethod]
        public void SameDeviceReResolutionSurfacesSupersededOwnedIdentity()
        {
            HidHideManagedDeviceRegistry<object> registry = new();
            object device = new();
            HidHideConnectionClaim<object> oldClaim =
                registry.BeginConnection(device, new[] { OldHid });
            registry.CompleteConnection(oldClaim, Array.Empty<string>(),
                new[] { OldHid });

            HidHideConnectionClaim<object> replacement =
                registry.BeginConnection(device, new[] { NewHid });
            CollectionAssert.AreEquivalent(new[] { OldHid },
                replacement.SupersededPersistentReleaseIds.ToArray());
        }

        [TestMethod]
        public void MixedAudioContainerAddsHidSiblingsButNeverUsbBase()
        {
            Guid container = Guid.NewGuid();
            Guid usbClass = Guid.NewGuid();
            Guid audioClass = Guid.NewGuid();
            FakeTree tree = new();
            tree.Add("BASE", container, usbClass, null,
                OldHid, "HID-SIBLING", "AUDIO");
            tree.Add(OldHid, container, HidHideDeviceIdentity.HidClassGuid,
                "BASE");
            tree.Add("HID-SIBLING", container,
                HidHideDeviceIdentity.HidClassGuid, "BASE");
            tree.Add("AUDIO", container, audioClass, "BASE");

            IReadOnlyList<string> result = HidHideDeviceIdentity
                .ExpandToBaseContainerAndChildren(OldHid, tree);

            CollectionAssert.AreEquivalent(new[] { OldHid, "HID-SIBLING" },
                result.ToArray());
            CollectionAssert.DoesNotContain(result.ToArray(), "BASE");
            CollectionAssert.DoesNotContain(result.ToArray(), "AUDIO");
        }

        [TestMethod]
        public void UnreadableOrForeignChildMakesBaseIneligible()
        {
            Guid container = Guid.NewGuid();
            FakeTree tree = new();
            tree.Add("BASE", container, HidHideDeviceIdentity.XusbClassGuid,
                null, OldHid, "UNREADABLE", "FOREIGN");
            tree.Add(OldHid, container, HidHideDeviceIdentity.HidClassGuid,
                "BASE");
            tree.Add("FOREIGN", Guid.NewGuid(),
                HidHideDeviceIdentity.HidClassGuid, "BASE");

            IReadOnlyList<string> result = HidHideDeviceIdentity
                .ExpandToBaseContainerAndChildren(OldHid, tree);

            CollectionAssert.AreEquivalent(new[] { OldHid }, result.ToArray());
            CollectionAssert.DoesNotContain(result.ToArray(), "BASE");
            CollectionAssert.DoesNotContain(result.ToArray(), "FOREIGN");
        }

        [TestMethod]
        public void AllHidXusbContainerIncludesBaseAndSiblings()
        {
            Guid container = Guid.NewGuid();
            FakeTree tree = new();
            tree.Add("BASE", container, HidHideDeviceIdentity.XusbClassGuid,
                null, OldHid, "HID-SIBLING");
            tree.Add(OldHid, container, HidHideDeviceIdentity.HidClassGuid,
                "BASE");
            tree.Add("HID-SIBLING", container,
                HidHideDeviceIdentity.HidClassGuid, "BASE");

            IReadOnlyList<string> result = HidHideDeviceIdentity
                .ExpandToBaseContainerAndChildren(OldHid, tree);

            CollectionAssert.AreEquivalent(
                new[] { OldHid, "BASE", "HID-SIBLING" }, result.ToArray());
        }

        [TestMethod]
        public void ActiveWiredControllerIsNeverRestartedForSteamReclaim()
        {
            Assert.IsFalse(ControlService.ShouldRestartDeviceForSteamReclaim(
                ConnectionType.USB));
            Assert.IsTrue(ControlService.ShouldRestartDeviceForSteamReclaim(
                ConnectionType.BT));
        }

        [TestMethod]
        public void WiredRestartGuardDominatesTaskAndPnputilCallSite()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "ControlService.cs"));
            string queue = Extract(source,
                "private void QueueSteamInputReclaim",
                "internal static bool ShouldRestartDeviceForSteamReclaim");

            int guard = queue.IndexOf(
                "ShouldRestartDeviceForSteamReclaim(",
                StringComparison.Ordinal);
            int task = queue.IndexOf("Task.Run(", StringComparison.Ordinal);
            int pnputil = queue.IndexOf("pnputil.exe",
                StringComparison.Ordinal);
            Assert.IsTrue(guard >= 0 && task > guard && pnputil > guard,
                "The wired safety decision must happen before any async restart owner or pnputil invocation is created.");
        }

        [TestMethod]
        public void PerDeviceCleanupCannotClearProcessWideSessionEntries()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "ControlService.cs"));
            string perDevice = Extract(source,
                "private void ReleaseHidHideManagedDevice",
                "private void QueueSteamInputReclaim");
            string removal = Extract(source,
                "protected void On_DS4Removal",
                "public bool[] lag");

            Assert.IsFalse(perDevice.Contains("ClearSessionBlacklist",
                StringComparison.Ordinal));
            StringAssert.Contains(perDevice,
                "hidHideManagedDevices.Disconnect(device)");
            Assert.IsTrue(removal.LastIndexOf(
                    "ReleaseHidHideManagedDevice(device);",
                    StringComparison.Ordinal) >
                removal.IndexOf("UnplugOutDev(ind, device);",
                    StringComparison.Ordinal),
                "Persistent fallback ownership was released before the virtual output retired.");
        }

        [TestMethod]
        public void AllPersistentBlacklistMutationsShareOneBoundary()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "ControlService.cs"));
            string ensure = Extract(source,
                "private bool EnsureHidHideSessionForDevice",
                "private void ReleaseHidHideManagedDevice");
            string release = Extract(source,
                "private void ReleasePersistentHidHideIds",
                "private void QueueSteamInputReclaim");
            string exemption = Extract(source,
                "private void EnsureHidHideDoesNotCloakVirtualSonyOutputs",
                "private void TestQueueBus");

            StringAssert.Contains(ensure,
                "lock (hidHideDriverMutationLock)");
            StringAssert.Contains(release,
                "lock (hidHideDriverMutationLock)");
            StringAssert.Contains(exemption,
                "lock (hidHideDriverMutationLock)");
            StringAssert.Contains(ensure, "TryGetBlacklist(");
            StringAssert.Contains(release, "TryGetBlacklist(");
            StringAssert.Contains(exemption, "TryGetBlacklist(");
        }

        [TestMethod]
        public void LateConnectionPreventsServiceWideActiveRestore()
        {
            HidHideManagedDeviceRegistry<object> registry = new();
            registry.BeginConnection(new object(), new[] { NewHid });

            Assert.IsTrue(registry.HasConnections);
            Assert.IsFalse(!registry.HasConnections && !registry.HasOwnedIds,
                "A service-wide release would disable HidHide under a pending reconnect.");
        }

        [TestMethod]
        public void ServiceWideSessionClearGenerationFencesHotPlugAdmission()
        {
            HidHideManagedDeviceRegistry<object> registry = new();
            object oldDevice = new();
            HidHideConnectionClaim<object> oldClaim =
                registry.BeginConnection(oldDevice, new[] { OldHid });
            registry.CompleteConnection(oldClaim, new[] { OldHid },
                Array.Empty<string>());

            // This claim models HotPlug after it entered Ensure but before it
            // acquired the serialized HidHide driver-mutation boundary.
            object enteredHotPlug = new();
            HidHideConnectionClaim<object> enteredClaim =
                registry.BeginConnection(enteredHotPlug, new[] { NewHid });

            object driverMutationLock = new();
            using ManualResetEventSlim snapshotTaken = new(false);
            using ManualResetEventSlim allowSessionClear = new(false);
            HidHideServiceReleasePlan releasePlan = default;
            Task release = Task.Run(() =>
            {
                lock (driverMutationLock)
                {
                    releasePlan = registry.BeginServiceRelease();
                    snapshotTaken.Set();
                    allowSessionClear.Wait(TimeSpan.FromSeconds(5));
                    registry.CompleteSessionRelease(releasePlan.SessionIds,
                        success: true);
                }
            });

            Assert.IsTrue(snapshotTaken.Wait(TimeSpan.FromSeconds(5)),
                "The release barrier was not reached.");
            try
            {
                Assert.IsFalse(registry.TryBeginConnection(new object(),
                        new[] { NewHid }, out _),
                    "A HotPlug generation was admitted during the process-wide session clear.");
                Assert.IsFalse(registry.IsCurrent(enteredClaim),
                    "A claim that entered before Stop survived the lifecycle fence.");
            }
            finally
            {
                allowSessionClear.Set();
            }

            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)),
                "The release task did not complete.");
            CollectionAssert.AreEquivalent(new[] { OldHid },
                releasePlan.SessionIds.ToArray());
            Assert.AreEqual(0, registry.SessionOwnedIds.Count);
            Assert.IsFalse(registry.HasConnections);
        }

        [TestMethod]
        public void ServiceWideSessionClearIsInsideMutationAndLifecycleBoundary()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "ControlService.cs"));
            string release = Extract(source,
                "private void ReleaseHidHideManagedDevices",
                "private void EnsureHidHideForVirtualOutput");

            int mutationLock = release.IndexOf(
                "lock (hidHideDriverMutationLock)",
                StringComparison.Ordinal);
            int lifecycleSnapshot = release.IndexOf(
                "hidHideManagedDevices.BeginServiceRelease()",
                StringComparison.Ordinal);
            int clearSession = release.IndexOf("ClearSessionBlacklist()",
                StringComparison.Ordinal);
            Assert.IsTrue(mutationLock >= 0 &&
                lifecycleSnapshot > mutationLock &&
                clearSession > lifecycleSnapshot,
                "Admission must close and ownership must be snapshotted under the same mutation boundary as process-wide session clear.");
        }

        [TestMethod]
        public void CriticalHidHidePolicyReadsFailClosed()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "ControlService.cs"));
            string presence = Extract(source,
                "public void CheckHidHidePresence",
                "public void LoadPermanentSlotsConfig");
            string ensure = Extract(source,
                "private bool EnsureHidHideSessionForDevice",
                "private void ReleaseHidHideManagedDevice");
            string attributes = Extract(source,
                "public void UpdateHidHideAttributes",
                "public void UpdateHidHiddenAttributes");

            StringAssert.Contains(presence,
                "if (!hidHideDevice.TryGetWhitelistInverseState(");
            Assert.IsTrue(presence.IndexOf(
                    "TryGetWhitelistInverseState(",
                    StringComparison.Ordinal) <
                presence.IndexOf("SetWhitelist(",
                    StringComparison.Ordinal),
                "Whitelist policy was mutated before inverse-mode semantics were proven.");
            StringAssert.Contains(ensure,
                "if (!hidHideDevice.TryGetActiveState(out bool active))");
            Assert.IsTrue(ensure.IndexOf("TryGetActiveState(",
                    StringComparison.Ordinal) <
                ensure.IndexOf("hidHideActiveStateBeforeManagedSession ??= active",
                    StringComparison.Ordinal),
                "The original active policy was cached without proving the query succeeded.");
            int refreshActive = attributes.IndexOf("TryGetActiveState(",
                StringComparison.Ordinal);
            int refreshBlacklist = attributes.IndexOf("TryGetBlacklist(",
                StringComparison.Ordinal);
            int clearCache = attributes.IndexOf(
                "hidDeviceHidingAffectedDevs.Clear()",
                StringComparison.Ordinal);
            Assert.IsTrue(refreshActive >= 0 &&
                refreshBlacklist > refreshActive &&
                clearCache > refreshBlacklist,
                "A failed HidHide refresh could erase the last verified policy cache.");
        }

        private sealed class FakeTree : IHidHideDeviceNodeTree
        {
            private readonly Dictionary<string, HidHideDeviceNode> nodes =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, IReadOnlyList<string>> children =
                new(StringComparer.OrdinalIgnoreCase);

            public void Add(string id, Guid container, Guid classGuid,
                string parent, params string[] childIds)
            {
                nodes[id] = new HidHideDeviceNode(container, classGuid, parent);
                children[id] = childIds ?? Array.Empty<string>();
            }

            public bool TryGetNode(string instanceId,
                out HidHideDeviceNode node) =>
                nodes.TryGetValue(instanceId, out node);

            public IReadOnlyList<string> GetChildren(string instanceId) =>
                children.TryGetValue(instanceId, out IReadOnlyList<string> value) ?
                    value : Array.Empty<string>();
        }

        private static string Extract(string source, string startMarker,
            string endMarker)
        {
            int start = source.IndexOf(startMarker,
                StringComparison.Ordinal);
            int end = source.IndexOf(endMarker,
                start + Math.Max(0, startMarker.Length),
                StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, startMarker);
            Assert.IsTrue(end > start, endMarker);
            return source.Substring(start, end - start);
        }

        private static string FindRepositoryFile(params string[] parts)
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(new[] { directory.FullName }
                    .Concat(parts).ToArray());
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }

            Assert.Fail("Unable to locate repository file: " +
                Path.Combine(parts));
            return null;
        }
    }
}
