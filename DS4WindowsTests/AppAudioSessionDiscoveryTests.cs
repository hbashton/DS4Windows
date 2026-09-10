using DS4Windows;
using DS4WinWPF.DS4Forms;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
public sealed class AppAudioSessionDiscoveryTests
{
    [TestMethod]
    public void NondefaultOutputAppAppearsWithItsCompleteSavedIdentity()
    {
        var defaultOutput = new Endpoint(new AppAudioSnapshot("Discord", 10));
        var sonarOutput = new Endpoint(Chrome());
        var otherOutput = new Endpoint(new AppAudioSnapshot("Game", 30));
        var settings = ChromeSettings();

        var sessions = Read(defaultOutput, sonarOutput, otherOutput);
        var choices = AudioHapticsControl.BuildAudioSourceChoices(settings, sessions);
        var chrome = choices.Single(choice => choice.ProcessId == 20);

        Assert.AreEqual(3, sessions.Count);
        Assert.AreEqual("Chrome  ·  App", chrome.DisplayName);
        Assert.AreEqual(AudioHapticsSourceKind.AppSession, chrome.Kind);
        Assert.AreEqual(settings.ExecutableName, chrome.ExecutableName);
        Assert.AreEqual(settings.ProcessPath, chrome.ProcessPath);
        Assert.AreEqual(settings.SessionIdentifier, chrome.SessionIdentifier);
        Assert.AreEqual(settings.SessionInstanceIdentifier, chrome.SessionInstanceIdentifier);
        Assert.IsTrue(AudioHapticsControl.SourceMatches(chrome, settings));
        Assert.IsFalse(choices.Any(choice => choice.DisplayName.EndsWith("Unavailable")));
        foreach (var endpoint in new[] { defaultOutput, sonarOutput, otherOutput })
        {
            Assert.AreEqual(1, endpoint.ReadCount);
            Assert.AreEqual(1, endpoint.DisposeCount);
        }
    }

    [TestMethod]
    public void DisappearingEndpointCannotHideSessionsOnLaterOutputs()
    {
        var vanished = new Endpoint(new AppAudioSnapshot("Already copied", 10))
            { FailAfterCopy = true };
        var sonarOutput = new Endpoint(Chrome());

        var sessions = Read(vanished, sonarOutput);

        CollectionAssert.AreEquivalent(new[] { 10, 20 },
            sessions.Select(session => session.ProcessId).ToArray());
        Assert.AreEqual(1, vanished.DisposeCount);
        Assert.AreEqual(1, sonarOutput.DisposeCount);
    }

    [TestMethod]
    public void DuplicateEndpointViewIsCollapsedButDistinctAppSessionsRemain()
    {
        var first = new Endpoint(Chrome(), new AppAudioSnapshot("System sounds", 0));
        var second = new Endpoint(Chrome(), new AppAudioSnapshot("Chrome tab", 20,
            "chrome", @"C:\Apps\chrome.exe", "session-two", "instance-two"));

        var sessions = Read(first, second);
        var choices = AudioHapticsControl.BuildAudioSourceChoices(null, sessions);

        Assert.AreEqual(2, sessions.Count);
        Assert.AreEqual(2, choices.Count(choice => choice.Kind == AudioHapticsSourceKind.AppSession));
        CollectionAssert.AreEquivalent(new[] { "instance-one", "instance-two" },
            sessions.Select(session => session.SessionInstanceIdentifier).ToArray());
    }

    [TestMethod]
    public void MissingSavedAppRemainsUnavailableWithoutChangingStoredSettings()
    {
        var settings = ChromeSettings();
        var choices = AudioHapticsControl.BuildAudioSourceChoices(settings,
            Array.Empty<AppAudioSnapshot>());
        var saved = choices.Single(choice => choice.Kind == AudioHapticsSourceKind.AppSession);

        Assert.AreEqual("Chrome  ·  Unavailable", saved.DisplayName);
        Assert.IsTrue(AudioHapticsControl.SourceMatches(saved, settings));
        Assert.AreEqual(20, settings.ProcessId);
        Assert.AreEqual(@"C:\Apps\chrome.exe", saved.ProcessPath);
        Assert.AreEqual("session-one", saved.SessionIdentifier);
        Assert.AreEqual("instance-one", saved.SessionInstanceIdentifier);
    }

    [TestMethod]
    public void EndpointMoveStillMatchesSavedProcessIdentity()
    {
        var settings = ChromeSettings();
        var moved = new AppAudioSnapshot("Chrome", 20, "chrome",
            @"C:\Apps\chrome.exe", "new-session", "new-instance");
        var choices = AudioHapticsControl.BuildAudioSourceChoices(settings,
            Read(new Endpoint(moved)));

        var choice = choices.Single(item => item.Kind == AudioHapticsSourceKind.AppSession);
        Assert.AreEqual("Chrome  ·  App", choice.DisplayName);
        Assert.IsTrue(AudioHapticsControl.SourceMatches(choice, settings));
        Assert.AreEqual("new-instance", choice.SessionInstanceIdentifier);
        Assert.AreEqual("instance-one", settings.SessionInstanceIdentifier,
            "Discovery does not rewrite the profile before a user selection.");
    }

    [TestMethod]
    public void AutomaticDetectionKeepsItsEmptyFallbackAndBuiltInSources()
    {
        var settings = new AudioHapticsProfileSettings
        {
            Source = AudioHapticsSourceKind.AppSession,
            AutomaticGameDetection = true,
        };
        var choices = AudioHapticsControl.BuildAudioSourceChoices(settings,
            Read(new Endpoint(Chrome())));

        Assert.AreEqual(AudioHapticsSourceKind.SystemAudio, choices[0].Kind);
        Assert.AreEqual("System audio · default output", choices[0].DisplayName);
        Assert.AreEqual(AudioHapticsSourceKind.ControllerAudio, choices[1].Kind);
        Assert.AreEqual("No fallback app selected", choices[2].DisplayName);
        Assert.IsTrue(AudioHapticsControl.SourceMatches(choices[2], settings));
        Assert.AreEqual(4, choices.Count);
    }

    [TestMethod]
    public void SourceHelpExplainsDefaultOutputWithoutPromisingAnAllOutputMix()
    {
        var markup = System.Xml.Linq.XDocument.Load(Path.Combine(FindSourceRoot(),
            "DS4Windows", "DS4Forms", "AudioHapticsControl.xaml"));
        System.Xml.Linq.XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var help = markup.Descendants().Single(element =>
            (string)element.Attribute(xaml + "Name") == "sourceScopeHelp");
        string text = (string)help.Attribute("Text");
        StringAssert.Contains(text, "only Windows' default output");
        StringAssert.Contains(text, "not every Sonar output");
        StringAssert.Contains(text, "Choose an app");
        Assert.AreEqual("Wrap", (string)help.Attribute("TextWrapping"));
    }

    [TestMethod]
    public void BothProductionPickersUseTheSameAllActiveRenderEndpointDiscovery()
    {
        // Wiring contract complements the behavioral traversal/selection tests:
        // neither picker may accidentally reintroduce default-only enumeration.
        var root = FindSourceRoot();
        string control = File.ReadAllText(Path.Combine(root, "DS4Windows",
            "DS4Forms", "AudioHapticsControl.xaml.cs"));
        string cache = File.ReadAllText(Path.Combine(root, "DS4Windows",
            "DS4Forms", "ViewModels", "AudioEndpointChoiceCache.cs"));
        string discovery = File.ReadAllText(Path.Combine(root, "DS4Windows",
            "DS4Forms", "ViewModels", "AppAudioSessionDiscovery.cs"));

        StringAssert.Contains(control, "sessions = AppAudioSessionDiscovery.Read();");
        StringAssert.Contains(control, "sourceCombo.ItemsSource = BuildAudioSourceChoices(settings, sessions);");
        StringAssert.Contains(cache, "AppAudioSessionDiscovery.Read(enumerator)");
        StringAssert.Contains(discovery, "EnumerateAudioEndPoints(DataFlow.Render,");
        StringAssert.Contains(discovery, "DeviceState.Active).Cast<MMDevice>(), CopySessions)");
        Assert.IsFalse(control.Contains("GetDefaultAudioEndpoint", StringComparison.Ordinal));
        Assert.IsFalse(discovery.Contains("GetDefaultAudioEndpoint", StringComparison.Ordinal));
    }

    private static AppAudioSnapshot Chrome() => new("Chrome", 20, "chrome",
        @"C:\Apps\chrome.exe", "session-one", "instance-one");

    private static AudioHapticsProfileSettings ChromeSettings() => new()
    {
        Source = AudioHapticsSourceKind.AppSession,
        ProcessId = 20,
        DisplayName = "Chrome",
        ExecutableName = "chrome",
        ProcessPath = @"C:\Apps\chrome.exe",
        SessionIdentifier = "session-one",
        SessionInstanceIdentifier = "instance-one",
    };

    private static List<AppAudioSnapshot> Read(params Endpoint[] endpoints) =>
        AppAudioSessionDiscovery.ReadEndpoints(endpoints, (endpoint, sessions) =>
        {
            endpoint.ReadCount++;
            sessions.AddRange(endpoint.Sessions);
            if (endpoint.FailAfterCopy) throw new IOException("Endpoint removed during snapshot.");
        });

    private sealed class Endpoint(params AppAudioSnapshot[] sessions) : IDisposable
    {
        internal AppAudioSnapshot[] Sessions { get; } = sessions;
        internal bool FailAfterCopy { get; init; }
        internal int ReadCount { get; set; }
        internal int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private static string FindSourceRoot()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root != null; root = root.Parent)
            if (File.Exists(Path.Combine(root.FullName, "DS4Windows", "DS4WinWPF.csproj")))
                return root.FullName;
        throw new DirectoryNotFoundException("DS4Windows source root was not found.");
    }
}
