namespace OptiGraphExtensions.Features.PinnedResults.Models
{
    /// <summary>
    /// Represents the result of a synchronization operation with Graph.
    /// </summary>
    public class SyncResult
    {
        public bool Success { get; set; }
        public string? GraphId { get; set; }
        public string? Error { get; set; }
        public DateTime SyncAttemptTime { get; set; } = DateTime.UtcNow;

        public static SyncResult CreateSuccess(string? graphId = null) =>
            new() { Success = true, GraphId = graphId };

        public static SyncResult CreateFailure(string error) =>
            new() { Success = false, Error = error };
    }
}
