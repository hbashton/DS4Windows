/*
DS4Windows
Copyright (C) 2026  DS4Windows contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace DS4Windows
{
    public enum AudioHapticsSourceKind : byte
    {
        ControllerAudio,
        SystemAudio,
        AppSession,
    }

    public enum AudioHapticsMode : byte
    {
        Mix,
        Replace,
    }

    public enum AudioHapticsBassFocus : byte
    {
        Deep,
        Balanced,
        Punchy,
        Wide,
    }

    public enum AudioHapticsResponse : byte
    {
        Subtle,
        Balanced,
        Strong,
    }

    public enum AudioHapticsAttack : byte
    {
        Soft,
        Balanced,
        Fast,
        Sharp,
    }

    public enum AudioHapticsRelease : byte
    {
        Tight,
        Balanced,
        Smooth,
        Long,
    }

    /// <summary>
    /// Per-profile audio-to-advanced-haptics settings. Defaults and ranges are
    /// shared by the UI, persistence layer, and native DS4Windows runtime.
    /// </summary>
    public sealed class AudioHapticsProfileSettings
    {
        public const int MinimumGainPercent = 0;
        public const int MaximumGainPercent = 200;
        public const int DefaultGainPercent = 100;

        public bool Enabled { get; set; }
        public bool StreamAppAudioToController { get; set; }
        public bool StreamAppAudioToHeadsetOnly { get; set; }
        public bool AutomaticGameDetection { get; set; }
        public AudioHapticsSourceKind Source { get; set; } = AudioHapticsSourceKind.SystemAudio;
        public AudioHapticsMode Mode { get; set; } = AudioHapticsMode.Mix;
        public int GainPercent { get; set; } = DefaultGainPercent;
        public AudioHapticsBassFocus BassFocus { get; set; } = AudioHapticsBassFocus.Balanced;
        public AudioHapticsResponse Response { get; set; } = AudioHapticsResponse.Balanced;
        public AudioHapticsAttack Attack { get; set; } = AudioHapticsAttack.Balanced;
        public AudioHapticsRelease Release { get; set; } = AudioHapticsRelease.Balanced;

        // App-session identity is deliberately redundant: Windows can recycle a
        // PID, while the Core Audio session identifiers remain stable enough to
        // restore a user's selection after an application restarts.
        public int ProcessId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string ExecutableName { get; set; } = string.Empty;
        public string ProcessPath { get; set; } = string.Empty;
        public string SessionIdentifier { get; set; } = string.Empty;
        public string SessionInstanceIdentifier { get; set; } = string.Empty;

        public AudioHapticsProfileSettings Normalize()
        {
            if (!Enum.IsDefined(typeof(AudioHapticsSourceKind), Source)) Source = AudioHapticsSourceKind.SystemAudio;
            if (!Enum.IsDefined(typeof(AudioHapticsMode), Mode)) Mode = AudioHapticsMode.Mix;
            if (!Enum.IsDefined(typeof(AudioHapticsBassFocus), BassFocus)) BassFocus = AudioHapticsBassFocus.Balanced;
            if (!Enum.IsDefined(typeof(AudioHapticsResponse), Response)) Response = AudioHapticsResponse.Balanced;
            if (!Enum.IsDefined(typeof(AudioHapticsAttack), Attack)) Attack = AudioHapticsAttack.Balanced;
            if (!Enum.IsDefined(typeof(AudioHapticsRelease), Release)) Release = AudioHapticsRelease.Balanced;
            GainPercent = Math.Clamp(GainPercent, MinimumGainPercent, MaximumGainPercent);
            DisplayName = (DisplayName ?? string.Empty).Trim();
            ExecutableName = (ExecutableName ?? string.Empty).Trim();
            ProcessPath = (ProcessPath ?? string.Empty).Trim();
            SessionIdentifier = (SessionIdentifier ?? string.Empty).Trim();
            SessionInstanceIdentifier = (SessionInstanceIdentifier ?? string.Empty).Trim();
            ProcessId = Math.Max(0, ProcessId);
            if (AutomaticGameDetection)
            {
                Source = AudioHapticsSourceKind.AppSession;
            }
            if (Source != AudioHapticsSourceKind.AppSession)
            {
                StreamAppAudioToController = false;
            }
            if (!StreamAppAudioToController)
            {
                StreamAppAudioToHeadsetOnly = false;
            }
            return this;
        }

        public AudioHapticsProfileSettings Clone() => new AudioHapticsProfileSettings
        {
            Enabled = Enabled,
            StreamAppAudioToController = StreamAppAudioToController,
            StreamAppAudioToHeadsetOnly = StreamAppAudioToHeadsetOnly,
            AutomaticGameDetection = AutomaticGameDetection,
            Source = Source,
            Mode = Mode,
            GainPercent = GainPercent,
            BassFocus = BassFocus,
            Response = Response,
            Attack = Attack,
            Release = Release,
            ProcessId = ProcessId,
            DisplayName = DisplayName,
            ExecutableName = ExecutableName,
            ProcessPath = ProcessPath,
            SessionIdentifier = SessionIdentifier,
            SessionInstanceIdentifier = SessionInstanceIdentifier,
        }.Normalize();

        public bool IsDefaultConfiguration() =>
            !Enabled && !StreamAppAudioToController &&
            !StreamAppAudioToHeadsetOnly &&
            !AutomaticGameDetection &&
            Source == AudioHapticsSourceKind.SystemAudio &&
            Mode == AudioHapticsMode.Mix && GainPercent == DefaultGainPercent &&
            BassFocus == AudioHapticsBassFocus.Balanced &&
            Response == AudioHapticsResponse.Balanced &&
            Attack == AudioHapticsAttack.Balanced &&
            Release == AudioHapticsRelease.Balanced && ProcessId == 0 &&
            string.IsNullOrWhiteSpace(DisplayName) &&
            string.IsNullOrWhiteSpace(ExecutableName) &&
            string.IsNullOrWhiteSpace(ProcessPath) &&
            string.IsNullOrWhiteSpace(SessionIdentifier) &&
            string.IsNullOrWhiteSpace(SessionInstanceIdentifier);
    }

    public enum TriggerLabMode : byte
    {
        Feedback,
        Weapon,
        Vibration,
    }

    public sealed class TriggerLabEffectSettings
    {
        public const int SliderStep = 5;
        public const int DefaultStartPercent = 20;
        public const int DefaultWallPercent = 60;
        public const int DefaultForcePercent = 85;

        public string ProfileId { get; set; } = TriggerLabProfileSettings.DefaultProfileId;
        public TriggerLabMode Mode { get; set; } = TriggerLabMode.Weapon;
        public int StartPercent { get; set; } = DefaultStartPercent;
        public int WallPercent { get; set; } = DefaultWallPercent;
        public int ForcePercent { get; set; } = DefaultForcePercent;

        public TriggerLabEffectSettings Normalize()
        {
            ProfileId = string.IsNullOrWhiteSpace(ProfileId)
                ? TriggerLabProfileSettings.DefaultProfileId
                : ProfileId.Trim();
            if (!Enum.IsDefined(typeof(TriggerLabMode), Mode)) Mode = TriggerLabMode.Feedback;
            StartPercent = Snap(StartPercent);
            WallPercent = Snap(WallPercent);
            ForcePercent = Snap(ForcePercent);
            return this;
        }

        public TriggerLabEffectSettings Clone() => new TriggerLabEffectSettings
        {
            ProfileId = ProfileId,
            Mode = Mode,
            StartPercent = StartPercent,
            WallPercent = WallPercent,
            ForcePercent = ForcePercent,
        }.Normalize();

        private static int Snap(int value) =>
            Math.Clamp((int)Math.Round(value / (double)SliderStep) * SliderStep, 0, 100);
    }

    public sealed class TriggerLabPreset
    {
        public TriggerLabPreset(string id, string name, string description,
            TriggerLabMode mode, int startPercent, int wallPercent,
            int forcePercent)
        {
            Id = id;
            Name = name;
            Description = description;
            Mode = mode;
            StartPercent = startPercent;
            WallPercent = wallPercent;
            ForcePercent = forcePercent;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public TriggerLabMode Mode { get; }
        public int StartPercent { get; }
        public int WallPercent { get; }
        public int ForcePercent { get; }

        public TriggerLabEffectSettings CreateEffect() =>
            new TriggerLabEffectSettings
            {
                ProfileId = Id,
                Mode = Mode,
                StartPercent = StartPercent,
                WallPercent = WallPercent,
                ForcePercent = ForcePercent,
            }.Normalize();
    }

    public static class TriggerLabPresetCatalog
    {
        private static readonly IReadOnlyList<TriggerLabPreset> presets =
            Array.AsReadOnly(new[]
            {
                new TriggerLabPreset("default", "Balanced Weapon",
                    "A clean, medium travel weapon break.",
                    TriggerLabMode.Weapon, 20, 60, 85),
                new TriggerLabPreset("soft-resistance", "Soft Resistance",
                    "Gentle resistance through most of the trigger travel.",
                    TriggerLabMode.Feedback, 10, 50, 30),
                new TriggerLabPreset("firm-resistance", "Firm Resistance",
                    "A heavier continuous pull for brakes and throttles.",
                    TriggerLabMode.Feedback, 5, 50, 75),
                new TriggerLabPreset("hair-trigger", "Hair Trigger",
                    "A short pull with an early, light break.",
                    TriggerLabMode.Weapon, 5, 25, 65),
                new TriggerLabPreset("pistol-break", "Pistol Break",
                    "A defined mid-travel wall with a strong click.",
                    TriggerLabMode.Weapon, 20, 55, 95),
                new TriggerLabPreset("rifle-break", "Rifle Break",
                    "A later, heavier break suited to rifles.",
                    TriggerLabMode.Weapon, 35, 75, 100),
                new TriggerLabPreset("machine-gun", "Machine Gun",
                    "Fast, strong vibration after the initial take-up.",
                    TriggerLabMode.Vibration, 15, 80, 85),
                new TriggerLabPreset("road-texture", "Road Texture",
                    "A lower-frequency vibration for terrain and engines.",
                    TriggerLabMode.Vibration, 5, 35, 45),
            });

        public static IReadOnlyList<TriggerLabPreset> Presets => presets;

        public static bool IsBuiltIn(string id) =>
            presets.Any(preset => string.Equals(preset.Id, id,
                StringComparison.Ordinal));

        public static bool TryCreateEffect(string id,
            out TriggerLabEffectSettings effect)
        {
            TriggerLabPreset preset = presets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            effect = preset?.CreateEffect();
            return effect != null;
        }
    }

    public sealed class TriggerLabCustomProfile
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public TriggerLabMode Mode { get; set; } = TriggerLabMode.Weapon;
        public int StartPercent { get; set; } = TriggerLabEffectSettings.DefaultStartPercent;
        public int WallPercent { get; set; } = TriggerLabEffectSettings.DefaultWallPercent;
        public int ForcePercent { get; set; } = TriggerLabEffectSettings.DefaultForcePercent;
        public bool Active { get; set; }

        public TriggerLabCustomProfile Normalize()
        {
            Id = (Id ?? string.Empty).Trim();
            Name = (Name ?? string.Empty).Trim();
            if (Name.Length > 48) Name = Name.Substring(0, 48);
            TriggerLabEffectSettings normalized = new TriggerLabEffectSettings
            {
                Mode = Mode,
                StartPercent = StartPercent,
                WallPercent = WallPercent,
                ForcePercent = ForcePercent,
            }.Normalize();
            Mode = normalized.Mode;
            StartPercent = normalized.StartPercent;
            WallPercent = normalized.WallPercent;
            ForcePercent = normalized.ForcePercent;
            Active &= ForcePercent > 0;
            return this;
        }

        public TriggerLabCustomProfile Clone() => new TriggerLabCustomProfile
        {
            Id = Id,
            Name = Name,
            Mode = Mode,
            StartPercent = StartPercent,
            WallPercent = WallPercent,
            ForcePercent = ForcePercent,
            Active = Active,
        }.Normalize();
    }

    public sealed class TriggerLabProfileSettings
    {
        public const string DefaultProfileId = "default";

        public bool Enabled { get; set; }
        public bool Linked { get; set; } = true;
        public bool LeftActive { get; set; }
        public bool RightActive { get; set; }
        public bool LeftGameRumbleVibration { get; set; }
        public bool RightGameRumbleVibration { get; set; }
        public TriggerLabEffectSettings Left { get; set; } = new TriggerLabEffectSettings();
        public TriggerLabEffectSettings Right { get; set; } = new TriggerLabEffectSettings();
        public bool HasSplitState { get; set; }
        public bool SplitLeftActive { get; set; }
        public bool SplitRightActive { get; set; }
        public TriggerLabEffectSettings SplitLeft { get; set; } = new TriggerLabEffectSettings();
        public TriggerLabEffectSettings SplitRight { get; set; } = new TriggerLabEffectSettings();
        public List<TriggerLabCustomProfile> CustomProfiles { get; set; } = new List<TriggerLabCustomProfile>();

        public bool HasActiveOverride => Enabled && (LeftActive || RightActive);
        public bool HasGameRumbleVibration => Enabled &&
            (LeftGameRumbleVibration || RightGameRumbleVibration);

        public void RememberSplitState()
        {
            HasSplitState = true;
            SplitLeft = (Left ?? new TriggerLabEffectSettings()).Clone();
            SplitRight = (Right ?? new TriggerLabEffectSettings()).Clone();
            SplitLeftActive = LeftActive;
            SplitRightActive = RightActive;
        }

        public void SetLinkedMode(bool linked)
        {
            if (Linked == linked)
            {
                return;
            }

            if (linked)
            {
                RememberSplitState();
                Linked = true;
                Right = (Left ?? new TriggerLabEffectSettings()).Clone();
            }
            else
            {
                Linked = false;
                if (HasSplitState)
                {
                    Left = (SplitLeft ?? new TriggerLabEffectSettings()).Clone();
                    Right = (SplitRight ?? new TriggerLabEffectSettings()).Clone();
                    LeftActive = SplitLeftActive;
                    RightActive = SplitRightActive;
                }
                else
                {
                    // The first switch to Split starts with two independent copies
                    // of the currently linked design.
                    RememberSplitState();
                }
            }

            Normalize();
        }

        public void MirrorLinkedEffect(bool sourceIsLeft)
        {
            if (!Linked)
            {
                return;
            }

            if (sourceIsLeft)
            {
                Right = (Left ?? new TriggerLabEffectSettings()).Clone();
            }
            else
            {
                Left = (Right ?? new TriggerLabEffectSettings()).Clone();
            }
        }

        public TriggerLabProfileSettings Normalize()
        {
            Left = (Left ?? new TriggerLabEffectSettings()).Normalize();
            Right = (Right ?? new TriggerLabEffectSettings()).Normalize();
            SplitLeft = (SplitLeft ?? new TriggerLabEffectSettings()).Normalize();
            SplitRight = (SplitRight ?? new TriggerLabEffectSettings()).Normalize();
            CustomProfiles = (CustomProfiles ?? new List<TriggerLabCustomProfile>())
                .Where(profile => profile != null)
                .Select(profile => profile.Normalize())
                .Where(profile => profile.Id == "custom" || profile.Id.StartsWith("custom-", StringComparison.Ordinal))
                .Where(profile => profile.Name.Length > 0)
                .GroupBy(profile => profile.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();

            LeftActive &= Left.ForcePercent > 0;
            RightActive &= Right.ForcePercent > 0;
            if (Linked)
            {
                Right = Left.Clone();
            }

            Enabled &= LeftActive || RightActive ||
                LeftGameRumbleVibration || RightGameRumbleVibration;
            return this;
        }

        public TriggerLabProfileSettings Clone() => new TriggerLabProfileSettings
        {
            Enabled = Enabled,
            Linked = Linked,
            LeftActive = LeftActive,
            RightActive = RightActive,
            LeftGameRumbleVibration = LeftGameRumbleVibration,
            RightGameRumbleVibration = RightGameRumbleVibration,
            Left = Left?.Clone(),
            Right = Right?.Clone(),
            HasSplitState = HasSplitState,
            SplitLeftActive = SplitLeftActive,
            SplitRightActive = SplitRightActive,
            SplitLeft = SplitLeft?.Clone(),
            SplitRight = SplitRight?.Clone(),
            CustomProfiles = CustomProfiles?.Select(profile => profile.Clone()).ToList(),
        }.Normalize();

        public bool IsDefaultConfiguration() =>
            !Enabled && Linked && !LeftActive && !RightActive &&
            !LeftGameRumbleVibration && !RightGameRumbleVibration &&
            !HasSplitState && (CustomProfiles?.Count ?? 0) == 0 &&
            IsDefaultEffect(Left) && IsDefaultEffect(Right);

        private static bool IsDefaultEffect(TriggerLabEffectSettings effect) =>
            effect != null && effect.ProfileId == DefaultProfileId &&
            effect.Mode == TriggerLabMode.Weapon &&
            effect.StartPercent == TriggerLabEffectSettings.DefaultStartPercent &&
            effect.WallPercent == TriggerLabEffectSettings.DefaultWallPercent &&
            effect.ForcePercent == TriggerLabEffectSettings.DefaultForcePercent;
    }
}
