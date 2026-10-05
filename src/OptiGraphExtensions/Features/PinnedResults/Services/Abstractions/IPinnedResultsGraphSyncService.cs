using OptiGraphExtensions.Entities;
using OptiGraphExtensions.Features.PinnedResults.Models;

namespace OptiGraphExtensions.Features.PinnedResults.Services.Abstractions;

public interface IPinnedResultsGraphSyncService
{
    Task<bool> SyncCollectionToOptimizelyGraphAsync(PinnedResultsCollection collection);
    Task<bool> SyncPinnedResultsToOptimizelyGraphAsync(Guid collectionId);
    Task<string> SyncSinglePinnedResultToOptimizelyGraphAsync(PinnedResult pinnedResult, string graphCollectionId);
    Task<bool> UpdateSinglePinnedResultInOptimizelyGraphAsync(PinnedResult pinnedResult, string graphCollectionId, string graphItemId);
    Task<int> SyncPinnedResultsFromOptimizelyGraphAsync(Guid collectionId);
    Task<bool> DeletePinnedResultFromOptimizelyGraphAsync(string graphCollectionId, string graphId);
    Task<bool> DeleteAllItemsFromCollectionAsync(string graphCollectionId);
    Task<bool> DeleteCollectionFromOptimizelyGraphAsync(string graphCollectionId);
    Task<IList<GraphCollectionResponse>> SyncCollectionsFromOptimizelyGraphAsync();

    // Enhanced methods for comprehensive synchronization
    /// <summary>
    /// Updates a pinned result in Graph by deleting the old one and creating a new one.
    /// This is necessary because Graph API does not support direct updates.
    /// </summary>
    Task<string> UpdatePinnedResultInGraphAsync(PinnedResult pinnedResult, string graphCollectionId);

    /// <summary>
    /// Activates (creates) a pinned result in Graph.
    /// </summary>
    Task<string> ActivatePinnedResultInGraphAsync(PinnedResult pinnedResult, string graphCollectionId);

    /// <summary>
    /// Deactivates (deletes) a pinned result from Graph.
    /// </summary>
    Task<bool> DeactivatePinnedResultFromGraphAsync(string graphCollectionId, string graphId);

    /// <summary>
    /// Performs full reconciliation sync from Graph using GraphId as the key.
    /// Preserves local-only metadata (CreatedBy, CreatedAt, TargetName).
    /// </summary>
    Task<int> SyncPinnedResultsFromOptimizelyGraphWithReconciliationAsync(Guid collectionId);
}