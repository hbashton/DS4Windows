using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace DS4WindowsTests;

[TestClass]
public sealed class ReleaseSigningPolicyTests
{
    [DataTestMethod]
    [DataRow("true", "VIIPERRC4.5", true)]
    [DataRow("true", "VIIPERRC4", true)]
    [DataRow("true", "VIIPERRC4.5.1.2", true)]
    [DataRow("false", "VIIPERRC4.5", false)]
    [DataRow("", "VIIPERRC4.5", false)]
    [DataRow("True", "VIIPERRC4.5", false)]
    [DataRow("true", "v5.0.5", false)]
    [DataRow("false", "v5.0.5", false)]
    [DataRow("true", "5.0.5-rc.1", false)]
    [DataRow("true", "VIIPERBeta10", false)]
    [DataRow("true", "viiperrc4.5", false)]
    [DataRow("true", "vVIIPERRC4.5", false)]
    [DataRow("true", "VIIPERRC4.5-extra", false)]
    [DataRow("true", "VIIPERRC4.5.1.2.3", false)]
    [DataRow("true", "VIIPERRC4.5\n", false)]
    public void OnlyVerifiedNamedRcPrereleasesMayOmitSigning(string flag, string tag, bool expected)
    {
        string source = Workflow();
        StringAssert.Contains(source, "$unsignedRc = $isPrerelease -ceq 'true' -and");
        Match pattern = Regex.Match(source, @"\$tag -cmatch '(?<pattern>[^']+)'");
        Assert.IsTrue(pattern.Success, "Test the actual workflow pattern, not a duplicated permissive helper.");
        Assert.AreEqual(expected, flag == "true" && Regex.IsMatch(tag, pattern.Groups["pattern"].Value,
            RegexOptions.CultureInvariant));
    }

    [TestMethod]
    public void IdentityComesFromSameRepositoryApiAndDispatchRequiresDraft()
    {
        string source = Workflow();
        StringAssert.Contains(source, "EVENT_PRERELEASE: ${{ github.event.release.prerelease }}");
        StringAssert.Contains(source, "repos/$env:GITHUB_REPOSITORY/releases/tags/$tag");
        StringAssert.Contains(source, "$release.tag_name -cne $tag -or $release.id -le 0");
        StringAssert.Contains(source, "$dispatch -and -not $release.draft");
        StringAssert.Contains(source, "$env:EVENT_RELEASE_ID");
        StringAssert.Contains(source, "$release.prerelease.ToString().ToLowerInvariant() -cne $env:EVENT_PRERELEASE");
        StringAssert.Contains(source, "$isPrerelease = $release.prerelease.ToString().ToLowerInvariant()");
        StringAssert.Contains(source, "UNSIGNED_RC_RELEASE: ${{ needs.identity.outputs.unsigned_rc }}");
        Assert.IsFalse(source.Contains("github.event.release.body", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DraftDispatchRejectsStableAndUnknownReleaseTypesBeforeExposingOutputs()
    {
        string source = Workflow();
        int gate = source.IndexOf("if ($dispatch -and -not $unsignedRc)", StringComparison.Ordinal);
        int output = source.IndexOf("\"tag=$tag\" >> $env:GITHUB_OUTPUT", StringComparison.Ordinal);
        Assert.IsTrue(gate >= 0 && gate < output);
        StringAssert.Contains(source[gate..output], "throw 'Draft dispatch is limited to named RC prereleases;");
        StringAssert.Contains(source, "if ($dispatch -and -not $release.draft)");
        StringAssert.Contains(source, "RequireSigning = $env:UNSIGNED_RC_RELEASE -ne 'true'");
    }

    [TestMethod]
    public void StableAndUnknownPrereleasesRetainCertificateIdentityAndInstallerSigningGates()
    {
        string source = Workflow();
        string signing = Between(source, "    - name: Sign and verify release binaries", "    - name: Post-Build script X64");
        StringAssert.Contains(signing, "if: env.UNSIGNED_RC_RELEASE != 'true'");
        foreach (string secret in new[] { "DS4W_SIGN_CERT_BASE64", "DS4W_SIGN_CERT_PASSWORD", "DS4W_SIGN_EXPECTED_THUMBPRINT" })
            StringAssert.Contains(signing, "${{ secrets." + secret + " }}");
        StringAssert.Contains(signing, "'^[0-9A-Fa-f]{40}$'");
        StringAssert.Contains(signing, "$signature.Status -ne \"Valid\"");
        StringAssert.Contains(signing, "$signature.SignerCertificate.Thumbprint -ne $approvedThumbprint");
        StringAssert.Contains(signing, "-not $signature.TimeStamperCertificate");
        StringAssert.Contains(source, "RequireSigning = $env:UNSIGNED_RC_RELEASE -ne 'true'");
        StringAssert.Contains(source, "env.UNSIGNED_RC_RELEASE != 'true' && secrets.DS4W_SIGN_CERT_PASSWORD || ''");
        StringAssert.Contains(source, "Unsigned named release candidate; not a signed stable release.");
    }

    [TestMethod]
    public void UploadRequiresCurrentAssetHashesCorrespondingSourceAndNoOverwrite()
    {
        string source = Workflow();
        string records = Between(source, "    - name: Prepare verified release records", "    - name: Verify and publish exact release assets");
        StringAssert.Contains(records, "if ($sourceCommit -cne $tagCommit)");
        StringAssert.Contains(records, "git archive --format=zip --prefix=DS4Windows/");
        StringAssert.Contains(records, "$brokerTagCommit.Trim() -cne $brokerCommit");
        StringAssert.Contains(records, "Hash -cne $brokerSourceHash");
        StringAssert.Contains(records, "Hash -cne $brokerHash");
        StringAssert.Contains(records, "'SOURCE-REVISIONS.txt'");
        StringAssert.Contains(records, "'SHA256SUMS.txt'");
        StringAssert.Contains(records, "'RELEASE-BUILD.json'");
        string upload = Between(source, "    - name: Verify and publish exact release assets", "    - name: Remove signing material");
        StringAssert.Contains(upload, "Refusing to overwrite existing release asset:");
        StringAssert.Contains(upload, "Hash -cne $expected[$name]");
        Assert.IsTrue(upload.IndexOf("Hash -cne $expected[$name]", StringComparison.Ordinal) <
            upload.IndexOf("gh release upload", StringComparison.Ordinal));
        StringAssert.Contains(upload, "gh release upload $env:RELEASE_TAG @assets");
        Assert.IsFalse(source.Contains("--clobber", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PublishedRcVerifiesSuccessfulDraftRunAndActualBytesWithoutRebuild()
    {
        string source = Workflow();
        StringAssert.Contains(source, "if: needs.identity.outputs.verify_existing != 'true'");
        string verify = source[source.IndexOf("  verify_published_rc:", StringComparison.Ordinal)..];
        StringAssert.Contains(verify, "if: needs.identity.outputs.verify_existing == 'true'");
        StringAssert.Contains(verify, "$receipt.sourceCommit -cne $tagCommit");
        StringAssert.Contains(verify, "$run.event -cne 'workflow_dispatch'");
        StringAssert.Contains(verify, "$run.conclusion -cne 'success'");
        StringAssert.Contains(verify, "$run.head_sha -cne $tagCommit");
        StringAssert.Contains(verify, "$run.path -cne '.github/workflows/release.yml'");
        StringAssert.Contains(verify, "$published[0].uploader.login -cne 'github-actions[bot]'");
        StringAssert.Contains(verify, "Hash -cne $asset.sha256");
        Assert.IsFalse(verify.Contains("dotnet publish", StringComparison.Ordinal));
        Assert.IsFalse(verify.Contains("gh release upload", StringComparison.Ordinal));
    }

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0 && last > first);
        return source[first..last];
    }

    private static string Workflow([CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".github", "workflows", "release.yml")));
}
