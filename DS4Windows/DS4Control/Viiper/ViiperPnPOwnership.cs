using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Identifies the Windows transport which owns a VIIPER-created USB
    /// device. Unknown is intentionally not a wildcard.
    /// </summary>
    internal enum ViiperPnPTransport
    {
        Unknown,
        NativeUdeCx,
        LegacyUsbIp,
    }

    /// <summary>
    /// The exact portion of a Windows PnP ancestry used to correlate HID and
    /// UAC interfaces which belong to the same emulated USB device.
    /// </summary>
    internal readonly struct ViiperPnPTopologyIdentity :
        IEquatable<ViiperPnPTopologyIdentity>
    {
        internal ViiperPnPTopologyIdentity(ViiperPnPTransport transport,
            string rootInstanceId, string usbDeviceInstanceId,
            int usbPortNumber)
        {
            Transport = transport;
            RootInstanceId = Normalize(rootInstanceId);
            UsbDeviceInstanceId = Normalize(usbDeviceInstanceId);
            UsbPortNumber = usbPortNumber >= 0 ? usbPortNumber : -1;
        }

        internal ViiperPnPTransport Transport { get; }
        internal string RootInstanceId { get; }
        internal string UsbDeviceInstanceId { get; }
        internal int UsbPortNumber { get; }

        internal bool IsResolved =>
            Transport != ViiperPnPTransport.Unknown &&
            !string.IsNullOrEmpty(RootInstanceId) &&
            !string.IsNullOrEmpty(UsbDeviceInstanceId);

        internal bool IsUsbDeviceResolved =>
            !string.IsNullOrEmpty(UsbDeviceInstanceId);

        internal bool IsSameUsbDevice(
            ViiperPnPTopologyIdentity other)
        {
            if (!IsUsbDeviceResolved || !other.IsUsbDeviceResolved)
            {
                return false;
            }

            if (!string.Equals(UsbDeviceInstanceId,
                    other.UsbDeviceInstanceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.IsNullOrEmpty(RootInstanceId) ||
                string.IsNullOrEmpty(other.RootInstanceId) ||
                string.Equals(RootInstanceId, other.RootInstanceId,
                    StringComparison.OrdinalIgnoreCase);
        }

        public bool Equals(ViiperPnPTopologyIdentity other)
        {
            return Transport == other.Transport &&
                UsbPortNumber == other.UsbPortNumber &&
                string.Equals(RootInstanceId, other.RootInstanceId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(UsbDeviceInstanceId,
                    other.UsbDeviceInstanceId,
                    StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return obj is ViiperPnPTopologyIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Transport,
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    RootInstanceId ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    UsbDeviceInstanceId ?? string.Empty),
                UsbPortNumber);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty :
                value.Trim().TrimEnd('\0');
        }
    }

    /// <summary>
    /// Source-provided identity of one VIIPER virtual-device lifetime. A
    /// native identity is publishable only when it carries the exclusive
    /// controller-session nonce, driver's exact device ID/generation, and an
    /// exact PnP correlation anchor. Stream reconnect counters are not valid
    /// substitutes for any of those source-bound fields.
    /// </summary>
    internal readonly struct ViiperPnPCorrelation :
        IEquatable<ViiperPnPCorrelation>
    {
        internal ViiperPnPCorrelation(ViiperPnPTransport transport,
            ulong nativeDeviceId, uint deviceGeneration,
            ulong controllerSessionId,
            string rootInstanceId, string usbDeviceInstanceId,
            int usbPortNumber, string legacyOwnerSerial = null)
        {
            Transport = transport;
            NativeDeviceId = nativeDeviceId;
            DeviceGeneration = deviceGeneration;
            ControllerSessionId = controllerSessionId;
            RootInstanceId = Normalize(rootInstanceId);
            UsbDeviceInstanceId = Normalize(usbDeviceInstanceId);
            UsbPortNumber = usbPortNumber >= 0 ? usbPortNumber : -1;
            LegacyOwnerSerial = Normalize(legacyOwnerSerial);
        }

        internal ViiperPnPTransport Transport { get; }
        internal ulong NativeDeviceId { get; }
        internal uint DeviceGeneration { get; }
        internal ulong ControllerSessionId { get; }
        internal string RootInstanceId { get; }
        internal string UsbDeviceInstanceId { get; }
        internal int UsbPortNumber { get; }
        internal string LegacyOwnerSerial { get; }

        internal bool IsExact
        {
            get
            {
                bool hasRoot = !string.IsNullOrEmpty(RootInstanceId);
                return Transport switch
                {
                    ViiperPnPTransport.NativeUdeCx =>
                        NativeDeviceId != 0 && DeviceGeneration != 0 &&
                        ControllerSessionId != 0 &&
                        hasRoot && UsbPortNumber > 0,
                    ViiperPnPTransport.LegacyUsbIp =>
                        UsbPortNumber >= 0 &&
                        !string.IsNullOrEmpty(LegacyOwnerSerial),
                    _ => false,
                };
            }
        }

        internal bool Matches(ViiperPnPTopologyIdentity candidate)
        {
            if (!IsExact || !candidate.IsResolved ||
                Transport != candidate.Transport)
            {
                return false;
            }

            if (Transport == ViiperPnPTransport.NativeUdeCx)
            {
                if (!string.Equals(RootInstanceId,
                        candidate.RootInstanceId,
                        StringComparison.OrdinalIgnoreCase) ||
                    candidate.UsbPortNumber != UsbPortNumber)
                {
                    return false;
                }

                // The authenticated create result's controller instance and
                // UdeCx port are the source-bound ownership key. A concrete
                // top-level USB instance, when a future contract supplies it,
                // is an additional constraint rather than a port substitute.
                return string.IsNullOrEmpty(UsbDeviceInstanceId) ||
                    string.Equals(UsbDeviceInstanceId,
                        candidate.UsbDeviceInstanceId,
                        StringComparison.OrdinalIgnoreCase);
            }

            return UsbPortNumber >= 0 &&
                candidate.UsbPortNumber == UsbPortNumber;
        }

        public bool Equals(ViiperPnPCorrelation other)
        {
            return Transport == other.Transport &&
                NativeDeviceId == other.NativeDeviceId &&
                DeviceGeneration == other.DeviceGeneration &&
                ControllerSessionId == other.ControllerSessionId &&
                UsbPortNumber == other.UsbPortNumber &&
                string.Equals(RootInstanceId, other.RootInstanceId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(UsbDeviceInstanceId,
                    other.UsbDeviceInstanceId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(LegacyOwnerSerial, other.LegacyOwnerSerial,
                    StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return obj is ViiperPnPCorrelation other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Transport, NativeDeviceId,
                DeviceGeneration, ControllerSessionId,
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    RootInstanceId ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    UsbDeviceInstanceId ?? string.Empty),
                UsbPortNumber,
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    LegacyOwnerSerial ?? string.Empty));
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty :
                value.Trim().TrimEnd('\0');
        }
    }

    /// <summary>
    /// Testable projection of one node in a child-to-root PnP ancestry walk.
    /// </summary>
    internal readonly struct ViiperPnPAncestryNode
    {
        internal ViiperPnPAncestryNode(string instanceId,
            string parentInstanceId, IEnumerable<string> hardwareIds,
            string locationInfo)
        {
            InstanceId = instanceId ?? string.Empty;
            ParentInstanceId = parentInstanceId ?? string.Empty;
            HardwareIds = hardwareIds == null ? Array.Empty<string>() :
                new List<string>(hardwareIds).ToArray();
            LocationInfo = locationInfo ?? string.Empty;
        }

        internal string InstanceId { get; }
        internal string ParentInstanceId { get; }
        internal IReadOnlyList<string> HardwareIds { get; }
        internal string LocationInfo { get; }
    }

    /// <summary>
    /// Generation-fenced table which turns exact PnP correlations into small
    /// positive owner tokens. The int token is retained as a temporary adapter
    /// for existing audio-haptics call sites; zero and negative values always
    /// fail closed and never mean "any controller".
    /// </summary>
    internal sealed class ViiperPnPOwnershipTable
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<int, ViiperPnPCorrelation> entries =
            new Dictionary<int, ViiperPnPCorrelation>();
        private int nextToken;

        internal int AllocateToken()
        {
            for (;;)
            {
                int token = Interlocked.Increment(ref nextToken);
                if (token > 0)
                {
                    return token;
                }

                Interlocked.CompareExchange(ref nextToken, 0, token);
            }
        }

        internal bool Publish(int token, ViiperPnPCorrelation correlation)
        {
            if (token <= 0 || !correlation.IsExact)
            {
                return false;
            }

            lock (syncRoot)
            {
                if (!entries.TryGetValue(token,
                        out ViiperPnPCorrelation current))
                {
                    entries[token] = correlation;
                    return true;
                }

                if (correlation.Transport != current.Transport ||
                    (correlation.Transport ==
                        ViiperPnPTransport.NativeUdeCx &&
                        (correlation.ControllerSessionId !=
                            current.ControllerSessionId ||
                         correlation.NativeDeviceId !=
                            current.NativeDeviceId)))
                {
                    // A positive token names one created device in one
                    // exclusive controller-file session. Never recycle it
                    // across a broker/controller restart or another device.
                    return false;
                }

                if (correlation.DeviceGeneration < current.DeviceGeneration)
                {
                    return false;
                }

                if (correlation.DeviceGeneration == current.DeviceGeneration)
                {
                    // A stream reconnect may republish the same exact lifetime.
                    // The same generation may never silently change its PnP
                    // anchor or logical device identity.
                    return current.Equals(correlation);
                }

                entries[token] = correlation;
                return true;
            }
        }

        internal bool Matches(int token,
            ViiperPnPTopologyIdentity candidate)
        {
            if (token <= 0)
            {
                return false;
            }

            lock (syncRoot)
            {
                return entries.TryGetValue(token,
                    out ViiperPnPCorrelation correlation) &&
                    correlation.Matches(candidate);
            }
        }

        internal bool MatchesAny(ViiperPnPTopologyIdentity candidate)
        {
            if (!candidate.IsResolved)
            {
                return false;
            }

            lock (syncRoot)
            {
                foreach (ViiperPnPCorrelation correlation in entries.Values)
                {
                    if (correlation.Matches(candidate))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        internal bool TryGet(int token, out ViiperPnPCorrelation correlation)
        {
            if (token <= 0)
            {
                correlation = default;
                return false;
            }

            lock (syncRoot)
            {
                return entries.TryGetValue(token, out correlation);
            }
        }

        internal void Remove(int token)
        {
            if (token <= 0)
            {
                return;
            }

            lock (syncRoot)
            {
                entries.Remove(token);
            }
        }
    }

    internal static class ViiperPnPOwnershipRegistry
    {
        private static readonly ViiperPnPOwnershipTable Table =
            new ViiperPnPOwnershipTable();
        private static readonly object SourcesLock = new object();
        private static readonly ConditionalWeakTable<ViiperOutDevice,
            SourceRegistration> Sources = new ConditionalWeakTable<
                ViiperOutDevice, SourceRegistration>();
        private static readonly Dictionary<int,
            WeakReference<ViiperOutDevice>> TokenSources = new Dictionary<int,
                WeakReference<ViiperOutDevice>>();

        private sealed class SourceRegistration
        {
            internal SourceRegistration(int token)
            {
                Token = token;
            }

            internal int Token { get; }
        }

        internal static int AllocateToken()
        {
            return Table.AllocateToken();
        }

        internal static bool Publish(int token,
            ViiperPnPCorrelation correlation)
        {
            return Table.Publish(token, correlation);
        }

        internal static bool Matches(int token,
            ViiperPnPTopologyIdentity candidate)
        {
            RefreshLiveSourceIdentities();
            return Table.Matches(token, candidate);
        }

        internal static bool MatchesAny(
            ViiperPnPTopologyIdentity candidate)
        {
            RefreshLiveSourceIdentities();
            return Table.MatchesAny(candidate);
        }

        internal static int AttachOrUpdate(ViiperOutDevice source,
            ViiperPnPCorrelation correlation)
        {
            if (source == null || !correlation.IsExact)
            {
                return -1;
            }

            lock (SourcesLock)
            {
                if (!Sources.TryGetValue(source,
                        out SourceRegistration registration))
                {
                    registration = new SourceRegistration(
                        Table.AllocateToken());
                    if (!Table.Publish(registration.Token, correlation))
                    {
                        return -1;
                    }

                    Sources.Add(source, registration);
                    TokenSources[registration.Token] =
                        new WeakReference<ViiperOutDevice>(source);
                    return registration.Token;
                }

                if (Table.TryGet(registration.Token,
                        out ViiperPnPCorrelation current) &&
                    RequiresNewOwnerToken(current, correlation))
                {
                    // A new controller session or native device is a new
                    // creation lifetime even if its generation/root/port were
                    // numerically reused. Rotate the adapter token and retire
                    // the prior correlation before publishing the replacement.
                    Sources.Remove(source);
                    Table.Remove(registration.Token);
                    TokenSources.Remove(registration.Token);
                    var replacement = new SourceRegistration(
                        Table.AllocateToken());
                    if (!Table.Publish(replacement.Token, correlation))
                    {
                        return -1;
                    }

                    Sources.Add(source, replacement);
                    TokenSources[replacement.Token] =
                        new WeakReference<ViiperOutDevice>(source);
                    return replacement.Token;
                }

                if (Table.Publish(registration.Token, correlation))
                {
                    TokenSources[registration.Token] =
                        new WeakReference<ViiperOutDevice>(source);
                    return registration.Token;
                }

                // The source now claims an older or conflicting lifetime.
                // Retaining its previous table entry would keep stale HID/UAC
                // interfaces owned even though callers correctly received a
                // failed token. Withdraw the entire registration fail-closed;
                // a later authoritative identity may attach as a new token.
                Sources.Remove(source);
                Table.Remove(registration.Token);
                TokenSources.Remove(registration.Token);
                return -1;
            }
        }

        private static void RefreshLiveSourceIdentities()
        {
            var liveSources = new List<ViiperOutDevice>();
            var expiredTokens = new List<int>();
            lock (SourcesLock)
            {
                foreach (KeyValuePair<int,
                    WeakReference<ViiperOutDevice>> entry in TokenSources)
                {
                    if (entry.Value.TryGetTarget(
                            out ViiperOutDevice source))
                    {
                        liveSources.Add(source);
                    }
                    else
                    {
                        expiredTokens.Add(entry.Key);
                    }
                }

                foreach (int token in expiredTokens)
                {
                    TokenSources.Remove(token);
                    Table.Remove(token);
                }
            }

            foreach (ViiperOutDevice source in liveSources)
            {
                // A null identity is the expected brief state during a
                // transport-only reconnect; the created USB device and its
                // existing correlation remain valid. Any newly published
                // authoritative identity is immediately generation/session
                // fenced by AttachOrUpdate.
                if (source.VirtualDeviceIdentity != null)
                {
                    GetToken(source);
                }
            }
        }

        private static bool RequiresNewOwnerToken(
            ViiperPnPCorrelation current,
            ViiperPnPCorrelation replacement)
        {
            if (current.Transport != replacement.Transport)
            {
                return true;
            }

            return replacement.Transport == ViiperPnPTransport.NativeUdeCx &&
                (current.ControllerSessionId !=
                    replacement.ControllerSessionId ||
                 current.NativeDeviceId != replacement.NativeDeviceId);
        }

        internal static int AttachOrUpdate(ViiperOutDevice source)
        {
            if (!TryCreateCorrelation(source?.VirtualDeviceIdentity,
                    out ViiperPnPCorrelation correlation))
            {
                return -1;
            }

            return AttachOrUpdate(source, correlation);
        }

        internal static int GetToken(ViiperOutDevice source)
        {
            if (source == null)
            {
                return -1;
            }

            ViiperVirtualDeviceIdentity identity =
                source.VirtualDeviceIdentity;
            if (identity != null)
            {
                if (TryCreateCorrelation(identity,
                        out ViiperPnPCorrelation correlation))
                {
                    return AttachOrUpdate(source, correlation);
                }

                // A reconnect which has not yet published its authoritative
                // device generation must not retain the old endpoint binding.
                Detach(source);
                return -1;
            }

            lock (SourcesLock)
            {
                return Sources.TryGetValue(source,
                    out SourceRegistration registration) ?
                    registration.Token : -1;
            }
        }

        internal static void Detach(ViiperOutDevice source)
        {
            if (source == null)
            {
                return;
            }

            lock (SourcesLock)
            {
                if (!Sources.TryGetValue(source,
                        out SourceRegistration registration))
                {
                    return;
                }

                Sources.Remove(source);
                Table.Remove(registration.Token);
                TokenSources.Remove(registration.Token);
            }
        }

        private static bool TryCreateCorrelation(
            ViiperVirtualDeviceIdentity identity,
            out ViiperPnPCorrelation correlation)
        {
            correlation = default;
            if (identity == null)
            {
                return false;
            }

            if (identity.TransportMode == ViiperTransportMode.NativeUde)
            {
                ViiperNativePnpAnchor anchor = identity.NativePnpAnchor;
                if (anchor?.IsExact != true ||
                    anchor.UdecxUsbPortNumber > int.MaxValue)
                {
                    return false;
                }

                correlation = new ViiperPnPCorrelation(
                    ViiperPnPTransport.NativeUdeCx,
                    anchor.NativeDeviceId,
                    anchor.NativeDeviceGeneration,
                    anchor.ControllerSessionId,
                    anchor.ControllerInstanceId,
                    // The authoritative contract intentionally binds by the
                    // exclusive controller instance plus its exact UdeCx port;
                    // it does not claim a top-level USB PnP instance string.
                    string.Empty,
                    (int)anchor.UdecxUsbPortNumber);
                return correlation.IsExact;
            }

            if (identity.TransportMode == ViiperTransportMode.Usbip)
            {
                correlation = new ViiperPnPCorrelation(
                    ViiperPnPTransport.LegacyUsbIp, 0, 0, 0,
                    string.Empty, string.Empty,
                    identity.LegacyUsbipPort,
                    identity.LegacyUsbipOwnerSerial);
                return correlation.IsExact;
            }

            return false;
        }

        internal static bool TryGet(int token,
            out ViiperPnPCorrelation correlation)
        {
            return Table.TryGet(token, out correlation);
        }

        internal static void Remove(int token)
        {
            lock (SourcesLock)
            {
                TokenSources.Remove(token);
                Table.Remove(token);
            }
        }
    }
}
