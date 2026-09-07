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
if (args.Length != 4 || args[3] is not ("headset-observe" or "active-opus5" or "active-opus20"))
{
    Console.Error.WriteLine("Use <existing lab client.exe> <session.json> <explicit Line In ID> headset-observe|active-opus5|active-opus20");
    return 2;
}
if (!(TraceEventSession.IsElevated() ?? false)) { Console.Error.WriteLine("Same-user elevation required."); return 1; }
string sessionName = "DS4W-S2-Audio-Headers-" + Environment.ProcessId;
var summary = new PacketSummary();
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
    try
    {
        foreach (string command in new[] { "status", "audio-state", args[3], "status" })
        {
            var start = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.GetFullPath(args[1])); start.ArgumentList.Add(command);
            if (command.StartsWith("active-")) start.ArgumentList.Add(args[2]);
            using var client = Process.Start(start) ?? throw new InvalidOperationException("Client failed to start.");
            Task<string> output = client.StandardOutput.ReadToEndAsync(), error = client.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            await client.WaitForExitAsync(deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { Command = command, client.ExitCode, Response = await output, Error = await error }));
            if (client.ExitCode != 0) throw new InvalidOperationException("Existing probe rejected command; no retry.");
        }
        session.Flush();
    }
    finally { session.Stop(); await consuming; }
    Console.WriteLine(JsonSerializer.Serialize(new { Session = sessionName, Summary = summary, source.EventsLost, StoredAudio = false, StoredRawTrace = false }));
    return summary.BoundLinks.Count == 1 && source.EventsLost == 0 ? 0 : 1;
}
catch (Exception e) { Console.Error.WriteLine(JsonSerializer.Serialize(new { Error = e.Message, Session = sessionName })); return 1; }

sealed class PacketSummary
{
    static readonly byte[] StateRequest = Convert.FromHexString("1891010100000000");
    public int Events { get; private set; }
    public int CompleteAtt { get; private set; }
    public int IgnoredFragmentOrShape { get; private set; }
    public Dictionary<byte, int> PacketTypes { get; } = new();
    public SortedSet<int> CommandAttributes { get; } = new();
    public SortedSet<int> BoundLinks { get; } = new();
    public Dictionary<string, int> HeadsetHeaders { get; } = new();
    public Dictionary<string, int> AttCounts { get; } = new();
    public List<double> HeadsetTimesMs { get; } = new();
    public List<double> OutputTimesMs { get; } = new();

    public void Accept(ReadOnlySpan<byte> data, double timestamp)
    {
        Events++;
        if (data.Length >= 8) PacketTypes[data[3]] = PacketTypes.GetValueOrDefault(data[3]) + 1;
        // UInt8 credits, UInt16 ACL credits, UInt8 BIP type, UInt32 length.
        // Type 2 was observed carrying LE Meta Events, not ACL on this host.
        if (data.Length < 8 || data[3] is not (3 or 4) || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != data.Length - 8) return;
        var acl = data[8..];
        if (acl.Length < 11 || U16(acl, 2) != acl.Length - 4 || ((U16(acl, 0) >> 12) & 3) == 1 ||
            U16(acl, 4) != acl.Length - 8 || U16(acl, 6) != 4) { IgnoredFragmentOrShape++; return; }
        CompleteAtt++;
        int link = U16(acl, 0) & 0xfff;
        var att = acl[8..];
        int handle = U16(att, 1);
        // Bind only to this probe's exact observed, read-only 18/01 command.
        // Never infer a peer from a coincidentally matching attribute number.
        if (att[0] is 0x52 or 0x12 && att[3..].SequenceEqual(StateRequest)) { BoundLinks.Add(link); CommandAttributes.Add(handle); }
        if (BoundLinks.Count != 1 || !BoundLinks.Contains(link)) return;
        string key = $"{att[0]:X2}:{handle}:{att.Length - 3}";
        if (AttCounts.Count < 64 || AttCounts.ContainsKey(key)) AttCounts[key] = AttCounts.GetValueOrDefault(key) + 1;
        // Live ATT trace resolved value handles 44/46. WinRT inventory reports
        // their preceding declaration handles 43/45; do not conflate the two.
        if (att[0] == 0x52 && handle == 44 && OutputTimesMs.Count < 256) OutputTimesMs.Add(timestamp);
        if (att[0] != 0x1b || handle != 46 || att.Length != 115) return;
        var header = att[3..];
        key = $"Jack={header[13]:X2},AudioLength={header[14]},MotionLength={header[65]}";
        if (HeadsetHeaders.Count < 64 || HeadsetHeaders.ContainsKey(key)) HeadsetHeaders[key] = HeadsetHeaders.GetValueOrDefault(key) + 1;
        if (HeadsetTimesMs.Count < 256) HeadsetTimesMs.Add(timestamp);
    }
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
        var s = new PacketSummary();
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
        Console.WriteLine("Passed: truncation sweep, binding, both audio lengths, other peer/attribute, fragment and ambiguity rejection.");
    }
}
