using System.Runtime.InteropServices;
using DS4Windows.Switch2;
using Windows.Storage.Streams;
using WinRT;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothThroughputPreferenceTests
{
    [TestMethod]
    public void OwnedReferenceUsesNativeWinRtIdentity()
    {
        // A real Windows object, but no radio, controller or connection request.
        // A managed CCW identity is not the native BluetoothLEDevice identity
        // needed when querying the Windows 11-only Device6 interface.
        using var stream = new InMemoryRandomAccessStream();
        nint acquired = 0;
        nint actualIdentity = 0;
        nint expectedIdentity = 0;
        try
        {
            acquired = Switch2BluetoothThroughputPreference.
                AcquireNativeReference(stream);
            Guid unknownId = new("00000000-0000-0000-C000-000000000046");
            Assert.AreEqual(0, Marshal.QueryInterface(acquired,
                ref unknownId, out actualIdentity));
            Assert.AreEqual(0, Marshal.QueryInterface(
                ((IWinRTObject)stream).NativeObject.ThisPtr,
                ref unknownId, out expectedIdentity));
            Assert.AreEqual(expectedIdentity, actualIdentity,
                "The owned pointer must refer to the native WinRT object, " +
                "not a CLR wrapper around the C#/WinRT projection.");
        }
        finally
        {
            if (expectedIdentity != 0) Marshal.Release(expectedIdentity);
            if (actualIdentity != 0) Marshal.Release(actualIdentity);
            if (acquired != 0) Marshal.Release(acquired);
        }

        // Releasing our owned reference must not consume the projection's ref.
        Assert.IsTrue(stream.CanRead);
        Assert.IsTrue(stream.CanWrite);
    }
}
