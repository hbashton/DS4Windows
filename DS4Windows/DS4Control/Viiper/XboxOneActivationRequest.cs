/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Threading;

namespace DS4Windows;

/// <summary>
/// One cold management request, not the controller's feedback transport.
/// Serializes cancellation with source disposal so a retirement snapshot
/// cannot call Cancel on a source already released by request completion.
/// </summary>
internal sealed class XboxOneActivationRequest : IDisposable
{
    private readonly object gate = new();
    private readonly ViiperVirtualDeviceLifetime owner;
    private readonly CancellationTokenSource cancellation = new();
    private bool disposed;

    internal XboxOneActivationRequest(ViiperVirtualDeviceLifetime owner)
    {
        this.owner = owner;
        Token = cancellation.Token;
    }

    internal CancellationToken Token { get; }

    internal void Cancel()
    {
        lock (gate)
        {
            if (!disposed)
                cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            cancellation.Dispose();
        }
        owner.ReleaseXboxOneActivationRequest(this);
    }
}
