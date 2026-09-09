using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class XboxOnePersonaPackagingTests
{
    [TestMethod]
    public void ShippedPersonaPreservesReviewedBytesAndPassesProductionParser()
    {
        string path = RepositoryPath("extras/xbox-one-authorized-persona.json");
        byte[] bytes = File.ReadAllBytes(path);
        Assert.AreEqual(
            "2A85D3395529C7305F55338E4965A7FFD4E269DDE535FA41BD98BC54D67111C4",
            Convert.ToHexString(SHA256.HashData(bytes)));
        XboxOneAuthorizedPersonaConfiguration configuration =
            XboxOneAuthorizedPersonaConfiguration.ParseExplicit(
                File.ReadAllText(path), path);
        Assert.AreEqual((ushort)0xf00d, configuration.Identity.VendorId);
        Assert.AreEqual((ushort)0xbeed, configuration.Identity.ProductId);
        Assert.IsTrue(configuration.DerivePerRegistrationIdentity,
            "Repeated or simultaneous virtual controllers need distinct GIP identities.");
        Assert.AreEqual("VIIPER Portable Lab", configuration.Strings.Manufacturer);
        Assert.AreEqual("VIIPER GIP Test Controller", configuration.Strings.Product);
    }

    [DataTestMethod]
    [DataRow("xbox-one-authorized-persona.json", "xbox-one-authorized-persona.json")]
    [DataRow("XBOX-ONE-PERSONA-NOTICE.md", "extras\\XBOX-ONE-PERSONA-NOTICE.md")]
    public void PersonaAndNoticeAreIncludedInRawBuildAndPublish(string name, string link)
    {
        XDocument project = XDocument.Load(RepositoryPath("DS4Windows/DS4WinWPF.csproj"));
        XElement content = project.Descendants("Content").Single(element =>
            (string)element.Attribute("Include") == "..\\extras\\" + name);
        Assert.AreEqual(link, (string)content.Element("Link"));
        Assert.AreEqual("PreserveNewest", (string)content.Element("CopyToOutputDirectory"));
        Assert.AreEqual("PreserveNewest", (string)content.Element("CopyToPublishDirectory"));
        Assert.IsTrue(File.Exists(RepositoryPath("extras/" + name)));
    }

    private static string RepositoryPath(string path, [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "..", path));
}
