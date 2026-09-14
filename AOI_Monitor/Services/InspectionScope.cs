namespace AOI_Monitor.Services;

/// <summary>
/// Ambient per-inspection scope for dataset-driven runs (batch validation, Stage 1 exit
/// evidence, model acceptance, soak tests). The station's workflow state only knows the
/// configured board program, so without this scope a threshold profile deployed for a
/// specific board model can never match a dataset row: verdict-affecting lookups would
/// silently fall back to the station identity and only ANY-scoped profiles would ever
/// apply. Batch loops set the manifest row's board model here around each engine call;
/// the live single-board path leaves it unset and station workflow state governs.
/// </summary>
public static class InspectionScope
{
    private static readonly AsyncLocal<string?> AmbientBoardModel = new();

    /// <summary>The board model of the dataset row currently being inspected, if any.</summary>
    public static string? BoardModel => AmbientBoardModel.Value;

    /// <summary>
    /// Sets the ambient board model until the returned scope is disposed. Blank values
    /// leave the ambient state unchanged semantics-wise (null: station identity governs).
    /// </summary>
    public static IDisposable ForBoardModel(string? boardModel)
    {
        var previous = AmbientBoardModel.Value;
        AmbientBoardModel.Value = string.IsNullOrWhiteSpace(boardModel) ? null : boardModel.Trim();
        return new RestoreOnDispose(previous);
    }

    private sealed class RestoreOnDispose(string? previous) : IDisposable
    {
        public void Dispose() => AmbientBoardModel.Value = previous;
    }
}
