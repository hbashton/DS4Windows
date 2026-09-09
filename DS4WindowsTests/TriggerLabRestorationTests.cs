using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public sealed class TriggerLabRestorationTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void DisabledOrPausedLabRestoresBothIndependentLegacyEffects(bool leftArmed, bool rightArmed)
    {
        var lab = new TriggerLabProfileSettings
        {
            Enabled = false, LeftActive = leftArmed, RightActive = rightArmed,
        };
        List<Dispatch> result = Restore(lab);
        AssertLegacyPair(result);
        Assert.IsFalse(lab.Enabled);
        Assert.AreEqual(leftArmed, lab.LeftActive);
        Assert.AreEqual(rightArmed, lab.RightActive);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NoPersistentLabOverrideUsesTheProfileLegacyEffects(bool gameRumble)
    {
        var lab = new TriggerLabProfileSettings
        {
            Enabled = true, LeftGameRumbleVibration = gameRumble,
        };
        AssertLegacyPair(Restore(lab));
    }

    [TestMethod]
    public void MissingLabSettingsRestoreLegacyEffects()
    {
        AssertLegacyPair(Restore(null));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ActiveLabPreservesItsExistingPerTriggerActivationPolicy(bool leftActive)
    {
        var lab = new TriggerLabProfileSettings
        {
            Enabled = true, Linked = false,
            LeftActive = leftActive, RightActive = !leftActive,
            Left = TriggerLabPresetCatalog.Presets[1].CreateEffect(),
            Right = TriggerLabPresetCatalog.Presets[4].CreateEffect(),
        };
        List<Dispatch> result = Restore(lab);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(TriggerId.LeftTrigger, result[0].Trigger);
        Assert.AreEqual(TriggerId.RightTrigger, result[1].Trigger);
        Assert.IsTrue(result.All(item => item.IsLab));
        Assert.AreSame(lab.Left, result[0].LabEffect);
        Assert.AreSame(lab.Right, result[1].LabEffect);
        Assert.AreEqual(leftActive, result[0].Active);
        Assert.AreEqual(!leftActive, result[1].Active);
        Assert.IsTrue(TriggerLabEffectEncoder.Encode(result[leftActive ? 1 : 0].LabEffect,
            result[leftActive ? 1 : 0].Active).IsOff,
            "As in profile application, an inactive side stays explicitly off while the other Lab side overrides.");
    }

    [TestMethod]
    public void ArmedLabDesignSurvivesPauseAndResumeWithoutBeingRewritten()
    {
        var lab = new TriggerLabProfileSettings
        {
            Enabled = true, Linked = false, LeftActive = true, RightActive = true,
            Left = TriggerLabPresetCatalog.Presets[1].CreateEffect(),
            Right = TriggerLabPresetCatalog.Presets[4].CreateEffect(),
        };
        TriggerLabEffectSettings left = lab.Left;
        TriggerLabEffectSettings right = lab.Right;
        Assert.IsTrue(Restore(lab).All(item => item.IsLab && item.Active));
        lab.Enabled = false;
        AssertLegacyPair(Restore(lab));
        lab.Enabled = true;
        Assert.IsTrue(Restore(lab).All(item => item.IsLab && item.Active));
        Assert.AreSame(left, lab.Left);
        Assert.AreSame(right, lab.Right);
        Assert.IsTrue(lab.LeftActive && lab.RightActive);
    }

    [TestMethod]
    public void ControlRestorationUsesItsCorrectProfileAndPreviewRemainsTemporary()
    {
        // Wiring-only source check; no WPF control, App, HID, or profile write.
        string source = ReadControlSource();
        string persistent = Extract(source, "public void ApplyPersistentEffects()",
            "public void RestorePhysicalProfileEffects()");
        string restore = Extract(source, "public void RestorePhysicalProfileEffects()",
            "private void ApplyProfileEffects(");
        string apply = Extract(source, "private void ApplyProfileEffects(",
            "private void ApplyEffect(");
        string preview = Extract(source, "private void Preview(", "private void ResetSide(");
        StringAssert.Contains(persistent, "ApplyProfileEffects(deviceIndex);");
        StringAssert.Contains(restore, "ApplyProfileEffects(physicalDeviceIndex);");
        StringAssert.Contains(apply, "TriggerLabProfileEffectRestoration.ApplyToDevice(device,");
        StringAssert.Contains(apply, "Global.L2OutputSettings[profileIndex], Global.R2OutputSettings[profileIndex]");
        StringAssert.Contains(preview, "previewResetTimer.Start();");
        Assert.IsFalse(preview.Contains("SettingsChanged", StringComparison.Ordinal));
        Assert.IsFalse(preview.Contains("Commit(", StringComparison.Ordinal));
        Assert.IsFalse(apply.Contains("CheckProfileOptions", StringComparison.Ordinal));
    }

    private static List<Dispatch> Restore(TriggerLabProfileSettings lab)
    {
        var left = new TriggerOutputSettings
        {
            triggerEffect = TriggerEffects.FullClick,
            effectSettings = new TriggerEffectSettings { startValue = 2, maxValue = 180 },
        };
        var right = new TriggerOutputSettings
        {
            triggerEffect = TriggerEffects.Resistance,
            effectSettings = new TriggerEffectSettings { startValue = 4, maxValue = 90 },
        };
        var result = new List<Dispatch>();
        TriggerLabProfileEffectRestoration.Apply(lab, left, right,
            (trigger, effect, active) => result.Add(new(trigger, true, effect, active, default, default)),
            (trigger, effect, settings) => result.Add(new(trigger, false, null, false, effect, settings)));
        return result;
    }

    private static void AssertLegacyPair(List<Dispatch> result)
    {
        Assert.AreEqual(2, result.Count);
        Assert.IsFalse(result.Any(item => item.IsLab),
            "An absent/paused persistent Lab override must restore normal profile effects, not send a Lab Off block.");
        Assert.AreEqual(TriggerId.LeftTrigger, result[0].Trigger);
        Assert.AreEqual(TriggerEffects.FullClick, result[0].LegacyEffect);
        Assert.AreEqual((byte)2, result[0].LegacySettings.startValue);
        Assert.AreEqual((byte)180, result[0].LegacySettings.maxValue);
        Assert.AreEqual(TriggerId.RightTrigger, result[1].Trigger);
        Assert.AreEqual(TriggerEffects.Resistance, result[1].LegacyEffect);
        Assert.AreEqual((byte)4, result[1].LegacySettings.startValue);
        Assert.AreEqual((byte)90, result[1].LegacySettings.maxValue);
    }

    private readonly record struct Dispatch(TriggerId Trigger, bool IsLab,
        TriggerLabEffectSettings LabEffect, bool Active, TriggerEffects LegacyEffect,
        TriggerEffectSettings LegacySettings);

    private static string ReadControlSource([CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(testPath), "..", "DS4Windows",
            "DS4Forms", "TriggerLabControl.xaml.cs"));

    private static string Extract(string source, string signature, string next)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        int end = source.IndexOf(next, start + signature.Length, StringComparison.Ordinal);
        Assert.IsTrue(end > start);
        return source[start..end];
    }
}
