using DS4Windows.Sunshine;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class SunshineControllerBridgeArrivalRegistryTests
{
    [TestMethod]
    public void RemoveInvalidatesPendingRetryWithoutClearingNewArrival()
    {
        var registry = new SunshineControllerBridgeArrivalRegistry();
        var key = new SunshineControllerBridgeDeviceKey(21, 3);

        registry.MarkActive(key);
        object staleRetry = registry.TryBeginRetry(key);
        Assert.IsNotNull(staleRetry);

        registry.Remove(key);
        Assert.IsFalse(registry.IsActive(key));
        Assert.IsFalse(registry.IsRetryCurrent(key, staleRetry));

        registry.MarkActive(key);
        object currentRetry = registry.TryBeginRetry(key);
        Assert.IsNotNull(currentRetry);
        Assert.AreNotSame(staleRetry, currentRetry);

        registry.CompleteRetry(key, staleRetry);
        Assert.IsTrue(registry.IsRetryCurrent(key, currentRetry));
    }
}
