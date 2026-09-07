using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels;

public sealed record JoyConLinkActionView
{
    internal Switch2JoyConPairCandidate Candidate { get; init; }
    internal InputControllerSlotToken JoinedToken { get; init; }
    public bool Visible { get; init; }
    public bool Enabled { get; init; }
    public bool IsArmed { get; init; }
    public string Text { get; init; } = "Link";
    public string ToolTip { get; init; }
}
