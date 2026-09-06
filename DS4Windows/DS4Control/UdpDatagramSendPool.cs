using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace DS4Windows;

/// <summary>
/// Bounded, nonwaiting ownership of UDP send arguments and their bytes. Each
/// entry stays reserved until its exact send completes, independent of how
/// many later publishers pass through the pool.
/// </summary>
/// <remarks>
/// The sender follows Socket.SendToAsync: true means exactly one eventual
/// completion (which may race the sender's return); false means synchronous
/// completion with no event; throwing means no submitted operation/event.
/// The sender must preserve UserToken and use only the supplied operation.
/// Dispose closes new admission but cannot retract an already admitted send.
/// The socket owner must close its exact socket to cancel transport activity.
/// </remarks>
internal sealed class UdpDatagramSendPool : IDisposable
{
    private const int Free = 0, Reserved = 1, Disposed = 2;
    private readonly Entry[] entries;
    private readonly int maxDatagramLength;
    private readonly Func<SocketAsyncEventArgs, bool> send;
    private int disposed, inFlightCount, disposedEntryCount;
    private long capacityDropCount, failureCount;

    internal UdpDatagramSendPool(int capacity, int maxDatagramLength,
        Func<SocketAsyncEventArgs, bool> send)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDatagramLength);
        ArgumentNullException.ThrowIfNull(send);
        this.maxDatagramLength = maxDatagramLength;
        this.send = send;
        entries = new Entry[capacity];
        try
        {
            for (int i = 0; i < entries.Length; i++)
                entries[i] = new Entry(this, maxDatagramLength);
        }
        catch
        {
            foreach (Entry entry in entries)
                entry?.Args.Dispose();
            throw;
        }
    }

    internal long CapacityDropCount => Interlocked.Read(ref capacityDropCount);
    internal long FailureCount => Interlocked.Read(ref failureCount);
    internal int InFlightCount => Volatile.Read(ref inFlightCount);
    // Exposes the actual completed disposal, useful for deterministic lifecycle
    // tests without depending on unspecified SocketAsyncEventArgs getters.
    internal int DisposedEntryCount => Volatile.Read(ref disposedEntryCount);

    internal bool TrySend(ReadOnlySpan<byte> datagram, IPEndPoint recipient)
    {
        if (Volatile.Read(ref disposed) != 0)
            return false;
        if (recipient == null || datagram.Length > maxDatagramLength)
        {
            Interlocked.Increment(ref failureCount);
            return false;
        }

        Entry owned = null;
        // One bounded scan, no semaphore, spin-wait, or ring-position reuse.
        // A completion just after we pass an entry may cause a conservative
        // drop, but can never grant two publishers ownership of one buffer.
        foreach (Entry entry in entries)
        {
            if (Interlocked.CompareExchange(ref entry.State, Reserved, Free) == Free)
            {
                owned = entry;
                Interlocked.Increment(ref inFlightCount);
                break;
            }
        }
        if (owned == null)
        {
            if (Volatile.Read(ref disposed) == 0)
                Interlocked.Increment(ref capacityDropCount);
            return false;
        }

        try
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                Release(owned, failed: false);
                return false;
            }
            datagram.CopyTo(owned.Buffer);
            // Keep the mutable IPEndPoint object private to this entry. IP
            // addresses are borrowed values; callers must not mutate an
            // address's IPv6 ScopeId concurrently with publication.
            owned.Recipient.Address = recipient.Address;
            owned.Recipient.Port = recipient.Port;
            owned.Args.RemoteEndPoint = owned.Recipient;
            owned.Args.SetBuffer(0, datagram.Length);
            owned.Args.SocketError = SocketError.Success;
            if (Volatile.Read(ref disposed) != 0)
            {
                Release(owned, failed: false);
                return false;
            }

            if (send(owned.Args))
                return true;
            bool success = owned.Args.SocketError == SocketError.Success;
            Release(owned, failed: !success);
            return success;
        }
        catch (Exception)
        {
            Release(owned, failed: true);
            return false;
        }
    }

    /// <summary>
    /// Actual OS completion path, also callable by a fake asynchronous sender
    /// obeying the one-completion contract. Foreign or already-returned args
    /// cannot release an entry. A sender must not replay a completion after
    /// that args object has legitimately been reused for another operation.
    /// </summary>
    internal bool Return(SocketAsyncEventArgs args)
    {
        if (args?.UserToken is not Entry entry ||
            !ReferenceEquals(entry.Owner, this) || !ReferenceEquals(entry.Args, args))
            return false;
        return Release(entry, args.SocketError != SocketError.Success);
    }

    private bool Release(Entry entry, bool failed)
    {
        if (Interlocked.CompareExchange(ref entry.State, Free, Reserved) != Reserved)
            return false;
        if (failed) Interlocked.Increment(ref failureCount);
        Interlocked.Decrement(ref inFlightCount);
        // Do not select Free/Disposed from a stale pre-release read. Dispose
        // may have already skipped our Reserved entry before this release.
        if (Volatile.Read(ref disposed) != 0)
            DisposeFreeEntry(entry);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (Entry entry in entries)
            DisposeFreeEntry(entry);
    }

    private void DisposeFreeEntry(Entry entry)
    {
        if (Interlocked.CompareExchange(ref entry.State, Disposed, Free) != Free)
            return;
        entry.Args.Dispose();
        Interlocked.Increment(ref disposedEntryCount);
    }

    private sealed class Entry
    {
        internal readonly UdpDatagramSendPool Owner;
        internal readonly byte[] Buffer;
        internal readonly SocketAsyncEventArgs Args;
        internal readonly IPEndPoint Recipient = new(IPAddress.Any, 0);
        internal int State;

        internal Entry(UdpDatagramSendPool owner, int length)
        {
            Owner = owner;
            Buffer = new byte[length];
            Args = new SocketAsyncEventArgs();
            try
            {
                Args.UserToken = this;
                Args.SetBuffer(Buffer, 0, length);
                Args.Completed += static (_, args) =>
                {
                    if (args.UserToken is Entry entry)
                        entry.Owner.Return(args);
                };
            }
            catch
            {
                Args.Dispose();
                throw;
            }
        }
    }
}
