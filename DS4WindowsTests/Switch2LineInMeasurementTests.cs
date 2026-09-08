namespace DS4WindowsTests;

[TestClass]
public class Switch2LineInMeasurementTests
{
    [TestMethod]
    public void ActualFramesEstablishBaselineAndFullWindowsKeepExistingMeasurements()
    {
        var buffer = new LineInMeasurementBuffer();
        buffer.Append(Tone(16800, .005f));
        Assert.AreEqual(16800, buffer.CapturedFrames);
        Assert.IsTrue(buffer.IsValid);
        var result = buffer.Analyze();
        Assert.IsTrue(result.CaptureValid);
        Assert.AreEqual(4, result.Blocks.Length);
        Assert.AreEqual(2400, result.Blocks[^1].FrameCount);
        Assert.AreEqual(50, result.Blocks[^1].DurationMs, 1e-9);
        foreach (var block in result.Blocks)
        {
            Assert.AreEqual(.005, block.Channels[0].Hz440, 1e-8);
            Assert.AreEqual(.005, block.Channels[1].Hz660, 1e-8);
            Assert.AreEqual(.005 / Math.Sqrt(2), block.Channels[0].Rms, 1e-8);
            Assert.AreEqual(0, block.Channels[0].Hz660, 1e-8);
            Assert.AreEqual(0, block.Channels[1].Hz440, 1e-8);
        }
    }

    [TestMethod]
    public void WallTimeDoesNotSubstituteForMissingBaselineFrames()
    {
        var buffer = new LineInMeasurementBuffer();
        buffer.Append(Tone(16799, 0));
        Assert.IsFalse(buffer.Analyze().CaptureValid);
        buffer.Append(Tone(1, 0));
        Assert.IsTrue(buffer.Analyze().CaptureValid);
    }

    [TestMethod]
    public void BaselineOnlyOrShortTailCannotBeClassifiedAsCompleteCapture()
    {
        Assert.IsFalse(LineInMeasurementBuffer.HasCompletePostSubmitCapture(16800, 16800, 16800, 350));
        Assert.IsFalse(LineInMeasurementBuffer.HasCompletePostSubmitCapture(16800, 48000, 64799, 350));
        Assert.IsFalse(LineInMeasurementBuffer.HasCompletePostSubmitCapture(16799, 48000, 64800, 350));
        Assert.IsFalse(LineInMeasurementBuffer.HasCompletePostSubmitCapture(16800, 16799, 64800, 350));
    }

    [DataTestMethod]
    [DataRow(350, 16800)]
    [DataRow(2000, 96000)]
    public void ExactMeasuredTailAndNormalExtraCallbackFramesAreAdmitted(int milliseconds, int requiredFrames)
    {
        Assert.IsTrue(LineInMeasurementBuffer.HasCompletePostSubmitCapture(17280, 48000, 48000 + requiredFrames, milliseconds));
        Assert.IsTrue(LineInMeasurementBuffer.HasCompletePostSubmitCapture(17280, 48000, 48000 + requiredFrames + 480, milliseconds));
        Assert.IsFalse(LineInMeasurementBuffer.HasCompletePostSubmitCapture(17280, 48000, 48000 + requiredFrames - 1, milliseconds));
    }

    [TestMethod]
    public void PriorTraceSizedCompleteQuietCaptureIsNotAutomaticallyInvalidated()
    {
        // The latest extended hardware trace contained 142,560 frames. This
        // synthetic shape check does not reconstruct or reclassify that audio;
        // it proves the new bounds still admit complete captures of that size.
        var buffer = new LineInMeasurementBuffer();
        buffer.Append(Tone(142560, 0));
        var result = buffer.Analyze();
        Assert.IsTrue(result.CaptureValid);
        Assert.AreEqual(142560, result.CapturedFrames);
        Assert.AreEqual(0L, result.DroppedFrames);
        Assert.IsTrue(result.Blocks.All(block => block.Channels.All(channel => channel.Peak == 0)));
    }

    [TestMethod]
    public void OverflowIsExplicitInsteadOfSilentlyTruncatedNegative()
    {
        var buffer = new LineInMeasurementBuffer(16800);
        buffer.Append(Tone(16810, .005f));
        var result = buffer.Analyze();
        Assert.AreEqual(16800, result.CapturedFrames);
        Assert.AreEqual(16810L, result.ObservedFrames);
        Assert.AreEqual(10L, result.DroppedFrames);
        Assert.IsFalse(buffer.IsValid);
        Assert.IsFalse(result.CaptureValid);
    }

    [DataTestMethod]
    [DataRow(float.NaN)]
    [DataRow(float.PositiveInfinity)]
    [DataRow(float.NegativeInfinity)]
    public void NonfiniteInputIsInvalidEvenThoughSummaryNumbersStayFinite(float value)
    {
        var buffer = new LineInMeasurementBuffer();
        byte[] bytes = Tone(16800, 0);
        BitConverter.GetBytes(value).CopyTo(bytes, 8);
        buffer.Append(bytes);
        var result = buffer.Analyze();
        Assert.AreEqual(1L, result.NonfiniteSamples);
        Assert.IsFalse(result.CaptureValid);
        Assert.IsTrue(result.Blocks.All(block => block.Channels.All(channel => double.IsFinite(channel.Rms) && double.IsFinite(channel.Hz440))));
    }

    [TestMethod]
    public void PartialLastWindowIncludesClippingThatWouldPreviouslyBeOmitted()
    {
        var buffer = new LineInMeasurementBuffer();
        byte[] bytes = Tone(19201, 0);
        BitConverter.GetBytes(1f).CopyTo(bytes, bytes.Length - 8);
        buffer.Append(bytes);
        var result = buffer.Analyze();
        Assert.AreEqual(5, result.Blocks.Length);
        Assert.AreEqual(1, result.Blocks[^1].FrameCount);
        Assert.AreEqual(1, result.Blocks[^1].Channels[0].ClippedSamples);
        Assert.AreEqual(1, result.Blocks[^1].Channels[0].Peak, 0);
    }

    [TestMethod]
    public void UnalignedCaptureDataIsInvalidAndNotShiftedIntoAnotherChannel()
    {
        var buffer = new LineInMeasurementBuffer();
        buffer.Append(new byte[9]);
        var result = buffer.Analyze();
        Assert.AreEqual(1, result.CapturedFrames);
        Assert.AreEqual(1L, result.UnalignedBytes);
        Assert.IsFalse(buffer.IsValid);
    }

    [TestMethod]
    public void ClearingScrubsRetainedSignalWithoutClaimingASecondCapture()
    {
        var buffer = new LineInMeasurementBuffer();
        buffer.Append(Tone(16800, .005f));
        buffer.Clear();
        Assert.IsTrue(buffer.Analyze().Blocks.All(block => block.Channels.All(channel => channel.Peak == 0)));
    }

    private static byte[] Tone(int frames, float amplitude)
    {
        var bytes = new byte[frames * 8];
        for (int frame = 0; frame < frames; frame++)
            for (int channel = 0; channel < 2; channel++)
                BitConverter.GetBytes(amplitude * (float)Math.Sin(2 * Math.PI * (channel == 0 ? 440 : 660) * frame / 48000))
                    .CopyTo(bytes, frame * 8 + channel * 4);
        return bytes;
    }
}
