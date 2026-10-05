using OptiGraphExtensions.Entities;
using OptiGraphExtensions.Features.PinnedResults.Models;

namespace OptiGraphExtensions.Features.PinnedResults.Services.Abstractions
{
    public interface IPinnedResultService
    {
        Task<IEnumerable<PinnedResult>> GetAllPinnedResultsAsync(Guid? collectionId = null);
        Task<PinnedResult?> GetPinnedResultByIdAsync(Guid id);
        Task<PinnedResult> CreatePinnedResultAsync(Guid collectionId, string? phrases, string? targetKey, string? targetName, string? language, int priority, bool isActive, string? createdBy = null, string? graphId = null);
        Task<PinnedResult?> UpdatePinnedResultAsync(Guid id, string? phrases, string? targetKey, string? targetName, string? language, int priority, bool isActive);
        Task<bool> DeletePinnedResultAsync(Guid id);
        Task<bool> DeletePinnedResultsByCollectionIdAsync(Guid collectionId);
        Task<bool> PinnedResultExistsAsync(Guid id);
        Task<bool> CollectionExistsAsync(Guid collectionId);
        Task<IEnumerable<PinnedResult>> GetPinnedResultsByCollectionIdAsync(Guid collectionId);

        /// <summary>
        /// Updates only the GraphId of a pinned result to link it with an existing Graph item.
        /// </summary>
        Task<PinnedResult?> UpdatePinnedResultGraphIdAsync(Guid id, string graphId);

        /// <summary>
        /// Updates only the GraphId of an already-loaded entity (no re-fetch needed).
        /// Use this when you already have the entity loaded to avoid concurrency issues.
        /// </summary>
        Task<bool> UpdatePinnedResultGraphIdAsync(PinnedResult pinnedResult, string graphId);

        // Enhanced synchronization support
        /// <summary>
        /// Activates a pinned result and synchronizes it to Graph if not already synced.
        /// </summary>
        Task<SyncResult> ActivatePinnedResultAsync(Guid id, string? activatedBy = null);

        /// <summary>
        /// Deactivates a pinned result. If it has a GraphId, it will be deleted from Graph.
        /// </summary>
        Task<SyncResult> DeactivatePinnedResultAsync(Guid id);

        /// <summary>
        /// Updates a pinned result and synchronizes changes to Graph if it has a GraphId.
        /// </summary>
        Task<SyncResult> UpdateAndSyncPinnedResultAsync(Guid id, string? phrases, string? targetKey, string? targetName, string? language, int priority, bool isActive);

        /// <summary>
        /// Deletes a pinned result and removes it from Graph if it exists there.
        /// </summary>
        Task<SyncResult> DeleteAndSyncPinnedResultAsync(Guid id);
    }
}