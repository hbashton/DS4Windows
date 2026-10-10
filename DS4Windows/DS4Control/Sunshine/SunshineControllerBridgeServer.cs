/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Sunshine;

/// <summary>Local, fixed-record IPC endpoint for a Sunshine input backend.</summary>
internal sealed class SunshineControllerBridgeServer : IDisposable
{
    internal const string PipeName = "Vibeshine.ControllerBridge.v1";

    private readonly object lifecycleGate = new();
    private readonly object pipeGate = new();
    private readonly object writeGate = new();
    private readonly Action<SunshineControllerBridgePacket> packetReceived;
    private readonly Action clientDisconnected;
    private readonly string pipeName;
    private CancellationTokenSource stopping;
    private NamedPipeServerStream activePipe;
    private Task runTask;
    private int disposed;
    private static int debugReadRecords;

    internal SunshineControllerBridgeServer(
        Action<SunshineControllerBridgePacket> packetReceived,
        Action clientDisconnected, string pipeName = PipeName)
    {
        this.packetReceived = packetReceived ??
            throw new ArgumentNullException(nameof(packetReceived));
        this.clientDisconnected = clientDisconnected ??
            throw new ArgumentNullException(nameof(clientDisconnected));
        this.pipeName = string.IsNullOrWhiteSpace(pipeName) ?
            throw new ArgumentException("A pipe name is required.", nameof(pipeName)) :
            pipeName;
    }

    internal bool IsRunning => runTask is { IsCompleted: false };

    internal void Start()
    {
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(SunshineControllerBridgeServer));
            if (IsRunning) return;
            var source = new CancellationTokenSource();
            stopping = source;
            runTask = Task.Run(() => RunAsync(source));
        }
    }

    internal bool Send(in SunshineControllerBridgePacket packet)
    {
        lock (writeGate)
        {
            NamedPipeServerStream pipe = Volatile.Read(ref activePipe);
            if (pipe == null || !pipe.IsConnected) return false;
            Span<byte> record = stackalloc byte[SunshineControllerBridgePacket.RecordLength];
            packet.Write(record);
            try
            {
                pipe.Write(record);
                return true;
            }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    private async Task RunAsync(CancellationTokenSource source)
    {
        CancellationToken cancellationToken = source.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                bool connected = false;
                try
                {
                    pipe = CreatePipe(pipeName);
                    lock (pipeGate)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        activePipe = pipe;
                    }
                    await pipe.WaitForConnectionAsync(cancellationToken).
                        ConfigureAwait(false);
                    connected = true;

                    byte[] firstRecord = await ReadRecordAsync(pipe,
                        cancellationToken).ConfigureAwait(false);
                    if (firstRecord == null ||
                        !SunshineControllerBridgePacket.TryRead(firstRecord,
                            out SunshineControllerBridgePacket hello) ||
                        hello.Kind != SunshineControllerBridgePacketKind.Hello)
                    {
                        continue;
                    }

                    var accepted = new SunshineControllerBridgePacket(
                        SunshineControllerBridgePacketKind.HelloAccepted,
                        hello.SessionId, 0, 0, 0);
                    if (!Send(accepted))
                    {
                        DS4Windows.AppLogger.LogToGui(
                            "Sunshine bridge could not send its handshake response.", true);
                        continue;
                    }
                    DS4Windows.AppLogger.LogToGui(
                        "Sunshine bridge pipe handshake accepted.", false);
                    DS4Windows.AppLogger.LogToGui(
                        "Sunshine bridge reader is waiting for controller records.", false);

                    int loggedStates = 0;
                    var failedPacketHandlers = new HashSet<SunshineControllerBridgePacketKind>();
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        byte[] record = await ReadRecordAsync(pipe,
                            cancellationToken).ConfigureAwait(false);
                        if (record == null)
                            break;
                        if (!SunshineControllerBridgePacket.TryRead(record,
                                out SunshineControllerBridgePacket packet))
                        {
                            DS4Windows.AppLogger.LogToGui(
                                $"Sunshine bridge rejected a 64-byte record: {Convert.ToHexString(record.AsSpan(0, 24))}", true);
                            break;
                        }
                        if (packet.Kind == SunshineControllerBridgePacketKind.Hello)
                            break;
                        if (packet.Kind is SunshineControllerBridgePacketKind.Arrival or
                            SunshineControllerBridgePacketKind.Remove or
                            SunshineControllerBridgePacketKind.Feedback ||
                            (packet.Kind == SunshineControllerBridgePacketKind.State &&
                                loggedStates++ < 2))
                        {
                            DS4Windows.AppLogger.LogToGui(
                                $"Sunshine bridge read {packet.Kind} controller={packet.ControllerId} client={packet.ClientIndex} sequence={packet.Sequence} session={packet.SessionId:X16}.", false);
                        }
                        if (failedPacketHandlers.Contains(packet.Kind))
                            continue;
                        try
                        {
                            packetReceived(packet);
                        }
                        catch (Exception exception)
                        {
                            // Keep a faulty packet handler from disconnecting
                            // the pipe and replaying Arrival in a tight loop.
                            failedPacketHandlers.Add(packet.Kind);
                            DS4Windows.AppLogger.LogToGui(
                                $"Sunshine bridge {packet.Kind} handler failed; later {packet.Kind} records will be ignored for this connection: {exception}", true);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException) { }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // A malformed or disconnected peer must not tear down input
                    // handling or prevent a fresh local client from reconnecting.
                    DS4Windows.AppLogger.LogToGui(
                        $"Sunshine bridge pipe loop failed: {exception.GetType().Name}: {exception.Message}", true);
                }
                finally
                {
                    lock (pipeGate)
                    {
                        if (ReferenceEquals(activePipe, pipe))
                            activePipe = null;
                    }
                    pipe?.Dispose();
                    if (connected) clientDisconnected();
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(100, cancellationToken).
                            ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when
                        (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (lifecycleGate)
            {
                source.Dispose();
                if (ReferenceEquals(stopping, source)) stopping = null;
            }
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = new PipeSecurity();
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
        if (user == null)
            throw new InvalidOperationException("Could not identify the interactive user for the controller bridge.");

        const PipeAccessRights access = PipeAccessRights.ReadWrite |
            PipeAccessRights.CreateNewInstance;
        security.AddAccessRule(new PipeAccessRule(user, access,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            access, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            0, 0, security);
    }

    private static async Task<byte[]> ReadRecordAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] record = new byte[SunshineControllerBridgePacket.RecordLength];
        int offset = 0;
        while (offset < record.Length)
        {
            int read = await stream.ReadAsync(record.AsMemory(offset,
                    record.Length - offset), cancellationToken).
                ConfigureAwait(false);
            if (read == 0) return null;
            offset += read;
        }
        if (Interlocked.Increment(ref debugReadRecords) <= 8)
            DS4Windows.AppLogger.LogToGui(
                $"Sunshine bridge completed a {record.Length}-byte record (kind={record[6]}, sequence={BitConverter.ToUInt32(record, 12)}).", false);
        return record;
    }

    public void Dispose()
    {
        CancellationTokenSource source;
        lock (lifecycleGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            source = stopping;
        }
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
        lock (pipeGate)
        {
            try { activePipe?.Dispose(); }
            catch (IOException) { }
            activePipe = null;
        }
    }
}
