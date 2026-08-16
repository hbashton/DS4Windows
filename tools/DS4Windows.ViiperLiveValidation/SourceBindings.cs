using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DS4Windows.ViiperLiveValidation;

internal sealed class SourceBindings : IDisposable
{
    private SourceBindings(ViiperNativeRuntimeMetadata metadata,
        BindingEvidence evidence, ImmutableProbeExecutable inputProbe,
        ImmutableProbeExecutable mediaProbe,
        InstalledRuntimeLease installedRuntime)
    {
        Metadata = metadata;
        Evidence = evidence;
        InputProbe = inputProbe;
        MediaProbe = mediaProbe;
        InstalledRuntime = installedRuntime;
    }

    internal ViiperNativeRuntimeMetadata Metadata { get; }
    internal BindingEvidence Evidence { get; }
    internal ImmutableProbeExecutable InputProbe { get; }
    internal ImmutableProbeExecutable MediaProbe { get; }
    private InstalledRuntimeLease InstalledRuntime { get; }

    internal void ValidateEvidenceOutputPath(string outputPath)
    {
        string full = Path.GetFullPath(outputPath);
        IEnumerable<FileBindingEvidence> inputs =
            Evidence.PackageArtifacts.Append(Evidence.Metadata)
                .Append(Evidence.RunnerExecutable)
                .Append(Evidence.RunnerAssembly)
                .Append(Evidence.Ds4WindowsAssembly);
        if (inputs.Any(input => string.Equals(input.Path, full,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ViiperIdentityException(
                "The evidence output path collides with a source-bound input.");
        }
    }

    internal void Revalidate()
    {
        IEnumerable<FileBindingEvidence> bindings =
            Evidence.PackageArtifacts.Append(Evidence.Metadata)
                .Append(Evidence.RunnerExecutable)
                .Append(Evidence.RunnerAssembly)
                .Append(Evidence.Ds4WindowsAssembly);
        foreach (FileBindingEvidence binding in bindings)
        {
            string path = RequireRegularFile(binding.Path, binding.Role);
            var info = new FileInfo(path);
            string hash = Sha256File(path);
            if (info.Length != binding.Length ||
                !string.Equals(hash, binding.Sha256,
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"Source-bound file '{binding.Role}' changed during live validation: startLength={binding.Length} finalLength={info.Length} startSha256={binding.Sha256} finalSha256={hash}.");
            }
        }
        InputProbe.Revalidate();
        MediaProbe.Revalidate();
        InstalledRuntime.Revalidate(Evidence);
    }

    public void Dispose()
    {
        InputProbe.Dispose();
        MediaProbe.Dispose();
        InstalledRuntime.Dispose();
    }

    internal static SourceBindings Load(LiveValidationOptions options)
    {
        string metadataPath = RequireRegularFile(options.MetadataPath,
            "native runtime metadata");
        string raw = File.ReadAllText(metadataPath);
        ViiperNativeRuntimeContract.ValidateNoDuplicateJsonProperties(raw,
            ViiperNativeRuntimeMetadata.FileName);
        ViiperNativeRuntimeMetadata metadata =
            ViiperNativeRuntimeMetadata.Parse(metadataPath,
                Environment.GetEnvironmentVariable(
                    ViiperTransportSettings.LocalTestEnvironmentVariable));

        ValidateProductionHandlerContracts(metadata);
        List<FileBindingEvidence> artifacts = ReadAndValidateArtifacts(raw,
            options.ArtifactRoot);
        ValidateRequiredArtifactNames(artifacts);
        ValidateLiveProbeManifest(metadata, artifacts);
        FileBindingEvidence input = artifacts.Single(binding =>
            binding.Role == "input-probe");
        FileBindingEvidence media = artifacts.Single(binding =>
            binding.Role == "media-probe");

        string processPath = Environment.ProcessPath ?? throw new IOException(
            "The runner executable path is unavailable.");
        if (!string.Equals(Path.GetFileName(processPath),
                "DS4Windows.ViiperLiveValidation.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ViiperIdentityException(
                "Live validation must run through its exact Windows apphost executable, not a generic dotnet host.");
        }
        var evidence = new BindingEvidence
        {
            ViiperSourceRevision = metadata.SourceRevision,
            ReleaseEligibility = metadata.ReleaseEligibility,
            DriverPackageVersion = metadata.DriverPackageVersion,
            DriverBuildIdentity = metadata.LoadedDriverBuildIdentity,
            AbiMajor = metadata.AbiMajor,
            AbiMinor = metadata.AbiMinor,
            Capabilities = metadata.RequiredCapabilities,
            RunnerExecutable = BindFile("runner-executable", processPath),
            RunnerAssembly = BindFile("runner-managed-assembly",
                Assembly.GetExecutingAssembly().Location),
            Ds4WindowsAssembly = BindFile("ds4windows-production-assembly",
                typeof(ViiperOutDevice).Assembly.Location),
            Metadata = BindFile("runtime-metadata", metadataPath),
            PackageArtifacts = artifacts,
        };
        ImmutableProbeExecutable? lockedInput = null;
        ImmutableProbeExecutable? lockedMedia = null;
        InstalledRuntimeLease? installedRuntime = null;
        try
        {
            lockedInput = new ImmutableProbeExecutable(input);
            lockedMedia = new ImmutableProbeExecutable(media);
            evidence.InputProbeExecution = lockedInput.Evidence;
            evidence.MediaProbeExecution = lockedMedia.Evidence;
            installedRuntime = InstalledRuntimeLease.Create(evidence);
            evidence.InstalledRuntime = installedRuntime.Evidence;
            return new SourceBindings(metadata, evidence, lockedInput,
                lockedMedia, installedRuntime);
        }
        catch
        {
            installedRuntime?.Dispose();
            lockedMedia?.Dispose();
            lockedInput?.Dispose();
            throw;
        }
    }

    internal static string Sha256File(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static List<FileBindingEvidence> ReadAndValidateArtifacts(
        string metadataJson, string artifactRoot)
    {
        string root = Path.GetFullPath(artifactRoot);
        if (!Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"The artifact root is missing or reparse-backed: '{root}'.");
        }
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using JsonDocument document = JsonDocument.Parse(metadataJson);
        if (!document.RootElement.TryGetProperty("artifacts",
                out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() == 0 || array.GetArrayLength() > 64)
        {
            throw new ViiperNativeMetadataException(
                "Native runtime metadata must bind 1 through 64 artifacts.");
        }

        var result = new List<FileBindingEvidence>();
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement artifact in array.EnumerateArray())
        {
            string role = RequiredString(artifact, "role");
            string relative = RequiredString(artifact, "relativePath");
            string expectedHash = RequiredString(artifact, "sha256");
            string[] pathParts = relative.Split('/');
            if (!roles.Add(role) || relative.Contains('\\') ||
                Path.IsPathRooted(relative) ||
                !relative.StartsWith("viiper-native-package/",
                    StringComparison.Ordinal) ||
                pathParts.Any(part => part.Length == 0 || part == "." ||
                    part == ".." || part.IndexOfAny(
                        Path.GetInvalidFileNameChars()) >= 0 ||
                    !string.Equals(part, part.TrimEnd(' ', '.'),
                        StringComparison.Ordinal)))
            {
                throw new ViiperNativeMetadataException(
                    $"Artifact '{role}' has a duplicate role or non-canonical path.");
            }
            ViiperNativeRuntimeMetadata.ValidateLowercaseSha256(expectedHash,
                $"artifact {role} sha256");
            if (!artifact.TryGetProperty("length", out JsonElement lengthNode) ||
                !lengthNode.TryGetInt64(out long expectedLength) ||
                expectedLength <= 0)
            {
                throw new ViiperNativeMetadataException(
                    $"Artifact '{role}' has an invalid length.");
            }

            string path = Path.GetFullPath(Path.Combine(root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(rootPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ViiperNativeMetadataException(
                    $"Artifact '{role}' escapes the exact artifact root.");
            }
            path = RequireRegularFile(path, role);
            RejectReparseAncestors(path, root);
            var info = new FileInfo(path);
            string actualHash = Sha256File(path);
            bool exact = info.Length == expectedLength &&
                CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actualHash),
                    System.Text.Encoding.ASCII.GetBytes(expectedHash));
            var binding = new FileBindingEvidence
            {
                Role = role,
                Path = path,
                Length = info.Length,
                Sha256 = actualHash,
                ExpectedLength = expectedLength,
                ExpectedSha256 = expectedHash,
                ExactMatch = exact,
            };
            result.Add(binding);
            if (!exact)
            {
                throw new ViiperIdentityException(
                    $"Artifact '{role}' does not match its exact metadata binding: expectedLength={expectedLength} actualLength={info.Length} expectedSha256={expectedHash} actualSha256={actualHash}.");
            }
        }

        foreach (string required in new[] { "broker", "driver-helper",
                     "input-probe", "media-probe", "live-probe-manifest" })
        {
            if (!roles.Contains(required))
            {
                throw new ViiperNativeMetadataException(
                    $"Native runtime metadata omitted required artifact role '{required}'.");
            }
        }
        return result.OrderBy(binding => binding.Role,
            StringComparer.Ordinal).ToList();
    }

    private static void ValidateLiveProbeManifest(
        ViiperNativeRuntimeMetadata metadata,
        IReadOnlyList<FileBindingEvidence> artifacts)
    {
        FileBindingEvidence manifest = artifacts.Single(binding =>
            binding.Role == "live-probe-manifest");
        string raw = File.ReadAllText(manifest.Path);
        ViiperNativeRuntimeContract.ValidateNoDuplicateJsonProperties(raw,
            "live-probe-manifest");
        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out JsonElement schema) ||
            !schema.TryGetInt32(out int schemaVersion) ||
            schemaVersion != 1 ||
            !root.TryGetProperty("sourceRevision", out JsonElement source) ||
            source.ValueKind != JsonValueKind.String ||
            !string.Equals(source.GetString(), metadata.SourceRevision,
                StringComparison.Ordinal) ||
            !root.TryGetProperty("probes", out JsonElement probes) ||
            probes.ValueKind != JsonValueKind.Object)
        {
            throw new ViiperNativeMetadataException(
                "The live-probe manifest is not bound to the exact metadata source revision.");
        }

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ViiperUdeMediaProbe.exe"] = artifacts.Single(binding =>
                binding.Role == "media-probe").Sha256,
            ["ViiperUdeInputProbe.exe"] = artifacts.Single(binding =>
                binding.Role == "input-probe").Sha256,
        };
        JsonProperty[] properties = probes.EnumerateObject().ToArray();
        if (properties.Length != expected.Count)
        {
            throw new ViiperNativeMetadataException(
                "The live-probe manifest must name exactly the input and media observers.");
        }
        foreach (JsonProperty property in properties)
        {
            if (!expected.TryGetValue(property.Name,
                    out string? expectedHash) ||
                property.Value.ValueKind != JsonValueKind.String ||
                !string.Equals(property.Value.GetString(), expectedHash,
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"Live-probe manifest entry '{property.Name}' is not an exact source-bound observer hash.");
            }
        }
    }

    private static void ValidateRequiredArtifactNames(
        IReadOnlyList<FileBindingEvidence> artifacts)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["broker"] = "viiper.exe",
            ["driver-helper"] = "ViiperUdeCtl.exe",
            ["input-probe"] = "ViiperUdeInputProbe.exe",
            ["media-probe"] = "ViiperUdeMediaProbe.exe",
            ["live-probe-manifest"] =
                "ViiperUdeLiveProbes.manifest.json",
        };
        foreach ((string role, string fileName) in expected)
        {
            FileBindingEvidence binding = artifacts.Single(candidate =>
                candidate.Role == role);
            if (!string.Equals(Path.GetFileName(binding.Path), fileName,
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"Source-bound artifact '{role}' must retain the exact packaged name '{fileName}'.");
            }
        }
    }

    private static void ValidateProductionHandlerContracts(
        ViiperNativeRuntimeMetadata metadata)
    {
        foreach (ControllerSpec spec in ControllerSpec.All)
        {
            ViiperNativeControllerRegistration registration =
                metadata.ControllerApiContract.TryGetValue(spec.Handler,
                    out ViiperNativeControllerRegistration? value) ? value :
                    throw new ViiperIdentityException(
                        $"Metadata does not register production handler '{spec.Handler}'.");
            if (!string.Equals(registration.DefaultVid, spec.Vid,
                    StringComparison.Ordinal) ||
                !string.Equals(registration.Ds4WindowsPid, spec.Pid,
                    StringComparison.Ordinal) ||
                !string.Equals(registration.InterfaceProfile,
                    "hid-audio-duplex", StringComparison.Ordinal) ||
                !string.Equals(registration.StreamProtocol,
                    spec.StreamProtocol, StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"Metadata handler '{spec.Handler}' is not the exact DS4Windows HID/audio contract.");
            }
        }
    }

    internal static FileBindingEvidence BindFile(string role, string path)
    {
        string exact = RequireRegularFile(path, role);
        var info = new FileInfo(exact);
        return new FileBindingEvidence
        {
            Role = role,
            Path = exact,
            Length = info.Length,
            Sha256 = Sha256File(exact),
            ExactMatch = true,
        };
    }

    internal static string RequireRegularFile(string path, string role)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"Required {role} file is missing.", full);
        }
        FileAttributes attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory |
                FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException(
                $"Required {role} file is not a regular non-reparse file: '{full}'.");
        }
        return full;
    }

    internal static void RejectReparseAncestors(string path, string root)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        DirectoryInfo? cursor = new FileInfo(path).Directory;
        while (cursor != null && cursor.FullName.Length >=
            canonicalRoot.Length)
        {
            if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Source-bound artifact traverses reparse directory '{cursor.FullName}'.");
            }
            if (string.Equals(cursor.FullName, canonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            cursor = cursor.Parent;
        }
        throw new IOException(
            $"Source-bound artifact '{path}' is not beneath '{canonicalRoot}'.");
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement node) ||
            node.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(node.GetString()))
        {
            throw new ViiperNativeMetadataException(
                $"Artifact field '{name}' must be a non-empty string.");
        }
        string value = node.GetString()!;
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ViiperNativeMetadataException(
                $"Artifact field '{name}' is not canonical.");
        }
        return value;
    }
}

internal sealed record ControllerSpec(string Name, string Handler,
    string Vid, string Pid, OutContType OutputType,
    ViiperVirtualDeviceType VirtualType, string StreamProtocol,
    byte StreamVersion, int MarkerOffset, bool MarkerUsesRightStick,
    string FeedbackKind, string MediaKind, int MicrophonePcmLength)
{
    internal static IReadOnlyList<ControllerSpec> All { get; } =
        new[]
        {
            new ControllerSpec("DualShock4", "dualshock4audioduplexv3",
                "0x054c", "0x05c4", OutContType.ViiperDS4,
                ViiperVirtualDeviceType.DualShock4, "framed-v3", 0x03, 1,
                false, "dualshock4", "dualshock4", 160 * sizeof(short)),
            new ControllerSpec("DualSense",
                "dualsensecombinedaudioduplexv5", "0x054c", "0x0ce6",
                OutContType.ViiperDualSense,
                ViiperVirtualDeviceType.DualSense, "framed-v5", 0x05, 1,
                false, "dualsense", "dualsense", 480 * 2 * sizeof(short)),
            new ControllerSpec("DualSenseEdge",
                "dualsenseedgecombinedaudioduplexv5", "0x054c", "0x0df2",
                OutContType.ViiperDualSenseEdge,
                ViiperVirtualDeviceType.DualSenseEdge, "framed-v5", 0x05, 3,
                true, "dualsense-edge", "dualsenseedge",
                480 * 2 * sizeof(short)),
        };
}
