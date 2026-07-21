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
    /// Per-profile audio-to-advanced-haptics settings. Defaults and ranges match
    /// the DS5 Bridge feature contract, while the implementation is native to
    /// DS4Windows.
    /// </summary>
    public sealed class AudioHapticsProfileSettings
    {
        public const int MinimumGainPercent = 0;
        public const int MaximumGainPercent = 200;
        public const int DefaultGainPercent = 100;

        public bool Enabled { get; set; }
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
            return this;
        }

        public AudioHapticsProfileSettings Clone() => new AudioHapticsProfileSettings
        {
            Enabled = Enabled,
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
            !Enabled && Source == AudioHapticsSourceKind.SystemAudio &&
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
        public TriggerLabEffectSettings Left { get; set; } = new TriggerLabEffectSettings();
        public TriggerLabEffectSettings Right { get; set; } = new TriggerLabEffectSettings();
        public bool HasSplitState { get; set; }
        public bool SplitLeftActive { get; set; }
        public bool SplitRightActive { get; set; }
        public TriggerLabEffectSettings SplitLeft { get; set; } = new TriggerLabEffectSettings();
        public TriggerLabEffectSettings SplitRight { get; set; } = new TriggerLabEffectSettings();
        public List<TriggerLabCustomProfile> CustomProfiles { get; set; } = new List<TriggerLabCustomProfile>();

        public bool HasActiveOverride => Enabled && (LeftActive || RightActive);

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

            // The built-in default is a template, never a persistent override.
            if (Left.ProfileId == DefaultProfileId) LeftActive = false;
            if (Right.ProfileId == DefaultProfileId) RightActive = false;
            LeftActive &= Left.ForcePercent > 0;
            RightActive &= Right.ForcePercent > 0;
            if (Linked)
            {
                Right = Left.Clone();
                bool active = LeftActive || RightActive;
                LeftActive = active;
                RightActive = active;
            }

            Enabled &= LeftActive || RightActive;
            return this;
        }

        public TriggerLabProfileSettings Clone() => new TriggerLabProfileSettings
        {
            Enabled = Enabled,
            Linked = Linked,
            LeftActive = LeftActive,
            RightActive = RightActive,
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
