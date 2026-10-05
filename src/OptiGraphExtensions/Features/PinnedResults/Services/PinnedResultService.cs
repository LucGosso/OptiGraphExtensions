using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OptiGraphExtensions.Entities;
using OptiGraphExtensions.Features.PinnedResults.Models;
using OptiGraphExtensions.Features.PinnedResults.Repositories;
using OptiGraphExtensions.Features.PinnedResults.Services.Abstractions;

namespace OptiGraphExtensions.Features.PinnedResults.Services
{
    public class PinnedResultService : IPinnedResultService
    {
        private readonly IPinnedResultRepository _pinnedResultRepository;
        private readonly IPinnedResultsCollectionRepository _collectionRepository;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PinnedResultService>? _logger;

        public PinnedResultService(
            IPinnedResultRepository pinnedResultRepository,
            IPinnedResultsCollectionRepository collectionRepository,
            IServiceProvider serviceProvider,
            ILogger<PinnedResultService>? logger = null)
        {
            _pinnedResultRepository = pinnedResultRepository;
            _collectionRepository = collectionRepository;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        private IPinnedResultsGraphSyncService GraphSyncService => 
            _serviceProvider.GetRequiredService<IPinnedResultsGraphSyncService>();

        public async Task<IEnumerable<PinnedResult>> GetAllPinnedResultsAsync(Guid? collectionId = null)
        {
            return await _pinnedResultRepository.GetAllAsync(collectionId);
        }

        public async Task<PinnedResult?> GetPinnedResultByIdAsync(Guid id)
        {
            return await _pinnedResultRepository.GetByIdAsync(id);
        }

        public async Task<PinnedResult> CreatePinnedResultAsync(Guid collectionId, string? phrases, string? targetKey, string? targetName, string? language, int priority, bool isActive, string? createdBy = null, string? graphId = null)
        {
            var pinnedResult = new PinnedResult
            {
                Id = Guid.NewGuid(),
                CollectionId = collectionId,
                Phrases = phrases,
                TargetKey = targetKey,
                TargetName = targetName,
                Language = language,
                Priority = priority,
                IsActive = isActive,
                GraphId = graphId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = createdBy
            };

            // Create the local record first
            var createdPinnedResult = await _pinnedResultRepository.CreateAsync(pinnedResult);

            // If no GraphId was provided and the item is active, sync to Graph
            if (string.IsNullOrEmpty(graphId) && isActive)
            {
                var collection = await _collectionRepository.GetByIdAsync(collectionId);
                if (collection != null)
                {
                    // If collection doesn't have a GraphCollectionId, sync it first
                    if (string.IsNullOrEmpty(collection.GraphCollectionId))
                    {
                        try
                        {
                            await GraphSyncService.SyncCollectionToOptimizelyGraphAsync(collection);
                            // Refresh collection to get the updated GraphCollectionId
                            collection = await _collectionRepository.GetByIdAsync(collectionId);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to sync collection to Graph before syncing pinned result");
                            // If collection sync fails, return the local result without Graph sync
                            return createdPinnedResult;
                        }
                    }

                    // Sync to Graph and update local record with Graph ID
                    if (collection != null && !string.IsNullOrEmpty(collection.GraphCollectionId))
                    {
                        try
                        {
                            var returnedGraphId = await GraphSyncService.SyncSinglePinnedResultToOptimizelyGraphAsync(createdPinnedResult, collection.GraphCollectionId);

                            if (!string.IsNullOrEmpty(returnedGraphId))
                            {
                                // Update the local record with the Graph ID
                                createdPinnedResult.GraphId = returnedGraphId;
                                await _pinnedResultRepository.UpdateAsync(createdPinnedResult);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to sync pinned result to Graph after creation");
                            // If Graph sync fails, return the local result without Graph ID
                            // The record exists locally and can be retried later
                        }
                    }
                }
            }

            return createdPinnedResult;
        }

        public async Task<PinnedResult?> UpdatePinnedResultAsync(Guid id, string? phrases, string? targetKey, string? targetName, string? language, int priority, bool isActive)
        {
            var pinnedResult = await _pinnedResultRepository.GetByIdAsync(id);
            if (pinnedResult == null)
            {
                return null;
            }

            pinnedResult.Phrases = phrases;
            pinnedResult.TargetKey = targetKey;
            pinnedResult.TargetName = targetName;
            pinnedResult.Language = language;
            pinnedResult.Priority = priority;
            pinnedResult.IsActive = isActive;

            return await _pinnedResultRepository.UpdateAsync(pinnedResult);
        }

        /// <summary>
        /// Updates only the GraphId of a pinned result to link it with an existing Graph item.
        /// Handles optimistic concurrency exceptions gracefully - if the item was modified, it will be retried on next sync.
        /// </summary>
        public async Task<PinnedResult?> UpdatePinnedResultGraphIdAsync(Guid id, string graphId)
        {
            try
            {
                var pinnedResult = await _pinnedResultRepository.GetByIdAsync(id);
                if (pinnedResult == null)
                {
                    return null;
                }

                pinnedResult.GraphId = graphId;
                return await _pinnedResultRepository.UpdateAsync(pinnedResult);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Handle optimistic concurrency: item was modified or deleted since we read it
                // Log and continue - the item will be retried on next sync cycle
                _logger?.LogWarning(ex, $"Concurrency conflict updating GraphId for pinned result {id}. Item will be retried on next sync.");
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Unexpected error updating GraphId for pinned result {id}");
                // Return null to indicate failure, but don't throw - allow sync to continue
                return null;
            }
        }

        /// <summary>
        /// Updates only the GraphId of an already-loaded entity (no re-fetch needed).
        /// Use this when you already have the entity loaded to avoid concurrency issues.
        /// </summary>
        public async Task<bool> UpdatePinnedResultGraphIdAsync(PinnedResult pinnedResult, string graphId)
        {
            if (pinnedResult == null)
                return false;

            try
            {
                pinnedResult.GraphId = graphId;
                await _pinnedResultRepository.UpdateAsync(pinnedResult);
                return true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Handle optimistic concurrency: item was modified or deleted since we read it
                _logger?.LogWarning(ex, $"Concurrency conflict updating GraphId for pinned result {pinnedResult.Id}. Item will be retried on next sync.");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Unexpected error updating GraphId for pinned result {pinnedResult.Id}");
                return false;
            }
        }

        public async Task<bool> DeletePinnedResultAsync(Guid id)
        {
            // Get the pinned result with its collection in a single query (eager loading)
            var pinnedResult = await _pinnedResultRepository.GetByIdWithCollectionAsync(id);
            if (pinnedResult == null)
                return false;

            // If the pinned result has a Graph ID, delete it from Graph first
            if (!string.IsNullOrEmpty(pinnedResult.GraphId))
            {
                // Collection is already loaded via Include - no additional DB call needed
                var collection = pinnedResult.Collection;
                if (collection != null && !string.IsNullOrEmpty(collection.GraphCollectionId))
                {
                    try
                    {
                        await GraphSyncService.DeletePinnedResultFromOptimizelyGraphAsync(collection.GraphCollectionId, pinnedResult.GraphId);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to delete pinned result from Graph, continuing with local deletion");
                        // Continue with local deletion even if Graph deletion fails
                        // This prevents orphaned records in the local database
                    }
                }
            }

            // Delete locally
            return await _pinnedResultRepository.DeleteAsync(id);
        }

        public async Task<bool> DeletePinnedResultsByCollectionIdAsync(Guid collectionId)
        {
            // Delete locally - no need to fetch first since DeleteByCollectionIdAsync handles the deletion
            return await _pinnedResultRepository.DeleteByCollectionIdAsync(collectionId);
        }

        public async Task<bool> PinnedResultExistsAsync(Guid id)
        {
            return await _pinnedResultRepository.ExistsAsync(id);
        }

        public async Task<bool> CollectionExistsAsync(Guid collectionId)
        {
            return await _pinnedResultRepository.CollectionExistsAsync(collectionId);
        }

        public async Task<IEnumerable<PinnedResult>> GetPinnedResultsByCollectionIdAsync(Guid collectionId)
        {
            return await _pinnedResultRepository.GetByCollectionIdAsync(collectionId);
        }

        /// <summary>
        /// Activates a pinned result and synchronizes it to Graph if not already synced.
        /// </summary>
        public async Task<SyncResult> ActivatePinnedResultAsync(Guid id, string? activatedBy = null)
        {
            var pinnedResult = await _pinnedResultRepository.GetByIdWithCollectionAsync(id);
            if (pinnedResult == null)
            {
                return SyncResult.CreateFailure("Pinned result not found");
            }

            // If already active, nothing to do
            if (pinnedResult.IsActive)
            {
                return SyncResult.CreateSuccess(pinnedResult.GraphId);
            }

            // Mark as active locally
            pinnedResult.IsActive = true;
            await _pinnedResultRepository.UpdateAsync(pinnedResult);

            // If it already has a GraphId, it's already in Graph (was deactivated)
            if (!string.IsNullOrEmpty(pinnedResult.GraphId))
            {
                return SyncResult.CreateSuccess(pinnedResult.GraphId);
            }

            // Otherwise, sync to Graph
            var collection = pinnedResult.Collection;
            if (collection == null)
            {
                return SyncResult.CreateFailure("Collection not found");
            }

            if (string.IsNullOrEmpty(collection.GraphCollectionId))
            {
                try
                {
                    await GraphSyncService.SyncCollectionToOptimizelyGraphAsync(collection);
                    collection = await _collectionRepository.GetByIdAsync(collection.Id);
                    if (collection == null || string.IsNullOrEmpty(collection.GraphCollectionId))
                    {
                        return SyncResult.CreateFailure("Failed to sync collection to Graph");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to sync collection to Graph for activation");
                    return SyncResult.CreateFailure($"Failed to sync collection: {ex.Message}");
                }
            }

            try
            {
                var graphId = await GraphSyncService.ActivatePinnedResultInGraphAsync(
                    pinnedResult,
                    collection.GraphCollectionId);

                if (!string.IsNullOrEmpty(graphId))
                {
                    pinnedResult.GraphId = graphId;
                    await _pinnedResultRepository.UpdateAsync(pinnedResult);
                    return SyncResult.CreateSuccess(graphId);
                }

                return SyncResult.CreateFailure("Graph did not return an ID");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to activate pinned result in Graph");
                return SyncResult.CreateFailure($"Failed to sync to Graph: {ex.Message}");
            }
        }

        /// <summary>
        /// Deactivates a pinned result. If it has a GraphId, it will be deleted from Graph.
        /// </summary>
        public async Task<SyncResult> DeactivatePinnedResultAsync(Guid id)
        {
            var pinnedResult = await _pinnedResultRepository.GetByIdWithCollectionAsync(id);
            if (pinnedResult == null)
            {
                return SyncResult.CreateFailure("Pinned result not found");
            }

            // If already inactive, nothing to do
            if (!pinnedResult.IsActive)
            {
                return SyncResult.CreateSuccess(pinnedResult.GraphId);
            }

            // Mark as inactive locally
            pinnedResult.IsActive = false;
            await _pinnedResultRepository.UpdateAsync(pinnedResult);

            // If it has a GraphId, delete from Graph
            if (!string.IsNullOrEmpty(pinnedResult.GraphId))
            {
                var collection = pinnedResult.Collection;
                if (collection != null && !string.IsNullOrEmpty(collection.GraphCollectionId))
                {
                    try
                    {
                        await GraphSyncService.DeactivatePinnedResultFromGraphAsync(
                            collection.GraphCollectionId,
                            pinnedResult.GraphId);

                        return SyncResult.CreateSuccess(pinnedResult.GraphId);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to deactivate pinned result in Graph, but marked inactive locally");
                        return SyncResult.CreateFailure($"Failed to sync deactivation to Graph: {ex.Message}");
                    }
                }
            }

            return SyncResult.CreateSuccess(pinnedResult.GraphId);
        }

        /// <summary>
        /// Updates a pinned result and synchronizes changes to Graph if it has a GraphId.
        /// Uses delete+create pattern since Graph API doesn't support direct updates.
        /// </summary>
        public async Task<SyncResult> UpdateAndSyncPinnedResultAsync(
            Guid id,
            string? phrases,
            string? targetKey,
            string? targetName,
            string? language,
            int priority,
            bool isActive)
        {
            var pinnedResult = await _pinnedResultRepository.GetByIdWithCollectionAsync(id);
            if (pinnedResult == null)
            {
                return SyncResult.CreateFailure("Pinned result not found");
            }

            var oldGraphId = pinnedResult.GraphId;
            var hadGraphId = !string.IsNullOrEmpty(oldGraphId);

            // Update local record
            pinnedResult.Phrases = phrases;
            pinnedResult.TargetKey = targetKey;
            pinnedResult.TargetName = targetName;
            pinnedResult.Language = language;
            pinnedResult.Priority = priority;
            pinnedResult.IsActive = isActive;

            await _pinnedResultRepository.UpdateAsync(pinnedResult);

            // If it has a GraphId and is active, update in Graph
            if (hadGraphId && isActive)
            {
                var collection = pinnedResult.Collection;
                if (collection != null && !string.IsNullOrEmpty(collection.GraphCollectionId))
                {
                    try
                    {
                        // Update in Graph (delete + create pattern)
                        var newGraphId = await GraphSyncService.UpdatePinnedResultInGraphAsync(
                            pinnedResult,
                            collection.GraphCollectionId);

                        if (!string.IsNullOrEmpty(newGraphId))
                        {
                            // Update GraphId if it changed
                            if (newGraphId != oldGraphId)
                            {
                                pinnedResult.GraphId = newGraphId;
                                await _pinnedResultRepository.UpdateAsync(pinnedResult);
                            }
                            return SyncResult.CreateSuccess(newGraphId);
                        }

                        return SyncResult.CreateFailure("Graph did not return an ID after update");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to update pinned result in Graph");
                        return SyncResult.CreateFailure($"Failed to sync update to Graph: {ex.Message}");
                    }
                }
            }
            else if (hadGraphId && !isActive)
            {
                // Item was previously in Graph but is now inactive, remove it
                var collection = pinnedResult.Collection;
                if (collection != null && !string.IsNullOrEmpty(collection.GraphCollectionId))
                {
                    try
                    {
                        await GraphSyncService.DeactivatePinnedResultFromGraphAsync(
                            collection.GraphCollectionId,
                            oldGraphId!);

                        return SyncResult.CreateSuccess(oldGraphId);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to deactivate pinned result in Graph");
                        return SyncResult.CreateFailure($"Failed to sync deactivation to Graph: {ex.Message}");
                    }
                }
            }

            // If no GraphId, it wasn't in Graph, so just return success
            return SyncResult.CreateSuccess(null);
        }

        /// <summary>
        /// Deletes a pinned result and removes it from Graph if it exists there.
        /// </summary>
        public async Task<SyncResult> DeleteAndSyncPinnedResultAsync(Guid id)
        {
            // First, get the pinned result WITHOUT tracking to check if it has a Graph ID
            var pinnedResult = await _pinnedResultRepository.GetByIdAsNoTrackingAsync(id);
            if (pinnedResult == null)
            {
                return SyncResult.CreateFailure("Pinned result not found");
            }

            // If it has a Graph ID, we need collection info to delete from Graph
            if (!string.IsNullOrEmpty(pinnedResult.GraphId))
            {
                // Get collection info (this will be tracked, but we won't delete this instance)
                var collectionInfo = await _pinnedResultRepository.GetByIdWithCollectionAsync(id);

                if (collectionInfo?.Collection != null && !string.IsNullOrEmpty(collectionInfo.Collection.GraphCollectionId))
                {
                    try
                    {
                        await GraphSyncService.DeletePinnedResultFromOptimizelyGraphAsync(
                            collectionInfo.Collection.GraphCollectionId,
                            pinnedResult.GraphId);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to delete from Graph. Will continue with local deletion.");
                        // Continue with local deletion even if Graph deletion fails
                    }
                }
            }

            // Delete locally - use direct delete to avoid concurrency issues
            // This uses a stub entity so it won't conflict with any tracked instances
            try
            {
                var deleteSuccess = await _pinnedResultRepository.DeleteByIdDirectAsync(id);

                if (!deleteSuccess)
                {
                    return SyncResult.CreateFailure("Failed to delete from local database");
                }

                return SyncResult.CreateSuccess(null);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Exception during local database deletion.");
                return SyncResult.CreateFailure($"Failed to delete pinned result: {ex.Message}");
            }
        }
    }
}