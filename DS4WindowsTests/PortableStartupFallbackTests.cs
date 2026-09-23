using System.Reflection;
using DS4Windows;
using FakeHost = DS4WindowsTests.PortableBrokerRepairTests.FakeHost;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class PortableStartupFallbackTests
{
    private string root;
    private FakeHost host;
    private static readonly FieldInfo CurrentField = typeof(PortableBrokerContext)
        .GetField("current", BindingFlags.Static | BindingFlags.NonPublic);

    [TestInitialize]
    public void Initialize()
    {
        Assert.IsNull(PortableBrokerContext.Current);
        root = Path.Combine(AppContext.BaseDirectory, "portable-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), PortableBrokerContext.MarkerText);
        host = new FakeHost();
    }

    [TestCleanup]
    public void Cleanup()
    {
        PortableBrokerContext.Current?.Dispose();
        CurrentField.SetValue(null, null);
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
            Path.GetFileName(root).StartsWith("portable-fallback-", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }

    private PortableBrokerContext Create(string directory) =>
        PortableBrokerContext.CreateUnavailable(directory, host, () => Array.Empty<string>());

    [TestMethod]
    public void VerifiedPortableIdentityKeepsSettingsAvailableWithoutStartingAnything()
    {
        Assert.IsTrue(PortableBrokerContext.TryInitializeUnavailable(root, out string failure, Create));
        Assert.IsNull(failure);
        Assert.IsTrue(PortableBrokerContext.IsActive);
        Assert.AreEqual(Path.Combine(root, "viiper.exe"), PortableBrokerContext.Current.ViiperPath);
        Assert.IsFalse(PortableBrokerContext.Current.IsVerifiedBackend(PortableBrokerContext.Current.ViiperPath));
        Assert.AreEqual(0, host.Starts);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "portable-data")));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not a portable package")]
    public void InvalidMarkerCannotThrowAgainInsideStartupFailureHandling(string marker)
    {
        File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), marker);
        Assert.IsFalse(PortableBrokerContext.TryInitializeUnavailable(root, out string failure, Create));
        StringAssert.Contains(failure, "marker is invalid");
        Assert.IsNull(PortableBrokerContext.Current);
        Assert.AreEqual(0, host.Starts);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "portable-data")));
    }

    [TestMethod]
    public void MissingMarkerCannotInventPortableAuthority()
    {
        File.Delete(Path.Combine(root, PortableBrokerContext.MarkerFileName));
        Assert.IsFalse(PortableBrokerContext.TryInitializeUnavailable(root, out string failure, Create));
        Assert.IsNotNull(failure);
        Assert.IsNull(PortableBrokerContext.Current);
        Assert.AreEqual(0, host.Starts);
    }

    [TestMethod]
    public void ManagedOrUnverifiableIdentityCannotBecomeAPortableRepairTarget()
    {
        Assert.IsFalse(PortableBrokerContext.TryInitializeUnavailable(root, out _, directory =>
            PortableBrokerContext.CreateUnavailable(directory, host, () => new[] { root })));
        Assert.IsNull(PortableBrokerContext.Current);
        Assert.IsFalse(PortableBrokerContext.TryInitializeUnavailable(root, out _, directory =>
            PortableBrokerContext.CreateUnavailable(directory, host, () => throw new UnauthorizedAccessException())));
        Assert.IsNull(PortableBrokerContext.Current);
        Assert.AreEqual(0, host.Starts);
    }

    [TestMethod]
    public void FallbackCannotReplaceAnAlreadyEstablishedOwner()
    {
        Assert.IsTrue(PortableBrokerContext.TryInitializeUnavailable(root, out _, Create));
        PortableBrokerContext existing = PortableBrokerContext.Current;
        Assert.IsFalse(PortableBrokerContext.TryInitializeUnavailable(root, out _, _ =>
        {
            Assert.Fail("An established owner cannot be replaced by a failure handler.");
            return null;
        }));
        Assert.AreSame(existing, PortableBrokerContext.Current);
    }
}
