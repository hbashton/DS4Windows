using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2JoyConPairingPolicyTests
{
    [TestMethod]
    public void AutomaticPolicy_SelectsOldestCompatibleHalfFromEachSide()
    {
        Switch2JoyConPairCandidate[] candidates =
        {
            Candidate(31, Switch2ControllerModel.JoyCon2Right, 8),
            Candidate(11, Switch2ControllerModel.JoyCon2Left, 2),
            Candidate(12, Switch2ControllerModel.JoyCon2Left, 3),
            Candidate(30, Switch2ControllerModel.JoyCon2Right, 7),
        };

        Assert.IsTrue(Switch2JoyConAutomaticPairingPolicy.
            TrySelectOldestCompatiblePair(candidates, out int left,
                out int right));
        Assert.AreEqual(11, left);
        Assert.AreEqual(30, right);
    }

    [TestMethod]
    public void AutomaticPolicy_CompactsSurvivingCompatibleHalves()
    {
        Switch2JoyConPairCandidate[] candidates =
        {
            // The earlier partners disappeared. These are the surviving
            // halves, and their original arrival order remains deterministic.
            Candidate(5, Switch2ControllerModel.JoyCon2Left, 5),
            Candidate(4, Switch2ControllerModel.JoyCon2Right, 4),
        };

        Assert.IsTrue(Switch2JoyConAutomaticPairingPolicy.
            TrySelectOldestCompatiblePair(candidates, out int left,
                out int right));
        Assert.AreEqual(5, left);
        Assert.AreEqual(4, right);
    }

    [TestMethod]
    public void AutomaticPolicy_GreedilyFormsOneTwoThreeFourFiveSixPairs()
    {
        var pending = new List<Switch2JoyConPairCandidate>();
        var pairs = new List<(int Left, int Right)>();
        Switch2JoyConPairCandidate[] arrivals =
        {
            Candidate(1, Switch2ControllerModel.JoyCon2Left, 1),
            Candidate(2, Switch2ControllerModel.JoyCon2Right, 2),
            Candidate(3, Switch2ControllerModel.JoyCon2Left, 3),
            Candidate(4, Switch2ControllerModel.JoyCon2Right, 4),
            Candidate(5, Switch2ControllerModel.JoyCon2Left, 5),
            Candidate(6, Switch2ControllerModel.JoyCon2Right, 6),
        };

        foreach (Switch2JoyConPairCandidate arrival in arrivals)
        {
            pending.Add(arrival);
            if (!Switch2JoyConAutomaticPairingPolicy.
                    TrySelectOldestCompatiblePair(pending.ToArray(),
                        out int left, out int right))
            {
                continue;
            }
            pairs.Add((left, right));
            pending.RemoveAll(candidate => candidate.Id == left ||
                candidate.Id == right);
        }

        CollectionAssert.AreEqual(new[]
        {
            (1, 2),
            (3, 4),
            (5, 6),
        }, pairs);
        Assert.AreEqual(0, pending.Count);
    }

    [TestMethod]
    public void AutomaticPolicy_RefusesSameSideOnlyPool()
    {
        Switch2JoyConPairCandidate[] candidates =
        {
            Candidate(1, Switch2ControllerModel.JoyCon2Left, 1),
            Candidate(2, Switch2ControllerModel.JoyCon2Left, 2),
        };

        Assert.IsFalse(Switch2JoyConAutomaticPairingPolicy.
            TrySelectOldestCompatiblePair(candidates, out int left,
                out int right));
        Assert.AreEqual(0, left);
        Assert.AreEqual(0, right);
    }

    [TestMethod]
    public void ManualSelection_ArmsCancelsAndCommitsEitherClickOrder()
    {
        var selection = new Switch2JoyConManualPairSelection();
        Switch2JoyConPairCandidate right = Candidate(8,
            Switch2ControllerModel.JoyCon2Right, 1);
        Switch2JoyConPairCandidate left = Candidate(3,
            Switch2ControllerModel.JoyCon2Left, 2);

        Assert.AreEqual(Switch2JoyConManualPairSelectionDisposition.Armed,
            selection.Select(right).Disposition);
        Assert.IsTrue(selection.IsArmed(right));

        Switch2JoyConManualPairSelectionResult ready =
            selection.Select(left);
        Assert.AreEqual(
            Switch2JoyConManualPairSelectionDisposition.PairReady,
            ready.Disposition);
        Assert.AreEqual(3, ready.LeftCandidateId);
        Assert.AreEqual(8, ready.RightCandidateId);
        Assert.IsFalse(selection.HasArmedCandidate);

        Assert.AreEqual(Switch2JoyConManualPairSelectionDisposition.Armed,
            selection.Select(left).Disposition);
        Assert.AreEqual(
            Switch2JoyConManualPairSelectionDisposition.Cancelled,
            selection.Select(left).Disposition);
        Assert.IsFalse(selection.HasArmedCandidate);
    }

    [TestMethod]
    public void ManualSelection_SameSideDoesNotReplaceArmedHalf()
    {
        var selection = new Switch2JoyConManualPairSelection();
        Switch2JoyConPairCandidate first = Candidate(1,
            Switch2ControllerModel.JoyCon2Left, 1);
        Switch2JoyConPairCandidate second = Candidate(2,
            Switch2ControllerModel.JoyCon2Left, 2);

        selection.Select(first);
        Assert.AreEqual(
            Switch2JoyConManualPairSelectionDisposition.IncompatibleSide,
            selection.Select(second).Disposition);
        Assert.IsTrue(selection.IsArmed(first));
    }

    [TestMethod]
    public void ManualSelection_ReconcileCancelsDisappearedCandidate()
    {
        var selection = new Switch2JoyConManualPairSelection();
        Switch2JoyConPairCandidate left = Candidate(1,
            Switch2ControllerModel.JoyCon2Left, 1);
        selection.Select(left);

        Assert.IsTrue(selection.Reconcile(new[]
        {
            Candidate(2, Switch2ControllerModel.JoyCon2Right, 2),
        }));
        Assert.IsFalse(selection.HasArmedCandidate);
    }

    private static Switch2JoyConPairCandidate Candidate(int id,
        Switch2ControllerModel model, ulong arrivalOrdinal) =>
        new(id, model, arrivalOrdinal);
}
