using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ThemeResourceTests
    {
        [TestMethod]
        public void DefaultThemeLoadsBridgeShellStylesOnFreshConfiguration()
        {
            Exception failure = null;
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
                    application.Shutdown();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)),
                "Theme resource loading did not finish.");
            if (failure != null)
            {
                Assert.Fail(failure.ToString());
            }
        }
    }
}
