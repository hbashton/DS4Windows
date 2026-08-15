using System.Globalization;

namespace DS4Windows.ViiperLiveValidation;

internal sealed class LiveValidationOptions
{
    internal const int DefaultSamples = 256;
    internal const int DefaultMediaSeconds = 10;
    internal const int MinimumSamples = 32;
    internal const int MaximumSamples = 512;
    internal const int MinimumMediaSeconds = 1;
    internal const int MaximumMediaSeconds = 300;

    internal required string Nonce { get; init; }
    internal required string OutputPath { get; init; }
    internal required string MetadataPath { get; init; }
    internal required string ArtifactRoot { get; init; }
    internal int Samples { get; init; } = DefaultSamples;
    internal int MediaSeconds { get; init; } = DefaultMediaSeconds;

    internal static LiveValidationOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length ||
                !args[index].StartsWith("--", StringComparison.Ordinal) ||
                !AllowedOptions.Contains(args[index]) ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException(Usage);
            }
        }

        if (!values.TryGetValue("--nonce", out string? nonce) ||
            string.IsNullOrEmpty(nonce))
        {
            throw new ArgumentException(
                "--nonce is required. " + Usage);
        }

        string metadata = values.TryGetValue("--metadata", out string? rawMetadata) ?
            Path.GetFullPath(rawMetadata) : FindDefaultMetadata();
        string artifactRoot = values.TryGetValue("--artifact-root",
            out string? rawArtifactRoot) ? Path.GetFullPath(rawArtifactRoot) :
            Path.GetDirectoryName(metadata) ?? AppContext.BaseDirectory;
        string output = values.TryGetValue("--output", out string? rawOutput) ?
            Path.GetFullPath(rawOutput) : DefaultOutputPath;

        int samples = ParseBoundedInteger(values, "--samples",
            DefaultSamples, MinimumSamples, MaximumSamples);
        int mediaSeconds = ParseBoundedInteger(values, "--media-seconds",
            DefaultMediaSeconds, MinimumMediaSeconds, MaximumMediaSeconds);

        return new LiveValidationOptions
        {
            Nonce = nonce,
            OutputPath = output,
            MetadataPath = metadata,
            ArtifactRoot = artifactRoot,
            Samples = samples,
            MediaSeconds = mediaSeconds,
        };
    }

    internal static string PreExtractOutputPath(string[]? args)
    {
        if (args != null)
        {
            for (int index = 0; index + 1 < args.Length; index += 2)
            {
                if (string.Equals(args[index], "--output",
                        StringComparison.Ordinal))
                {
                    try
                    {
                        return Path.GetFullPath(args[index + 1]);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }
        return DefaultOutputPath;
    }

    private static int ParseBoundedInteger(
        IReadOnlyDictionary<string, string> values, string name,
        int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(name, out string? raw))
        {
            return fallback;
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture,
                out int parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException(
                $"{name} must be an integer from {minimum} through {maximum}.");
        }
        return parsed;
    }

    private static string FindDefaultMetadata()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "extras",
                ViiperNativeRuntimeMetadata.FileName),
            Path.Combine(AppContext.BaseDirectory,
                ViiperNativeRuntimeMetadata.FileName),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string DefaultOutputPath => Path.GetFullPath(
        Path.Combine(Environment.CurrentDirectory,
            "viiper-live-validation.evidence.json"));

    private static readonly HashSet<string> AllowedOptions =
        new(StringComparer.Ordinal)
        {
            "--nonce", "--output", "--metadata", "--artifact-root",
            "--samples", "--media-seconds",
        };

    internal const string Usage =
        "Usage: DS4Windows.ViiperLiveValidation.exe --nonce <64-lower-hex> " +
        "[--output <json>] [--metadata <json>] [--artifact-root <dir>] " +
        "[--samples 32..512] [--media-seconds 1..300]";
}
