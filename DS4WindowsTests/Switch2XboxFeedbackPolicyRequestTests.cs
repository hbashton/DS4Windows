using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2XboxFeedbackPolicyRequestTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OlderPublicationCannotReplaceNewerPendingWork(bool retry)
    {
        Switch2VirtualFeedbackSession session = new(null, null, 1);
        var newer = new Switch2XboxFeedbackPolicyRequest(session, 0, 7, 3, new(false, false));
        Switch2XboxFeedbackPolicyRequest pending = newer;
        Switch2XboxFeedbackPolicyRequest.Enqueue(ref pending,
            newer with { PublicationRevision = 2, Policy = new(true, true) }, retry);
        Assert.AreSame(newer, pending);
    }

    [TestMethod]
    public void NewerRetryReplacesLateOlderRequestWithinSameLifetime()
    {
        Switch2VirtualFeedbackSession session = new(null, null, 1);
        var newer = new Switch2XboxFeedbackPolicyRequest(session, 0, 7, 3, new(false, false));
        Switch2XboxFeedbackPolicyRequest pending = newer with { PublicationRevision = 2 };
        Switch2XboxFeedbackPolicyRequest.Enqueue(ref pending, newer, retry: true);
        Assert.AreSame(newer, pending);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void CasRetryRevalidatesOwnerAfterReplacement(int boundary)
    {
        Switch2VirtualFeedbackSession session = new(null, null, 1);
        var previous = new Switch2XboxFeedbackPolicyRequest(session, 0, 7, 2, new(false, false));
        var replacement = boundary switch
        {
            0 => previous with { Session = new(null, null, 2) },
            1 => previous with { StreamGeneration = 8 },
            _ => previous with { DeviceIndex = 1 }
        };
        Switch2XboxFeedbackPolicyRequest pending = null;
        int checks = 0;
        Switch2XboxFeedbackPolicyRequest.Enqueue(ref pending, previous, isCurrent: request =>
        {
            Assert.AreSame(previous, request);
            if (++checks != 1) return false;
            // Replace the live owner and publish its wake after the old owner's
            // first identity check. This deterministically forces a CAS retry.
            Interlocked.Exchange(ref pending, replacement);
            return true;
        });
        Assert.AreEqual(2, checks);
        Assert.AreSame(replacement, pending);
    }

    [TestMethod]
    public void RapidOffOnPreservesRestrictiveEdgeOnlyForExactPublication()
    {
        Switch2VirtualFeedbackSession session = new(null, null, 1);
        Switch2XboxFeedbackPolicyRequest pending = null;
        var enabled = new Switch2XboxFeedbackPolicyRequest(session, 0, 7, 2, new(true, true));
        Switch2XboxFeedbackPolicyRequest.Enqueue(ref pending, enabled with { Policy = new(false, false) });
        Switch2XboxFeedbackPolicyRequest.Enqueue(ref pending, enabled);
        Assert.AreEqual(new Switch2XboxFeedbackPolicy(false, false), pending.Policy);
        Switch2XboxFeedbackPolicyRequest.Enqueue(ref pending, enabled with { PublicationRevision = 3 });
        Assert.AreEqual(enabled.Policy, pending.Policy, "A newer game packet is a new effect, not resurrection.");
        Switch2XboxFeedbackPolicyRequest.Enqueue(ref pending, enabled, retry: true);
        Assert.AreEqual(3UL, pending.PublicationRevision, "An older retry must not overwrite newer work.");
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void ReplacedSessionStreamOrSlotCannotInheritOldRestrictions(int boundary)
    {
        Switch2VirtualFeedbackSession session = new(null, null, 1);
        var previous = new Switch2XboxFeedbackPolicyRequest(session, 0, 7, 2, new(false, false));
        var next = previous with { Policy = new(true, true) };
        next = boundary switch
        {
            0 => next with { Session = new(null, null, 2) },
            1 => next with { StreamGeneration = 8 },
            _ => next with { DeviceIndex = 1 }
        };
        Switch2XboxFeedbackPolicyRequest pending = previous;
        Switch2XboxFeedbackPolicyRequest.Enqueue(ref pending, next);
        Assert.AreEqual(next, pending);
        Switch2XboxFeedbackPolicyRequest.Enqueue(ref pending, previous, retry: true);
        Assert.AreEqual(next, pending);
    }
}
