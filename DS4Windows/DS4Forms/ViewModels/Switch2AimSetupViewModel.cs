using System;
using System.ComponentModel;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    // A friendly editor for the existing gyro fields, not a second aiming mode.
    public sealed class Switch2AimSetupViewModel : INotifyPropertyChanged
    {
        private readonly ProfileSettingsViewModel owner;
        private readonly int slot;
        private static readonly string[] Triggers = { "-1", "5", "7" };
        private static readonly string[] Labels = { "Always On", "L2", "R2" };

        internal Switch2AimSetupViewModel(ProfileSettingsViewModel owner, int slot)
        {
            this.owner = owner;
            this.slot = slot;
            owner.GyroOutModeIndexChanged += (_, _) => Refresh();
            owner.GyroMouseTrigDisplayChanged += (_, _) => Refresh();
            owner.GyroMouseStickTrigDisplayChanged += (_, _) => Refresh();
        }

        public int OutputIndex { get => owner.GyroOutModeIndex; set => owner.GyroOutModeIndex = value; }
        public bool IsMouse => Global.GyroOutputMode[slot] == GyroOutMode.Mouse;
        public bool CanChooseActivation => IsMouse || Global.GyroOutputMode[slot] == GyroOutMode.MouseJoystick;
        public string Description => Global.GyroOutputMode[slot] switch
        {
            GyroOutMode.Mouse => "Tilt to move the mouse. Use this for precise aim in games that accept mouse input alongside a controller.",
            GyroOutMode.MouseJoystick => "Tilt to move the virtual aiming stick. Use this when a game only accepts controller aiming.",
            GyroOutMode.DirectionalSwipe => "Quick tilts trigger actions you assign in Gyro settings. This does not move the mouse.",
            GyroOutMode.Passthru => "Let a compatible game or emulator use your motion directly. The game decides what it does.",
            _ => "Tilts use your button mappings. To move the mouse by tilting, choose Mouse pointer above.",
        };

        // Custom is a read-only summary; visiting this page never overwrites a
        // combination, toggle, or inverted activation made in the gyro editor.
        public int ActivationIndex
        {
            get
            {
                if (!CanChooseActivation) return 3;
                bool turns = IsMouse ? owner.GyroMouseTurns : owner.GyroMouseStickTurns;
                bool toggle = IsMouse ? owner.GyroMouseToggle : owner.GyroMouseStickToggle;
                string trigger = IsMouse ? Global.SATriggers[slot] : Global.SAMousestickTriggers[slot];
                int index = Array.IndexOf(Triggers, trigger);
                return turns && !toggle && index >= 0 ? index : 3;
            }
            set
            {
                if (!CanChooseActivation || value < 0 || value >= Triggers.Length ||
                    ActivationIndex == value) return;
                if (IsMouse)
                {
                    owner.GyroMouseTurns = true;
                    owner.GyroMouseToggle = false;
                    owner.GyroMouseEvalCondIndex = 0;
                    Global.SATriggers[slot] = Triggers[value];
                    owner.GyroMouseTrigDisplay = Labels[value];
                }
                else
                {
                    owner.GyroMouseStickTurns = true;
                    owner.GyroMouseStickToggle = false;
                    owner.GyroMouseStickEvalCondIndex = 0;
                    Global.SAMousestickTriggers[slot] = Triggers[value];
                    owner.GyroMouseStickTrigDisplay = Labels[value];
                }
                Refresh();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
