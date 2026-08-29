using Microsoft.VisualStudio.TestTools.UnitTesting;
using DS4WinWPF;
using DS4WinWPF.DS4Forms.ViewModels;
using System;
using System.IO;
using System.Linq;

namespace DS4Windows.Tests
{
    [TestClass]
    public class ProfileSwipeGestureTests
    {
        private static Touch TouchAt(int x, int y, byte id) =>
            new Touch(x, y, id);

        [TestMethod]
        public void TwoFingerCentroidDetectsHorizontalSwipe()
        {
            Touch[] touches =
            {
                TouchAt(730, 330, 1),
                TouchAt(1130, 350, 2),
            };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                700, 300, touches, out int direction);

            Assert.IsTrue(detected);
            Assert.AreEqual(1, direction);
        }

        [TestMethod]
        public void NaturalVerticalDriftDoesNotRejectHorizontalSwipe()
        {
            Touch[] touches =
            {
                TouchAt(810, 390, 1),
                TouchAt(1210, 410, 2),
            };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                750, 300, touches, out int direction);

            Assert.IsTrue(detected);
            Assert.AreEqual(1, direction);
        }

        [TestMethod]
        public void PredominantlyVerticalGestureIsNotProfileSwipe()
        {
            Touch[] touches =
            {
                TouchAt(590, 690, 1),
                TouchAt(990, 710, 2),
            };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                700, 300, touches, out int direction);

            Assert.IsFalse(detected);
            Assert.AreEqual(0, direction);
        }

        [TestMethod]
        public void BothContactsContributeToDirection()
        {
            Touch[] touches =
            {
                TouchAt(300, 300, 9),
                TouchAt(700, 300, 3),
            };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                750, 300, touches, out int direction);

            Assert.IsTrue(detected);
            Assert.AreEqual(-1, direction);
        }

        [TestMethod]
        public void OneFingerMotionCannotChangeProfile()
        {
            Touch[] touches = { TouchAt(1200, 300, 1) };

            bool detected = Mouse.TryGetProfileSwipeDirection(
                700, 300, touches, out int direction);

            Assert.IsFalse(detected);
            Assert.AreEqual(0, direction);
        }

        [TestMethod]
        public void AppliedProfileRequiresModelRuntimeAndEntityAgreement()
        {
            ProfileEntity target = new ProfileEntity { Name = "AXBOX" };
            ProfileEntity stale = new ProfileEntity { Name = "ds" };

            Assert.IsTrue(CompositeDeviceModel.IsProfileSelectionApplied(
                "AXBOX", "AXBOX", target, target));
            Assert.IsFalse(CompositeDeviceModel.IsProfileSelectionApplied(
                "ds", "ds", stale, target));
            Assert.IsFalse(CompositeDeviceModel.IsProfileSelectionApplied(
                "AXBOX", "ds", target, target));
            Assert.IsFalse(CompositeDeviceModel.IsProfileSelectionApplied(
                "AXBOX", "AXBOX", stale, target));
        }

        [TestMethod]
        public void SwipeApplicationDoesNotDependOnRealizedComboBox()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "MainWindow.xaml.cs"));
            string processSwipe = Extract(source,
                "private void ProcessProfileSwipeHotkeys()",
                "private void ApplyProfileSelection");

            StringAssert.Contains(processSwipe,
                "() => item.SelectAndApplyProfile(targetIndex)");
            Assert.IsFalse(processSwipe.Contains(
                "item.SelectedIndex = ComputeSwipeProfileIndex",
                StringComparison.Ordinal));
        }

        [TestMethod]
        public void ProfileSelectionCoordinatorFlushesOnceBeforeApplication()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "MainWindow.xaml.cs"));
            string coordinator = Extract(source,
                "private void ApplyProfileSelection",
                "/// <summary>");

            const string flush =
                "FlushOverviewQuickSettings(item.DevIndex, false);";
            int flushIndex = coordinator.IndexOf(flush,
                StringComparison.Ordinal);
            int applyIndex = coordinator.IndexOf("apply();",
                StringComparison.Ordinal);
            int refreshIndex = coordinator.IndexOf(
                "mainWinVM.RefreshRuntimeState(App.rootHub);",
                StringComparison.Ordinal);

            Assert.AreEqual(1, CountOccurrences(coordinator, flush));
            Assert.IsTrue(flushIndex >= 0 && applyIndex > flushIndex,
                "Pending quick settings must be saved once before the target profile is applied.");
            Assert.IsTrue(refreshIndex > applyIndex,
                "Runtime presentation must refresh after the profile is applied.");
        }

        [TestMethod]
        public void DirectSwipeApplicationFencesBindingReentry()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "ViewModels",
                "ControllerListViewModel.cs"));
            string apply = Extract(source,
                "public void SelectAndApplyProfile(int profileIndex)",
                "internal static bool IsProfileSelectionApplied");

            int fence = apply.IndexOf(
                "IsSynchronizingRuntimeProfile = true;",
                StringComparison.Ordinal);
            int publish = apply.IndexOf("SelectedIndex = profileIndex;",
                StringComparison.Ordinal);
            int release = apply.IndexOf(
                "IsSynchronizingRuntimeProfile = false;",
                StringComparison.Ordinal);
            int directApply = apply.IndexOf("ChangeSelectedProfile();",
                StringComparison.Ordinal);

            Assert.IsTrue(fence >= 0 && publish > fence &&
                release > publish && directApply > release,
                "The source index must be published under a reentrancy fence before one direct runtime apply.");
        }

        [TestMethod]
        public void RealizedBindingDoesNotFlushDuringDirectApply()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "MainWindow.xaml.cs"));
            string handler = Extract(source,
                "private void SelectProfCombo_SelectionChanged",
                "private void CustomColorPick_SelectedColorChanged");

            int fenceGate = handler.IndexOf(
                "if (!item.IsSynchronizingRuntimeProfile &&",
                StringComparison.Ordinal);
            int coordinatedApply = handler.IndexOf(
                "ApplyProfileSelection(item,",
                StringComparison.Ordinal);

            Assert.AreEqual(0, CountOccurrences(handler,
                "FlushOverviewQuickSettings("));
            Assert.IsTrue(fenceGate >= 0 && coordinatedApply > fenceGate,
                "The binding callback must skip its flush while direct application owns the profile transition.");
        }

        [TestMethod]
        public void NamedProfileApplicationUsesDirectModelPath()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "ViewModels",
                "ControllerListViewModel.cs"));
            string namedApply = Extract(source,
                "public void ChangeSelectedProfile(string loadprofile)",
                "public void RequestDisconnect()");

            StringAssert.Contains(namedApply,
                "SelectAndApplyProfile(profileIndex);");
            Assert.IsFalse(namedApply.Contains(
                "SelectedIndex = profileListHolder",
                StringComparison.Ordinal));
        }

        [TestMethod]
        public void TrayAndCreatedProfileEntrypointsUseCoordinator()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "MainWindow.xaml.cs"));
            string tray = Extract(source,
                "private void TrayIconVM_ProfileSelected",
                "private void ShowNotification");
            string created = Extract(source,
                "private void Editor_CreatedProfile",
                "private void NotifyIcon_TrayMouseDoubleClick");

            StringAssert.Contains(tray,
                "ApplyProfileSelection(devitem,");
            StringAssert.Contains(tray,
                "devitem.ChangeSelectedProfile(profile)");
            StringAssert.Contains(created,
                "ApplyProfileSelection(item,");
            StringAssert.Contains(created,
                "item.ChangeSelectedProfile(profile)");
        }

        [TestMethod]
        public void IpcLoadProfileEntrypointUsesCoordinator()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "MainWindow.xaml.cs"));
            string windowMessages = Extract(source,
                "private IntPtr WndProc",
                "private void InnerHotplug2()");

            StringAssert.Contains(windowMessages,
                "ApplyProfileSelection(item,");
            StringAssert.Contains(windowMessages,
                "item.ChangeSelectedProfile(strData[2])");
        }

        [TestMethod]
        public void ActiveFirstProfileDeletionSelectsSurvivor()
        {
            ProfileEntity deleted = new ProfileEntity { Name = "AXBOX" };
            ProfileEntity survivor = new ProfileEntity { Name = "ds" };

            ProfileEntity fallback =
                CompositeDeviceModel.FindProfileDeletionFallback(
                    new[] { deleted, survivor }, deleted);

            Assert.AreSame(survivor, fallback);
        }

        [TestMethod]
        public void LastProfileDeletionHasNoFallback()
        {
            ProfileEntity deleted = new ProfileEntity { Name = "ds" };

            ProfileEntity fallback =
                CompositeDeviceModel.FindProfileDeletionFallback(
                    new[] { deleted }, deleted);

            Assert.IsNull(fallback);
        }

        [TestMethod]
        public void NonActiveRemovalRequiresSelectedIndexReconciliation()
        {
            ProfileEntity active = new ProfileEntity { Name = "ds" };

            Assert.IsFalse(
                CompositeDeviceModel.IsRuntimeProfileSynchronized(
                    "ds", "ds", active, active, 1, 0));
            Assert.IsTrue(
                CompositeDeviceModel.IsRuntimeProfileSynchronized(
                    "ds", "ds", active, active, 0, 0));
        }

        [TestMethod]
        public void PermanentDeletionFlushesBeforeFallbackAndRefreshesAfter()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "MainWindow.xaml.cs"));
            string deletion = Extract(source,
                "private void DeleteProfBtn_Click",
                "private void SelectProfCombo_KeyDown");

            int affected = deletion.IndexOf(
                "item.IsUsingProfile(entity)",
                StringComparison.Ordinal);
            int flush = deletion.IndexOf(
                "FlushOverviewQuickSettings(item.DevIndex, false);",
                StringComparison.Ordinal);
            int delete = deletion.IndexOf("entity.DeleteFile();",
                StringComparison.Ordinal);
            int remove = deletion.IndexOf(
                "profileListHolder.ProfileListCol.Remove(entity);",
                StringComparison.Ordinal);
            int fallback = deletion.IndexOf(
                "item.ApplyProfileDeletionFallback(entity);",
                StringComparison.Ordinal);
            int refresh = deletion.IndexOf(
                "mainWinVM.RefreshRuntimeState(App.rootHub);",
                StringComparison.Ordinal);

            Assert.IsTrue(affected >= 0 && flush > affected &&
                delete > flush && remove > delete && fallback > remove &&
                refresh > fallback,
                "Dirty active profiles must flush before deletion; fallback runs after removal and presentation refreshes last.");
        }

        [TestMethod]
        public void PermanentDeletionUsesDirectModelFallbackOrBlankState()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "ViewModels",
                "ControllerListViewModel.cs"));
            string deleted = Extract(source,
                "internal void ApplyProfileDeletionFallback",
                "internal static ProfileEntity FindProfileDeletionFallback");

            StringAssert.Contains(deleted,
                "SelectAndApplyProfile(fallbackIndex);");
            StringAssert.Contains(deleted,
                "ClearSelectedProfileAfterDeletion();");
        }

        [TestMethod]
        public void RenameDoesNotTriggerPermanentDeletionFallback()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Forms", "ViewModels",
                "ControllerListViewModel.cs"));
            string hooks = Extract(source,
                "private void HookEvents(bool state)",
                "internal void ApplyProfileDeletionFallback");

            Assert.IsFalse(hooks.Contains("ProfileDeleted",
                StringComparison.Ordinal));
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int offset = 0;
            while ((offset = source.IndexOf(value, offset,
                StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += value.Length;
            }

            return count;
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
            DirectoryInfo directory = new DirectoryInfo(
                AppContext.BaseDirectory);
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
