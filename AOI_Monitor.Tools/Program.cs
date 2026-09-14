using AOI_Monitor.Data;

namespace AOI_Monitor.Tools;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var storageRoot = Environment.GetEnvironmentVariable("AOI_MONITOR_STORAGE_ROOT");
        if (!string.IsNullOrWhiteSpace(storageRoot))
            AoiDatabase.ConfigureStorageRoot(storageRoot);

        if (args.Length > 0 && string.Equals(args[0], "camera-adapter-validate", StringComparison.OrdinalIgnoreCase))
            return CameraAdapterValidationCommand.Execute(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "stage2-camera-pilot", StringComparison.OrdinalIgnoreCase))
            return Stage2CameraPilotCommand.Execute(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "import-image-learning-project", StringComparison.OrdinalIgnoreCase))
            return ImageLearningProjectImportCommand.Execute(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "client-image-learning-demo", StringComparison.OrdinalIgnoreCase))
            return ClientImageLearningDemoCommand.Execute(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "learn-from-images", StringComparison.OrdinalIgnoreCase))
            return LearnFromImagesCommand.Execute(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "batch-soak", StringComparison.OrdinalIgnoreCase))
            return await BatchSoakCommand.ExecuteAsync(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "benchmark", StringComparison.OrdinalIgnoreCase))
            return BenchmarkCommand.Execute(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "prepare-dataset", StringComparison.OrdinalIgnoreCase))
            return PrepareDatasetCommand.Execute(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "stage1-readiness", StringComparison.OrdinalIgnoreCase))
            return Stage1ReadinessCommand.Execute(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "record-build-evidence", StringComparison.OrdinalIgnoreCase))
            return RecordBuildEvidenceCommand.Execute(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "threshold-profile", StringComparison.OrdinalIgnoreCase))
            return ThresholdProfileCommand.Execute(args, Console.Out, Console.Error);

        return Stage1ExitCommand.Execute(args, Console.Out, Console.Error);
    }
}
