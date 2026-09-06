using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DS4Windows.Switch2.Verification;

internal enum VerificationFailureCode
{
    InvalidPlan,
    InvalidArguments,
    HidClassSetOpenFailed,
    HidInterfaceIterationFailed,
    InterfaceDetailSizeQueryFailed,
    InterfaceDetailReadFailed,
    InterfacePathInvalid,
    DeviceInstanceIdReadFailed,
    DeviceParentLookupFailed,
    DeviceParentOpenFailed,
    DeviceContainerIdReadFailed,
    DeviceRegistryPropertyReadFailed,
    HidTargetCountNotOne,
    HidMetadataOpenFailed,
    HidIdentityChanged,
    HidReportTopologyMismatch,
    WinUsbNodeCountNotOne,
    WinUsbServiceMismatch,
    WinUsbInterfaceGuidMissing,
    WinUsbInterfaceGuidInvalid,
    WinUsbInterfaceClassSetOpenFailed,
    WinUsbInterfaceIterationFailed,
    WinUsbInterfacePathCountNotOne,
    WinUsbIdentityChanged,
    SessionIdentityRevalidationFailed,
    HidReadOpenFailed,
    HidReadWriteOpenFailed,
    WinUsbOpenFailed,
    WinUsbInitializeFailed,
    WinUsbAlternateSettingQueryFailed,
    WinUsbAlternateSettingMismatch,
    WinUsbInterfaceDescriptorQueryFailed,
    WinUsbInterfaceDescriptorMismatch,
    WinUsbPipeQueryFailed,
    WinUsbPipeTopologyMismatch,
    WinUsbPipePolicySetFailed,
    WinUsbPipePolicyReadFailed,
    WinUsbPipePolicyMismatch,
    OutputLeaseIncomplete,
    CommandTransferFailed,
    CommandResponseInvalid,
    CommandOperationTimedOut,
    InputReadFailed,
    InputReportInvalid,
    InputCounterInvalid,
    InputBacklogNotDrained,
    InputCapturePhaseTimedOut,
    HapticWriteFailed,
    HapticMutationSafetyGateClosed,
    HapticPhaseTimedOut,
    HapticDurationExceeded,
    Cancelled,
    UnexpectedFailure,
}

internal enum AbandonedResourceOwnership
{
    None,
    InputCaptureHid,
    CommandOutputWinUsb,
    HapticOutputHid,
}

internal enum CommandTransferFailureStage
{
    TransactionBegin,
    StaleInputFlush,
    RequestWrite,
    ResponseRead,
    ResponseAdmission,
}

internal sealed class HardwareVerificationException : Exception
{
    internal HardwareVerificationException(VerificationFailureCode code,
        AbandonedResourceOwnership abandonedResource =
            AbandonedResourceOwnership.None,
        int? nativeErrorCode = null,
        UsbPipeTopologyObservation? pipeTopology = null,
        WinUsbPipePolicyObservation? pipePolicy = null,
        Switch2UsbCommandFailure? commandResponseFailure = null,
        CommandTransferFailureStage? commandTransferStage = null,
        int? observedResponseLength = null,
        int? observedResponseHeaderByte4 = null,
        int? observedResponseAcknowledgement = null)
        : base(code.ToString())
    {
        Code = code;
        AbandonedResource = abandonedResource;
        NativeErrorCode = nativeErrorCode;
        PipeTopology = pipeTopology;
        PipePolicy = pipePolicy;
        CommandResponseFailure = commandResponseFailure;
        CommandTransferStage = commandTransferStage;
        ObservedResponseLength = observedResponseLength;
        ObservedResponseHeaderByte4 = observedResponseHeaderByte4;
        ObservedResponseAcknowledgement = observedResponseAcknowledgement;
    }

    internal VerificationFailureCode Code { get; }
    internal AbandonedResourceOwnership AbandonedResource { get; }
    internal int? NativeErrorCode { get; }
    internal UsbPipeTopologyObservation? PipeTopology { get; }
    internal WinUsbPipePolicyObservation? PipePolicy { get; }
    internal Switch2UsbCommandFailure? CommandResponseFailure { get; }
    internal CommandTransferFailureStage? CommandTransferStage { get; }
    internal int? ObservedResponseLength { get; }
    internal int? ObservedResponseHeaderByte4 { get; }
    internal int? ObservedResponseAcknowledgement { get; }
    internal bool ResourceOwnershipReturned =>
        AbandonedResource == AbandonedResourceOwnership.None;
}

internal sealed record DeviceInterfaceToken(uint DevInst, string InstanceId,
    Guid ContainerId, string InterfacePath, string Service)
{
    internal bool SameIdentity(DeviceInterfaceToken other) =>
        DevInst == other.DevInst && ContainerId == other.ContainerId &&
        string.Equals(InstanceId, other.InstanceId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(InterfacePath, other.InterfacePath,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Service, other.Service,
            StringComparison.OrdinalIgnoreCase);
}

internal sealed record TargetDeviceSessionIdentity(DeviceInterfaceToken Hid,
    DeviceInterfaceToken WinUsb)
{
    internal bool SameIdentity(TargetDeviceSessionIdentity other) =>
        Hid.SameIdentity(other.Hid) && WinUsb.SameIdentity(other.WinUsb);
}

internal static class TargetSessionIdentityValidator
{
    internal static void RequireSame(TargetDeviceSessionIdentity expected,
        TargetDeviceSessionIdentity current)
    {
        if (!expected.Hid.SameIdentity(current.Hid))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.HidIdentityChanged);
        }
        if (!expected.WinUsb.SameIdentity(current.WinUsb))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbIdentityChanged);
        }
    }
}

internal static class SetupEnumerationGate
{
    internal const int ErrorNoMoreItems = 259;

    internal static void RequireClassSet(bool opened,
        VerificationFailureCode failureCode)
    {
        if (!opened)
        {
            throw new HardwareVerificationException(failureCode);
        }
    }

    // Returns true only for the documented, successful end of a complete
    // enumeration. Every other failed SetupDi enumeration is fatal.
    internal static bool IsComplete(bool callSucceeded, int nativeError,
        VerificationFailureCode failureCode)
    {
        if (callSucceeded)
        {
            return false;
        }
        if (nativeError == ErrorNoMoreItems)
        {
            return true;
        }
        throw new HardwareVerificationException(failureCode);
    }
}

internal static class DeviceInterfaceGuidRegistryValue
{
    private const uint RegSz = 1;
    private const uint RegMultiSz = 7;

    internal static bool TryParseSingle(uint valueType,
        ReadOnlySpan<byte> bytes, out Guid guid)
    {
        guid = default;
        if (valueType != RegSz || bytes.Length < 4 ||
            (bytes.Length & 1) != 0 || bytes[^1] != 0 || bytes[^2] != 0)
        {
            return false;
        }

        string value = System.Text.Encoding.Unicode.GetString(bytes);
        if (value.Length < 2 || value[^1] != '\0' ||
            value.AsSpan(0, value.Length - 1).Contains('\0') ||
            !Guid.TryParse(value.AsSpan(0, value.Length - 1), out guid) ||
            guid == Guid.Empty)
        {
            guid = default;
            return false;
        }
        return true;
    }

    internal static bool TryParse(uint valueType, ReadOnlySpan<byte> bytes,
        out Guid[] guids)
    {
        guids = [];
        if (valueType != RegMultiSz || bytes.Length < 4 ||
            (bytes.Length & 1) != 0 || bytes[^1] != 0 || bytes[^2] != 0 ||
            bytes[^3] != 0 || bytes[^4] != 0)
        {
            return false;
        }

        string value = System.Text.Encoding.Unicode.GetString(bytes);
        string[] entries = value.Split('\0');
        if (entries.Length < 3 || entries[^1].Length != 0 ||
            entries[^2].Length != 0)
        {
            return false;
        }

        var parsed = new List<Guid>(entries.Length - 2);
        for (int index = 0; index < entries.Length - 2; index++)
        {
            string entry = entries[index];
            if (entry.Length == 0 || !Guid.TryParse(entry, out Guid guid) ||
                guid == Guid.Empty || parsed.Contains(guid))
            {
                return false;
            }
            parsed.Add(guid);
        }
        if (parsed.Count == 0)
        {
            return false;
        }

        guids = parsed.ToArray();
        return true;
    }
}

internal static class DeviceInterfaceFlags
{
    private const uint Active = 0x00000001;
    private const uint Removed = 0x00000004;

    internal static bool IsActive(uint flags) => (flags & Active) != 0 &&
        (flags & Removed) == 0;
}

internal static class DeviceRegistryStringValue
{
    private const uint RegSz = 1;
    private const uint RegMultiSz = 7;

    internal static bool TryDecode(uint actualType, uint expectedType,
        ReadOnlySpan<byte> bytes, out string value)
    {
        value = string.Empty;
        if (actualType != expectedType || bytes.Length < sizeof(char) ||
            (bytes.Length & 1) != 0)
        {
            return false;
        }

        string decoded = System.Text.Encoding.Unicode.GetString(bytes);
        if (expectedType == RegSz)
        {
            if (decoded.Length < 1 || decoded[^1] != '\0' ||
                decoded.AsSpan(0, decoded.Length - 1).Contains('\0'))
            {
                return false;
            }
            value = decoded[..^1];
            return value.Length != 0;
        }
        if (expectedType != RegMultiSz || decoded.Length < 2 ||
            decoded[^1] != '\0' || decoded[^2] != '\0')
        {
            return false;
        }

        string[] entries = decoded.Split('\0');
        if (entries.Length < 3 || entries[^1].Length != 0 ||
            entries[^2].Length != 0 ||
            entries.Take(entries.Length - 2).Any(entry => entry.Length == 0))
        {
            return false;
        }
        value = string.Join('\0', entries.Take(entries.Length - 2));
        return value.Length != 0;
    }
}

internal readonly record struct HidCapsFact(ushort UsagePage, ushort Usage,
    ushort InputReportByteLength, ushort OutputReportByteLength,
    ushort FeatureReportByteLength);

internal static class HidCapsValidator
{
    internal static bool IsExact(in HidCapsFact caps) =>
        caps.UsagePage == 0x01 && caps.Usage == 0x05 &&
        caps.InputReportByteLength == VerificationPlan.HidReportLength &&
        caps.OutputReportByteLength == VerificationPlan.HidReportLength &&
        caps.FeatureReportByteLength == 0;
}

internal static class WindowsTargetDiscovery
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int SpdrpHardwareId = 0x00000001;
    private const int SpdrpService = 0x00000004;
    private const uint RegSz = 1;
    private const uint RegMultiSz = 7;
    private const uint DevPropTypeGuid = 0x0000000D;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorFileNotFound = 2;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const int MaximumPropertyBytes = 4096;
    private const int MaximumInstanceCharacters = 512;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDev = 0x00000001;
    private const int KeyQueryValue = 0x0001;
    private const int ErrorSuccess = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly DevPropKey ContainerIdKey = new()
    {
        FormatId = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        PropertyId = 2,
    };

    internal static TargetDeviceSessionIdentity DiscoverSessionIdentity()
    {
        DeviceInterfaceToken hid = DiscoverUniqueHidMetadataOnly();
        DeviceInterfaceToken winUsb = DiscoverUniqueWinUsb(hid.ContainerId);
        return new TargetDeviceSessionIdentity(hid, winUsb);
    }

    internal static void Revalidate(TargetDeviceSessionIdentity expected)
    {
        TargetDeviceSessionIdentity current = DiscoverSessionIdentity();
        TargetSessionIdentityValidator.RequireSame(expected, current);
    }

    private static DeviceInterfaceToken DiscoverUniqueHidMetadataOnly()
    {
        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceSet = NativeMethods.SetupDiGetClassDevsW(ref hidGuid,
            null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        SetupEnumerationGate.RequireClassSet(deviceSet != InvalidHandleValue,
            VerificationFailureCode.HidClassSetOpenFailed);

        var matches = new List<DeviceInterfaceToken>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = NewInterfaceData();
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceSet,
                        IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (SetupEnumerationGate.IsComplete(false, error,
                            VerificationFailureCode
                                .HidInterfaceIterationFailed))
                    {
                        break;
                    }
                }
                if (!DeviceInterfaceFlags.IsActive(interfaceData.Flags))
                {
                    continue;
                }

                InterfaceDetail detail = GetInterfaceDetail(deviceSet,
                    ref interfaceData);
                string instanceId = GetInstanceId(deviceSet,
                    ref detail.DeviceInfo);
                // An unrelated HID top-level collection is allowed to omit
                // this optional registry view or expose a different type.
                // Only an exact target candidate is admitted below.
                string? hardwareIds = TryGetRegistryString(deviceSet,
                    ref detail.DeviceInfo, SpdrpHardwareId, RegMultiSz);
                if (!IsPotentialHidTarget(instanceId, hardwareIds))
                {
                    continue;
                }
                string admittedHardwareIds = hardwareIds ??
                    throw new HardwareVerificationException(
                        VerificationFailureCode
                            .DeviceRegistryPropertyReadFailed);

                Guid containerId = GetContainerId(deviceSet,
                    ref detail.DeviceInfo);
                HiddAttributes attributes = GetHidAttributesMetadataOnly(
                    detail.Path);

                // A HID interface enumerates the top-level collection PDO.
                // HidUsb owns its immediate USB parent, so validate that
                // explicit parent edge rather than expecting SPDRP_SERVICE on
                // the child collection.
                SpDevinfoData parentInfo = GetParentDeviceInfo(deviceSet,
                    ref detail.DeviceInfo);
                string parentInstanceId = GetInstanceId(deviceSet,
                    ref parentInfo);
                string parentHardwareIds = GetRegistryString(deviceSet,
                    ref parentInfo, SpdrpHardwareId, RegMultiSz);
                string parentService = GetRegistryString(deviceSet,
                    ref parentInfo, SpdrpService, RegSz);
                Guid parentContainerId = GetContainerId(deviceSet,
                    ref parentInfo);

                if (TargetIdentityRules.IsHidCollection(instanceId,
                        admittedHardwareIds, containerId, attributes.VendorId,
                        attributes.ProductId, attributes.VersionNumber) &&
                    TargetIdentityRules.IsHidParent(parentInstanceId,
                        parentHardwareIds, parentService, parentContainerId,
                        containerId))
                {
                    matches.Add(new DeviceInterfaceToken(
                        detail.DeviceInfo.DevInst, instanceId, containerId,
                        detail.Path, parentService));
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceSet);
        }

        if (matches.Count != 1)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.HidTargetCountNotOne);
        }
        return matches[0];
    }

    private static DeviceInterfaceToken DiscoverUniqueWinUsb(
        Guid expectedContainerId)
    {
        IntPtr deviceSet = NativeMethods.SetupDiGetClassDevsW(IntPtr.Zero,
            null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        SetupEnumerationGate.RequireClassSet(deviceSet != InvalidHandleValue,
            VerificationFailureCode.WinUsbNodeCountNotOne);

        var candidates = new List<(uint DevInst, string InstanceId,
            Guid ContainerId, string Service, Guid[] InterfaceGuids)>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfo = NewDeviceInfo();
                if (!NativeMethods.SetupDiEnumDeviceInfo(deviceSet, index,
                        ref deviceInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (SetupEnumerationGate.IsComplete(false, error,
                            VerificationFailureCode.WinUsbNodeCountNotOne))
                    {
                        break;
                    }
                }

                string instanceId = GetInstanceId(deviceSet, ref deviceInfo);
                string hardwareIds = TryGetRegistryString(deviceSet,
                    ref deviceInfo, SpdrpHardwareId, RegMultiSz) ?? string.Empty;
                if (!ContainsTargetInterface(instanceId, hardwareIds,
                        VerificationPlan.WinUsbInterfaceMarker))
                {
                    continue;
                }

                string service = GetRegistryString(deviceSet, ref deviceInfo,
                    SpdrpService, RegSz);
                Guid containerId = GetContainerId(deviceSet, ref deviceInfo);
                if (!TargetIdentityRules.IsWinUsbNode(instanceId, hardwareIds,
                        service, containerId, expectedContainerId))
                {
                    continue;
                }

                Guid[] guids = ReadDeviceInterfaceGuids(deviceSet,
                    ref deviceInfo);
                candidates.Add((deviceInfo.DevInst, instanceId, containerId,
                    service, guids));
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceSet);
        }

        if (candidates.Count != 1)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbNodeCountNotOne);
        }
        if (candidates[0].InterfaceGuids.Length == 0)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbInterfaceGuidMissing);
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Guid guid in candidates[0].InterfaceGuids)
        {
            foreach (string path in ResolveInterfacePaths(guid,
                         candidates[0].InstanceId,
                         candidates[0].ContainerId))
            {
                paths.Add(path);
            }
        }
        if (paths.Count != 1)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbInterfacePathCountNotOne);
        }

        return new DeviceInterfaceToken(candidates[0].DevInst,
            candidates[0].InstanceId, candidates[0].ContainerId,
            paths.Single(), candidates[0].Service);
    }

    private static IEnumerable<string> ResolveInterfacePaths(Guid classGuid,
        string expectedInstanceId, Guid expectedContainerId)
    {
        IntPtr deviceSet = NativeMethods.SetupDiGetClassDevsW(ref classGuid,
            null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        SetupEnumerationGate.RequireClassSet(deviceSet != InvalidHandleValue,
            VerificationFailureCode.WinUsbInterfaceClassSetOpenFailed);

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = NewInterfaceData();
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceSet,
                        IntPtr.Zero, ref classGuid, index,
                        ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (SetupEnumerationGate.IsComplete(false, error,
                            VerificationFailureCode
                                .WinUsbInterfaceIterationFailed))
                    {
                        yield break;
                    }
                }
                if (!DeviceInterfaceFlags.IsActive(interfaceData.Flags))
                {
                    continue;
                }

                InterfaceDetail detail = GetInterfaceDetail(deviceSet,
                    ref interfaceData);
                string instanceId = GetInstanceId(deviceSet,
                    ref detail.DeviceInfo);
                Guid containerId = GetContainerId(deviceSet,
                    ref detail.DeviceInfo);
                if (containerId == expectedContainerId &&
                    string.Equals(instanceId, expectedInstanceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    yield return detail.Path;
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    private static bool ContainsTargetInterface(string instanceId,
        string hardwareIds, string marker)
        => TargetIdentityMarker.HasExactInterfaceIdentity(instanceId, marker) ||
            TargetIdentityMarker.HasExactInterfaceIdentity(hardwareIds, marker);

    internal static bool IsPotentialHidTarget(string instanceId,
        string? hardwareIds)
    {
        bool instanceClaimsTarget =
            TargetIdentityMarker.HasExactInterfaceIdentity(instanceId,
                VerificationPlan.HidInterfaceMarker);
        bool hardwareClaimsTarget = hardwareIds is not null &&
            TargetIdentityMarker.HasExactInterfaceIdentity(hardwareIds,
                VerificationPlan.HidInterfaceMarker);
        if (instanceClaimsTarget && !hardwareClaimsTarget)
        {
            // A target-marker node whose required hardware-ID property cannot
            // be read or does not corroborate the exact identity cannot be
            // skipped while establishing uniqueness.
            throw new HardwareVerificationException(
                VerificationFailureCode.DeviceRegistryPropertyReadFailed);
        }
        return hardwareClaimsTarget;
    }

    private static HiddAttributes GetHidAttributesMetadataOnly(string path)
    {
        using SafeFileHandle handle = NativeMethods.CreateFileW(path, 0,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.HidMetadataOpenFailed);
        }

        var attributes = new HiddAttributes
        {
            Size = Marshal.SizeOf<HiddAttributes>(),
        };
        if (!NativeMethods.HidD_GetAttributes(handle, ref attributes))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.HidMetadataOpenFailed);
        }
        if (!HasExpectedHidCaps(handle))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.HidReportTopologyMismatch);
        }
        return attributes;
    }

    internal static bool HasExpectedHidCaps(SafeFileHandle handle)
    {
        if (!NativeMethods.HidD_GetPreparsedData(handle,
                out IntPtr preparsedData) || preparsedData == IntPtr.Zero)
        {
            return false;
        }

        bool valid = false;
        try
        {
            const int hidpStatusSuccess = 0x00110000;
            if (NativeMethods.HidP_GetCaps(preparsedData,
                    out HidpCaps caps) == hidpStatusSuccess)
            {
                var fact = new HidCapsFact(caps.UsagePage, caps.Usage,
                    caps.InputReportByteLength,
                    caps.OutputReportByteLength,
                    caps.FeatureReportByteLength);
                valid = HidCapsValidator.IsExact(fact);
            }
        }
        finally
        {
            if (!NativeMethods.HidD_FreePreparsedData(preparsedData))
            {
                valid = false;
            }
        }
        return valid;
    }

    private static Guid[] ReadDeviceInterfaceGuids(IntPtr deviceSet,
        ref SpDevinfoData deviceInfo)
    {
        IntPtr rawKey = NativeMethods.SetupDiOpenDevRegKey(deviceSet,
            ref deviceInfo, DicsFlagGlobal, 0, DiregDev, KeyQueryValue);
        if (rawKey == InvalidHandleValue)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbInterfaceGuidMissing);
        }

        using var key = new SafeRegistryHandle(rawKey, ownsHandle: true);
        byte[]? multiBytes = TryReadRegistryValue(key,
            "DeviceInterfaceGUIDs", out uint multiType);
        byte[]? singleBytes = TryReadRegistryValue(key,
            "DeviceInterfaceGUID", out uint singleType);
        if (multiBytes is null && singleBytes is null)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbInterfaceGuidMissing);
        }

        var guids = new List<Guid>();
        if (multiBytes is not null)
        {
            if (!DeviceInterfaceGuidRegistryValue.TryParse(multiType,
                    multiBytes, out Guid[] parsed))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.WinUsbInterfaceGuidInvalid);
            }
            guids.AddRange(parsed);
        }
        if (singleBytes is not null)
        {
            if (!DeviceInterfaceGuidRegistryValue.TryParseSingle(singleType,
                    singleBytes, out Guid parsed) || guids.Contains(parsed))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.WinUsbInterfaceGuidInvalid);
            }
            guids.Add(parsed);
        }
        return guids.ToArray();
    }

    private static byte[]? TryReadRegistryValue(SafeRegistryHandle key,
        string valueName, out uint valueType)
    {
        uint byteCount = 0;
        int queryResult = NativeMethods.RegQueryValueExW(key, valueName,
            IntPtr.Zero, out valueType, null, ref byteCount);
        if (queryResult == ErrorFileNotFound)
        {
            return null;
        }
        if (queryResult != ErrorSuccess || byteCount < 4 ||
            byteCount > MaximumPropertyBytes || (byteCount & 1) != 0)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbInterfaceGuidInvalid);
        }

        var bytes = new byte[checked((int)byteCount)];
        uint returnedByteCount = byteCount;
        queryResult = NativeMethods.RegQueryValueExW(key, valueName,
            IntPtr.Zero, out valueType, bytes, ref returnedByteCount);
        if (queryResult != ErrorSuccess || returnedByteCount != byteCount)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbInterfaceGuidInvalid);
        }
        return bytes;
    }

    private static InterfaceDetail GetInterfaceDetail(IntPtr deviceSet,
        ref SpDeviceInterfaceData interfaceData)
    {
        bool querySucceeded = NativeMethods.SetupDiGetDeviceInterfaceDetailW(
            deviceSet, ref interfaceData, IntPtr.Zero, 0,
            out uint required, IntPtr.Zero);
        int queryError = Marshal.GetLastWin32Error();
        int headerBytes = IntPtr.Size == 8 ? 8 : 6;
        if (querySucceeded || queryError != ErrorInsufficientBuffer ||
            required < headerBytes + sizeof(char) || required > int.MaxValue)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InterfaceDetailSizeQueryFailed);
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            Marshal.WriteInt32(buffer, headerBytes);
            var deviceInfo = NewDeviceInfo();
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetailW(deviceSet,
                    ref interfaceData, buffer, required, out _,
                    ref deviceInfo))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.InterfaceDetailReadFailed);
            }
            string? path = Marshal.PtrToStringUni(buffer + 4);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.InterfacePathInvalid);
            }
            return new InterfaceDetail(path, deviceInfo);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetInstanceId(IntPtr deviceSet,
        ref SpDevinfoData deviceInfo)
    {
        var buffer = new char[MaximumInstanceCharacters];
        if (!NativeMethods.SetupDiGetDeviceInstanceIdW(deviceSet,
                ref deviceInfo, buffer, buffer.Length, out int required) ||
            required <= 1 || required > buffer.Length)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.DeviceInstanceIdReadFailed);
        }
        return new string(buffer, 0, required - 1);
    }

    private static SpDevinfoData GetParentDeviceInfo(IntPtr deviceSet,
        ref SpDevinfoData childInfo)
    {
        const int configurationManagerSuccess = 0;
        if (NativeMethods.CM_Get_Parent(out uint parentDevInst,
                childInfo.DevInst, 0) != configurationManagerSuccess)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.DeviceParentLookupFailed);
        }

        var buffer = new char[MaximumInstanceCharacters];
        if (NativeMethods.CM_Get_Device_IDW(parentDevInst, buffer,
                checked((uint)buffer.Length), 0) !=
            configurationManagerSuccess)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.DeviceParentLookupFailed);
        }
        int terminator = Array.IndexOf(buffer, '\0');
        if (terminator <= 0)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.DeviceParentLookupFailed);
        }

        var parentInfo = NewDeviceInfo();
        string parentInstanceId = new(buffer, 0, terminator);
        if (!NativeMethods.SetupDiOpenDeviceInfoW(deviceSet,
                parentInstanceId, IntPtr.Zero, 0, ref parentInfo) ||
            parentInfo.DevInst != parentDevInst)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.DeviceParentOpenFailed);
        }
        return parentInfo;
    }

    private static Guid GetContainerId(IntPtr deviceSet,
        ref SpDevinfoData deviceInfo)
    {
        byte[] bytes = new byte[16];
        DevPropKey key = ContainerIdKey;
        if (!NativeMethods.SetupDiGetDevicePropertyW(deviceSet,
                ref deviceInfo, ref key, out uint propertyType, bytes,
                bytes.Length, out uint required, 0) ||
            propertyType != DevPropTypeGuid || required != (uint)bytes.Length)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.DeviceContainerIdReadFailed);
        }
        return new Guid(bytes);
    }

    private static string GetRegistryString(IntPtr deviceSet,
        ref SpDevinfoData deviceInfo, int property, uint expectedType) =>
        TryGetRegistryString(deviceSet, ref deviceInfo, property,
            expectedType) ?? throw new HardwareVerificationException(
                VerificationFailureCode.DeviceRegistryPropertyReadFailed);

    private static string? TryGetRegistryString(IntPtr deviceSet,
        ref SpDevinfoData deviceInfo, int property, uint expectedType)
    {
        byte[] buffer = new byte[MaximumPropertyBytes];
        if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(deviceSet,
                ref deviceInfo, property, out uint propertyType, buffer,
                buffer.Length, out uint required))
        {
            return null;
        }
        if (required < sizeof(char) || required > (uint)buffer.Length ||
            (required & 1) != 0 ||
            !DeviceRegistryStringValue.TryDecode(propertyType, expectedType,
                buffer.AsSpan(0, checked((int)required)), out string value))
        {
            return null;
        }
        return value;
    }

    private static SpDeviceInterfaceData NewInterfaceData() => new()
    {
        Size = (uint)Marshal.SizeOf<SpDeviceInterfaceData>(),
    };

    private static SpDevinfoData NewDeviceInfo() => new()
    {
        Size = (uint)Marshal.SizeOf<SpDevinfoData>(),
    };

    private sealed class InterfaceDetail
    {
        internal InterfaceDetail(string path, SpDevinfoData deviceInfo)
        {
            Path = path;
            DeviceInfo = deviceInfo;
        }

        internal string Path;
        internal SpDevinfoData DeviceInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        internal int Size;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HidpCaps
    {
        internal ushort Usage;
        internal ushort UsagePage;
        internal ushort InputReportByteLength;
        internal ushort OutputReportByteLength;
        internal ushort FeatureReportByteLength;
        internal fixed ushort Reserved[17];
        internal ushort NumberLinkCollectionNodes;
        internal ushort NumberInputButtonCaps;
        internal ushort NumberInputValueCaps;
        internal ushort NumberInputDataIndices;
        internal ushort NumberOutputButtonCaps;
        internal ushort NumberOutputValueCaps;
        internal ushort NumberOutputDataIndices;
        internal ushort NumberFeatureButtonCaps;
        internal ushort NumberFeatureValueCaps;
        internal ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevinfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DevPropKey
    {
        internal Guid FormatId;
        internal uint PropertyId;
    }

    private static class NativeMethods
    {
        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle,
            ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetPreparsedData(
            SafeFileHandle handle, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_FreePreparsedData(
            IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData,
            out HidpCaps capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(
            ref Guid classGuid, string? enumerator, IntPtr parent,
            uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(
            IntPtr classGuid, string? enumerator, IntPtr parent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInfo(IntPtr deviceSet,
            uint memberIndex, ref SpDevinfoData deviceInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceSet, IntPtr deviceInfo,
            ref Guid interfaceClassGuid, uint memberIndex,
            ref SpDeviceInterfaceData interfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr deviceSet, ref SpDeviceInterfaceData interfaceData,
            IntPtr detailData, uint detailSize, out uint requiredSize,
            IntPtr deviceInfo);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr deviceSet, ref SpDeviceInterfaceData interfaceData,
            IntPtr detailData, uint detailSize, out uint requiredSize,
            ref SpDevinfoData deviceInfo);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr deviceSet, ref SpDevinfoData deviceInfo,
            [Out] char[] instanceId, int instanceIdSize,
            out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceRegistryPropertyW(
            IntPtr deviceSet, ref SpDevinfoData deviceInfo, int property,
            out uint propertyType, [Out] byte[] propertyBuffer,
            int propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern IntPtr SetupDiOpenDevRegKey(
            IntPtr deviceSet, ref SpDevinfoData deviceInfo, uint scope,
            uint hardwareProfile, uint keyType, int desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern int RegQueryValueExW(
            SafeRegistryHandle key, string valueName, IntPtr reserved,
            out uint valueType, [Out] byte[]? data, ref uint dataSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiOpenDeviceInfoW(
            IntPtr deviceSet, string deviceInstanceId, IntPtr parent,
            uint openFlags, ref SpDevinfoData deviceInfo);

        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_Parent(out uint parentDevInst,
            uint childDevInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_IDW(uint devInst,
            [Out] char[] buffer, uint bufferLength, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDevicePropertyW(
            IntPtr deviceSet, ref SpDevinfoData deviceInfo,
            ref DevPropKey propertyKey, out uint propertyType,
            [Out] byte[] propertyBuffer, int propertyBufferSize,
            out uint requiredSize, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(
            IntPtr deviceSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName,
            uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes,
            IntPtr templateFile);
    }
}
