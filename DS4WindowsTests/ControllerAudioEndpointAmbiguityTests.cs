using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ControllerAudioEndpointAmbiguityTests
    {
        private sealed class Candidate
        {
            internal Candidate(string id, bool replaces = false)
            {
                Id = id;
                Replaces = replaces;
            }

            internal string Id { get; }
            internal bool Replaces { get; }
        }

        [TestMethod]
        public void MissingOwnerIdentityRejectsTwoSonyEndpoints()
        {
            Candidate[] endpoints =
            {
                new("physical-sony"),
                new("viiper-sony"),
            };

            Candidate selected = DualSenseAudioPassthrough.
                SelectUnambiguousControllerEndpoint(endpoints,
                    _ => false, _ => false);

            Assert.IsNull(selected);
        }

        [TestMethod]
        public void ExactSavedEndpointSurvivesPhysicalVirtualCoexistence()
        {
            Candidate physical = new("physical-sony");
            Candidate viiper = new("viiper-sony");
            Candidate[] endpoints = { physical, viiper };

            Candidate selected = DualSenseAudioPassthrough.
                SelectUnambiguousControllerEndpoint(endpoints,
                    candidate => candidate.Id == "viiper-sony",
                    _ => false);

            Assert.AreSame(viiper, selected);
        }

        [TestMethod]
        public void AmbiguousEndpointRecreationHistoryFailsClosed()
        {
            Candidate[] endpoints =
            {
                new("first-replacement", replaces: true),
                new("second-replacement", replaces: true),
            };

            Candidate selected = DualSenseAudioPassthrough.
                SelectUnambiguousControllerEndpoint(endpoints,
                    _ => false, candidate => candidate.Replaces);

            Assert.IsNull(selected);
        }

        [TestMethod]
        public void UniqueSingleControllerFallbackRemainsAvailable()
        {
            Candidate only = new("only-sony");

            Candidate selected = DualSenseAudioPassthrough.
                SelectUnambiguousControllerEndpoint(new[] { only },
                    _ => false, _ => false);

            Assert.AreSame(only, selected);
        }
    }
}
