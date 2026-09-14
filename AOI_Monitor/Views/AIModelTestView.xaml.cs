using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class AIModelTestView : UserControl, IAsyncNavigationPage, IDisposable
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    private static readonly HashSet<string> UnsupportedImageLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".tif", ".tiff", ".gif", ".webp",
    };

    private readonly ObservableCollection<BatchTestRow> _rows = new();
    private readonly ObservableCollection<ThresholdSweepPoint> _falseCallPoints = new();
    private readonly ObservableCollection<ValidationBreakdownMetric> _defectClassBreakdown = new();
    private readonly ObservableCollection<ValidationBreakdownMetric> _sideBreakdown = new();
    private readonly ObservableCollection<ValidationBreakdownMetric> _roiBreakdown = new();
    private readonly ObservableCollection<ValidationBreakdownMetric> _falseCallSources = new();
    private readonly ObservableCollection<ValidationBreakdownMetric> _escapeSources = new();
    private string? _selectedFolder;
    private string? _groundTruthCsvPath;
    private long? _currentRunId;
    private DateTime? _currentRunCreatedAtUtc;
    private string _currentEngineDisplay = $"Pixel Difference Prototype Engine / {PixelDifferenceInspectionEngine.EngineVersion}";
    private bool _currentRunUsedFormalManifest;
    private FalseCallReductionRun? _currentFalseCallRun;
    private DatasetQualitySummary? _currentDatasetQuality;
    private CustomerDatasetPreflightResult? _currentPreflightResult;
    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _refreshCts;

    public AIModelTestView()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
        FalseCallGrid.ItemsSource = _falseCallPoints;
        DefectClassBreakdownGrid.ItemsSource = _defectClassBreakdown;
        SideBreakdownGrid.ItemsSource = _sideBreakdown;
        RoiBreakdownGrid.ItemsSource = _roiBreakdown;
        FalseCallSourcesGrid.ItemsSource = _falseCallSources;
        EscapeSourcesGrid.ItemsSource = _escapeSources;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        Unloaded += (_, _) => InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
        RefreshEngineText();
    }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _refreshCts.Token;
        RefreshEngineText();
        AiTrainingSetupPanel.RefreshFromState();
        await LoadLatestRunAsync(token);
    }

    public void RefreshFromState()
    {
        _ = RefreshAsync(CancellationToken.None);
    }

    public void ExportResults()
    {
        OnExportCsvClick(this, new RoutedEventArgs());
    }

    private void OnSelectFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Stage 1 validation image folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        _selectedFolder = dialog.FolderName;
        FolderPathText.Text = _selectedFolder;
        StatusText.Text = $"Selected folder: {Path.GetFileName(_selectedFolder)}";
    }

    private void OnSelectGroundTruthClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select validation manifest or ground-truth CSV",
            Filter = "CSV files|*.csv|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        _groundTruthCsvPath = dialog.FileName;
        GroundTruthPathText.Text = _groundTruthCsvPath;
        StatusText.Text = $"Loaded validation CSV: {Path.GetFileName(_groundTruthCsvPath)}";
    }

    private void OnRunDatasetPreflightClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedFolder) || !Directory.Exists(_selectedFolder))
        {
            MessageBox.Show("Select a valid customer dataset image folder first.", "Dataset Preflight", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_groundTruthCsvPath) || !File.Exists(_groundTruthCsvPath))
        {
            MessageBox.Show("Select the customer validation manifest CSV first.", "Dataset Preflight", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _currentPreflightResult = CustomerDatasetPreflightService.Validate(_selectedFolder, _groundTruthCsvPath);
            ApplyPreflightResult(_currentPreflightResult);
            WorkflowState.Instance.AddEvent("DATASET_PREFLIGHT", $"Customer dataset preflight {_currentPreflightResult.Status}: rows={_currentPreflightResult.ManifestRows}; failures={_currentPreflightResult.BlockingFailures.Count}; warnings={_currentPreflightResult.Warnings.Count}.");
            var issues = _currentPreflightResult.BlockingFailures.Concat(_currentPreflightResult.Warnings).Take(8).ToArray();
            MessageBox.Show(
                issues.Length == 0
                    ? "Dataset preflight PASS. Dataset is ready for Stage 1 validation execution."
                    : $"Dataset preflight {_currentPreflightResult.Status}:{Environment.NewLine}{string.Join(Environment.NewLine, issues)}",
                "Dataset Preflight",
                MessageBoxButton.OK,
                _currentPreflightResult.Status == "FAIL" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = $"Dataset preflight failed: {ex.Message}";
            WorkflowState.Instance.AddEvent("DATASET_PREFLIGHT_ERROR", StatusText.Text);
            MessageBox.Show(StatusText.Text, "Dataset Preflight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenManifestTemplateClick(object sender, RoutedEventArgs e)
    {
        var templatePath = FindManifestTemplatePath();
        if (templatePath is null)
        {
            MessageBox.Show(
                "The manifest template was not found. Expected SampleData/customer_validation_manifest_template.csv.",
                "Dataset Preflight",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(templatePath) { UseShellExecute = true });
            WorkflowState.Instance.AddEvent("DATASET_PREFLIGHT", $"Opened customer validation manifest template: {templatePath}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show($"Could not open manifest template:\n{ex.Message}", "Dataset Preflight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRunBatchClick(object sender, RoutedEventArgs e)
    {
        // Operators may view the AI Model Test page, but running batch validation is an Engineer/Admin
        // action (roles spec RP-001/RP-002 and RTM AI-001).
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanRunModelTests, "Running AI Model Test batch validation", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_workCts is not null)
        {
            MessageBox.Show("A validation/export task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedFolder) || !Directory.Exists(_selectedFolder))
        {
            MessageBox.Show("Select a valid test image folder first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cts = BeginWork("Preparing validation batch...");
        var progress = new Progress<WorkProgress>(UpdateProgress);

        try
        {
            var batch = await Task.Run(() =>
            {
                var errors = new List<string>();
                string[] folderFiles;
                try
                {
                    folderFiles = Directory.EnumerateFiles(_selectedFolder, "*.*", SearchOption.TopDirectoryOnly)
                        .OrderBy(Path.GetFileName)
                        .ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var message = $"Validation image folder cannot be read: {ex.Message}";
                    errors.Add(message);
                    return BatchRunOutcome.Empty(message, errors);
                }

                foreach (var skipped in folderFiles.Where(path => UnsupportedImageLikeExtensions.Contains(Path.GetExtension(path))))
                    errors.Add($"Unsupported image format skipped: {Path.GetFileName(skipped)}. Use PNG/JPG/JPEG for Stage 1 validation.");

                var imageFiles = folderFiles
                    .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
                    .ToArray();

                if (imageFiles.Length == 0)
                    return BatchRunOutcome.Empty("The selected folder does not contain PNG/JPG/JPEG images.", errors);

                ValidationManifest manifest;
                try
                {
                    manifest = BatchValidationService.LoadValidationManifest(_groundTruthCsvPath, _selectedFolder);
                    _currentPreflightResult = CustomerDatasetPreflightService.Validate(_selectedFolder, _groundTruthCsvPath ?? string.Empty);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    errors.Add($"Invalid ground-truth CSV: {ex.Message}");
                    manifest = new ValidationManifest(new Dictionary<string, GroundTruthEntry>(), new List<GroundTruthEntry>(), false, Array.Empty<string>());
                }
                errors.AddRange(manifest.Warnings);

                var engine = InspectionEngineFactory.Create();
                var engineDisplay = $"{engine.Name} / {engine.Version}";
                var runItems = BatchValidationService.BuildRunItems(imageFiles, manifest);
                var rows = new List<BatchTestRow>();

                for (var i = 0; i < runItems.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var item = runItems[i];
                    ((IProgress<WorkProgress>)progress).Report(new WorkProgress(i, runItems.Count, $"Inspecting {Path.GetFileName(item.ImagePath)}..."));

                    try
                    {
                        if (!File.Exists(item.ImagePath))
                        {
                            var message = $"Missing image file: {item.ImagePath}";
                            errors.Add(message);
                            rows.Add(BatchValidationService.ToErrorRow(item.ImagePath, item.Manifest, message, engine.Name, engine.Version));
                            continue;
                        }

                        var analysis = engine.Analyze(
                            item.ImagePath,
                            string.IsNullOrWhiteSpace(item.Manifest.GoldenPath) ? null : item.Manifest.GoldenPath,
                            WorkflowState.Instance.DetectionPriority);
                        var analysisEndUtc = DateTime.UtcNow;

                        if (analysis.Timing.IsOverOneSecond)
                            errors.Add($"Performance warning: {Path.GetFileName(item.ImagePath)} took {analysis.Timing.TotalInspectionMilliseconds:F0} ms, above the 1 second target.");

                        rows.Add(BatchValidationService.ToRow(item.ImagePath, item.Manifest, analysis));
                        try
                        {
                            var (width, height) = ReadImageDimensions(item.ImagePath);
                            var trace = InspectionLatencyService.StartTrace(
                                "BatchValidation",
                                engine.Name,
                                engine.Version,
                                width,
                                height,
                                File.GetLastWriteTimeUtc(item.ImagePath));
                            PopulateLatencyTrace(trace, analysis.Timing, analysisEndUtc.AddMilliseconds(-Math.Max(0, analysis.Timing.TotalInspectionMilliseconds)), analysisEndUtc);
                            trace.Trace.Verdict = analysis.Verdict;
                            InspectionLatencyService.Persist(trace, saved: false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                        {
                            errors.Add($"Latency trace warning for {Path.GetFileName(item.ImagePath)}: {ex.Message}");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
                    {
                        var message = FriendlyFileError(item.ImagePath, ex);
                        errors.Add(message);
                        rows.Add(BatchValidationService.ToErrorRow(item.ImagePath, item.Manifest, message, engine.Name, engine.Version));
                    }
                }

                var metrics = BatchValidationService.CalculateMetrics(rows);
                long runId;
                try
                {
                    runId = AoiDatabase.RecordBatchTestRun(
                        _selectedFolder,
                        _groundTruthCsvPath,
                        engine.Name,
                        engine.Version,
                        metrics.Accuracy,
                        metrics.Precision,
                        metrics.Recall,
                        metrics.FalseCallRate,
                        rows.Select(r => r.ToRecord()).ToArray(),
                        rows.Select(r => r.ThresholdProfileId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? string.Empty,
                        rows.Select(r => r.ThresholdProfileRevision).FirstOrDefault(revision => !string.IsNullOrWhiteSpace(revision)) ?? string.Empty);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    errors.Add($"Database write failure: {ex.Message}");
                    runId = 0;
                }

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(runItems.Count, runItems.Count, "Validation batch complete."));
                return new BatchRunOutcome(rows, metrics, manifest.IsFormalManifest, engineDisplay, runId, errors, null);
            }, cts.Token);

            if (batch.Message is not null)
            {
                StatusText.Text = batch.Message;
                foreach (var error in batch.Errors.Take(20))
                    WorkflowState.Instance.AddEvent("MODEL_TEST_ERROR", error);

                MessageBox.Show(batch.Message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentRunId = batch.RunId > 0 ? batch.RunId : null;
            _currentRunCreatedAtUtc = DateTime.UtcNow;
            _currentEngineDisplay = batch.EngineDisplay;
            _currentRunUsedFormalManifest = batch.IsFormalManifest;
            _currentFalseCallRun = null;
            _falseCallPoints.Clear();
            FalseCallRecommendationText.Text = "Batch complete. Analyze false calls to generate threshold candidates.";
            CreateThresholdProfileDraftButton.IsEnabled = false;
            ApplyRecommendedThresholdButton.IsEnabled = false;

            _rows.Clear();
            foreach (var row in batch.Rows)
                _rows.Add(row);
            _currentDatasetQuality = DatasetQualityService.Analyze(_rows, TryLoadManifest(_groundTruthCsvPath, _selectedFolder ?? string.Empty));

            ApplyMetrics(batch.Metrics);
            RunSummaryText.Text = $"{batch.Rows.Count} images / {batch.Rows.Count(r => r.IsFailed)} failed / run {(_currentRunId?.ToString(CultureInfo.InvariantCulture) ?? "not saved")}";
            var performance = BatchValidationService.CalculatePerformanceSummary(batch.Rows);
            var performanceSuffix = performance.CountOverOneSecond > 0
                ? $" Performance warning: {performance.CountOverOneSecond} image(s) exceeded 1 second."
                : string.Empty;
            ApplyDatasetQuality(_currentDatasetQuality);
            if (_currentPreflightResult is not null)
                ApplyPreflightResult(_currentPreflightResult);
            var qualitySuffix = _currentDatasetQuality.Status == "FAIL"
                ? " Dataset not sufficient for factory acceptance."
                : string.Empty;
            StatusText.Text = (batch.IsFormalManifest
                ? $"Formal manifest validation complete. {_rows.Count} row(s), {batch.Errors.Count} issue(s)."
                : $"Batch inspection complete. {_rows.Count} row(s), {batch.Errors.Count} issue(s).") + performanceSuffix + qualitySuffix;
            ReportPathText.Text = "Report: not generated";
            WorkflowState.Instance.AddEvent("MODEL_TEST", $"Stage 1 validation run {_currentRunId?.ToString(CultureInfo.InvariantCulture) ?? "not saved"}: {_rows.Count} images, {_rows.Count(r => r.IsFailed)} failed, {batch.Errors.Count} issue(s).");
            if (performance.CountOverOneSecond > 0)
                WorkflowState.Instance.AddEvent("PERFORMANCE_WARNING", $"Stage 1 validation run had {performance.CountOverOneSecond} image(s) over 1 second. Max={performance.MaxMilliseconds:F0} ms.");
            foreach (var error in batch.Errors.Take(20))
                WorkflowState.Instance.AddEvent("MODEL_TEST_ERROR", error);

            if (batch.Errors.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, batch.Errors.Take(8)), "Validation completed with skipped rows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Validation batch canceled.";
            WorkflowState.Instance.AddEvent("MODEL_TEST", "Stage 1 validation batch canceled by user.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Validation batch failed. The app is still usable.";
            WorkflowState.Instance.AddEvent("MODEL_TEST_ERROR", $"Validation batch failed: {ex.Message}");
            MessageBox.Show($"Validation batch failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndWork();
        }
    }

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is BatchTestRow row)
            StatusText.Text = $"{row.Image}: {row.EngineResult}, score {row.ScoreDisplay}, {row.PassFail}, time {row.TotalTimeDisplay}.";
    }

    private void OnInspectionConfigurationChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshEngineText);

    private void RefreshEngineText()
    {
        var engine = InspectionEngineFactory.Create();
        var status = InspectionModelConfigurationService.GetStatusText();
        EngineText.Text = $"{engine.Name} / {engine.Version} / {status}";
    }

    private void OnPreviewSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not BatchTestRow row)
        {
            MessageBox.Show("Select a result row first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PreviewRow(row);
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show("No batch results are available to export.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Stage 1 test results",
            Filter = "CSV file|*.csv",
            FileName = $"stage1_validation_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, BatchValidationService.BuildResultsCsv(_rows), Encoding.UTF8);
            var verified = ExportVerificationService.RecordVerifiedExport("Stage1ValidationCsv", dialog.FileName);
            StatusText.Text = $"CSV exported: {dialog.FileName}. Verification: {verified.Verification.Status}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var message = ex is UnauthorizedAccessException
                ? "CSV export failed: export folder access was denied or the file is locked."
                : $"CSV export failed: {ex.Message}";
            StatusText.Text = message;
            WorkflowState.Instance.AddEvent("EXPORT_ERROR", message);
            MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnExportAnnotatedImagesClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("A validation/export task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_rows.Count == 0)
        {
            MessageBox.Show("No batch results are available to export.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select folder for annotated validation images",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var cts = BeginWork("Exporting annotated images...");
        var progress = new Progress<WorkProgress>(UpdateProgress);
        try
        {
            var rows = _rows.ToArray();
            var result = await Task.Run(() =>
            {
                var errors = new List<string>();
                var exported = 0;
                Directory.CreateDirectory(dialog.FolderName);

                for (var i = 0; i < rows.Length; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var row = rows[i];
                    ((IProgress<WorkProgress>)progress).Report(new WorkProgress(i, rows.Length, $"Exporting {row.Image}..."));

                    try
                    {
                        if (!File.Exists(row.ImagePath))
                        {
                            errors.Add($"Missing image file: {row.ImagePath}");
                            continue;
                        }

                        var annotated = CreateAnnotatedBitmap(row);
                        var target = Path.Combine(
                            dialog.FolderName,
                            $"{Path.GetFileNameWithoutExtension(row.Image)}_{row.PassFail.ToLowerInvariant()}_overlay.png");

                        SavePng(annotated, target);
                        exported++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
                    {
                        errors.Add(FriendlyFileError(row.ImagePath, ex));
                    }
                }

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(rows.Length, rows.Length, "Annotated image export complete."));
                return new ExportOutcome(exported, errors);
            }, cts.Token);

            var verified = ExportVerificationService.RecordVerifiedExport(
                "Stage1AnnotatedImages",
                dialog.FolderName,
                result.Errors.Count == 0 ? "OK" : "WARN");
            StatusText.Text = $"Annotated image export complete: {result.Count} file(s), {result.Errors.Count} issue(s). Verification: {verified.Verification.Status}.";
            WorkflowState.Instance.AddEvent("EXPORT", $"Stage 1 annotated images exported: {result.Count} file(s), {result.Errors.Count} issue(s).");
            foreach (var error in result.Errors.Take(20))
                WorkflowState.Instance.AddEvent("EXPORT_ERROR", error);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Annotated image export canceled.";
            WorkflowState.Instance.AddEvent("EXPORT", "Stage 1 annotated image export canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "Annotated image export failed. Check export folder permissions.";
            WorkflowState.Instance.AddEvent("EXPORT_ERROR", $"Stage 1 annotated export failed: {ex.Message}");
            MessageBox.Show($"Annotated image export failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndWork();
        }
    }

    private void OnCancelWorkClick(object sender, RoutedEventArgs e)
    {
        CancelWork();
        StatusText.Text = "Cancel requested. Finishing current file...";
    }

    public void CancelWork()
    {
        _workCts?.Cancel();
        _refreshCts?.Cancel();
    }

    private void OnAnalyzeFalseCallsClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show("Run or load a validation batch before analyzing false calls.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var configuration = InspectionModelConfigurationService.Load();
        var criteria = FalseCallReductionService.CreateSweepCriteria(GetSelectedFalseCallMode());

        _currentFalseCallRun = FalseCallReductionService.AnalyzeAndPersist(
            _rows.ToArray(),
            GetEngineNamePart(),
            GetEngineVersionPart(),
            configuration.ActiveModelId,
            configuration.ActiveModelSha256,
            _currentRunId,
            criteria,
            WorkflowState.Instance.OperatorWithRole);

        _falseCallPoints.Clear();
        foreach (var point in _currentFalseCallRun.Points.OrderBy(point => point.ConfidenceThreshold))
            _falseCallPoints.Add(point);

        var selected = _currentFalseCallRun.Recommendation.Point;
        FalseCallRecommendationText.Text = selected is null
            ? $"{_currentFalseCallRun.Recommendation.Status}: {string.Join(" ", _currentFalseCallRun.Recommendation.Messages.Take(2))}"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{_currentFalseCallRun.Recommendation.Status}: mode {_currentFalseCallRun.Recommendation.Mode}, threshold {selected.ConfidenceThreshold:F3}, false call {selected.FalseCallRate:P1}, possible escapes {selected.FalseNegative}, review burden {selected.EstimatedManualReviewMinutes:F1} min.");
        ApplyRecommendedThresholdButton.IsEnabled = string.Equals(_currentFalseCallRun.Recommendation.Status, "VALID", StringComparison.OrdinalIgnoreCase) &&
            RoleAuthorization.CanChangeThresholds(WorkflowState.Instance.CurrentRole);
        CreateThresholdProfileDraftButton.IsEnabled = _currentFalseCallRun.Recommendation.Point is not null &&
            !string.Equals(_currentFalseCallRun.Recommendation.Status, "INVALID", StringComparison.OrdinalIgnoreCase) &&
            RoleAuthorization.CanChangeThresholds(WorkflowState.Instance.CurrentRole);
        StatusText.Text = $"False-call analysis generated for run {_currentRunId?.ToString(CultureInfo.InvariantCulture) ?? "unsaved"}: {_currentFalseCallRun.Recommendation.Status}.";
    }

    private void OnCreateThresholdProfileDraftClick(object sender, RoutedEventArgs e)
    {
        if (_currentFalseCallRun?.Recommendation.Point is null)
        {
            MessageBox.Show("Analyze false calls before creating a threshold profile draft.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanChangeThresholds, "Creating threshold profile drafts", out var message))
        {
            MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var state = WorkflowState.Instance;
            var boardModel = CustomerValidationReportService.SummarizeDistinct(_rows.Select(row => row.BoardModel));
            if (string.Equals(boardModel, "Not provided", StringComparison.OrdinalIgnoreCase))
                boardModel = state.BoardProgram;
            var recipe = RecipeService.LoadLatestRecipe(state.BoardProgram).Recipe;
            var profile = ThresholdProfileService.CreateDraftFromFalseCallReductionRecommendation(
                _currentFalseCallRun,
                boardModel,
                state.BoardProgram,
                recipe?.RecipeName ?? "ANY",
                recipe?.Revision ?? "ANY",
                state.OperatorWithRole);

            StatusText.Text = $"Threshold profile draft created: {profile.ProfileId}/{profile.Revision}. Approve and deploy it from Settings.";
            FalseCallRecommendationText.Text += $" Draft profile {profile.ProfileId}/{profile.Revision} created.";
            WorkflowState.Instance.AddEvent("THRESHOLD_PROFILE_DRAFT", $"Draft profile created from false-call run {_currentFalseCallRun.Id}: {profile.ProfileId}/{profile.Revision}.");
            MessageBox.Show(
                $"Threshold profile draft created:\n{profile.ProfileId} / {profile.Revision}\n\nApprove and deploy it from Settings > Threshold Profiles.",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnApplyRecommendedThresholdClick(object sender, RoutedEventArgs e)
    {
        if (_currentFalseCallRun?.Recommendation.Point is not { } point)
        {
            MessageBox.Show("Analyze false calls before applying a threshold recommendation.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanChangeThresholds, "Applying false-call threshold recommendations", out var message))
        {
            MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(_currentFalseCallRun.Recommendation.Status, "VALID", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Only VALID recommendations can be applied. Review the recommendation limitations first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Apply confidence threshold {point.ConfidenceThreshold:F3}?\n\nThis updates the current model configuration based on Stage 1 labeled-data evidence only. It does not prove production accuracy.",
            "Confirm Threshold Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        FalseCallReductionService.ApplyRecommendedThreshold(_currentFalseCallRun, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
        RefreshEngineText();
        StatusText.Text = $"Applied recommended threshold {point.ConfidenceThreshold:F3}.";
        FalseCallRecommendationText.Text += " Applied to current model configuration.";
    }

    private void OnGenerateReportClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0 || _currentRunId is null)
        {
            MessageBox.Show("Run a validation batch before exporting the validation package.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var metrics = BatchValidationService.CalculateMetrics(_rows);

        try
        {
            var request = BuildPackageRequest(metrics);
            var package = CustomerValidationPackageService.CreatePackage(request);
            ReportPathText.Text = $"Package: {package.PackageFolder}";
            StatusText.Text = $"Validation package exported: {package.Acceptance.Status} / {package.PackageId}. Manifest: {package.ManifestPath}";
            WorkflowState.Instance.AddEvent("EXPORT", $"Stage 1 validation package exported for run {_currentRunId}: {package.PackageId}; status={package.Acceptance.Status}.");
            foreach (var warning in package.Warnings.Take(20))
                WorkflowState.Instance.AddEvent("EXPORT_WARNING", warning);

            MessageBox.Show(
                $"Validation package exported.\n\nStatus: {package.Acceptance.Status}\nPackage ID: {package.PackageId}\nFolder: {package.PackageFolder}",
                "AOI Monitor",
                MessageBoxButton.OK,
                package.Acceptance.Status == "FAIL" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var message = ex is UnauthorizedAccessException
                ? "Validation package export failed: export folder access was denied or the file is locked."
                : $"Validation package export failed: {ex.Message}";
            StatusText.Text = message;
            WorkflowState.Instance.AddEvent("EXPORT_ERROR", message);
            MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private CustomerValidationPackageRequest BuildPackageRequest(BatchMetrics metrics)
    {
        var configuration = InspectionModelConfigurationService.Load();
        var state = WorkflowState.Instance;
        var boardModel = CustomerValidationReportService.SummarizeDistinct(_rows.Select(row => row.BoardModel));
        if (string.Equals(boardModel, "Not provided", StringComparison.OrdinalIgnoreCase))
            boardModel = state.BoardProgram;

        return new CustomerValidationPackageRequest
        {
            RunId = _currentRunId,
            RunCreatedAtUtc = _currentRunCreatedAtUtc,
            StationId = state.StationId,
            OperatorId = state.CurrentUser.UserId,
            OperatorRole = state.CurrentRole.ToString(),
            BoardModel = boardModel,
            LotId = CustomerValidationReportService.SummarizeDistinct(_rows.Select(row => row.LotId)),
            ModelId = string.IsNullOrWhiteSpace(configuration.ActiveModelId) ? "Not selected" : configuration.ActiveModelId,
            ModelSha256 = string.IsNullOrWhiteSpace(configuration.ActiveModelSha256) ? "Not available" : configuration.ActiveModelSha256,
            ModelValidationStatus = string.IsNullOrWhiteSpace(configuration.ActiveModelValidationStatus) ? ModelConfigurationTestStatus.NotTested.ToString() : configuration.ActiveModelValidationStatus,
            EngineName = GetEngineNamePart(),
            ModelVersion = GetEngineVersionPart(),
            ModelFileName = string.IsNullOrWhiteSpace(configuration.ModelFilePath)
                ? "Not configured"
                : Path.GetFileName(configuration.ModelFilePath),
            ConfidenceThreshold = configuration.ConfidenceThreshold,
            DatasetFolder = _selectedFolder ?? string.Empty,
            GroundTruthCsvPath = _groundTruthCsvPath ?? string.Empty,
            IsFormalManifest = _currentRunUsedFormalManifest,
            Metrics = metrics,
            PerformanceSummary = BatchValidationService.CalculatePerformanceSummary(_rows),
            Rows = _rows.ToArray(),
            FalseCallReductionRun = _currentFalseCallRun ?? AoiDatabase.GetLatestFalseCallReductionRun(_currentRunId),
            LearnedVisualModel = LearnedVisualModelRegistryService.GetActiveSummary(),
            DatasetQualitySummary = _currentDatasetQuality ?? DatasetQualityService.Analyze(_rows, TryLoadManifest(_groundTruthCsvPath, _selectedFolder ?? string.Empty)),
            DatasetPreflightResult = _currentPreflightResult,
        };
    }

    private async Task LoadLatestRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await Task.Run(() => LoadLatestRunSnapshot(cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot is null)
            {
                StatusText.Text = "No persisted Stage 1 validation run is available.";
                return;
            }

            var run = snapshot.Run;
            _currentRunId = run.Id;
            _currentRunCreatedAtUtc = run.CreatedAtUtc;
            _currentEngineDisplay = $"{run.EngineName} / {run.ModelVersion}";
            _selectedFolder = run.ImageFolder;
            _groundTruthCsvPath = run.GroundTruthCsvPath;
            _currentRunUsedFormalManifest = snapshot.UsedFormalManifest;
            ReportPathText.Text = "Report: not generated";
            FolderPathText.Text = run.ImageFolder;
            GroundTruthPathText.Text = string.IsNullOrWhiteSpace(run.GroundTruthCsvPath)
                ? "No CSV selected"
                : run.GroundTruthCsvPath;

            Replace(_rows, snapshot.Rows);
            ApplyMetrics(snapshot.Metrics);
            _currentDatasetQuality = snapshot.DatasetQuality;
            ApplyDatasetQuality(_currentDatasetQuality);
            RunSummaryText.Text = $"{run.TotalImages} images / {run.FailedCount} failed / run {run.Id}";
            StatusText.Text = $"Loaded latest persisted Stage 1 validation run: {run.Id}.";
            _currentFalseCallRun = snapshot.FalseCallRun;
            _falseCallPoints.Clear();
            if (_currentFalseCallRun is not null)
            {
                foreach (var candidate in _currentFalseCallRun.Points.OrderBy(point => point.ConfidenceThreshold))
                    _falseCallPoints.Add(candidate);

                FalseCallRecommendationText.Text = _currentFalseCallRun.Recommendation.Point is { } selectedPoint
                    ? $"{_currentFalseCallRun.Recommendation.Status}: mode {_currentFalseCallRun.Recommendation.Mode}, threshold {selectedPoint.ConfidenceThreshold:F3}, false call {selectedPoint.FalseCallRate:P1}, possible escapes {selectedPoint.FalseNegative}."
                    : $"{_currentFalseCallRun.Recommendation.Status}: no applied candidate.";
                ApplyRecommendedThresholdButton.IsEnabled = string.Equals(_currentFalseCallRun.Recommendation.Status, "VALID", StringComparison.OrdinalIgnoreCase) &&
                    RoleAuthorization.CanChangeThresholds(WorkflowState.Instance.CurrentRole);
                CreateThresholdProfileDraftButton.IsEnabled = _currentFalseCallRun.Recommendation.Point is not null &&
                    !string.Equals(_currentFalseCallRun.Recommendation.Status, "INVALID", StringComparison.OrdinalIgnoreCase) &&
                    RoleAuthorization.CanChangeThresholds(WorkflowState.Instance.CurrentRole);
            }
            else
            {
                FalseCallRecommendationText.Text = "Batch loaded. Analyze false calls to generate threshold candidates.";
                CreateThresholdProfileDraftButton.IsEnabled = false;
                ApplyRecommendedThresholdButton.IsEnabled = false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static LatestBatchRunSnapshot? LoadLatestRunSnapshot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = AoiDatabase.GetLatestBatchTestRun();
        if (run is null)
            return null;

        var rows = AoiDatabase.GetBatchTestResults(run.Id).Select(BatchTestRow.FromRecord).ToArray();
        var manifest = TryLoadManifest(run.GroundTruthCsvPath, run.ImageFolder);
        var metrics = BatchValidationService.CalculateMetrics(rows);
        var datasetQuality = DatasetQualityService.Analyze(rows, manifest);
        var falseCallRun = AoiDatabase.GetLatestFalseCallReductionRun(run.Id);
        var usedFormalManifest = TryLoadFormalManifestFlag(run.GroundTruthCsvPath, run.ImageFolder);
        return new LatestBatchRunSnapshot(run, rows, metrics, datasetQuality, falseCallRun, usedFormalManifest);
    }

    private FalseCallReductionMode GetSelectedFalseCallMode()
        => FalseCallModeCombo.SelectedIndex switch
        {
            0 => FalseCallReductionMode.MinimizeFalsePositives,
            2 => FalseCallReductionMode.MaximizeDefectRecall,
            _ => FalseCallReductionMode.Balanced,
        };

    private static bool TryLoadFormalManifestFlag(string? groundTruthCsvPath, string imageFolder)
    {
        try
        {
            return BatchValidationService.LoadValidationManifest(groundTruthCsvPath, imageFolder).IsFormalManifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static ValidationManifest? TryLoadManifest(string? groundTruthCsvPath, string imageFolder)
    {
        try
        {
            return BatchValidationService.LoadValidationManifest(groundTruthCsvPath, imageFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private void ApplyMetrics(BatchMetrics metrics)
    {
        AccuracyText.Text = FormatPercent(metrics.Accuracy);
        PrecisionText.Text = FormatPercent(metrics.Precision);
        RecallText.Text = FormatPercent(metrics.Recall);
        FalseCallRateText.Text = FormatPercent(metrics.FalseCallRate);
        TpText.Text = metrics.TruePositive.ToString(CultureInfo.InvariantCulture);
        TnText.Text = metrics.TrueNegative.ToString(CultureInfo.InvariantCulture);
        FpText.Text = metrics.FalsePositive.ToString(CultureInfo.InvariantCulture);
        FnText.Text = metrics.FalseNegative.ToString(CultureInfo.InvariantCulture);
        OkCountText.Text = metrics.OkCount.ToString(CultureInfo.InvariantCulture);
        NgCountText.Text = metrics.NgCount.ToString(CultureInfo.InvariantCulture);
        ReviewCountText.Text = metrics.ReviewCount.ToString(CultureInfo.InvariantCulture);
        FalseCallText.Text = metrics.FalseCall.ToString(CultureInfo.InvariantCulture);
        PossibleEscapeText.Text = metrics.PossibleEscape.ToString(CultureInfo.InvariantCulture);
        VerifiedNgText.Text = metrics.VerifiedNg.ToString(CultureInfo.InvariantCulture);
        UnknownText.Text = metrics.Unknown.ToString(CultureInfo.InvariantCulture);
        ApplyPerformance(BatchValidationService.CalculatePerformanceSummary(_rows));
        ApplyBreakdown(ClassMetricsService.Calculate(_rows));
    }

    private void ApplyDatasetQuality(DatasetQualitySummary summary)
    {
        DatasetQualityText.Text = $"{summary.Status}: {summary.TotalImages} images, OK {summary.OkImages}, NG {summary.NgImages}, unknown {summary.UnknownLabelImages}";
        DatasetQualityText.Foreground = summary.Status switch
        {
            "PASS" => Brushes.LightGreen,
            "FAIL" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFCDD0")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1A334")),
        };
        var topMessages = summary.BlockingFailures.Concat(summary.Warnings).Take(3).ToArray();
        DatasetQualityWarningsText.Text = topMessages.Length == 0
            ? "Dataset quality criteria satisfied."
            : string.Join(" ", topMessages);
    }

    private void ApplyPreflightResult(CustomerDatasetPreflightResult result)
    {
        DatasetPreflightText.Text = $"Preflight {result.Status}: rows {result.ManifestRows}, OK {result.OkCount}, NG {result.NgCount}, defect classes {result.DefectClassCount}, sides {result.SideCoverageCount}, missing images {result.MissingImageCount}, missing golden {result.MissingGoldenCount}, duplicate hashes {result.DuplicateFileHashCount}.";
        DatasetPreflightText.Foreground = result.Status switch
        {
            "PASS" => Brushes.LightGreen,
            "FAIL" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFCDD0")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1A334")),
        };
        var issues = result.BlockingFailures.Concat(result.Warnings).Take(3).ToArray();
        if (issues.Length > 0)
            DatasetQualityWarningsText.Text = string.Join(" ", issues);

        Replace(PreflightFailuresList.Items, result.BlockingFailures.Take(12));
        Replace(PreflightWarningsList.Items, result.Warnings.Take(12));
    }

    private void ApplyBreakdown(ValidationBreakdownSummary summary)
    {
        Replace(_defectClassBreakdown, summary.DefectClassMetrics.Take(20));
        Replace(_sideBreakdown, summary.SideMetrics.Take(20));
        Replace(_roiBreakdown, summary.RoiMetrics.Take(20));
        Replace(_falseCallSources, summary.TopFalseCallContributors.Take(20));
        Replace(_escapeSources, summary.TopPossibleEscapeContributors.Take(20));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    private static void Replace(ItemCollection target, IEnumerable<string> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    private static string? FindManifestTemplatePath()
    {
        foreach (var root in CandidateRoots())
        {
            var candidate = Path.Combine(root, "SampleData", "customer_validation_manifest_template.csv");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return Environment.CurrentDirectory;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private void ApplyPerformance(BatchPerformanceSummary performance)
    {
        AverageTimeText.Text = FormatMilliseconds(performance.AverageMilliseconds);
        MaxTimeText.Text = FormatMilliseconds(performance.MaxMilliseconds);
        MinTimeText.Text = FormatMilliseconds(performance.MinMilliseconds);
        OverOneSecondText.Text = performance.CountOverOneSecond.ToString(CultureInfo.InvariantCulture);
        OverOneSecondText.Foreground = performance.CountOverOneSecond > 0
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1A334"))
            : Brushes.White;
    }

    private string GetEngineNamePart()
        => _currentEngineDisplay.Split(" / ", StringSplitOptions.None).FirstOrDefault() ?? _currentEngineDisplay;

    private string GetEngineVersionPart()
    {
        var parts = _currentEngineDisplay.Split(" / ", StringSplitOptions.None);
        return parts.Length > 1 ? parts[1] : "UNKNOWN";
    }

    private void PreviewRow(BatchTestRow row)
    {
        if (!File.Exists(row.ImagePath))
        {
            MessageBox.Show($"Image file is missing:\n{row.ImagePath}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var bitmap = CreateAnnotatedBitmap(row);
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
        };

        var window = new Window
        {
            Title = $"Validation Preview - {row.Image}",
            Width = 920,
            Height = 680,
            Content = new Border
            {
                Background = Brushes.Black,
                Padding = new Thickness(10),
                Child = image,
            },
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        window.Closed += (_, _) => image.Source = null;
        window.Show();
    }

    private static RenderTargetBitmap CreateAnnotatedBitmap(BatchTestRow row)
    {
        var source = LoadBitmap(row.ImagePath);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));

            var roi = new Rect(
                row.RoiX * source.PixelWidth,
                row.RoiY * source.PixelHeight,
                Math.Max(2, row.RoiWidth * source.PixelWidth),
                Math.Max(2, row.RoiHeight * source.PixelHeight));

            var color = row.IsFailed ? Colors.Red : Colors.Orange;
            var pen = new Pen(new SolidColorBrush(color), Math.Max(3, source.PixelWidth / 300.0));
            dc.DrawRectangle(null, pen, roi);

            var label = $"{row.EngineResult} / {row.PassFail}";
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                Math.Max(16, source.PixelWidth / 32.0),
                new SolidColorBrush(color),
                1.0);

            var textOrigin = new Point(Math.Max(0, roi.X), Math.Max(0, roi.Y - text.Height - 6));
            dc.DrawRectangle(Brushes.Black, null, new Rect(textOrigin.X, textOrigin.Y, text.Width + 10, text.Height + 6));
            dc.DrawText(text, new Point(textOrigin.X + 5, textOrigin.Y + 3));
        }

        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static BitmapSource LoadBitmap(string path)
        => ImageCacheService.LoadBitmap(path, cache: false);

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string FormatPercent(double value)
        => double.IsNaN(value) ? "--" : value.ToString("P1", CultureInfo.InvariantCulture);

    private static string FormatMilliseconds(double value)
        => value <= 0 ? "--" : $"{value:F0} ms";

    private CancellationTokenSource BeginWork(string message)
    {
        _workCts = new CancellationTokenSource();
        WorkProgressBar.Value = 0;
        CancelWorkButton.IsEnabled = true;
        StatusText.Text = message;
        return _workCts;
    }

    private void EndWork()
    {
        _workCts?.Dispose();
        _workCts = null;
        CancelWorkButton.IsEnabled = false;
        WorkProgressBar.Value = 0;
    }

    private void UpdateProgress(WorkProgress progress)
    {
        WorkProgressBar.Value = progress.Total <= 0 ? 0 : Math.Min(100, progress.Completed * 100.0 / progress.Total);
        StatusText.Text = progress.Message;
    }

    private static string FriendlyFileError(string path, Exception ex)
    {
        var name = string.IsNullOrWhiteSpace(path) ? "(unknown file)" : Path.GetFileName(path);
        return ex switch
        {
            UnauthorizedAccessException => $"Permission denied or locked file: {name}",
            NotSupportedException => $"Unsupported image format: {name}",
            IOException => $"File could not be read or written: {name} ({ex.Message})",
            _ => $"{name}: {ex.Message}",
        };
    }

    private static (int Width, int Height) ReadImageDimensions(string imagePath)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(imagePath, UriKind.Absolute),
            BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);
        var frame = decoder.Frames.FirstOrDefault();
        return frame is null ? (0, 0) : (frame.PixelWidth, frame.PixelHeight);
    }

    private static void PopulateLatencyTrace(InspectionLatencyTraceBuilder trace, InspectionTiming timing, DateTime analysisStartUtc, DateTime analysisEndUtc)
    {
        var cursor = analysisStartUtc.AddMilliseconds(Math.Max(0, timing.ImageLoadMilliseconds));
        trace.StartSpan("preprocessing", cursor);
        cursor = cursor.AddMilliseconds(Math.Max(0, timing.PreprocessingMilliseconds));
        trace.StopSpan("preprocessing", cursor);
        trace.StartSpan("inference", cursor);
        cursor = cursor.AddMilliseconds(Math.Max(0, timing.InferenceMilliseconds));
        if (cursor > analysisEndUtc)
            cursor = analysisEndUtc;
        trace.StopSpan("inference", cursor);
        trace.StartSpan("postprocess", cursor);
        trace.StopSpan("postprocess", analysisEndUtc);
        trace.StartSpan("overlay", analysisEndUtc);
        cursor = analysisEndUtc.AddMilliseconds(Math.Max(0, timing.OverlayRenderingMilliseconds));
        trace.StopSpan("overlay", cursor);
    }

    private sealed record WorkProgress(int Completed, int Total, string Message);

    private sealed record ExportOutcome(int Count, IReadOnlyList<string> Errors);

    private sealed record LatestBatchRunSnapshot(
        BatchTestRunRecord Run,
        BatchTestRow[] Rows,
        BatchMetrics Metrics,
        DatasetQualitySummary DatasetQuality,
        FalseCallReductionRun? FalseCallRun,
        bool UsedFormalManifest);

    private sealed record BatchRunOutcome(
        IReadOnlyList<BatchTestRow> Rows,
        BatchMetrics Metrics,
        bool IsFormalManifest,
        string EngineDisplay,
        long RunId,
        IReadOnlyList<string> Errors,
        string? Message)
    {
        public static BatchRunOutcome Empty(string message)
            => Empty(message, Array.Empty<string>());

        public static BatchRunOutcome Empty(string message, IReadOnlyList<string> errors)
            => new(Array.Empty<BatchTestRow>(), new BatchMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), false, string.Empty, 0, errors, message);
    }

    public void Dispose()
    {
        DisposeCancellation(ref _workCts);
        DisposeCancellation(ref _refreshCts);
        GC.SuppressFinalize(this);
    }

    private static void DisposeCancellation(ref CancellationTokenSource? cancellation)
    {
        var source = cancellation;
        cancellation = null;
        if (source is null)
            return;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed elsewhere; disposal must stay idempotent.
        }

        source.Dispose();
    }
}
