using System.Diagnostics;
using System.Globalization;

namespace DS4Windows.ViiperLiveValidation;

internal sealed class LiveValidationRunner
{
    private readonly LiveValidationOptions options;
    private readonly EvidenceDocument evidence;
    private readonly CancellationToken cancellationToken;
    private ViiperLiveValidationLease? lease;
    private SourceBindings? bindings;

    internal LiveValidationRunner(LiveValidationOptions options,
        EvidenceDocument evidence, CancellationToken cancellationToken)
    {
        this.options = options;
        this.evidence = evidence;
        this.cancellationToken = cancellationToken;
    }

    internal async Task RunAsync()
    {
        evidence.CurrentStage = "consent";
        lease = ViiperLiveValidationLease.Create(options.Nonce);
        evidence.ConsentNonceSha256 = Convert.ToHexString(
            lease.NonceFingerprint).ToLowerInvariant();

        evidence.CurrentStage = "source-bindings";
        bindings = SourceBindings.Load(options);
        try
        {
            evidence.Bindings = bindings.Evidence;
            bindings.ValidateEvidenceOutputPath(options.OutputPath);
            string temporaryRoot = Path.Combine(Path.GetTempPath(),
                "DS4Windows.ViiperLiveValidation-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                int failed = 0;
                foreach (ControllerSpec spec in ControllerSpec.All)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await RunControllerAsync(spec, temporaryRoot)
                            .ConfigureAwait(false))
                    {
                        failed++;
                    }
                }
                evidence.CurrentStage = "source-bindings-final";
                bindings.Revalidate();
                if (failed != 0)
                {
                    evidence.CurrentStage = "controller-summary";
                    throw new InvalidOperationException(
                        $"{failed} of {ControllerSpec.All.Count} production PlayStation handler validations failed; see per-controller finalized evidence.");
                }
            }
            finally
            {
                TryDeleteOwnedTemporaryDirectory(temporaryRoot);
            }
        }
        finally
        {
            bindings.Dispose();
        }
    }

    private async Task<bool> RunControllerAsync(ControllerSpec spec,
        string temporaryRoot)
    {
        var controller = new ControllerEvidence
        {
            Name = spec.Name,
            Handler = spec.Handler,
            Vid = spec.Vid,
            Pid = spec.Pid,
            StreamProtocol = spec.StreamProtocol,
        };
        evidence.Controllers.Add(controller);
        var failures = new List<Exception>();
        string prefix = Path.Combine(temporaryRoot,
            spec.Name.ToLowerInvariant());
        string inputBaseline = prefix + ".hid.before";
        string mediaBaseline = prefix + ".media.before";
        string inputAfter = prefix + ".hid.after";
        string mediaAfter = prefix + ".media.after";
        ViiperOutDevice? device = null;
        var mediaWitness = new MediaWitness();
        Action<ViiperOutDevice, byte[], int>? pcmHandler = null;
        ViiperAtomicAudioHapticsHandler? atomicHandler = null;

        try
        {
            evidence.CurrentStage = spec.Name + "/baseline-observers";
            await ProbeRunner.RunAsync(bindings!.InputProbe,
                new[] { "snapshot", inputBaseline }, TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            await ProbeRunner.RunAsync(bindings.MediaProbe,
                new[] { "snapshot", mediaBaseline }, TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            controller.Cleanup.HidBaselineSnapshotSha256 =
                SourceBindings.Sha256File(inputBaseline);
            controller.Cleanup.MediaBaselineSnapshotSha256 =
                SourceBindings.Sha256File(mediaBaseline);

            evidence.CurrentStage = spec.Name + "/connect-production-handler";
            device = new ViiperOutDevice(spec.OutputType, spec.VirtualType,
                audioOnlySidecar: false, lease!, bindings.Metadata);
            pcmHandler = (_, payload, length) =>
                mediaWitness.Observe(payload, 0, length);
            atomicHandler = (source, payload, feedbackOffset, feedbackLength,
                speakerOffset, speakerLength, targetDeviceIndex) =>
            {
                source.ApplyAtomicAudioHapticsFeedback(
                    CopyRange(payload, feedbackOffset, feedbackLength),
                    feedbackLength, targetDeviceIndex);
                mediaWitness.Observe(payload, speakerOffset, speakerLength);
            };
            device.VirtualSpeakerPcmReceived += pcmHandler;
            device.VirtualAtomicAudioHapticsReceived += atomicHandler;
            device.Connect();
            device.BindPhysicalController(0);

            ViiperLiveValidationSnapshot connected =
                device.GetLiveValidationSnapshot(lease!);
            ValidateConnectedReceipt(spec, connected);
            controller.PingReceipt = MapPing(connected.BackendIdentity!);
            controller.DeviceReceipt = MapDevice(connected.DeviceIdentity!);

            evidence.CurrentStage = spec.Name + "/hid-input-qpc";
            controller.Input = await ProbeRunner.MeasureInputAsync(
                bindings.InputProbe, inputBaseline, spec, options.Samples,
                device, cancellationToken).ConfigureAwait(false);

            evidence.CurrentStage = spec.Name + "/hid-output-feedback";
            controller.Feedback = await VerifyFeedbackAsync(spec, device,
                inputBaseline, controller.Input.ObserverPath)
                .ConfigureAwait(false);

            evidence.CurrentStage = spec.Name + "/coreaudio-duplex";
            controller.Media = await ExerciseMediaAsync(spec, device,
                mediaBaseline, mediaWitness, options.MediaSeconds)
                .ConfigureAwait(false);

            evidence.CurrentStage = spec.Name + "/stream-reconnect";
            controller.Reconnect = await VerifyReconnectAsync(spec, device,
                inputBaseline, mediaBaseline, mediaWitness)
                .ConfigureAwait(false);

            evidence.CurrentStage = spec.Name + "/final-counters";
            controller.Counters = MapCounters(
                device.GetLiveValidationSnapshot(lease!));
            ValidateCounters(spec, controller.Counters);
        }
        catch (Exception error)
        {
            failures.Add(error);
            AddControllerFailure(controller, error);
        }
        finally
        {
            evidence.CurrentStage = spec.Name + "/disconnect";
            controller.Cleanup.DisconnectAttempted = device != null;
            if (device != null)
            {
                try
                {
                    if (pcmHandler != null)
                    {
                        device.VirtualSpeakerPcmReceived -= pcmHandler;
                    }
                    if (atomicHandler != null)
                    {
                        device.VirtualAtomicAudioHapticsReceived -=
                            atomicHandler;
                    }
                    device.Disconnect();
                    controller.Cleanup.DisconnectSucceeded = true;
                }
                catch (Exception error)
                {
                    failures.Add(error);
                    AddControllerFailure(controller, error);
                }
            }

            try
            {
                evidence.CurrentStage = spec.Name + "/observer-cleanup";
                controller.Cleanup.HidBaselineRestored =
                    await WaitForBaselineAsync(bindings!.InputProbe,
                        inputBaseline, inputAfter).ConfigureAwait(false);
                controller.Cleanup.MediaBaselineRestored =
                    await WaitForBaselineAsync(bindings.MediaProbe,
                        mediaBaseline, mediaAfter).ConfigureAwait(false);
                if (File.Exists(inputAfter))
                {
                    controller.Cleanup.HidAfterSnapshotSha256 =
                        SourceBindings.Sha256File(inputAfter);
                }
                if (File.Exists(mediaAfter))
                {
                    controller.Cleanup.MediaAfterSnapshotSha256 =
                        SourceBindings.Sha256File(mediaAfter);
                }
                if (!controller.Cleanup.HidBaselineRestored ||
                    !controller.Cleanup.MediaBaselineRestored)
                {
                    throw new IOException(
                        "The independent HID/CoreAudio observer baseline was not restored after conditional device removal.");
                }
            }
            catch (Exception error)
            {
                failures.Add(error);
                AddControllerFailure(controller, error);
            }
        }

        controller.Status = failures.Count == 0 ? "pass" : "failure";
        return failures.Count == 0;
    }

    private async Task<FeedbackEvidence> VerifyFeedbackAsync(
        ControllerSpec spec, ViiperOutDevice device, string inputBaseline,
        string inputObserverPath)
    {
        ViiperLiveValidationSnapshot before =
            device.GetLiveValidationSnapshot(lease!);
        ProbeResult probe = await ProbeRunner.RunAsync(bindings!.InputProbe,
            new[]
            {
                "feedback", inputBaseline, spec.Vid, spec.Pid,
                spec.FeedbackKind, "hid-output-v1",
            }, TimeSpan.FromSeconds(45), cancellationToken)
            .ConfigureAwait(false);
        string[] receipt = probe.StandardOutput.Split(new[] { ' ' }, 3,
            StringSplitOptions.RemoveEmptyEntries);
        int expectedReportLength = spec.VirtualType ==
            ViiperVirtualDeviceType.DualShock4 ? 32 : 48;
        if (receipt.Length != 3 || receipt[0] != "WROTE" ||
            !int.TryParse(receipt[1], NumberStyles.None,
                CultureInfo.InvariantCulture, out int reportLength) ||
            reportLength != expectedReportLength ||
            !string.Equals(receipt[2], inputObserverPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"The exact HID output probe returned a non-canonical receipt: '{probe.StandardOutput}'.");
        }
        byte[] expected = ExpectedFeedback(spec);
        ViiperLiveValidationSnapshot observed = await WaitForSnapshotAsync(
            device, snapshot => snapshot.FeedbackFramesObserved >
                    before.FeedbackFramesObserved &&
                snapshot.LastFeedbackPayload != null &&
                snapshot.LastFeedbackPayload.SequenceEqual(expected),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        byte[] payload = observed.LastFeedbackPayload ?? Array.Empty<byte>();
        return new FeedbackEvidence
        {
            ProbeOutput = EvidenceLimits.Truncate(probe.StandardOutput, 8192),
            ObserverPath = receipt[2],
            HidOutputReportLength = reportLength,
            ExpectedPayloadHex = Convert.ToHexString(expected)
                .ToLowerInvariant(),
            ObservedPayloadHex = Convert.ToHexString(payload)
                .ToLowerInvariant(),
            ObservedFrameNumber = observed.FeedbackFramesObserved,
            ExactMatch = payload.SequenceEqual(expected),
        };
    }

    private async Task<MediaEvidence> ExerciseMediaAsync(ControllerSpec spec,
        ViiperOutDevice device, string mediaBaseline,
        MediaWitness witness, int durationSeconds)
    {
        MediaWitnessSnapshot witnessBefore = witness.Snapshot();
        ViiperLiveValidationSnapshot countersBefore =
            device.GetLiveValidationSnapshot(lease!);
        byte[] microphonePcm = BuildMicrophonePcm(spec);
        using var probeStop = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        Task<ProbeResult> probeTask = ProbeRunner.RunAsync(
            bindings!.MediaProbe,
            new[]
            {
                "exercise", mediaBaseline,
                durationSeconds.ToString(CultureInfo.InvariantCulture),
                spec.MediaKind,
            }, TimeSpan.FromSeconds(durationSeconds + 45),
            probeStop.Token);

        try
        {
            while (!probeTask.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                device.SubmitLiveValidationMicrophonePcm(lease!,
                    microphonePcm);
                // Match the reference gate's 8 ms producer against the
                // production handler's bounded 10 ms presentation cadence.
                await Task.Delay(8, cancellationToken).ConfigureAwait(false);
            }
            ProbeResult probe = await probeTask.ConfigureAwait(false);
            ViiperLiveValidationSnapshot countersAfter =
                device.GetLiveValidationSnapshot(lease!);
            MediaWitnessSnapshot witnessAfter = witness.Snapshot();
            long speakerFrames = witnessAfter.Frames - witnessBefore.Frames;
            long speakerBytes = witnessAfter.Bytes - witnessBefore.Bytes;
            long speakerNonZero = witnessAfter.NonZeroBytes -
                witnessBefore.NonZeroBytes;
            long microphoneFrames = countersAfter
                .ValidationMicrophoneFramesSubmitted - countersBefore
                .ValidationMicrophoneFramesSubmitted;
            long microphoneBytes = countersAfter
                .ValidationMicrophoneBytesSubmitted - countersBefore
                .ValidationMicrophoneBytesSubmitted;
            long bytesPerSecond = spec.VirtualType ==
                ViiperVirtualDeviceType.DualShock4 ? 32000L * 2 * 2 :
                48000L * 2 * 2;
            bool passed = speakerFrames > 0 &&
                speakerBytes >= bytesPerSecond * durationSeconds * 9 / 10 &&
                speakerNonZero >= speakerBytes / 4 &&
                microphoneFrames >= durationSeconds * 90L &&
                microphoneBytes == microphoneFrames *
                    spec.MicrophonePcmLength;
            if (!passed)
            {
                throw new IOException(
                    $"{spec.Name} internal media witness failed: speakerFrames={speakerFrames} speakerBytes={speakerBytes} speakerNonZero={speakerNonZero} microphoneFrames={microphoneFrames} microphoneBytes={microphoneBytes}.");
            }
            return new MediaEvidence
            {
                DurationSeconds = durationSeconds,
                ProbeOutput = EvidenceLimits.Truncate(probe.StandardOutput,
                    8192),
                ProbeMetrics = ProbeRunner.ParseMetrics(probe.StandardOutput),
                SpeakerFramesObserved = speakerFrames,
                SpeakerBytesObserved = speakerBytes,
                SpeakerNonZeroBytesObserved = speakerNonZero,
                MicrophoneFramesSubmitted = microphoneFrames,
                MicrophoneBytesSubmitted = microphoneBytes,
                Passed = true,
            };
        }
        catch
        {
            probeStop.Cancel();
            try
            {
                await probeTask.ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
    }

    private async Task<ReconnectEvidence> VerifyReconnectAsync(
        ControllerSpec spec, ViiperOutDevice device, string inputBaseline,
        string mediaBaseline, MediaWitness mediaWitness)
    {
        ViiperLiveValidationSnapshot before =
            device.GetLiveValidationSnapshot(lease!);
        DeviceReceiptEvidence beforeReceipt = MapDevice(
            before.DeviceIdentity!);
        device.InterruptLiveValidationTransport(lease!);
        var state = ViiperStatePacketBuilder.CreateNeutralState();
        for (int index = 0; index < 4; index++)
        {
            state.Cross = (index & 1) != 0;
            device.ConvertandSendReport(state, 0);
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        ViiperLiveValidationSnapshot after = await WaitForSnapshotAsync(device,
            snapshot => snapshot.Connected &&
                snapshot.DeviceIdentity?.StreamGeneration >
                    before.DeviceIdentity?.StreamGeneration &&
                snapshot.ValidationStreamRecoveriesCompleted ==
                    before.ValidationStreamRecoveriesCompleted + 1,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        ValidateConnectedReceipt(spec, after);
        DeviceReceiptEvidence afterReceipt = MapDevice(after.DeviceIdentity!);
        bool lifetimePreserved = SameLogicalLifetime(beforeReceipt,
            afterReceipt) && afterReceipt.StreamGeneration >
            beforeReceipt.StreamGeneration;
        bool oneRecoveryCompleted =
            after.ValidationStreamRecoveriesCompleted ==
            before.ValidationStreamRecoveriesCompleted + 1;
        if (!lifetimePreserved || !oneRecoveryCompleted)
        {
            throw new ViiperIdentityException(
                $"{spec.Name} reconnect did not preserve the exact native device lifetime receipt through one production recovery.");
        }

        InputEvidence postInput = await ProbeRunner.MeasureInputAsync(
            bindings!.InputProbe, inputBaseline, spec,
            LiveValidationOptions.MinimumSamples, device, cancellationToken)
            .ConfigureAwait(false);
        MediaEvidence postMedia = await ExerciseMediaAsync(spec, device,
            mediaBaseline, mediaWitness,
            Math.Min(3, options.MediaSeconds)).ConfigureAwait(false);
        return new ReconnectEvidence
        {
            Before = beforeReceipt,
            After = afterReceipt,
            PostReconnectInput = postInput,
            PostReconnectMedia = postMedia,
            RecoveryAttemptsAtCompletion = after.StreamRecoveryAttempts,
            RecoveriesCompletedAtCompletion =
                after.ValidationStreamRecoveriesCompleted,
            ExactLifetimePreserved = true,
            Passed = postInput.Summary.Passed && postMedia.Passed,
        };
    }

    private async Task<ViiperLiveValidationSnapshot> WaitForSnapshotAsync(
        ViiperOutDevice device,
        Func<ViiperLiveValidationSnapshot, bool> predicate,
        TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() +
            (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        ViiperLiveValidationSnapshot last;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = device.GetLiveValidationSnapshot(lease!);
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        } while (Stopwatch.GetTimestamp() < deadline);
        throw new TimeoutException(
            "The production VIIPER handler did not reach the required bounded state.");
    }

    private async Task<bool> WaitForBaselineAsync(
        ImmutableProbeExecutable probe,
        string baseline, string after)
    {
        byte[] expected = await File.ReadAllBytesAsync(baseline,
            cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            await ProbeRunner.RunAsync(probe, new[] { "snapshot", after },
                TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            byte[] actual = await File.ReadAllBytesAsync(after,
                cancellationToken).ConfigureAwait(false);
            if (actual.AsSpan().SequenceEqual(expected))
            {
                return true;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    internal static byte[] ExpectedFeedback(ControllerSpec spec)
    {
        if (spec.VirtualType == ViiperVirtualDeviceType.DualShock4)
        {
            return new byte[]
            {
                0x23, 0xA7, 0x11, 0x52, 0xC3, 0x04, 0x09,
            };
        }
        byte[] expected = new byte[ViiperOutDevice
            .DualSenseAtomicFeedbackLength];
        expected[0] = 0x22;
        expected[1] = 0x88;
        expected[2] = 0x11;
        expected[3] = 0x52;
        expected[4] = 0xC3;
        expected[5] = 0x24;
        expected[6] = 0x21;
        expected[7] = 0xFC;
        expected[8] = 0x03;
        expected[15] = 0x44;
        expected[17] = 0x25;
        expected[18] = 0x40;
        expected[19] = 0x05;
        expected[26] = 0x55;
        int raw = 28;
        expected[raw] = 0x02;
        expected[raw + 1] = 0x0F;
        expected[raw + 2] = 0x14;
        expected[raw + 3] = 0x22;
        expected[raw + 4] = 0x88;
        expected[raw + 11] = 0x21;
        expected[raw + 12] = 0xFC;
        expected[raw + 13] = 0x03;
        expected[raw + 20] = 0x44;
        expected[raw + 22] = 0x25;
        expected[raw + 23] = 0x40;
        expected[raw + 24] = 0x05;
        expected[raw + 31] = 0x55;
        expected[raw + 44] = 0x24;
        expected[raw + 45] = 0x11;
        expected[raw + 46] = 0x52;
        expected[raw + 47] = 0xC3;
        return expected;
    }

    private static byte[] BuildMicrophonePcm(ControllerSpec spec)
    {
        byte[] result = new byte[spec.MicrophonePcmLength];
        int channels = spec.VirtualType == ViiperVirtualDeviceType.DualShock4 ?
            1 : 2;
        int frames = result.Length / (sizeof(short) * channels);
        for (int frame = 0; frame < frames; frame++)
        {
            short sample = (short)(4096 + frame % 127);
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = (frame * channels + channel) * sizeof(short);
                result[offset] = (byte)sample;
                result[offset + 1] = (byte)(sample >> 8);
            }
        }
        return result;
    }

    private static byte[] CopyRange(byte[] source, int offset, int length)
    {
        if (source == null || offset < 0 || length <= 0 ||
            offset > source.Length - length)
        {
            throw new IOException(
                "The atomic audio/haptics callback returned an invalid feedback range.");
        }
        byte[] result = new byte[length];
        Buffer.BlockCopy(source, offset, result, 0, length);
        return result;
    }

    private static void ValidateConnectedReceipt(ControllerSpec spec,
        ViiperLiveValidationSnapshot snapshot)
    {
        ViiperNativeBackendIdentity backend = snapshot.BackendIdentity ??
            throw new ViiperIdentityException(
                "The production handler omitted its admitted ping receipt.");
        ViiperVirtualDeviceIdentity identity = snapshot.DeviceIdentity ??
            throw new ViiperIdentityException(
                "The production handler omitted its device receipt.");
        if (!snapshot.Connected || !snapshot.SupportsMicrophone ||
            !snapshot.SupportsDirectSpeaker ||
            snapshot.StreamFrameVersion != spec.StreamVersion ||
            !string.Equals(snapshot.HandlerName, spec.Handler,
                StringComparison.Ordinal) ||
            !string.Equals(snapshot.StreamProtocol, spec.StreamProtocol,
                StringComparison.Ordinal) ||
            !string.Equals(backend.Transport, "native-ude",
                StringComparison.Ordinal) ||
            identity.TransportMode != ViiperTransportMode.NativeUde ||
            !string.Equals(identity.DeviceType, spec.Handler,
                StringComparison.Ordinal) ||
            !string.Equals(identity.Vid, spec.Vid,
                StringComparison.Ordinal) ||
            !string.Equals(identity.Pid, spec.Pid,
                StringComparison.Ordinal) ||
            identity.LegacyUsbipPort != -1 ||
            identity.LegacyUsbipOwnerSerial != null ||
            identity.NativePnpAnchor?.IsExact != true ||
            backend.ControllerSessionId !=
                identity.NativePnpAnchor.ControllerSessionId ||
            !string.Equals(backend.ControllerInstanceId,
                identity.NativePnpAnchor.ControllerInstanceId,
                StringComparison.Ordinal))
        {
            throw new ViiperIdentityException(
                $"{spec.Name} did not connect through its exact production native HID/audio handler receipt.");
        }
        if ((spec.VirtualType is ViiperVirtualDeviceType.DualSense or
                ViiperVirtualDeviceType.DualSenseEdge) !=
            snapshot.SupportsAtomicAudioHaptics)
        {
            throw new ViiperIdentityException(
                $"{spec.Name} reported the wrong atomic audio/haptics capability.");
        }
    }

    private static PingReceiptEvidence MapPing(
        ViiperNativeBackendIdentity identity) => new()
        {
            Server = identity.Server,
            Version = identity.Version,
            Transport = identity.Transport,
            AbiMajor = identity.AbiMajor,
            AbiMinor = identity.AbiMinor,
            Capabilities = identity.Capabilities,
            DriverPackageVersion = identity.DriverPackageVersion,
            DriverBuildIdentity = identity.DriverBuildIdentity,
            ControllerInstanceId = identity.ControllerInstanceId,
            ControllerSessionId = identity.ControllerSessionId.ToString(
            CultureInfo.InvariantCulture),
        };

    private static DeviceReceiptEvidence MapDevice(
        ViiperVirtualDeviceIdentity identity)
    {
        ViiperNativePnpAnchor anchor = identity.NativePnpAnchor ??
            throw new ViiperIdentityException(
                "The native device receipt omitted its PnP anchor.");
        return new DeviceReceiptEvidence
        {
            Transport = "native-ude",
            BusId = identity.BusId,
            DevId = identity.DevId,
            DeviceType = identity.DeviceType,
            Vid = identity.Vid,
            Pid = identity.Pid,
            DeviceSerialNumber = identity.DeviceSerialNumber,
            BrokerBuildIdentity = identity.BrokerBuildIdentity,
            LogicalLifetimeId = identity.LogicalLifetimeId,
            StreamGeneration = identity.StreamGeneration,
            NativeDeviceId = anchor.NativeDeviceId.ToString(
                CultureInfo.InvariantCulture),
            NativeDeviceGeneration = anchor.NativeDeviceGeneration,
            ControllerSessionId = anchor.ControllerSessionId.ToString(
                CultureInfo.InvariantCulture),
            ControllerInstanceId = anchor.ControllerInstanceId,
            Usb20PortNumber = anchor.Usb20PortNumber,
            Usb30PortNumber = anchor.Usb30PortNumber,
        };
    }

    private static CounterEvidence MapCounters(
        ViiperLiveValidationSnapshot snapshot) => new()
        {
            StatePacketsSubmitted = snapshot.StatePacketsSubmitted,
            StatePacketsWritten = snapshot.StatePacketsWritten,
            StatePacketsCoalesced = snapshot.StatePacketsCoalesced,
            FeedbackFramesObserved = snapshot.FeedbackFramesObserved,
            SpeakerFramesEnqueued = snapshot.SpeakerFramesEnqueued,
            SpeakerFramesDequeued = snapshot.SpeakerFramesDequeued,
            SpeakerFramesDropped = snapshot.SpeakerFramesDropped,
            SpeakerFramesExpired = snapshot.SpeakerFramesExpired,
            SpeakerFramesDelivered = snapshot.SpeakerFramesDelivered,
            SpeakerFramesStale = snapshot.SpeakerFramesStale,
            SpeakerNoSubscriberDeferrals =
            snapshot.SpeakerNoSubscriberDeferrals,
            SpeakerCallbackFailures = snapshot.SpeakerCallbackFailures,
            ControlFramesEnqueued = snapshot.ControlFramesEnqueued,
            ControlFramesDequeued = snapshot.ControlFramesDequeued,
            ControlFramesDropped = snapshot.ControlFramesDropped,
            OrderedControlFramesEnqueued =
            snapshot.OrderedControlFramesEnqueued,
            OrderedControlFramesDequeued =
            snapshot.OrderedControlFramesDequeued,
            OrderedControlFramesDropped =
            snapshot.OrderedControlFramesDropped,
            OrderedControlFramesExpired =
            snapshot.OrderedControlFramesExpired,
            ControlFramesDelivered = snapshot.ControlFramesDelivered,
            ControlFramesStale = snapshot.ControlFramesStale,
            ControlCallbackFailures = snapshot.ControlCallbackFailures,
            ValidationMicrophoneFramesSubmitted =
            snapshot.ValidationMicrophoneFramesSubmitted,
            ValidationMicrophoneBytesSubmitted =
            snapshot.ValidationMicrophoneBytesSubmitted,
            ValidationTransportInterruptions =
            snapshot.ValidationTransportInterruptions,
            ValidationStreamRecoveriesCompleted =
            snapshot.ValidationStreamRecoveriesCompleted,
        };

    private static void ValidateCounters(ControllerSpec spec,
        CounterEvidence counters)
    {
        if (counters.StatePacketsSubmitted <= 0 ||
            counters.StatePacketsWritten <= 0 ||
            counters.FeedbackFramesObserved <= 0 ||
            counters.SpeakerFramesEnqueued <= 0 ||
            counters.SpeakerFramesDequeued <= 0 ||
            counters.SpeakerFramesDelivered <= 0 ||
            counters.ValidationMicrophoneFramesSubmitted <= 0 ||
            counters.ValidationMicrophoneBytesSubmitted <= 0 ||
            counters.ValidationTransportInterruptions != 1 ||
            counters.ValidationStreamRecoveriesCompleted != 1 ||
            counters.SpeakerFramesDropped != 0 ||
            counters.SpeakerFramesExpired != 0 ||
            counters.SpeakerFramesStale != 0 ||
            counters.SpeakerNoSubscriberDeferrals != 0 ||
            counters.SpeakerCallbackFailures != 0 ||
            counters.ControlFramesDropped != 0 ||
            counters.OrderedControlFramesDropped != 0 ||
            counters.OrderedControlFramesExpired != 0 ||
            counters.ControlFramesStale != 0 ||
            counters.ControlCallbackFailures != 0)
        {
            throw new IOException(
                $"{spec.Name} production handler counters contain missing traffic, loss, expiry, staleness, or callback failure.");
        }
    }

    private static bool SameLogicalLifetime(DeviceReceiptEvidence left,
        DeviceReceiptEvidence right)
    {
        return left.Transport == right.Transport &&
            left.BusId == right.BusId && left.DevId == right.DevId &&
            left.DeviceType == right.DeviceType && left.Vid == right.Vid &&
            left.Pid == right.Pid &&
            left.DeviceSerialNumber == right.DeviceSerialNumber &&
            left.BrokerBuildIdentity == right.BrokerBuildIdentity &&
            left.LogicalLifetimeId == right.LogicalLifetimeId &&
            left.NativeDeviceId == right.NativeDeviceId &&
            left.NativeDeviceGeneration == right.NativeDeviceGeneration &&
            left.ControllerSessionId == right.ControllerSessionId &&
            left.ControllerInstanceId == right.ControllerInstanceId &&
            left.Usb20PortNumber == right.Usb20PortNumber &&
            left.Usb30PortNumber == right.Usb30PortNumber;
    }

    private void AddControllerFailure(ControllerEvidence controller,
        Exception error)
    {
        if (controller.Failures.Count >= EvidenceLimits.MaximumFailures)
        {
            return;
        }
        controller.Failures.Add(new FailureEvidence
        {
            Stage = EvidenceLimits.Truncate(evidence.CurrentStage, 256),
            Type = EvidenceLimits.Truncate(error.GetType().FullName ??
                error.GetType().Name, 256),
            Message = EvidenceLimits.Truncate(error.Message, 4096),
            Detail = EvidenceLimits.Truncate(error.ToString(), 16384),
        });
    }

    private static void TryDeleteOwnedTemporaryDirectory(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith(
                    "DS4Windows.ViiperLiveValidation-",
                    StringComparison.Ordinal) && Directory.Exists(full) &&
                (File.GetAttributes(full) &
                    FileAttributes.ReparsePoint) == 0)
            {
                Directory.Delete(full, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class MediaWitness
    {
        private long frames;
        private long bytes;
        private long nonZeroBytes;

        internal void Observe(byte[] payload, int offset, int length)
        {
            if (payload == null || offset < 0 || length <= 0 ||
                offset > payload.Length - length)
            {
                throw new IOException(
                    "The production speaker callback returned an invalid PCM range.");
            }
            long nonZero = 0;
            for (int index = offset; index < offset + length; index++)
            {
                if (payload[index] != 0)
                {
                    nonZero++;
                }
            }
            Interlocked.Increment(ref frames);
            Interlocked.Add(ref bytes, length);
            Interlocked.Add(ref nonZeroBytes, nonZero);
        }

        internal MediaWitnessSnapshot Snapshot() => new(
            Interlocked.Read(ref frames), Interlocked.Read(ref bytes),
            Interlocked.Read(ref nonZeroBytes));
    }

    private readonly record struct MediaWitnessSnapshot(long Frames,
        long Bytes, long NonZeroBytes);
}
