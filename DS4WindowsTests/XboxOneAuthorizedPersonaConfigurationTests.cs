using System.Text.Json;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class XboxOneAuthorizedPersonaConfigurationTests
{
    [TestMethod]
    public void ExplicitBundleBuildsExactClosedFactoryRequest()
    {
        XboxOneAuthorizedPersonaConfiguration configuration =
            XboxOneAuthorizedPersonaConfiguration.ParseExplicit(
                ValidJson(), "test-bundle");
        XboxOneAuthorizedCreateRequestV1 request =
            XboxOneAuthorizedCreateRequestV1.Create(configuration);

        Assert.AreEqual((ushort)1, request.Version);
        Assert.IsTrue(request.IdentityAuthorizationGranted);
        Assert.AreEqual((ushort)0x1234, request.Identity.VendorId);
        Assert.AreEqual(0x0000fffb01020304UL, request.Identity.DeviceId);
        Assert.AreNotEqual(0UL, request.ImportDeviceId);
        Assert.AreEqual((byte)ControllerFeedbackSource.
            XboxOneVirtualDevice, request.Feedback.Source);
        Assert.AreEqual(1UL, request.Feedback.PersonaGeneration);
        Assert.AreNotEqual(0UL, request.Feedback.DeviceGeneration);
        Assert.AreNotEqual(0UL, request.Feedback.TransportGeneration);
        Assert.AreNotEqual(0UL, request.Feedback.OwnershipEpoch);
        Assert.AreEqual(ControllerFeedbackFrame.
            MaxTimeToLiveMicroseconds,
            request.Feedback.TimeToLiveMicroseconds);

        string wire = ViiperClient.
            SerializeAuthorizedXboxOneCreateRequest(request);
        using JsonDocument document = JsonDocument.Parse(wire);
        JsonElement root = document.RootElement;
        Assert.IsFalse(root.TryGetProperty("type", out _),
            "The authorized factory must not degrade into generic device creation.");
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual(1,
            root.GetProperty("feedback").GetProperty("source").GetInt32());
        Assert.AreEqual(request.ImportDeviceId,
            root.GetProperty("importDeviceId").GetUInt64());
    }

    [TestMethod]
    public void ExplicitFeedbackTargetUsesExactPhysicalGenerationsAndFreshEpochs()
    {
        XboxOneAuthorizedPersonaConfiguration configuration =
            XboxOneAuthorizedPersonaConfiguration.ParseExplicit(
                ValidJson(), "test-bundle");

        const ulong deviceGeneration = 0x1020304050607080UL;
        const ulong transportGeneration = 0x8877665544332211UL;
        XboxOneAuthorizedCreateRequestV1 first =
            XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(
                configuration, deviceGeneration, transportGeneration);
        XboxOneAuthorizedCreateRequestV1 successor =
            XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(
                configuration, deviceGeneration, transportGeneration);

        Assert.AreEqual(deviceGeneration,
            first.Feedback.DeviceGeneration);
        Assert.AreEqual(transportGeneration,
            first.Feedback.TransportGeneration);
        Assert.AreEqual(1UL, first.Feedback.PersonaGeneration);
        Assert.AreNotEqual(0UL, first.Feedback.OwnershipEpoch);
        Assert.AreEqual(first.Feedback.PersonaGeneration,
            successor.Feedback.PersonaGeneration);
        Assert.AreNotEqual(first.Feedback.OwnershipEpoch,
            successor.Feedback.OwnershipEpoch);
        Assert.AreEqual(deviceGeneration,
            successor.Feedback.DeviceGeneration);
        Assert.AreEqual(transportGeneration,
            successor.Feedback.TransportGeneration);
    }

    [TestMethod]
    public void ExplicitFeedbackTargetRejectsZeroPhysicalGenerations()
    {
        XboxOneAuthorizedPersonaConfiguration configuration =
            XboxOneAuthorizedPersonaConfiguration.ParseExplicit(
                ValidJson(), "test-bundle");

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(
                configuration, 0, 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(
                configuration, 1, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(
                configuration, 1, 1, 0));

        XboxOneAuthorizedCreateRequestV1 exact =
            XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(
                configuration, 1, 2, 3);
        Assert.AreEqual(3UL, exact.Feedback.OwnershipEpoch);
    }

    [TestMethod]
    public void ConcurrentVirtualRegistrationsDoNotReuseConfiguredGipIdentityAsImportKey()
    {
        XboxOneAuthorizedPersonaConfiguration configuration =
            XboxOneAuthorizedPersonaConfiguration.ParseExplicit(
                ValidJson(), "shared-authorized-bundle");
        var requests = new XboxOneAuthorizedCreateRequestV1[128];
        Parallel.For(0, requests.Length, index =>
            requests[index] = XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(
                configuration, (ulong)index + 1, 1, 7));

        Assert.AreEqual(requests.Length,
            requests.Select(request => request.ImportDeviceId).Distinct().Count(),
            "VIIPER's server-wide retained import authority keys live sessions by importDeviceId, not the USB/IP export alias.");
        foreach (XboxOneAuthorizedCreateRequestV1 request in requests)
        {
            Assert.AreNotEqual(0UL, request.ImportDeviceId);
            Assert.AreSame(configuration.Identity, request.Identity);
            Assert.AreSame(configuration.Usb, request.Usb);
            Assert.AreSame(configuration.Strings, request.Strings);
            Assert.AreEqual(7UL, request.Feedback.OwnershipEpoch,
                "Import identity is independent of the explicit feedback lifetime fence.");
            string firstWire = ViiperClient.SerializeAuthorizedXboxOneCreateRequest(request);
            Assert.AreEqual(firstWire,
                ViiperClient.SerializeAuthorizedXboxOneCreateRequest(request),
                "One registration keeps its allocated identity across request serialization.");
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PerRegistrationUsbSerialRequiresExplicitDeploymentPermission(bool enabled)
    {
        string json = ValidJson().Replace("\"version\":1",
            "\"version\":1,\"derivePerRegistrationSerial\":" +
            (enabled ? "true" : "false"));
        XboxOneAuthorizedPersonaConfiguration configuration =
            XboxOneAuthorizedPersonaConfiguration.ParseExplicit(json, "serial-policy");
        string originalConfiguration = JsonSerializer.Serialize(configuration);
        XboxOneAuthorizedCreateRequestV1 first =
            XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(configuration, 1, 1);
        XboxOneAuthorizedCreateRequestV1 second =
            XboxOneAuthorizedCreateRequestV1.CreateForFeedbackTarget(configuration, 2, 1);

        Assert.AreSame(configuration.Identity, first.Identity);
        Assert.AreSame(configuration.Usb, first.Usb);
        Assert.AreEqual(originalConfiguration, JsonSerializer.Serialize(configuration),
            "Derivation must not mutate the shared explicit deployment identity bundle.");
        Assert.AreEqual(configuration.Strings.Manufacturer, first.Strings.Manufacturer);
        Assert.AreEqual(configuration.Strings.Product, first.Strings.Product);
        if (enabled)
        {
            Assert.AreNotSame(configuration.Strings, first.Strings);
            Assert.AreNotSame(first.Strings, second.Strings);
            Assert.AreNotEqual(first.Strings.Serial, second.Strings.Serial);
            Assert.AreEqual(configuration.Identity.DeviceId.ToString("x16") +
                first.ImportDeviceId.ToString("x16"), first.Strings.Serial);
            Assert.AreEqual(32, first.Strings.Serial.Length);
            Assert.IsTrue(first.Strings.Serial.All(Uri.IsHexDigit));
        }
        else
        {
            Assert.AreSame(configuration.Strings, first.Strings);
            Assert.AreSame(configuration.Strings, second.Strings);
            Assert.AreEqual(configuration.Strings.Serial, first.Strings.Serial);
        }
        using JsonDocument wire = JsonDocument.Parse(
            ViiperClient.SerializeAuthorizedXboxOneCreateRequest(first));
        Assert.IsFalse(wire.RootElement.TryGetProperty("derivePerRegistrationSerial", out _),
            "Local deployment policy is not an extension of VIIPER's closed API schema.");
        Assert.AreEqual(first.Strings.Serial,
            wire.RootElement.GetProperty("strings").GetProperty("serial").GetString());
    }

    [TestMethod]
    public void RetainedImportIdentityAllocationIsConcurrentAndNeverWraps()
    {
        var source = new XboxOneRetainedImportIdentitySource(100);
        var identities = new ulong[1024];
        Parallel.For(0, identities.Length, index => identities[index] = source.Next());
        CollectionAssert.AreEqual(Enumerable.Range(100, identities.Length)
            .Select(value => (ulong)value).ToArray(), identities.OrderBy(value => value).ToArray());

        var exhausted = new XboxOneRetainedImportIdentitySource(ulong.MaxValue - 1);
        Assert.AreEqual(ulong.MaxValue - 1, exhausted.Next());
        Assert.AreEqual(ulong.MaxValue, exhausted.Next());
        Assert.ThrowsException<IOException>(() => exhausted.Next());
        Assert.ThrowsException<IOException>(() => exhausted.Next());
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new XboxOneRetainedImportIdentitySource(0));
    }

    [TestMethod]
    public void MissingAuthorizationAndUnknownFieldsFailClosed()
    {
        string denied = ValidJson().Replace(
            "\"identityAuthorizationGranted\":true",
            "\"identityAuthorizationGranted\":false");
        Assert.ThrowsException<IOException>(() =>
            XboxOneAuthorizedPersonaConfiguration.ParseExplicit(denied,
                "denied"));

        string unknown = ValidJson().Replace("\"version\":1",
            "\"version\":1,\"unexpected\":1");
        Assert.ThrowsException<JsonException>(() =>
            XboxOneAuthorizedPersonaConfiguration.ParseExplicit(unknown,
                "unknown"));
    }

    [TestMethod]
    public void SerialMustCarryExactPrimaryDeviceIdentity()
    {
        string mismatched = ValidJson().Replace(
            "0000fffb010203040000000000000000",
            "0000fffb010203050000000000000000");
        Assert.ThrowsException<IOException>(() =>
            XboxOneAuthorizedPersonaConfiguration.ParseExplicit(mismatched,
                "mismatch"));
    }

    private static string ValidJson() => $$"""
        {
          "version":1,
          "identityAuthorizationGranted":true,
          "identity":{
            "vendorId":4660,
            "productId":22136,
            "deviceReleaseBcd":256,
            "deviceId":{{0x0000fffb01020304UL}},
            "firmwareMajor":1,
            "firmwareMinor":2,
            "firmwareBuild":3,
            "firmwareRevision":4,
            "hardwareMajor":1,
            "hardwareMinor":0
          },
          "usb":{
            "maxPower2mA":250,
            "outIntervalMs":4,
            "inIntervalMs":4
          },
          "strings":{
            "manufacturer":"Authorized Manufacturer",
            "product":"Authorized GIP Controller",
            "serial":"0000fffb010203040000000000000000"
          }
        }
        """;
}
