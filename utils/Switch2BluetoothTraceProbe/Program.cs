using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

// Separate, bounded real-time observer. Never opens Bluetooth, enables a channel
// permanently, records ETL/PCM, or restarts the input owner. Event 402 v0 layout
// is taken from this machine's BTHPORT manifest. This Windows build uses internal
// BIP types (not H4 numbers); accept only structurally validated ACL/L2CAP/ATT.
// No HCI command/key bytes are exported.
if (args is ["--self-test"]) { PacketSummary.SelfTest(); return 0; }
bool packetPlan = args.Length == 5 && args[3] == "run-plan";
if (!packetPlan && (args.Length != 4 || args[3] is not ("headset-observe" or "active-opus5" or "active-opus20")))
{
    Console.Error.WriteLine("Use <existing lab client.exe> <session.json> <explicit Line In ID> headset-observe|active-opus5|active-opus20, or run-plan <reviewed-plan.json>");
    return 2;
}
if (!(TraceEventSession.IsElevated() ?? false)) { Console.Error.WriteLine("Same-user elevation required."); return 1; }
string sessionName = "DS4W-S2-Audio-Headers-" + Environment.ProcessId;
using var summary = new PacketSummary();
var provider = new Guid("8a1f9517-3a8c-4a9e-a018-4f17a200f277");
try
{
    using var session = new TraceEventSession(sessionName, null, TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate);
    session.StopOnDispose = true;
    session.BufferSizeMB = 8;
    var source = session.Source;
    void OnEvent(TraceEvent data)
    {
        if (data.ProviderGuid != provider || (int)data.ID != 402 || data.Version != 0 || data.EventDataLength > 4096) return;
        byte[] bytes = data.EventData();
        try { summary.Accept(bytes, data.TimeStampRelativeMSec); }
        finally { Array.Clear(bytes); }
    }
    source.Dynamic.All += OnEvent;
    source.UnhandledEvents += OnEvent;
    // Await callback retirement before disposing the session it owns.
    await using var timeout = new Timer(_ => session.Stop(), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; session.Stop(); };
    session.EnableProvider(provider, TraceEventLevel.Informational, 0x8000000000000000,
        new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 402 } });
    var consuming = Task.Run(() => source.Process());
    int? expectedOutputWrites = null;
    try
    {
        foreach (string command in new[] { "status", "audio-state", args[3], "status" })
        {
            var start = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.GetFullPath(args[1])); start.ArgumentList.Add(command);
            if (command.StartsWith("active-") || command == "run-plan") start.ArgumentList.Add(args[2]);
            if (command == "run-plan")
            {
                start.ArgumentList.Add(Path.GetFullPath(args[4]));
                // Accepted WinRT writes can still be queued below the app.
                // Retain Line-In/ETW observation through a bounded drain window.
                start.ArgumentList.Add("--capture-tail-ms"); start.ArgumentList.Add("2000");
            }
            using var client = Process.Start(start) ?? throw new InvalidOperationException("Client failed to start.");
            Task<string> output = client.StandardOutput.ReadToEndAsync(), error = client.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            await client.WaitForExitAsync(deadline.Token);
            string reply = await output;
            Console.WriteLine(JsonSerializer.Serialize(new { Command = command, client.ExitCode, Response = reply, Error = await error }));
            if (client.ExitCode != 0) throw new InvalidOperationException("Existing probe rejected command; no retry.");
            if (command == "run-plan")
            {
                using var response = JsonDocument.Parse(reply.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
                var result = response.RootElement;
                if (result.TryGetProperty("Tone", out var nested)) result = nested;
                expectedOutputWrites = result.GetProperty("Sent").GetInt32();
            }
        }
        session.Flush();
    }
    finally { session.Stop(); await consuming; }
    bool deliveryObserved = summary.BoundLinks.Count == 1 && source.EventsLost == 0 &&
        expectedOutputWrites is > 0 && expectedOutputWrites == summary.OutputTimesMs.Count && summary.PendingAssemblies == 0;
    Console.WriteLine(JsonSerializer.Serialize(new { Session = sessionName, Summary = summary, source.EventsLost,
        ExpectedOutputWrites = expectedOutputWrites, CompleteHostDeliveryObserved = deliveryObserved, StoredAudio = false, StoredRawTrace = false }));
    return summary.BoundLinks.Count == 1 && source.EventsLost == 0 && (!packetPlan || deliveryObserved) ? 0 : 1;
}
catch (Exception e) { Console.Error.WriteLine(JsonSerializer.Serialize(new { Error = e.Message, Session = sessionName })); return 1; }

sealed class PacketSummary : IDisposable
{
    private sealed class AssemblyLane
    {
        internal readonly byte[] Bytes = new byte[512];
        internal int Expected, Count;
        internal double Started;
        internal void Clear() { Array.Clear(Bytes); Expected = Count = 0; Started = 0; }
    }
    private readonly AssemblyLane[] lanes = { new(), new() };
    static readonly byte[] StateRequest = Convert.FromHexString("1891010100000000");
    public int Events { get; private set; }
    public int CompleteAtt { get; private set; }
    public int IgnoredFragmentOrShape { get; private set; }
    public int ReassembledAtt { get; private set; }
    public int AbandonedAssemblies { get; private set; }
    public int PendingAssemblies => lanes.Count(l => l.Expected != 0);
    public Dictionary<string, int> AclShapes { get; } = new();
    public Dictionary<byte, int> PacketTypes { get; } = new();
    public SortedSet<int> CommandAttributes { get; } = new();
    public SortedSet<int> BoundLinks { get; } = new();
    public Dictionary<string, int> HeadsetHeaders { get; } = new();
    public Dictionary<string, int> AttCounts { get; } = new();
    public List<double> HeadsetTimesMs { get; } = new();
    public List<double> OutputTimesMs { get; } = new();
    public List<double> OutputCompleteTimesMs { get; } = new();

    public void Accept(ReadOnlySpan<byte> data, double timestamp)
    {
        Events++;
        if (data.Length >= 8) PacketTypes[data[3]] = PacketTypes.GetValueOrDefault(data[3]) + 1;
        // UInt8 credits, UInt16 ACL credits, UInt8 BIP type, UInt32 length.
        // Type 2 was observed carrying LE Meta Events, not ACL on this host.
        if (data.Length < 8 || data[3] is not (3 or 4) || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != data.Length - 8) return;
        var acl = data[8..];
        if (acl.Length < 4 || U16(acl, 2) != acl.Length - 4 || (U16(acl, 0) & 0xc000) != 0)
        { IgnoredFragmentOrShape++; return; }
        int link = U16(acl, 0) & 0xfff;
        int boundary = (U16(acl, 0) >> 12) & 3;
        bool bound = BoundLinks.Count == 1 && BoundLinks.Contains(link);
        if (bound)
        {
            // Sizes/boundary bits only; continuation payload is never exported.
            string shape = $"Type={data[3]},PB={boundary},Bytes={acl.Length - 4}";
            if (AclShapes.Count < 64 || AclShapes.ContainsKey(shape)) AclShapes[shape] = AclShapes.GetValueOrDefault(shape) + 1;
        }
        var lane = lanes[data[3] - 3];
        if (boundary == 1)
        {
            if (!bound || lane.Expected == 0 || acl.Length == 4 || acl.Length - 4 > lane.Expected - lane.Count)
            {
                IgnoredFragmentOrShape++;
                if (bound && lane.Expected != 0) { lane.Clear(); AbandonedAssemblies++; }
                return;
            }
            acl[4..].CopyTo(lane.Bytes.AsSpan(lane.Count));
            lane.Count += acl.Length - 4;
            if (lane.Count == lane.Expected)
            {
                try { ReassembledAtt++; AcceptAtt(link, lane.Bytes.AsSpan(0, lane.Expected), lane.Started, timestamp); }
                finally { lane.Clear(); }
            }
            return;
        }
        if (bound && lane.Expected != 0) { lane.Clear(); AbandonedAssemblies++; }
        if (boundary is not (0 or 2) || acl.Length < 8 || U16(acl, 6) != 4 ||
            U16(acl, 4) is < 3 or > 512 || acl.Length - 8 > U16(acl, 4))
        { IgnoredFragmentOrShape++; return; }
        int expected = U16(acl, 4);
        if (acl.Length - 8 == expected) { AcceptAtt(link, acl[8..], timestamp, timestamp); return; }
        // Never retain another device's traffic. The short exact state request
        // binds this link before any audio plan starts. Two bounded lanes keep
        // opposite BIP directions separate, and are scrubbed after use/stop.
        if (!bound) { IgnoredFragmentOrShape++; return; }
        lane.Expected = expected; lane.Count = acl.Length - 8; lane.Started = timestamp;
        acl[8..].CopyTo(lane.Bytes);
    }

    private void AcceptAtt(int link, ReadOnlySpan<byte> att, double timestamp, double completed)
    {
        CompleteAtt++;
        int handle = U16(att, 1);
        // Bind only to this probe's exact observed, read-only 18/01 command.
        // Never infer a peer from a coincidentally matching attribute number.
        if (att[0] is 0x52 or 0x12 && att[3..].SequenceEqual(StateRequest)) { BoundLinks.Add(link); CommandAttributes.Add(handle); }
        if (BoundLinks.Count != 1)
        {
            foreach (var pending in lanes) { if (pending.Expected != 0) AbandonedAssemblies++; pending.Clear(); }
            return;
        }
        if (!BoundLinks.Contains(link)) return;
        string key = $"{att[0]:X2}:{handle}:{att.Length - 3}";
        if (AttCounts.Count < 64 || AttCounts.ContainsKey(key)) AttCounts[key] = AttCounts.GetValueOrDefault(key) + 1;
        // Live ATT trace resolved value handles 44/46. WinRT inventory reports
        // their preceding declaration handles 43/45; do not conflate the two.
        if (att[0] == 0x52 && handle == 44 && OutputTimesMs.Count < 1024)
        { OutputTimesMs.Add(timestamp); OutputCompleteTimesMs.Add(completed); }
        if (att[0] != 0x1b || handle != 46 || att.Length != 115) return;
        var header = att[3..];
        key = $"Jack={header[13]:X2},AudioLength={header[14]},MotionLength={header[65]}";
        if (HeadsetHeaders.Count < 64 || HeadsetHeaders.ContainsKey(key)) HeadsetHeaders[key] = HeadsetHeaders.GetValueOrDefault(key) + 1;
        if (HeadsetTimesMs.Count < 256) HeadsetTimesMs.Add(timestamp);
    }
    public void Dispose() { foreach (var lane in lanes) lane.Clear(); }
    static int U16(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(value[offset..]);

    public static void SelfTest()
    {
        byte[] Frame(int link, int handle, byte opcode, byte[] value)
        {
            byte[] data = new byte[19 + value.Length]; data[3] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length - 8);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)(link | 0x2000));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)(data.Length - 12));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)(value.Length + 3)); data[14] = 4;
            data[16] = opcode; BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(17), (ushort)handle); value.CopyTo(data, 19); return data;
        }
        using var s = new PacketSummary();
        byte[] headset = new byte[112]; headset[13] = 13; headset[14] = 0; headset[65] = 40;
        byte[] frame = Frame(5, 46, 0x1b, headset);
        for (int i = 0; i < frame.Length; i++) s.Accept(frame.AsSpan(0, i), 0);
        s.Accept(frame, 0); if (s.HeadsetHeaders.Count != 0) throw new Exception("Unbound frame leaked.");
        s.Accept(Frame(5, 20, 0x52, StateRequest), 0); s.Accept(frame, 1);
        headset[14] = 50; s.Accept(Frame(5, 46, 0x1b, headset), 2);
        s.Accept(Frame(6, 46, 0x1b, headset), 3);
        s.Accept(Frame(5, 9, 0x1b, headset), 3);
        if (s.HeadsetHeaders.Count != 2 || s.HeadsetTimesMs.Count != 2) throw new Exception("Header/filter mismatch.");
        byte[] fragment = (byte[])frame.Clone(); fragment[9] = 0x10; s.Accept(fragment, 4);
        if (s.HeadsetTimesMs.Count != 2) throw new Exception("Fragment was misread.");
        s.Accept(Frame(6, 20, 0x52, StateRequest), 4); s.Accept(frame, 4);
        if (s.HeadsetTimesMs.Count != 2 || s.BoundLinks.Count != 2) throw new Exception("Ambiguous peer accepted.");
        byte[] Fragment(byte[] whole, int offset, int count, byte type = 4)
        {
            // offset/count address the complete L2CAP packet, including its
            // four-byte header only in the first fragment.
            byte[] data = new byte[12 + count]; data[3] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length - 8);
            int flags = offset == 0 ? 0x2000 : 0x1000;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)(U16(whole, 8) & 0xfff | flags));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)count);
            whole.AsSpan(12 + offset, count).CopyTo(data.AsSpan(12)); return data;
        }
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        bool Scrubbed(PacketSummary value) => value.lanes.All(l => l.Count == 0 && l.Expected == 0 && l.Bytes.All(b => b == 0));
        foreach (int payloadSize in new[] { 480, 509 })
        {
            using var r = new PacketSummary();
            r.Accept(Frame(5, 20, 0x52, StateRequest), 0);
            byte[] output = Frame(5, 44, 0x52, Enumerable.Repeat((byte)0x5a, payloadSize).ToArray());
            byte[] first = Fragment(output, 0, 100), middle = Fragment(output, 100, 200), last = Fragment(output, 300, payloadSize + 7 - 300);
            for (int i = 0; i < first.Length; i++) r.Accept(first.AsSpan(0, i), 0);
            r.Accept(first, 10);
            Require(r.PendingAssemblies == 1 && r.OutputTimesMs.Count == 0, "Partial output counted.");
            byte[] other = Frame(6, 44, 0x52, new byte[payloadSize]);
            r.Accept(Fragment(other, 100, 200), 11);
            Require(r.PendingAssemblies == 1, "Other peer disrupted bound assembly.");
            // Independent input and output fragment lanes may interleave.
            byte[] input = Frame(5, 46, 0x1b, headset);
            r.Accept(Fragment(input, 0, 60, 3), 12);
            r.Accept(middle, 13);
            r.Accept(Fragment(input, 60, 59, 3), 14);
            r.Accept(last, 15);
            Require(r.ReassembledAtt == 2 && r.OutputTimesMs.SequenceEqual(new[] { 10.0 }) &&
                r.OutputCompleteTimesMs.SequenceEqual(new[] { 15.0 }) && r.HeadsetTimesMs.SequenceEqual(new[] { 12.0 }), "Reassembly timing/direction mismatch.");
            Require(Scrubbed(r), "Completed payload retained.");
            r.Accept(last, 16);
            Require(r.OutputTimesMs.Count == 1, "Orphan continuation counted.");
            r.Accept(first, 17);
            r.Accept(first, 18);
            Require(r.AbandonedAssemblies == 1 && r.PendingAssemblies == 1, "Replacement start did not abandon old assembly.");
            r.Accept(Fragment(output, 1, payloadSize + 6), 19);
            Require(r.AbandonedAssemblies == 2 && Scrubbed(r), "Oversized continuation not rejected/scrubbed.");
            r.Accept(first, 20);
            r.Dispose();
            Require(Scrubbed(r), "Disposal retained payload.");
            r.Accept(first, 21);
            r.Accept(Frame(6, 20, 0x52, StateRequest), 22);
            Require(r.BoundLinks.Count == 2 && Scrubbed(r), "Ambiguous binding retained payload.");
        }
        using var two = new PacketSummary();
        two.Accept(Frame(5, 20, 0x52, StateRequest), 0);
        byte[] paired = Frame(5, 44, 0x52, new byte[480]);
        byte[] pbZero = Fragment(paired, 0, 251); pbZero[9] = 0;
        two.Accept(pbZero, 1); two.Accept(Fragment(paired, 251, 236), 2);
        Require(two.OutputTimesMs.Count == 1 && two.ReassembledAtt == 1 && Scrubbed(two), "Two-fragment/PB0 output failed.");
        byte[] broadcast = Frame(5, 44, 0x52, new byte[50]); broadcast[9] |= 0x40;
        two.Accept(broadcast, 3);
        Require(two.OutputTimesMs.Count == 1, "Broadcast-shaped ACL accepted.");
        Console.WriteLine("Passed: truncation sweep, binding, header filters, 480/509-byte two/three-fragment reassembly, PB0/2 starts, interleaved directions, timestamps, wrong peers, orphan/oversized continuations, replacement starts, broadcast rejection, ambiguity and RAM scrubbing.");
    }
}
