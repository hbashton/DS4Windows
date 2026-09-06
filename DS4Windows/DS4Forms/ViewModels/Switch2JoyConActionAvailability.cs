using System;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels;

internal readonly struct Switch2JoyConActionAvailability
{
    internal Switch2JoyConActionAvailability(bool serviceRunning, bool automatic,
        bool busy, int leftId, int rightId)
    {
        CanSelect = serviceRunning && !automatic && !busy;
        CanUseLeft = CanSelect && leftId > 0;
        CanUseRight = CanSelect && rightId > 0;
        CanJoin = CanUseLeft && CanUseRight && leftId != rightId;
    }

    internal bool CanSelect { get; }
    internal bool CanJoin { get; }
    internal bool CanUseLeft { get; }
    internal bool CanUseRight { get; }

    internal static int PreserveSelection(int previousId,
        ReadOnlySpan<Switch2JoyConPairCandidate> candidates,
        Switch2ControllerModel side)
    {
        if (previousId <= 0 || side is not (Switch2ControllerModel.JoyCon2Left or
                Switch2ControllerModel.JoyCon2Right)) return 0;
        foreach (Switch2JoyConPairCandidate candidate in candidates)
        {
            if (candidate.Model == side && candidate.Id == previousId)
                return previousId;
        }
        // An explicitly selected half which disappeared must not silently
        // become a different controller on this or any subsequent refresh.
        // Even a sole candidate requires an explicit selection for manual pairing.
        return 0;
    }
}
