using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class DS4StateFieldMappingAxisTests
{
    [TestMethod]
    public void ColdStoreAndIndexedByteWritesKeepTheLegacyArrayContract()
    {
        var mapping = new DS4StateFieldMapping();
        Assert.AreEqual((int)DS4StateFieldMapping.LAST_DS4_ACTION + 1,
            mapping.axisdirs.Length);
        for (int index = 0; index < mapping.axisdirs.Length; index++)
        {
            Assert.AreEqual((byte)0, mapping.axisdirs[index]);
            Assert.AreEqual(DS4MappedStickAxis.FromLegacy(0),
                mapping.axisdirs.GetMappedAxis(index));

            for (int value = 0; value <= byte.MaxValue; value++)
            {
                mapping.axisdirs.SetMappedAxis(index,
                    DS4MappedStickAxis.FromSigned(16));
                mapping.axisdirs[index] = (byte)value;
                Assert.AreEqual((byte)value, mapping.axisdirs[index]);
                Assert.AreEqual(DS4MappedStickAxis.FromLegacy((byte)value),
                    mapping.axisdirs.GetMappedAxis(index));
            }
        }

        var state = new DS4State();
        new DS4StateFieldMapping().PopulateState(state);
        Assert.AreEqual((byte)0, state.LX);
        Assert.AreEqual((byte)0, state.LY);
        Assert.AreEqual((byte)0, state.RX);
        Assert.AreEqual((byte)0, state.RY);
        Assert.ThrowsException<IndexOutOfRangeException>(() =>
            _ = mapping.axisdirs[-1]);
        Assert.ThrowsException<IndexOutOfRangeException>(() =>
            mapping.axisdirs[mapping.axisdirs.Length] = 128);
    }

    [TestMethod]
    public void EverySignedValueSurvivesTheTypedStoreWithoutByteQuantization()
    {
        var mapping = new DS4StateFieldMapping();
        const int index = (int)DS4Controls.LXPos;
        for (int value = short.MinValue; value <= short.MaxValue; value++)
        {
            var axis = DS4MappedStickAxis.FromSigned((short)value);
            mapping.axisdirs.SetMappedAxis(index, axis);
            Assert.AreEqual(axis, mapping.axisdirs.GetMappedAxis(index));
            Assert.AreEqual((short)value,
                mapping.axisdirs.GetMappedAxis(index).ToSigned16());
            Assert.AreEqual(axis.LegacyValue, mapping.axisdirs[index]);
        }
    }

    [TestMethod]
    public void PopulateRoundTripCopiesAllMappedAxesAndNeverRestoresRawAxes()
    {
        var source = new DS4State
        {
            LXAxis = DS4MappedStickAxis.FromSigned(16),
            LYAxis = DS4MappedStickAxis.FromSigned(-32),
            RXAxis = DS4MappedStickAxis.FromLegacy(177),
            RYAxis = DS4MappedStickAxis.FromSigned(12345),
            Cross = true,
            L2 = 37,
            OutputLSOuter = 79,
            OutputRSOuter = 115,
            Switch2RawInputStatus = new()
            {
                IsValid = true,
                LeftStickX = 32000,
                LeftStickY = -32000,
            },
        };
        var mapping = new DS4StateFieldMapping(source,
            new DS4StateExposed(source), null);
        var output = new DS4State();
        mapping.PopulateState(output);

        AssertAxisPair(mapping, DS4Controls.LXNeg, source.LXAxis);
        AssertAxisPair(mapping, DS4Controls.LYNeg, source.LYAxis);
        AssertAxisPair(mapping, DS4Controls.RXNeg, source.RXAxis);
        AssertAxisPair(mapping, DS4Controls.RYNeg, source.RYAxis);
        Assert.AreEqual(source.LXAxis, output.LXAxis);
        Assert.AreEqual(source.LYAxis, output.LYAxis);
        Assert.AreEqual(source.RXAxis, output.RXAxis);
        Assert.AreEqual(source.RYAxis, output.RYAxis);
        Assert.AreEqual(source.Cross, output.Cross);
        Assert.AreEqual(source.L2, output.L2);
        Assert.AreEqual(source.OutputLSOuter, output.OutputLSOuter);
        Assert.AreEqual(source.OutputRSOuter, output.OutputRSOuter);

        output.LX = 0;
        source.LY = 255;
        Assert.AreEqual((short)16,
            mapping.axisdirs.GetMappedAxis((int)DS4Controls.LXPos).ToSigned16());
        Assert.AreEqual((short)-32,
            mapping.axisdirs.GetMappedAxis((int)DS4Controls.LYPos).ToSigned16());
        mapping.axisdirs[(int)DS4Controls.RYPos] = 128;
        Assert.AreEqual((short)12345, source.RYAxis.ToSigned16());
    }

    [TestMethod]
    public void IndependentDirectionSlotsPreserveHistoricalPositiveSlotPrecedence()
    {
        var mapping = new DS4StateFieldMapping();
        var output = new DS4State();
        var negative = DS4MappedStickAxis.FromSigned(-48);
        var positive = DS4MappedStickAxis.FromSigned(16);
        for (int first = (int)DS4Controls.LXNeg;
            first <= (int)DS4Controls.RYNeg; first += 2)
        {
            mapping.axisdirs.SetMappedAxis(first, negative);
            mapping.axisdirs.SetMappedAxis(first + 1, positive);
        }
        mapping.PopulateState(output);
        Assert.AreEqual(positive, output.LXAxis);
        Assert.AreEqual(positive, output.LYAxis);
        Assert.AreEqual(positive, output.RXAxis);
        Assert.AreEqual(positive, output.RYAxis);

        for (int first = (int)DS4Controls.LXNeg;
            first <= (int)DS4Controls.RYNeg; first += 2)
        {
            // The same compatibility byte is still a new owner's complete
            // value; it cannot leave a precise predecessor underneath it.
            mapping.axisdirs[first + 1] = mapping.axisdirs[first + 1];
            Assert.AreEqual(negative, mapping.axisdirs.GetMappedAxis(first));
            Assert.AreEqual(DS4MappedStickAxis.FromLegacy(128),
                mapping.axisdirs.GetMappedAxis(first + 1));
        }
        mapping.PopulateState(output);
        Assert.AreEqual(DS4MappedStickAxis.FromLegacy(128), output.LXAxis);
        Assert.AreEqual(DS4MappedStickAxis.FromLegacy(128), output.LYAxis);
        Assert.AreEqual(DS4MappedStickAxis.FromLegacy(128), output.RXAxis);
        Assert.AreEqual(DS4MappedStickAxis.FromLegacy(128), output.RYAxis);
    }

    [TestMethod]
    public void NeutralRepopulationAndExplicitDefaultsRetirePreviousPrecision()
    {
        var source = new DS4State
        {
            LXAxis = DS4MappedStickAxis.FromSigned(1000),
            LYAxis = DS4MappedStickAxis.FromSigned(-2000),
            RXAxis = DS4MappedStickAxis.FromSigned(3000),
            RYAxis = DS4MappedStickAxis.FromSigned(-4000),
        };
        var exposed = new DS4StateExposed(source);
        var mapping = new DS4StateFieldMapping(source, exposed, null);
        source.LX = source.LY = source.RX = source.RY = 128;
        mapping.PopulateFieldMapping(source, exposed, null);
        for (int index = (int)DS4Controls.LXNeg;
            index <= (int)DS4Controls.RYPos; index++)
        {
            Assert.AreEqual(DS4MappedStickAxis.FromLegacy(128),
                mapping.axisdirs.GetMappedAxis(index));
            mapping.axisdirs.SetMappedAxis(index,
                DS4MappedStickAxis.FromSigned(5000));
            mapping.axisdirs.SetMappedAxis(index, default);
            Assert.AreEqual(default(DS4MappedStickAxis),
                mapping.axisdirs.GetMappedAxis(index));
            Assert.AreEqual((byte)128, mapping.axisdirs[index]);
            Assert.IsFalse(mapping.axisdirs.GetMappedAxis(index).IsHighResolution);
        }
    }

    [TestMethod]
    public void EveryLegacyByteRoundTripsThroughBothDirectionSlotsExactly()
    {
        var source = new DS4State();
        var exposed = new DS4StateExposed(source);
        var mapping = new DS4StateFieldMapping();
        var output = new DS4State();
        for (int value = 0; value <= byte.MaxValue; value++)
        {
            source.LX = source.RY = (byte)value;
            source.LY = source.RX = (byte)(255 - value);
            mapping.PopulateFieldMapping(source, exposed, null);
            mapping.PopulateState(output);
            Assert.AreEqual(source.LXAxis, output.LXAxis);
            Assert.AreEqual(source.LYAxis, output.LYAxis);
            Assert.AreEqual(source.RXAxis, output.RXAxis);
            Assert.AreEqual(source.RYAxis, output.RYAxis);
            AssertAxisPair(mapping, DS4Controls.LXNeg, source.LXAxis);
            AssertAxisPair(mapping, DS4Controls.LYNeg, source.LYAxis);
            AssertAxisPair(mapping, DS4Controls.RXNeg, source.RXAxis);
            AssertAxisPair(mapping, DS4Controls.RYNeg, source.RYAxis);
        }
    }

    [TestMethod]
    public void WarmPopulateTypedCopyAndExplicitByteReplacementAllocateNothing()
    {
        var source = new DS4State();
        var exposed = new DS4StateExposed(source);
        var mapping = new DS4StateFieldMapping();
        var output = new DS4State();
        long checksum = 0;
        for (int index = 0; index < 2000; index++)
            checksum += WarmStep(source, exposed, mapping, output, index);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20000; index++)
            checksum += WarmStep(source, exposed, mapping, output, index);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(checksum > 0);
        Assert.AreEqual(0L, allocated);
    }

    private static int WarmStep(DS4State source, DS4StateExposed exposed,
        DS4StateFieldMapping mapping, DS4State output, int value)
    {
        source.LXAxis = DS4MappedStickAxis.FromSigned((short)value);
        mapping.PopulateFieldMapping(source, exposed, null);
        DS4MappedStickAxis axis = mapping.axisdirs.GetMappedAxis(
            (int)DS4Controls.LXPos);
        mapping.axisdirs.SetMappedAxis((int)DS4Controls.RXNeg, axis);
        mapping.axisdirs.SetMappedAxis((int)DS4Controls.RXPos, axis);
        mapping.axisdirs[(int)DS4Controls.LXNeg] = 128;
        mapping.axisdirs[(int)DS4Controls.LXPos] = 128;
        mapping.PopulateState(output);
        return output.RXAxis.ToSigned16() + output.LXAxis.ToSigned16();
    }

    private static void AssertAxisPair(DS4StateFieldMapping mapping,
        DS4Controls negativeControl, DS4MappedStickAxis expected)
    {
        int first = (int)negativeControl;
        Assert.AreEqual(expected, mapping.axisdirs.GetMappedAxis(first));
        Assert.AreEqual(expected, mapping.axisdirs.GetMappedAxis(first + 1));
        Assert.AreEqual(expected.LegacyValue, mapping.axisdirs[first]);
        Assert.AreEqual(expected.LegacyValue, mapping.axisdirs[first + 1]);
    }
}
