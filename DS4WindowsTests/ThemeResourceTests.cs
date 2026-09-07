using Microsoft.VisualStudio.TestTools.UnitTesting;
using DS4WinWPF.DS4Forms;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace DS4WindowsTests
{
    [TestClass]
    public class ThemeResourceTests
    {
        public TestContext TestContext { get; set; }
        [TestMethod]
        public void DefaultThemeLoadsBridgeShellStylesOnFreshConfiguration()
        {
            Exception failure = null;
            var renderedFiles = new System.Collections.Generic.List<string>();
            string resultsDirectory = TestContext.TestResultsDirectory;
            Thread thread = new Thread(() =>
            {
                try
                {
                    var application = new Application();
                    var defaultTheme = new ResourceDictionary();
                    application.Resources.MergedDictionaries.Add(defaultTheme);
                    defaultTheme.Source = new Uri(
                        "/DS4Windows;component/DS4Forms/Themes/DefaultTheme.xaml",
                        UriKind.Relative);

                    var bridgeStyles = new ResourceDictionary();
                    application.Resources.MergedDictionaries.Add(bridgeStyles);
                    bridgeStyles.Source = new Uri(
                        "/DS4Windows;component/DS4Forms/Themes/BridgeShellStyles.xaml",
                        UriKind.Relative);

                    Assert.IsNotNull(application.TryFindResource(
                        "BridgePrimaryButtonStyle"));
                    Assert.IsNotNull(application.TryFindResource(
                        "BridgeSecondaryButtonStyle"));
                    Assert.IsNotNull(application.TryFindResource(
                        "BridgeProfileComboBoxStyle"));
                    Assert.IsNotNull(application.TryFindResource(
                        "BridgeDescribedCheckBoxStyle"));

                    // This is the same construction path used by MainWindow
                    // on a clean install. It must not depend on a converter
                    // that exists only in DarkTheme or in a parent window.
                    var overview = new ControllerOverviewControl();
                    Assert.IsNotNull(overview.Resources[
                        "InverseBoolConverter"]);
                    ControllerOverviewAudioTests.ValidateRenderedOverview(overview, resultsDirectory, renderedFiles);

                    var repairPrompt = new ViiperSetupPrompt(
                        "usbip-win2 0.9.7.8 must be replaced with supported 0.9.7.7",
                        null, verifiedUpdateRequired: true,
                        usbipReplacementRequired: true,
                        mandatoryRepairRequired: true);
                    Assert.AreEqual("USB-IP version must be replaced",
                        ((TextBlock)repairPrompt.FindName(
                            "headingText")).Text);
                    Assert.AreEqual("Repair VIIPER + USB-IP",
                        ((Button)repairPrompt.FindName(
                            "installButton")).Content);
                    Assert.AreEqual(Visibility.Collapsed,
                        ((CheckBox)repairPrompt.FindName(
                            "suppressPromptCheck")).Visibility);
                    Assert.IsFalse(repairPrompt.ExitApplicationRequested,
                        "The settings UI must remain available in degraded mode while virtual output fails closed.");

                    var missingPrompt = new ViiperSetupPrompt(
                        "VIIPER and usbip-win2 need setup", null,
                        mandatoryRepairRequired: true);
                    Assert.AreEqual("VIIPER setup required",
                        ((TextBlock)missingPrompt.FindName(
                            "headingText")).Text);
                    Assert.AreEqual("Continue without virtual output",
                        ((Button)missingPrompt.FindName(
                            "notNowButton")).Content);
                    Assert.IsFalse(missingPrompt.ExitApplicationRequested,
                        "Missing prerequisites must leave repair and diagnostics accessible.");

                    // The repair progress surface is created before the
                    // elevated setup host runs. Its clean-install XAML must
                    // therefore resolve using only application theme assets.
                    var setupProgress = new ViiperSetupProgress(
                        System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(),
                            "ds4windows-missing-setup-log.txt"));
                    Assert.AreEqual("Setting up VIIPER",
                        ((TextBlock)setupProgress.FindName(
                            "headingText")).Text);
                    Assert.AreEqual("Preparing verified package...",
                        ((TextBlock)setupProgress.FindName(
                            "phaseText")).Text);
                    Switch2StickCalibrationEditorTests.ValidateStickCalibrationWindow(application);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)),
                "Theme resource loading did not finish.");
            if (failure != null)
            {
                Assert.Fail(failure.ToString());
            }
            Assert.AreEqual(2, renderedFiles.Count);
            foreach (string path in renderedFiles)
            {
                Assert.IsTrue(System.IO.File.Exists(path), path);
                TestContext.AddResultFile(path);
            }
        }
    }
}
