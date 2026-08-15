/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DS4Windows
{
    /// <summary>
    /// Capability token for the separately-built, headless native live gate.
    /// The ordinary DS4Windows process never creates one. Requiring the same
    /// canonical nonce at the command line and in the environment prevents an
    /// accidental invocation from acquiring validation-only hooks.
    /// </summary>
    internal sealed class ViiperLiveValidationLease
    {
        internal const string NonceEnvironmentVariable =
            "DS4WINDOWS_VIIPER_LIVE_VALIDATION_NONCE";
        internal const int NonceLength = 64;

        private ViiperLiveValidationLease(byte[] nonceFingerprint)
        {
            NonceFingerprint = nonceFingerprint;
        }

        internal byte[] NonceFingerprint { get; }

        internal static ViiperLiveValidationLease Create(string commandNonce)
        {
            string environmentNonce = Environment.GetEnvironmentVariable(
                NonceEnvironmentVariable);
            ValidateNonce(commandNonce, "command-line");
            ValidateNonce(environmentNonce, NonceEnvironmentVariable);

            byte[] command = Encoding.ASCII.GetBytes(commandNonce);
            byte[] environment = Encoding.ASCII.GetBytes(environmentNonce);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(command,
                        environment))
                {
                    throw new ViiperIdentityException(
                        $"The command-line live-validation nonce does not match {NonceEnvironmentVariable}.");
                }

                return new ViiperLiveValidationLease(
                    SHA256.HashData(command));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(command);
                CryptographicOperations.ZeroMemory(environment);
            }
        }

        private static void ValidateNonce(string value, string source)
        {
            if (value == null || value.Length != NonceLength ||
                value.Any(character =>
                    !(character >= '0' && character <= '9') &&
                    !(character >= 'a' && character <= 'f')))
            {
                throw new ViiperIdentityException(
                    $"The {source} live-validation nonce must be exactly {NonceLength} lowercase hexadecimal characters.");
            }
        }
    }

    internal sealed class ViiperLiveValidationSnapshot
    {
        internal string HandlerName { get; init; }
        internal string StreamProtocol { get; init; }
        internal byte StreamFrameVersion { get; init; }
        internal bool Connected { get; init; }
        internal bool SupportsMicrophone { get; init; }
        internal bool SupportsDirectSpeaker { get; init; }
        internal bool SupportsAtomicAudioHaptics { get; init; }
        internal ViiperNativeBackendIdentity BackendIdentity { get; init; }
        internal ViiperVirtualDeviceIdentity DeviceIdentity { get; init; }
        internal long StatePacketsSubmitted { get; init; }
        internal long StatePacketsWritten { get; init; }
        internal long StatePacketsCoalesced { get; init; }
        internal int StreamRecoveryAttempts { get; init; }
        internal long FeedbackFramesObserved { get; init; }
        internal byte[] LastFeedbackPayload { get; init; }
        internal long ValidationMicrophoneFramesSubmitted { get; init; }
        internal long ValidationMicrophoneBytesSubmitted { get; init; }
        internal long ValidationTransportInterruptions { get; init; }
        internal long ValidationStreamRecoveriesCompleted { get; init; }
        internal long SpeakerFramesEnqueued { get; init; }
        internal long SpeakerFramesDequeued { get; init; }
        internal long SpeakerFramesDropped { get; init; }
        internal long SpeakerFramesExpired { get; init; }
        internal long SpeakerFramesDelivered { get; init; }
        internal long SpeakerFramesStale { get; init; }
        internal long SpeakerNoSubscriberDeferrals { get; init; }
        internal long SpeakerCallbackFailures { get; init; }
        internal long ControlFramesEnqueued { get; init; }
        internal long ControlFramesDequeued { get; init; }
        internal long ControlFramesDropped { get; init; }
        internal long OrderedControlFramesEnqueued { get; init; }
        internal long OrderedControlFramesDequeued { get; init; }
        internal long OrderedControlFramesDropped { get; init; }
        internal long OrderedControlFramesExpired { get; init; }
        internal long ControlFramesDelivered { get; init; }
        internal long ControlFramesStale { get; init; }
        internal long ControlCallbackFailures { get; init; }
    }
}
