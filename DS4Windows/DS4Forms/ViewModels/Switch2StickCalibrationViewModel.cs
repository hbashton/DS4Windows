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
        SideLabels = Array.ConvertAll(sides, side => side == Switch2StickSide.Left ? Properties.Resources.Switch2CalibLeftStick : Properties.Resources.Switch2CalibRightStick);
        ControllerLabel = runtime.DeviceType switch
        {
            InputDeviceType.Switch2Pro => Properties.Resources.Switch2CalibProControllerName,
            InputDeviceType.Switch2JoyConJoined => Properties.Resources.Switch2CalibJoinedJoyCon,
            InputDeviceType.Switch2JoyConLeft => Properties.Resources.Switch2CalibJoyConLeft,
            _ => Properties.Resources.Switch2CalibJoyConRight,
        } + (runtime.Transport == Switch2Transport.Usb ? Properties.Resources.Switch2CalibUsbSuffix : Properties.Resources.Switch2CalibBluetoothSuffix);
        Heading = Properties.Resources.Switch2CalibChoosePhysicalStick;
        Instructions = Properties.Resources.Switch2CalibInitialInstructions;
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
        Properties.Resources.Switch2CalibPcActive : Properties.Resources.Switch2CalibUsingOriginal;
    public string Heading { get; private set; }
    public string Instructions { get; private set; }
    public string ResultText { get; private set; } = string.Empty;
    public double Progress { get; private set; }
    public string ProgressLabel { get; private set; } = Properties.Resources.Switch2CalibNotStarted;
    public bool IsBusy => busy;
    public bool CanStart => !closed && !busy && operation == null && ContextIsCurrent;
    public bool CanChooseSide => CanStart;
    public bool CanSave => !closed && !busy && operation != null &&
        stage == Switch2RawStickCalibrationStage.Ready && ContextIsCurrent;
    public bool CanCancel => !closed && operation != null;
    public string ResetConfirmation => string.Format(Properties.Resources.Switch2CalibResetConfirmFormat, SelectedSideLabel.ToLowerInvariant());

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
        Heading = reset ? Properties.Resources.Switch2CalibPreparingReset : Properties.Resources.Switch2CalibPreparingCalibration;
        Instructions = Properties.Resources.Switch2CalibReleasingInput;
        Progress = 0;
        ProgressLabel = Properties.Resources.Switch2CalibPleaseWait;
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
                Heading = Properties.Resources.Switch2CalibCouldNotStart;
                Instructions = Properties.Resources.Switch2CalibCouldNotStartDetail;
                ProgressLabel = Properties.Resources.Switch2CalibNotStarted;
                return false;
            }
            operation = started;
            UpdateProgress();
            return operation != null;
        }
        catch
        {
            if (started != null) runtime.CancelRawStickCalibration(started);
            if (!closed) ResultText = Properties.Resources.Switch2CalibCouldNotStartError;
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
        Heading = saving.Reset ? Properties.Resources.Switch2CalibResettingPc : Properties.Resources.Switch2CalibSavingPc;
        Instructions = Properties.Resources.Switch2CalibInputReleasedWhileSaving;
        ResultText = string.Empty;
        ProgressLabel = Properties.Resources.Switch2CalibPleaseWait;
        RaiseAll();
        try
        {
            var result = await runtime.CompleteRawStickCalibrationAsync(saving);
            if (closed) return;
            bool stillOwnsReceipt = ReferenceEquals(operation, saving);
            if (result == Switch2RawStickCalibrationCommitResult.AppliedAndStored)
            {
                operation = null;
                Heading = saving.Reset ? Properties.Resources.Switch2CalibResetDone : Properties.Resources.Switch2CalibSaveDone;
                Instructions = Properties.Resources.Switch2CalibCanCalibrateAnother;
                ResultText = saving.Reset ? string.Format(Properties.Resources.Switch2CalibResetResultFormat, side) :
                    string.Format(Properties.Resources.Switch2CalibSaveResultFormat, side);
                Progress = 100;
                ProgressLabel = Properties.Resources.Switch2CalibComplete;
            }
            else if (result == Switch2RawStickCalibrationCommitResult.StorageFailed && stillOwnsReceipt)
            {
                ResultText = Properties.Resources.Switch2CalibStorageFailed;
                UpdateProgress();
            }
            else if ((result is Switch2RawStickCalibrationCommitResult.NotReady or Switch2RawStickCalibrationCommitResult.Busy) && stillOwnsReceipt)
            {
                ResultText = Properties.Resources.Switch2CalibNotReady;
                UpdateProgress();
            }
            else
            {
                runtime.CancelRawStickCalibration(saving);
                operation = null;
                Heading = Properties.Resources.Switch2CalibEnded;
                Instructions = Properties.Resources.Switch2CalibReopenBeforeRetry;
                ProgressLabel = Properties.Resources.Switch2CalibNotApplied;
                ResultText = result == Switch2RawStickCalibrationCommitResult.StoredNotApplied ?
                    Properties.Resources.Switch2CalibStoredNotApplied :
                    Properties.Resources.Switch2CalibNoLongerCurrent;
            }
        }
        catch
        {
            runtime.CancelRawStickCalibration(saving);
            operation = null;
            if (!closed) ResultText = Properties.Resources.Switch2CalibFinishFailed;
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
        Heading = Properties.Resources.Switch2CalibCancelled;
        Instructions = Properties.Resources.Switch2CalibResumeInput;
        ResultText = busy ? Properties.Resources.Switch2CalibSaveInProgress :
            Properties.Resources.Switch2CalibDiscarded;
        ProgressLabel = Properties.Resources.Switch2CalibCancelledStatus;
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
            Heading = Properties.Resources.Switch2CalibEnded;
            Instructions = Properties.Resources.Switch2CalibChangedRetry;
            ProgressLabel = Properties.Resources.Switch2CalibNotSaved;
            return;
        }
        stage = progress.Stage;
        Progress = (stage == Switch2RawStickCalibrationStage.Rotate ? progress.RotationProgress : progress.StationaryProgress) * 100;
        (Heading, Instructions, ProgressLabel) = stage switch
        {
            Switch2RawStickCalibrationStage.Rotate => (string.Format(Properties.Resources.Switch2CalibRotateHeadingFormat, SelectedSideLabel.ToLowerInvariant()),
                Properties.Resources.Switch2CalibRotateInstructions,
                string.Format(Properties.Resources.Switch2CalibMovingSecondsFormat, Math.Max(0, Math.Ceiling(10 * (1 - progress.RotationProgress))).ToString("0"))),
            Switch2RawStickCalibrationStage.Settle => (Properties.Resources.Switch2CalibSettleHeading, Properties.Resources.Switch2CalibSettleInstructions, Properties.Resources.Switch2CalibWaitingRest),
            Switch2RawStickCalibrationStage.Center => (Properties.Resources.Switch2CalibCenterHeading, Properties.Resources.Switch2CalibCenterInstructions,
                string.Format(Properties.Resources.Switch2CalibStillSecondsFormat, Math.Max(0, Math.Ceiling(3 * (1 - progress.StationaryProgress))).ToString("0"))),
            Switch2RawStickCalibrationStage.Ready => (progress.Reset ? Properties.Resources.Switch2CalibReadyToReset : Properties.Resources.Switch2CalibReadyToSave, Properties.Resources.Switch2CalibChooseSaveOrCancel, Properties.Resources.Switch2CalibReady),
            Switch2RawStickCalibrationStage.InsufficientTravel => (Properties.Resources.Switch2CalibMoreTravelNeeded, Properties.Resources.Switch2CalibReachFullEdge, Properties.Resources.Switch2CalibRejected),
            _ => (Properties.Resources.Switch2CalibCancelled, Properties.Resources.Switch2CalibStartNewWhenReady, Properties.Resources.Switch2CalibCancelledStatus),
        };
    }

    private void EndContext()
    {
        contextEnded = true;
        pendingBegin?.Cancel();
        if (operation != null) runtime.CancelRawStickCalibration(operation);
        operation = null;
        Heading = Properties.Resources.Switch2CalibContextChanged;
        Instructions = Properties.Resources.Switch2CalibContextChangedDetail;
        ProgressLabel = Properties.Resources.Switch2CalibStopped;
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
