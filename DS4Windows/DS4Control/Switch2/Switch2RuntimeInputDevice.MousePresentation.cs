namespace DS4Windows.Switch2;

public sealed partial class Switch2RuntimeInputDevice : INintendoMousePresentation
{
    bool INintendoMousePresentation.TrySetHighRateMouseSource(Switch2ContinuousMouseSource source,
        bool active, double velocityX, double velocityY, long profileRevision) =>
        TrySetHighRateMouseSource(source, active, velocityX, velocityY, profileRevision);

    bool INintendoMousePresentation.TrySetHighRateMappingMouseSources(bool stickAssistActive,
        double stickAssistVelocityX, double stickAssistVelocityY, bool irActive,
        double irVelocityX, double irVelocityY, bool mappedStickActive,
        double mappedStickVelocityX, double mappedStickVelocityY, long profileRevision) =>
        TrySetHighRateMappingMouseSources(stickAssistActive, stickAssistVelocityX,
            stickAssistVelocityY, irActive, irVelocityX, irVelocityY, mappedStickActive,
            mappedStickVelocityX, mappedStickVelocityY, profileRevision);
}
