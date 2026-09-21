using System;
using System.IO;
using System.Threading;
using DS4Windows.DS4Control;

namespace DS4Windows;

/// <summary>
/// Application boundary for queued regular and temporary profile loads. File
/// preparation and writer/KBM contention happen while the old mapping still
/// reports. Only the prepared mapping commit needs a publication pause.
/// </summary>
internal static class GuardedProfileReload
{
    internal static bool Execute(int slot, string name, bool temporary,
        bool launchProgram, ControlService service, DS4Device source,
        long revision, Func<bool> loadGuard, out bool accepted,
        ControllerProfileActionTarget? submittedTarget = null)
    {
        accepted = false;
        if ((uint)slot >= Global.MAX_DS4_CONTROLLER_COUNT || service == null ||
            !Global.IsCurrentProfileSwitchRevision(slot, revision))
            return false;

        bool MatchesSource() => service.DS4Controllers != null &&
            (uint)slot < service.DS4Controllers.Length &&
            ReferenceEquals(service.DS4Controllers[slot], source) &&
            (source == null || !source.IsRemoving);

        if (!MatchesSource())
            return false;
        ControllerProfileActionTarget target = default;
        if (source != null)
        {
            if (submittedTarget.HasValue)
            {
                target = submittedTarget.Value;
                // A queued request never acquires the authority of a later
                // registration, even if the same source object was reused.
                if (!ReferenceEquals(target.Source, source) || !target.IsCurrent)
                    return false;
            }
            else if (!service.TryCaptureProfileActionTarget(slot, source, out target))
                return false;
        }

        string path = Path.Combine(Global.appdatapath, "Profiles", $"{name}.xml");
        bool preparedSuccessfully = PreparedProfileLoad.TryPrepare(path, slot,
            out var prepared, out _, out string error);

        bool IsCurrent() => Global.IsCurrentProfileSwitchRevision(slot, revision) &&
            MatchesSource() && (source == null || target.IsCurrent) &&
            (loadGuard == null || loadGuard());
        bool applied = false;
        long admissionDeadline = Environment.TickCount64 + 500;
        while (true)
        {
            bool retryAdmission = false;
            using (ProfileMutationGate.Enter(slot))
            {
                if (!IsCurrent())
                    return false;
                accepted = true;
                if (!preparedSuccessfully)
                {
                    // A malformed/deleted destination must not erase a working
                    // live mapping or unplug its output. Startup has its own fallback.
                    AppLogger.LogToGui($"Failed to prepare profile {path}. {error}", false);
                    return false;
                }

                service.RunWithStableProfileKbmMapping(() =>
                {
                    if (!IsCurrent())
                        return;
                    if (source == null)
                    {
                        Apply(); // An unconnected editor slot has no report publisher.
                        return;
                    }
                    bool paused = source.TryHaltReportingRunAction(() =>
                    {
                        if (!target.TryAcquire(out var lease, out var failure))
                        {
                            retryAdmission = failure is InputControllerSlotTableFailure.Busy or
                                InputControllerSlotTableFailure.TimedOut;
                            return;
                        }
                        using (lease)
                        {
                            if (IsCurrent())
                                Apply();
                        }
                    });
                    if (!paused) retryAdmission = true;
                });

                // Never enumerate processes, migrate files, or enqueue output
                // work under the publication pause/action lease/KBM boundary.
                if (applied)
                {
                    Global.store.CompletePreparedProfileLoad(prepared, launchProgram);
                    return true;
                }
            }

            if (!retryAdmission || !Global.IsCurrentProfileSwitchRevision(slot, revision) ||
                !MatchesSource())
                return false;
            if (Environment.TickCount64 >= admissionDeadline)
            {
                AppLogger.LogToGui($"Profile switch admission remained busy for controller {slot + 1}; the current profile was preserved.", false);
                return false;
            }
            // Only a contended admission retries. No fixed activation delay,
            // no reparse, and no lock/report pause is held during this backoff.
            Thread.Sleep(1);
        }

        void Apply()
        {
            applied = Global.store.ApplyPreparedProfileNew(prepared, launchProgram,
                service, out _, transitionRevision: revision,
                completeColdSideEffects: false, actionTarget: source == null ? null : target);
            if (!applied)
                return;
            if (!temporary)
                Global.ProfilePath[slot] = name;
            Global.tempprofilename[slot] = temporary ? name : string.Empty;
            Global.tempprofileDistance[slot] = temporary &&
                name.Contains("distance", StringComparison.OrdinalIgnoreCase);
            Global.useTempProfile[slot] = temporary;
        }
    }
}
