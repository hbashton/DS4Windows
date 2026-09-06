/*
DS4Windows
Copyright (C) 2026 hbashton
GPL-3.0-or-later; see LICENSE. The physical rotate/settle/center workflow
follows the source-pinned Switch2Connect reference documented in
docs/protocols/switch2-raw-stick-calibration.md. No new mapping path lives here.
*/
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels;

/// <summary>
/// UI-thread-owned workflow for one exact logical runtime and profile context.
/// Timed UI refresh only reads progress; physical reports own all sampling.
/// Begin and persistence run off the UI thread. Closing revokes the receipt;
/// a completion must never update a closed window or switch to a successor.
/// </summary>
public sealed class Switch2StickCalibrationViewModel : INotifyPropertyChanged
{
    private readonly Switch2RuntimeInputDevice runtime;
    private readonly Switch2StickSide[] sides;
    private readonly int slot;
    private readonly long profileRevision;
    private Switch2RawStickCalibrationOperation operation;
    private Switch2RawStickCalibrationStage stage;
    private int selectedSideIndex;
    private bool busy, closed, contextEnded;
    private CancellationTokenSource pendingBegin;

    internal Switch2StickCalibrationViewModel(Switch2RuntimeInputDevice runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        slot = runtime.DeviceSlotNumber;
        profileRevision = Math.Max(0, Global.ReadProfileSwitchRevision(slot));
        sides = runtime.DeviceType switch
        {
            InputDeviceType.Switch2Pro or InputDeviceType.Switch2JoyConJoined =>
                new[] { Switch2StickSide.Left, Switch2StickSide.Right },
            InputDeviceType.Switch2JoyConLeft => new[] { Switch2StickSide.Left },
            InputDeviceType.Switch2JoyConRight => new[] { Switch2StickSide.Right },
            _ => throw new ArgumentException("A Switch 2 controller is required.", nameof(runtime)),
        };
        SideLabels = Array.ConvertAll(sides, side => side == Switch2StickSide.Left ? "Left stick" : "Right stick");
        ControllerLabel = runtime.DeviceType switch
        {
            InputDeviceType.Switch2Pro => "Switch 2 Pro Controller",
            InputDeviceType.Switch2JoyConJoined => "Joined Joy-Con 2",
            InputDeviceType.Switch2JoyConLeft => "Joy-Con 2 (Left)",
            _ => "Joy-Con 2 (Right)",
        } + (runtime.Transport == Switch2Transport.Usb ? " · USB" : " · Bluetooth");
        Heading = "Choose a physical stick";
        Instructions = "Rotate the selected stick around its full edge, then let it rest in the center. Nothing is saved until you choose Save calibration.";
        Poll();
    }

    public IReadOnlyList<string> SideLabels { get; }
    public string ControllerLabel { get; }
    public int SelectedSideIndex
    {
        get => selectedSideIndex;
        set
        {
            if (!CanChooseSide || value < 0 || value >= sides.Length || value == selectedSideIndex) return;
            selectedSideIndex = value;
            ResultText = string.Empty;
            RaiseAll();
        }
    }
    public string SelectedSideLabel => SideLabels[selectedSideIndex];
    public string CalibrationStatus => (sides[selectedSideIndex] == Switch2StickSide.Left ?
        runtime.HasLocalLeftStickCalibration : runtime.HasLocalRightStickCalibration) ?
        "PC calibration is active for this stick." : "Using original controller calibration, or defaults if unavailable.";
    public string Heading { get; private set; }
    public string Instructions { get; private set; }
    public string ResultText { get; private set; } = string.Empty;
    public double Progress { get; private set; }
    public string ProgressLabel { get; private set; } = "Not started";
    public bool IsBusy => busy;
    public bool CanStart => !closed && !busy && operation == null && ContextIsCurrent;
    public bool CanChooseSide => CanStart;
    public bool CanSave => !closed && !busy && operation != null &&
        stage == Switch2RawStickCalibrationStage.Ready && ContextIsCurrent;
    public bool CanCancel => !closed && operation != null;
    public string ResetConfirmation => $"Remove the PC calibration for the {SelectedSideLabel.ToLowerInvariant()} on this controller?\n\nThe original factory calibration will not be changed. Other sticks and controllers are unaffected.";

    private bool ContextIsCurrent => !contextEnded &&
        runtime.RuntimeState == Switch2RuntimeInputDeviceState.Active &&
        runtime.DeviceSlotNumber == slot &&
        Math.Max(0, Global.ReadProfileSwitchRevision(slot)) == profileRevision;

    public async Task<bool> StartAsync() => await BeginAsync(reset: false);

    // Called only after the window obtains an explicit reset confirmation.
    public async Task ResetAsync()
    {
        if (await BeginAsync(reset: true)) await SaveAsync();
    }

    private async Task<bool> BeginAsync(bool reset)
    {
        if (!CanStart) { Poll(); return false; }
        var selected = sides[selectedSideIndex];
        busy = true;
        ResultText = string.Empty;
        Heading = reset ? "Preparing reset" : "Preparing calibration";
        Instructions = "Releasing this controller's mapped input…";
        Progress = 0;
        ProgressLabel = "Please wait";
        RaiseAll();
        Switch2RawStickCalibrationOperation started = null;
        var beginCancellation = new CancellationTokenSource();
        pendingBegin = beginCancellation;
        try
        {
            started = await Task.Run(() => runtime.TryBeginRawStickCalibration(selected, reset,
                out var receipt, beginCancellation.Token) ? receipt : null);
            if (closed || !ContextIsCurrent)
            {
                if (started != null) runtime.CancelRawStickCalibration(started);
                return false;
            }
            if (started == null)
            {
                Heading = "Calibration could not start";
                Instructions = "Wait for live controller input and finish any other calibration, then try again. A remembered controller identity and writable PC calibration store are required.";
                ProgressLabel = "Not started";
                return false;
            }
            operation = started;
            UpdateProgress();
            return operation != null;
        }
        catch
        {
            if (started != null) runtime.CancelRawStickCalibration(started);
            if (!closed) ResultText = "Calibration could not start. No calibration was saved.";
            return false;
        }
        finally
        {
            pendingBegin = null;
            beginCancellation.Dispose();
            busy = false;
            if (!closed) { if (!ContextIsCurrent) EndContext(); RaiseAll(); }
        }
    }

    public async Task SaveAsync()
    {
        if (!CanSave) { Poll(); return; }
        var saving = operation;
        string side = SelectedSideLabel;
        busy = true;
        Heading = saving.Reset ? "Resetting PC calibration" : "Saving PC calibration";
        Instructions = "Controller input remains released while the result is stored and applied.";
        ResultText = string.Empty;
        ProgressLabel = "Please wait";
        RaiseAll();
        try
        {
            var result = await runtime.CompleteRawStickCalibrationAsync(saving);
            if (closed) return;
            bool stillOwnsReceipt = ReferenceEquals(operation, saving);
            if (result == Switch2RawStickCalibrationCommitResult.AppliedAndStored)
            {
                operation = null;
                Heading = saving.Reset ? "PC calibration reset" : "Calibration saved";
                Instructions = "You can calibrate another stick or close this window.";
                ResultText = saving.Reset ? $"{side}: the PC override was removed. Source calibration is active again." :
                    $"{side}: saved on this PC and applied to this controller connection.";
                Progress = 100;
                ProgressLabel = "Complete";
            }
            else if (result == Switch2RawStickCalibrationCommitResult.StorageFailed && stillOwnsReceipt)
            {
                ResultText = "The PC calibration file could not be updated. The previous live calibration is unchanged. Retry Save calibration or cancel.";
                UpdateProgress();
            }
            else if ((result is Switch2RawStickCalibrationCommitResult.NotReady or Switch2RawStickCalibrationCommitResult.Busy) && stillOwnsReceipt)
            {
                ResultText = "The operation is not ready to finish. Wait for valid samples or cancel and retry.";
                UpdateProgress();
            }
            else
            {
                runtime.CancelRawStickCalibration(saving);
                operation = null;
                Heading = "Calibration ended";
                Instructions = "Close this window and reopen calibration from the current controller before trying again.";
                ProgressLabel = "Not applied";
                ResultText = result == Switch2RawStickCalibrationCommitResult.StoredNotApplied ?
                    "The PC file was updated, but the change was not applied to the active connection. Reconnect to load it, or explicitly reset it. Cancellation cannot undo a write already in progress." :
                    "This operation is no longer current. It did not complete a calibration change.";
            }
        }
        catch
        {
            runtime.CancelRawStickCalibration(saving);
            operation = null;
            if (!closed) ResultText = "Calibration could not finish. The file outcome could not be confirmed; reconnect and check the selected stick before retrying.";
        }
        finally
        {
            busy = false;
            if (!closed) { if (!ContextIsCurrent) EndContext(); RaiseAll(); }
        }
    }

    public void Cancel()
    {
        if (closed || operation == null) return;
        runtime.CancelRawStickCalibration(operation);
        operation = null;
        Heading = "Calibration cancelled";
        Instructions = "Ordinary controller input can resume. You can start again when ready.";
        ResultText = busy ? "A save already in progress may still update the PC file. Its final result will appear here." :
            "The unsaved capture was discarded; the existing calibration was not changed.";
        ProgressLabel = "Cancelled";
        Progress = 0;
        RaiseAll();
    }

    public void Poll()
    {
        if (closed) return;
        if (!ContextIsCurrent) EndContext();
        else if (!busy && operation != null) UpdateProgress();
        RaiseAll();
    }

    private void UpdateProgress()
    {
        if (!runtime.TryGetRawStickCalibrationProgress(operation, out var progress))
        {
            operation = null;
            Heading = "Calibration ended";
            Instructions = "The controller or calibration changed. Start again from the current controller.";
            ProgressLabel = "Not saved";
            return;
        }
        stage = progress.Stage;
        Progress = (stage == Switch2RawStickCalibrationStage.Rotate ? progress.RotationProgress : progress.StationaryProgress) * 100;
        (Heading, Instructions, ProgressLabel) = stage switch
        {
            Switch2RawStickCalibrationStage.Rotate => ($"1 · Rotate the {SelectedSideLabel.ToLowerInvariant()}",
                "Move slowly around the full outer edge in both directions. Keep moving until this step completes; pauses do not count.",
                $"{Math.Max(0, Math.Ceiling(10 * (1 - progress.RotationProgress))):0} moving seconds remaining"),
            Switch2RawStickCalibrationStage.Settle => ("2 · Release the stick", "Take your hand off the stick and let it return to center.", "Waiting for 2 seconds of rest"),
            Switch2RawStickCalibrationStage.Center => ("3 · Hold still", "Leave the stick untouched while its center is measured. Touching it restarts the rest period.",
                $"{Math.Max(0, Math.Ceiling(3 * (1 - progress.StationaryProgress))):0} still seconds remaining"),
            Switch2RawStickCalibrationStage.Ready => (progress.Reset ? "Ready to reset" : "Ready to save", "Choose Save calibration to apply this result, or Cancel to discard it.", "Ready"),
            Switch2RawStickCalibrationStage.InsufficientTravel => ("More stick travel is needed", "Cancel and try again. Reach the full edge in every direction, then let the stick center itself.", "Calibration rejected"),
            _ => ("Calibration cancelled", "Start a new calibration when the controller is ready.", "Cancelled"),
        };
    }

    private void EndContext()
    {
        contextEnded = true;
        pendingBegin?.Cancel();
        if (operation != null) runtime.CancelRawStickCalibration(operation);
        operation = null;
        Heading = "Controller context changed";
        Instructions = "The controller, profile or pair changed. Close this window and reopen calibration from the controller you want to use.";
        ProgressLabel = "Calibration stopped";
        // Preserve the explicit asynchronous disk outcome, if one exists.
    }

    public void Close()
    {
        if (closed) return;
        closed = true;
        pendingBegin?.Cancel();
        if (operation != null) runtime.CancelRawStickCalibration(operation);
        operation = null;
    }

    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    public event PropertyChangedEventHandler PropertyChanged;
}
