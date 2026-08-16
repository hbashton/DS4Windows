namespace DS4Windows.ViiperLiveValidation;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        return await RunAsync(args).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        var evidence = new EvidenceDocument();
        string outputPath = LiveValidationOptions.PreExtractOutputPath(args);
        evidence.OutputPath = outputPath;
        EvidenceOutputReservation? output = null;
        int exitCode = 1;
        try
        {
            evidence.CurrentStage = "evidence-output-reservation";
            output = EvidenceOutputReservation.Create(outputPath);
            evidence.OutputPath = output.Path;
            evidence.CurrentStage = "arguments";
            LiveValidationOptions options = LiveValidationOptions.Parse(args);
            outputPath = options.OutputPath;
            if (!output.IsFor(outputPath))
            {
                throw new ViiperIdentityException(
                    "The parsed evidence path differs from its collision-safe preflight reservation.");
            }
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(35));
            var runner = new LiveValidationRunner(options, evidence,
                timeout.Token);
            await runner.RunAsync().ConfigureAwait(false);
            evidence.Status = "pass";
            exitCode = 0;
        }
        catch (Exception error)
        {
            evidence.Status = "failure";
            evidence.RecordFailure(error);
        }
        finally
        {
            try
            {
                await EvidenceWriter.WriteFinalAsync(evidence, output)
                    .ConfigureAwait(false);
            }
            finally
            {
                output?.Dispose();
            }
            if (!string.Equals(evidence.Status, "pass",
                    StringComparison.Ordinal))
            {
                exitCode = 1;
            }
        }
        return exitCode;
    }
}
