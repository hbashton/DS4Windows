using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS4Windows.ViiperLiveValidation;

internal static class EvidenceWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static string Serialize(EvidenceDocument evidence)
    {
        string json = JsonSerializer.Serialize(evidence, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) + 1 >
            EvidenceLimits.MaximumJsonBytes)
        {
            throw new InvalidOperationException(
                "Finalized live-validation evidence exceeded its 2 MiB bound.");
        }
        return json;
    }

    internal static async Task WriteFinalAsync(EvidenceDocument evidence,
        EvidenceOutputReservation? output)
    {
        string? resolvedOutputPath = output?.Path;
        evidence.OutputPath = resolvedOutputPath ??
            EvidenceLimits.Truncate(evidence.OutputPath, 4096);
        if (resolvedOutputPath != null &&
            IsSourceBoundInput(evidence, resolvedOutputPath))
        {
            evidence.Status = "failure";
            evidence.CurrentStage = "finalize-evidence-path";
            evidence.RecordFailure(new IOException(
                "The evidence output path collides with a source-bound executable, assembly, metadata file, or package artifact."));
            resolvedOutputPath = null;
        }
        evidence.EndedUtc = DateTimeOffset.UtcNow.ToString("O");
        evidence.Finalized = true;
        string json = SerializeForFinalization(evidence);
        byte[] finalizedBytes = EncodeFinalizedJson(json);
        if (output != null && resolvedOutputPath != null)
        {
            try
            {
                await output.WriteOnceAsync(finalizedBytes)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                evidence.Status = "failure";
                evidence.CurrentStage = "finalize-evidence-file";
                evidence.RecordFailure(error);
                evidence.EndedUtc = DateTimeOffset.UtcNow.ToString("O");
                evidence.Finalized = true;
                json = SerializeForFinalization(evidence);
                finalizedBytes = EncodeFinalizedJson(json);
            }
        }

        Stream stdout = Console.OpenStandardOutput();
        await stdout.WriteAsync(finalizedBytes).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);
    }

    internal static byte[] EncodeFinalizedJson(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        if (bytes.Length > EvidenceLimits.MaximumJsonBytes)
        {
            throw new InvalidOperationException(
                "Finalized live-validation evidence exceeded its 2 MiB byte bound.");
        }
        return bytes;
    }

    private static string SerializeForFinalization(
        EvidenceDocument evidence)
    {
        try
        {
            return Serialize(evidence);
        }
        catch (Exception error)
        {
            evidence.Status = "failure";
            evidence.CurrentStage = "bound-evidence";
            evidence.RecordFailure(error);
            CompactFailureEvidence(evidence);
            try
            {
                return Serialize(evidence);
            }
            catch (Exception compactError)
            {
                evidence.RecordFailure(compactError);
                ReduceToMinimumFailureEvidence(evidence);
                return Serialize(evidence);
            }
        }
    }

    private static void CompactFailureEvidence(EvidenceDocument evidence)
    {
        if (evidence.Controllers.Count > 3)
        {
            evidence.Controllers.RemoveRange(3,
                evidence.Controllers.Count - 3);
        }
        foreach (ControllerEvidence controller in evidence.Controllers)
        {
            CompactInput(controller.Input);
            CompactInput(controller.Reconnect?.PostReconnectInput);
            if (controller.Failures.Count > 4)
            {
                controller.Failures.RemoveRange(4,
                    controller.Failures.Count - 4);
            }
            foreach (FailureEvidence failure in controller.Failures)
            {
                failure.Detail = EvidenceLimits.Truncate(failure.Detail, 4096);
            }
        }
        if (evidence.Failures.Count > 4)
        {
            evidence.Failures.RemoveRange(4, evidence.Failures.Count - 4);
        }
        foreach (FailureEvidence failure in evidence.Failures)
        {
            failure.Detail = EvidenceLimits.Truncate(failure.Detail, 4096);
        }
    }

    private static void CompactInput(InputEvidence? input)
    {
        if (input?.Samples.Count > 64)
        {
            input.Samples.RemoveRange(64, input.Samples.Count - 64);
        }
    }

    private static void ReduceToMinimumFailureEvidence(
        EvidenceDocument evidence)
    {
        evidence.Bindings = null;
        evidence.Controllers.Clear();
        evidence.OutputPath = EvidenceLimits.Truncate(evidence.OutputPath,
            4096);
        if (evidence.Failures.Count > 2)
        {
            evidence.Failures.RemoveRange(2, evidence.Failures.Count - 2);
        }
        foreach (FailureEvidence failure in evidence.Failures)
        {
            failure.Stage = EvidenceLimits.Truncate(failure.Stage, 256);
            failure.Type = EvidenceLimits.Truncate(failure.Type, 256);
            failure.Message = EvidenceLimits.Truncate(failure.Message, 2048);
            failure.Detail = EvidenceLimits.Truncate(failure.Detail, 4096);
        }
    }

    private static bool IsSourceBoundInput(EvidenceDocument evidence,
        string outputPath)
    {
        BindingEvidence? bindings = evidence.Bindings;
        if (bindings == null)
        {
            return false;
        }
        IEnumerable<FileBindingEvidence> inputs = bindings.PackageArtifacts
            .Append(bindings.Metadata)
            .Append(bindings.RunnerExecutable)
            .Append(bindings.RunnerAssembly)
            .Append(bindings.Ds4WindowsAssembly);
        return inputs.Any(input => string.Equals(input.Path, outputPath,
            StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Atomically claims a previously nonexistent evidence pathname before
/// consent or source processing. The held handle denies write and delete, so
/// finalization can never replace an existing input or evidence file.
/// </summary>
internal sealed class EvidenceOutputReservation : IDisposable
{
    private readonly FileStream stream;
    private int written;
    private int disposed;

    private EvidenceOutputReservation(string path, FileStream stream)
    {
        Path = path;
        this.stream = stream;
    }

    internal string Path { get; }

    internal static EvidenceOutputReservation Create(string outputPath)
    {
        string full = System.IO.Path.GetFullPath(outputPath);
        string fileName = System.IO.Path.GetFileName(full);
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >=
                0 ||
            !string.Equals(fileName, fileName.TrimEnd(' ', '.'),
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The evidence output filename is not a regular canonical Windows filename.");
        }
        string? directory = System.IO.Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(directory) ||
            !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The evidence output directory must already exist: '{directory}'.");
        }
        RejectReparseDirectoryChain(directory);
        var stream = new FileStream(full, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        return new EvidenceOutputReservation(full, stream);
    }

    internal bool IsFor(string path) => string.Equals(Path,
        System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    internal async Task WriteOnceAsync(ReadOnlyMemory<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (Interlocked.Exchange(ref written, 1) != 0 ||
            stream.Position != 0 || stream.Length != 0)
        {
            throw new IOException(
                "The collision-safe evidence output reservation is no longer empty or was already finalized.");
        }
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            stream.Dispose();
        }
    }

    private static void RejectReparseDirectoryChain(string directory)
    {
        DirectoryInfo? cursor = new DirectoryInfo(directory);
        while (cursor != null)
        {
            if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"The evidence output directory traverses reparse directory '{cursor.FullName}'.");
            }
            cursor = cursor.Parent;
        }
    }
}
