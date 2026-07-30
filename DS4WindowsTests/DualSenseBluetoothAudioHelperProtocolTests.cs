using System.Buffers.Binary;
using System.Reflection;
using DS4Windows.InputDevices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseBluetoothAudioHelperProtocolTests
    {
        private const string HelperArgument =
            "--dualsense-bt-audio-pacer-helper";
        private const int ProtocolVersion = 12;

        private static readonly MethodInfo TryParseHelperArgumentsMethod =
            typeof(DualSenseBluetoothAudioPacer).GetMethod(
                "TryParseHelperArguments",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo TryParseHelloMethod =
            typeof(DualSenseBluetoothAudioPacer).GetMethod(
                "TryParseHello",
                BindingFlags.NonPublic | BindingFlags.Static);

        [TestMethod]
        public void HelperArgumentsPreserveTheExactDedicatedDevicePath()
        {
            Guid token = new Guid("01ad84ef-2c70-46ff-b3d4-48a412985d69");
            const string devicePath =
                @"\\?\hid#vid_054c&pid_0ce6&mi_03#protocol-v12";
            string[] args = BuildValidHelperArguments(token, devicePath);

            Assert.IsTrue(TryParseHelperArguments(args,
                out string pipeName, out Guid parsedToken,
                out int parentProcessId, out string inputArrivalSignalName,
                out string inputClockMapName, out string parsedDevicePath));

            Assert.AreEqual("DS4Windows.ProtocolV12.Pipe", pipeName);
            Assert.AreEqual(token, parsedToken);
            Assert.AreEqual(4242, parentProcessId);
            Assert.AreEqual("DS4Windows.ProtocolV12.InputArrival",
                inputArrivalSignalName);
            Assert.AreEqual("DS4Windows.ProtocolV12.InputClock",
                inputClockMapName);
            Assert.AreEqual(devicePath, parsedDevicePath,
                "The helper must reopen the exact HID path supplied by the parent.");
        }

        [TestMethod]
        public void HelperArgumentsRejectMissingOrBlankDevicePath()
        {
            Guid token = new Guid("09efc966-9ed5-49b0-a2ca-709e52688cf5");
            string[] valid = BuildValidHelperArguments(token,
                @"\\?\hid#vid_054c&pid_0ce6#required-path");
            string[] missing = valid.Take(6).ToArray();

            Assert.IsFalse(TryParseHelperArguments(missing,
                out _, out _, out _, out _, out _, out string missingPath));
            Assert.AreEqual(string.Empty, missingPath);

            valid[6] = " \t ";
            Assert.IsFalse(TryParseHelperArguments(valid,
                out _, out _, out _, out _, out _, out string blankPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(blankPath));
        }

        [TestMethod]
        public void Protocol12HelloContainsOnlyVersionAndAuthenticationToken()
        {
            Guid token = new Guid("8c264fb4-ebc7-4e9d-b79b-0911497418d2");
            byte[] payload = BuildCurrentHello(ProtocolVersion, token);

            Assert.AreEqual(sizeof(int) + 16, payload.Length);
            Assert.IsTrue(TryParseHello(payload, token, out string error));
            Assert.AreEqual(string.Empty, error);
        }

        [TestMethod]
        public void Protocol12RejectsVersion11AndHandleBearingHelloForms()
        {
            Guid token = new Guid("ba637939-7cca-47b8-b410-56f743c6ab00");

            Assert.IsFalse(TryParseHello(BuildCurrentHello(11, token), token,
                out string versionError));
            StringAssert.Contains(versionError,
                "Unsupported pacer protocol version 11");

            byte[] oldHandleBearing = BuildHandleBearingHello(
                version: 11, handle: 0x12345678, token: token);
            Assert.IsFalse(TryParseHello(oldHandleBearing, token,
                out string oldHandleError));
            Assert.AreEqual("Invalid pacer hello payload length.",
                oldHandleError);

            byte[] version12WithHandle = BuildHandleBearingHello(
                version: ProtocolVersion, handle: 0x12345678, token: token);
            Assert.IsFalse(TryParseHello(version12WithHandle, token,
                out string version12HandleError));
            Assert.AreEqual("Invalid pacer hello payload length.",
                version12HandleError);
        }

        private static string[] BuildValidHelperArguments(Guid token,
            string devicePath)
        {
            return new[]
            {
                HelperArgument,
                "DS4Windows.ProtocolV12.Pipe",
                token.ToString("N"),
                "4242",
                "DS4Windows.ProtocolV12.InputArrival",
                "DS4Windows.ProtocolV12.InputClock",
                devicePath,
            };
        }

        private static byte[] BuildCurrentHello(int version, Guid token)
        {
            byte[] payload = new byte[sizeof(int) + 16];
            BinaryPrimitives.WriteInt32LittleEndian(payload, version);
            token.TryWriteBytes(payload.AsSpan(sizeof(int), 16));
            return payload;
        }

        private static byte[] BuildHandleBearingHello(int version, long handle,
            Guid token)
        {
            byte[] payload = new byte[sizeof(int) + sizeof(long) + 16];
            BinaryPrimitives.WriteInt32LittleEndian(payload, version);
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(sizeof(int), sizeof(long)), handle);
            token.TryWriteBytes(payload.AsSpan(sizeof(int) + sizeof(long), 16));
            return payload;
        }

        private static bool TryParseHelperArguments(string[] args,
            out string pipeName, out Guid authenticationToken,
            out int parentProcessId, out string inputArrivalSignalName,
            out string inputClockMapName, out string devicePath)
        {
            Assert.IsNotNull(TryParseHelperArgumentsMethod);
            object[] invocation =
            {
                args, string.Empty, Guid.Empty, 0, string.Empty,
                string.Empty, string.Empty,
            };
            bool parsed = (bool)TryParseHelperArgumentsMethod.Invoke(null,
                invocation);
            pipeName = (string)invocation[1];
            authenticationToken = (Guid)invocation[2];
            parentProcessId = (int)invocation[3];
            inputArrivalSignalName = (string)invocation[4];
            inputClockMapName = (string)invocation[5];
            devicePath = (string)invocation[6];
            return parsed;
        }

        private static bool TryParseHello(byte[] payload,
            Guid expectedAuthenticationToken, out string error)
        {
            Assert.IsNotNull(TryParseHelloMethod);
            object[] invocation =
            {
                payload, expectedAuthenticationToken, string.Empty,
            };
            bool parsed = (bool)TryParseHelloMethod.Invoke(null, invocation);
            error = (string)invocation[2];
            return parsed;
        }
    }
}
