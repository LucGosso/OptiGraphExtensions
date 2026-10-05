namespace OptiGraphExtensions.Features.PinnedResults.Models
{
    /// <summary>
    /// Represents the UI state for a pinned result including synchronization status.
    /// </summary>
    public class PinnedResultUIState
    {
        public Guid Id { get; set; }
        public string? SyncStatus { get; set; } // "Synced", "Pending", "Failed", "LocalOnly"
        public string? LastSyncError { get; set; }
        public DateTime? LastSyncAttempt { get; set; }
        public bool HasGraphId { get; set; }

        public string GetSyncStatusCssClass() => SyncStatus switch
        {
            "Synced" => "epi-badge--success",
            "Failed" => "epi-badge--danger",
            "Pending" => "epi-badge--warning",
            "LocalOnly" => "epi-badge--secondary",
            _ => "epi-badge--secondary"
        };

        public string GetSyncStatusDisplayText() => SyncStatus ?? "Unknown";
    }
}
