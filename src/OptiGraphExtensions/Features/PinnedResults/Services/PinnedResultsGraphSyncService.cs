using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OptiGraphExtensions.Common;
using OptiGraphExtensions.Entities;
using OptiGraphExtensions.Features.Common.Services;
using OptiGraphExtensions.Features.Common.Exceptions;
using OptiGraphExtensions.Features.PinnedResults.Models;
using OptiGraphExtensions.Features.PinnedResults.Services.Abstractions;
using OptiGraphExtensions.Features.Synonyms.Services.Abstractions;

namespace OptiGraphExtensions.Features.PinnedResults.Services;

public class PinnedResultsGraphSyncService : IPinnedResultsGraphSyncService
{
    private readonly HttpClient _httpClient;
    private readonly IOptiGraphConfigurationService _configurationService;
    private readonly IGraphConfigurationValidator _graphConfigurationValidator;
    private readonly IPinnedResultsCollectionService _collectionService;
    private readonly IPinnedResultService _pinnedResultService;
    private readonly ILogger<PinnedResultsGraphSyncService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public PinnedResultsGraphSyncService(
        HttpClient httpClient,
        IOptiGraphConfigurationService configurationService,
        IGraphConfigurationValidator graphConfigurationValidator,
        IPinnedResultsCollectionService collectionService,
        IPinnedResultService pinnedResultService,
        ILogger<PinnedResultsGraphSyncService> logger)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _graphConfigurationValidator = graphConfigurationValidator;
        _collectionService = collectionService;
        _pinnedResultService = pinnedResultService;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    public async Task<bool> SyncCollectionToOptimizelyGraphAsync(PinnedResultsCollection collection)
    {
        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections";

        var graphRequest = CreateGraphCollectionRequest(collection);

        using var request = CreateAuthenticatedRequest(HttpMethod.Post, graphApiUrl, authenticationHeader);
        request.Content = JsonContent.Create(graphRequest);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new GraphSyncException($"Graph sync failed: {response.StatusCode} - {errorContent}");
        }

        await UpdateCollectionWithGraphId(collection, response);
        return true;
    }

    public async Task<bool> SyncPinnedResultsToOptimizelyGraphAsync(Guid collectionId)
    {
        var collection = await _collectionService.GetCollectionByIdAsync(collectionId);
        if (collection == null)
            throw new InvalidOperationException("Collection not found.");

        // If collection doesn't have a GraphCollectionId, sync it first
        if (string.IsNullOrEmpty(collection.GraphCollectionId))
        {
            await SyncCollectionToOptimizelyGraphAsync(collection);
            // Refresh collection to get the updated GraphCollectionId
            collection = await _collectionService.GetCollectionByIdAsync(collectionId);
            if (collection == null || string.IsNullOrEmpty(collection.GraphCollectionId))
                throw new InvalidOperationException("Failed to sync collection to Optimizely Graph.");
        }

        var pinnedResults = await GetPinnedResultsForSync(collectionId);

        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections/{collection.GraphCollectionId}/items";

        // Fetch all existing Graph items to enable content-based matching
        var existingGraphItems = await FetchAllGraphItemsAsync(collection.GraphCollectionId);

        // Send pinned results in parallel batches for improved performance
        // For idempotency, separate items with existing GraphIds (updates) from new items (creates)
        var activePinnedResults = pinnedResults.Where(pr => pr.IsActive).ToList();
        var newItems = activePinnedResults.Where(pr => string.IsNullOrEmpty(pr.GraphId)).ToList();
        var existingItems = activePinnedResults.Where(pr => !string.IsNullOrEmpty(pr.GraphId)).ToList();

        const int batchSize = 5; // Limit concurrent requests to avoid overwhelming the API

        // Sync new items (creates) - but check for content-based duplicates first
        if (newItems.Any())
        {
            var newBatches = newItems
                .Select((item, index) => new { item, index })
                .GroupBy(x => x.index / batchSize)
                .Select(g => g.Select(x => x.item).ToList())
                .ToList();

            foreach (var batch in newBatches)
            {
                var tasks = batch.Select(async pinnedResult =>
                {
                    // Check if this item already exists in Graph by content
                    var existingGraphItem = FindGraphItemByContent(
                        existingGraphItems,
                        pinnedResult.Phrases,
                        pinnedResult.TargetKey,
                        pinnedResult.Language);

                    if (existingGraphItem != null)
                    {
                        // Item already exists in Graph by content
                        // Link locally by updating GraphId
                        var graphIdUpdateSucceeded = false;
                        try
                        {
                            _logger.LogInformation($"PinnedResult {pinnedResult.Id}: Found matching Graph item {existingGraphItem.Id} by content. Linking...");
                            var result = await _pinnedResultService.UpdatePinnedResultGraphIdAsync(pinnedResult.Id, existingGraphItem.Id);
                            graphIdUpdateSucceeded = result != null;
                            if (graphIdUpdateSucceeded)
                            {
                                _logger.LogInformation($"PinnedResult {pinnedResult.Id}: Successfully linked to Graph ID {existingGraphItem.Id}");
                            }
                            else
                            {
                                _logger.LogWarning($"PinnedResult {pinnedResult.Id}: GraphId link returned null");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"PinnedResult {pinnedResult.Id}: Failed to link GraphId: {ex.Message}. Will attempt update.");
                        }

                        // Try to update the existing Graph item with new values
                        try
                        {
                            _logger.LogInformation($"PinnedResult {pinnedResult.Id}: Updating existing Graph item {existingGraphItem.Id}...");
                            await UpdateSinglePinnedResultInOptimizelyGraphAsync(pinnedResult, collection.GraphCollectionId, existingGraphItem.Id);
                            _logger.LogInformation($"PinnedResult {pinnedResult.Id}: Successfully updated Graph item {existingGraphItem.Id}");

                            // Update GraphId locally if linking hasn't succeeded yet
                            if (!graphIdUpdateSucceeded)
                            {
                                try
                                {
                                    await _pinnedResultService.UpdatePinnedResultGraphIdAsync(pinnedResult.Id, existingGraphItem.Id);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning($"PinnedResult {pinnedResult.Id}: Late GraphId link failed after update: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"PinnedResult {pinnedResult.Id}: Failed to update Graph item: {ex.Message}");
                        }

                        return; // Skip create - we've updated the existing item
                    }

                    // Item doesn't exist in Graph - create it and capture the returned GraphId
                    try
                    {
                        _logger.LogInformation($"PinnedResult {pinnedResult.Id}: Creating new item in Graph...");
                        var returnedGraphId = await SyncSinglePinnedResultToOptimizelyGraphAsync(pinnedResult, collection.GraphCollectionId);
                        if (!string.IsNullOrEmpty(returnedGraphId))
                        {
                            _logger.LogInformation($"PinnedResult {pinnedResult.Id}: Created in Graph with ID {returnedGraphId}");
                            // Update local record with the Graph ID returned from create
                            await _pinnedResultService.UpdatePinnedResultGraphIdAsync(pinnedResult.Id, returnedGraphId);
                        }
                    }
                    catch (GraphSyncException gex) when (gex.Message.Contains("NOT_UNIQUE_ERROR"))
                    {
                        _logger.LogWarning($"PinnedResult {pinnedResult.Id}: Graph returned NOT_UNIQUE_ERROR on create. Attempting update via fresh fetch...");
                        // Item already exists in Graph but we don't have the GraphId
                        // Try to fetch it fresh and update - it was created externally or from a previous failed sync
                        try
                        {
                            // Refresh the Graph items list to get the latest
                            var freshGraphItems = await FetchAllGraphItemsAsync(collection.GraphCollectionId);
                            var freshGraphItem = FindGraphItemByContent(
                                freshGraphItems,
                                pinnedResult.Phrases,
                                pinnedResult.TargetKey,
                                pinnedResult.Language);

                            if (freshGraphItem != null)
                            {
                                _logger.LogInformation($"PinnedResult {pinnedResult.Id}: Found in fresh fetch with Graph ID {freshGraphItem.Id}. Updating...");

                                // Update the Graph item instead of trying to create again
                                await UpdateSinglePinnedResultInOptimizelyGraphAsync(pinnedResult, collection.GraphCollectionId, freshGraphItem.Id);
                                _logger.LogInformation($"PinnedResult {pinnedResult.Id}: Successfully updated Graph item after NOT_UNIQUE_ERROR");

                                // Link locally
                                await _pinnedResultService.UpdatePinnedResultGraphIdAsync(pinnedResult.Id, freshGraphItem.Id);
                                _logger.LogInformation($"PinnedResult {pinnedResult.Id}: Successfully linked after NOT_UNIQUE_ERROR update");
                            }
                            else
                            {
                                _logger.LogError($"PinnedResult {pinnedResult.Id}: NOT_UNIQUE_ERROR but item not found in fresh fetch. Data inconsistency!");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"PinnedResult {pinnedResult.Id}: Failed to recover from NOT_UNIQUE_ERROR: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"PinnedResult {pinnedResult.Id}: Unexpected error during sync: {ex.Message}");
                        throw new GraphSyncException($"Error syncing pinned result to Optimizely Graph: {ex.Message}", ex);
                    }
                });

                await Task.WhenAll(tasks);
            }
        }

        // For existing items, update them in Graph (delete+recreate pattern)
        if (existingItems.Any())
        {
            var existingBatches = existingItems
                .Select((item, index) => new { item, index })
                .GroupBy(x => x.index / batchSize)
                .Select(g => g.Select(x => x.item).ToList())
                .ToList();

            foreach (var batch in existingBatches)
            {
                var tasks = batch.Select(pinnedResult => UpdateSinglePinnedResultAsync(pinnedResult, graphApiUrl, authenticationHeader, collection.GraphCollectionId));
                await Task.WhenAll(tasks);
            }
        }

        return true;
    }

    private async Task UpdateSinglePinnedResultAsync(PinnedResult pinnedResult, string graphApiUrl, string authenticationHeader, string graphCollectionId)
    {
        // Delete the old item
        try
        {
            await DeletePinnedResultFromOptimizelyGraphAsync(graphCollectionId, pinnedResult.GraphId!);
        }
        catch
        {
            // Item might not exist in Graph, continue
        }

        // Create the new item
        await SyncSinglePinnedResultAsync(pinnedResult, graphApiUrl, authenticationHeader);
    }

    private async Task SyncSinglePinnedResultAsync(PinnedResult pinnedResult, string graphApiUrl, string authenticationHeader)
    {
        var graphItem = CreateGraphPinnedResultItem(pinnedResult);
        var jsonString = JsonSerializer.Serialize(graphItem, _jsonOptions);

        using var request = CreateAuthenticatedRequest(HttpMethod.Post, graphApiUrl, authenticationHeader);
        request.Content = new StringContent(jsonString, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new GraphSyncException($"Error syncing pinned result to Optimizely Graph: {response.StatusCode} - {errorContent}");
        }
    }

    public async Task<bool> DeletePinnedResultFromOptimizelyGraphAsync(string graphCollectionId, string graphId)
    {
        if (string.IsNullOrWhiteSpace(graphCollectionId))
            throw new ArgumentException("graphCollectionId cannot be null or empty", nameof(graphCollectionId));

        if (string.IsNullOrWhiteSpace(graphId))
            throw new ArgumentException("graphId cannot be null or empty", nameof(graphId));

        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var encodedCollectionId = Uri.EscapeDataString(graphCollectionId.Trim());
        var encodedGraphId = Uri.EscapeDataString(graphId.Trim());
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections/{encodedCollectionId}/items/{encodedGraphId}";

        _logger.LogInformation($"Attempting to delete pinned result from Graph. CollectionId: {encodedCollectionId}, ItemId: {encodedGraphId}");

        using var request = CreateAuthenticatedRequest(HttpMethod.Delete, graphApiUrl, authenticationHeader);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode) 
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"Failed to delete pinned result from Graph. URL: {graphApiUrl}, Status: {response.StatusCode}, Response: {errorContent}");
            throw new GraphSyncException($"Error deleting pinned result from Optimizely Graph: {response.StatusCode} - {errorContent}");
        }

        _logger.LogInformation($"Successfully deleted pinned result from Graph. ItemId: {encodedGraphId}");
        return true;
    }

    public async Task<bool> DeleteAllItemsFromCollectionAsync(string graphCollectionId)
    {
        if (string.IsNullOrWhiteSpace(graphCollectionId))
            throw new ArgumentException("graphCollectionId cannot be null or empty", nameof(graphCollectionId));

        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var encodedCollectionId = Uri.EscapeDataString(graphCollectionId.Trim());
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections/{encodedCollectionId}/items";

        _logger.LogInformation($"Attempting to delete all pinned results from collection. CollectionId: {encodedCollectionId}");

        using var request = CreateAuthenticatedRequest(HttpMethod.Delete, graphApiUrl, authenticationHeader);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation($"Collection {encodedCollectionId} not found in Graph (already deleted or never existed). Treating as success.");
                return true; //never mind, already removed, continue as removed. 
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"Failed to delete all pinned results from collection. URL: {graphApiUrl}, Status: {response.StatusCode}, Response: {errorContent}");
            throw new GraphSyncException($"Error deleting all pinned results from collection: {response.StatusCode} - {errorContent}");
        }

        _logger.LogInformation($"Successfully deleted all pinned results from collection. CollectionId: {encodedCollectionId}");
        return true;
    }

    public async Task<bool> DeleteCollectionFromOptimizelyGraphAsync(string graphCollectionId)
    {
        // Optimizely Graph requires all items to be removed before deleting a collection
        await DeleteAllItemsFromCollectionAsync(graphCollectionId);

        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections/{graphCollectionId}";

        using var request = CreateAuthenticatedRequest(HttpMethod.Delete, graphApiUrl, authenticationHeader);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new GraphSyncException($"Error deleting collection from Optimizely Graph: {response.StatusCode} - {errorContent}");
        }

        return true;
    }

    public async Task<IList<GraphCollectionResponse>> SyncCollectionsFromOptimizelyGraphAsync()
    {
        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections";

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, graphApiUrl, authenticationHeader);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new GraphSyncException($"Failed to fetch collections from Optimizely Graph: {response.StatusCode} - {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        return DeserializeGraphCollectionsResponse(responseContent);
    }

    public async Task<string> SyncSinglePinnedResultToOptimizelyGraphAsync(PinnedResult pinnedResult, string graphCollectionId)
    {
        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections/{graphCollectionId}/items";

        var graphItem = CreateGraphPinnedResultItem(pinnedResult);
        var jsonString = JsonSerializer.Serialize(graphItem, _jsonOptions);

        using var request = CreateAuthenticatedRequest(HttpMethod.Post, graphApiUrl, authenticationHeader);
        request.Content = new StringContent(jsonString, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new GraphSyncException($"Error syncing pinned result to Optimizely Graph: {response.StatusCode} - {errorContent}");
        }

        // Parse the response to get the Graph ID
        var responseContent = await response.Content.ReadAsStringAsync();
        var graphResponse = JsonSerializer.Deserialize<GraphPinnedResultResponse>(responseContent, _jsonOptions);

        return graphResponse?.Id ?? string.Empty;
    }

    public async Task<bool> UpdateSinglePinnedResultInOptimizelyGraphAsync(PinnedResult pinnedResult, string graphCollectionId, string graphItemId)
    {
        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections/{graphCollectionId}/items/{graphItemId}";

        var graphItem = CreateGraphPinnedResultItem(pinnedResult);
        var jsonString = JsonSerializer.Serialize(graphItem, _jsonOptions);

        using var request = CreateAuthenticatedRequest(HttpMethod.Put, graphApiUrl, authenticationHeader);
        request.Content = new StringContent(jsonString, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new GraphSyncException($"Error updating pinned result in Optimizely Graph: {response.StatusCode} - {errorContent}");
        }

        return true;
    }

    public async Task<int> SyncPinnedResultsFromOptimizelyGraphAsync(Guid collectionId)
    {
        // Use reconciliation sync to prevent duplicates when syncing to->from Graph
        // This matches items by GraphId and updates existing records instead of deleting and recreating
        return await SyncPinnedResultsFromOptimizelyGraphWithReconciliationAsync(collectionId);
    }

    private (string gatewayUrl, string hmacKey, string hmacSecret) GetAndValidateGraphConfiguration()
    {
        var gatewayUrl = _configurationService.GetGatewayUrl();
        var hmacKey = _configurationService.GetAppKey();
        var hmacSecret = _configurationService.GetSecret();

        _graphConfigurationValidator.ValidateConfiguration(gatewayUrl, hmacKey, hmacSecret);

        return (gatewayUrl, hmacKey, hmacSecret);
    }


    private async Task<IList<PinnedResult>> GetPinnedResultsForSync(Guid collectionId)
    {
        var pinnedResults = await _pinnedResultService.GetAllPinnedResultsAsync(collectionId);
        if (!pinnedResults.Any())
            throw new InvalidOperationException("No pinned results found to sync to Optimizely Graph.");
        
        return pinnedResults.ToList();
    }

    private static object CreateGraphCollectionRequest(PinnedResultsCollection collection)
    {
        return new
        {
            key = collection.Id.ToString(),
            title = collection.Title,
            isActive = collection.IsActive
        };
    }

    private static object CreateGraphPinnedResultItem(PinnedResult pinnedResult)
    {
        return new
        {
            phrases = pinnedResult.Phrases?.Trim() ?? string.Empty,
            targetKey = pinnedResult.TargetKey,
            language = pinnedResult.Language,
            priority = pinnedResult.Priority,
            isActive = pinnedResult.IsActive
        };
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, string authenticationHeader)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authenticationHeader);
        return request;
    }

    private async Task UpdateCollectionWithGraphId(PinnedResultsCollection collection, HttpResponseMessage response)
    {
        var responseContent = await response.Content.ReadAsStringAsync();
        try
        {
            var graphCollection = JsonSerializer.Deserialize<GraphCollectionResponse>(responseContent, _jsonOptions);
            if (!string.IsNullOrEmpty(graphCollection?.Id))
            {
                await _collectionService.UpdateGraphCollectionIdAsync(collection.Id, graphCollection.Id);
            }
        }
        catch (JsonException)
        {
            throw new GraphSyncException($"Failed to parse Graph collection response: {responseContent}");
        }
    }

    private IList<GraphCollectionResponse> DeserializeGraphCollectionsResponse(string responseContent)
    {
        try
        {
            var graphCollections = JsonSerializer.Deserialize<List<GraphCollectionResponse>>(responseContent, _jsonOptions);
            return graphCollections?.OrderBy(c => c.Title).ToList() ?? new List<GraphCollectionResponse>();
        }
        catch (JsonException)
        {
            throw new GraphSyncException($"Failed to parse Graph collections response: {responseContent[..Math.Min(500, responseContent.Length)]}");
        }
    }

    private IList<GraphPinnedResultResponse> DeserializeGraphPinnedResultsResponse(string responseContent)
    {
        try
        {
            var graphPinnedResults = JsonSerializer.Deserialize<List<GraphPinnedResultResponse>>(responseContent, _jsonOptions);
            return graphPinnedResults ?? new List<GraphPinnedResultResponse>();
        }
        catch (JsonException)
        {
            throw new GraphSyncException($"Failed to parse Graph pinned results response: {responseContent[..Math.Min(500, responseContent.Length)]}");
        }
    }

    /// <summary>
    /// Updates a pinned result in Graph by deleting the old one and creating a new one.
    /// This is necessary because Graph API does not support direct updates.
    /// </summary>
    public async Task<string> UpdatePinnedResultInGraphAsync(PinnedResult pinnedResult, string graphCollectionId)
    {
        // Delete the old item first
        if (!string.IsNullOrEmpty(pinnedResult.GraphId))
        {
            try
            {
                await DeletePinnedResultFromOptimizelyGraphAsync(graphCollectionId, pinnedResult.GraphId);
            }
            catch (Exception ex)
            {
                // Log but continue - we'll create the new one anyway
                // This handles the case where the old item no longer exists in Graph
                _logger.LogWarning($"Failed to delete old pinned result {pinnedResult.GraphId} during update. " +
                    $"CollectionId: {graphCollectionId}, Error: {ex.Message}. Proceeding with creation of new item.");
            }
        }

        // Create the new item
        return await SyncSinglePinnedResultToOptimizelyGraphAsync(pinnedResult, graphCollectionId);
    }

    /// <summary>
    /// Activates (creates) a pinned result in Graph.
    /// </summary>
    public async Task<string> ActivatePinnedResultInGraphAsync(PinnedResult pinnedResult, string graphCollectionId)
    {
        return await SyncSinglePinnedResultToOptimizelyGraphAsync(pinnedResult, graphCollectionId);
    }

    /// <summary>
    /// Deactivates (deletes) a pinned result from Graph.
    /// </summary>
    public async Task<bool> DeactivatePinnedResultFromGraphAsync(string graphCollectionId, string graphId)
    {
        return await DeletePinnedResultFromOptimizelyGraphAsync(graphCollectionId, graphId);
    }

    /// <summary>
    /// Performs full reconciliation sync from Graph using GraphId as the key, with content-based fallback.
    /// Preserves local-only metadata (CreatedBy, CreatedAt, TargetName).
    /// Uses an upsert strategy: matches by GraphId first, then by content (phrases+targetKey+language).
    /// </summary>
    public async Task<int> SyncPinnedResultsFromOptimizelyGraphWithReconciliationAsync(Guid collectionId)
    {
        var collection = await _collectionService.GetCollectionByIdAsync(collectionId);
        if (collection == null || string.IsNullOrEmpty(collection.GraphCollectionId))
            throw new InvalidOperationException("Collection has no Graph collection ID. Please sync the collection to Graph first.");

        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections/{collection.GraphCollectionId}/items";

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, graphApiUrl, authenticationHeader);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new GraphSyncException($"Failed to fetch pinned results from Optimizely Graph: {response.StatusCode} - {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var graphPinnedResults = DeserializeGraphPinnedResultsResponse(responseContent);

        // Get all existing local results for this collection
        var existingLocalResults = await _pinnedResultService.GetPinnedResultsByCollectionIdAsync(collectionId);
        var localByGraphId = existingLocalResults
            .Where(r => !string.IsNullOrEmpty(r.GraphId))
            .ToDictionary(r => r.GraphId!, r => r);

        // Track which local items have been matched (for content-based matching)
        var matchedLocalItems = new HashSet<Guid>();

        // Track GraphIds seen in Graph to detect deleted items
        var graphIdsInGraph = new HashSet<string>();

        // Reconcile: update or create for each Graph item
        int syncedCount = 0;
        foreach (var graphItem in graphPinnedResults)
        {
            if (string.IsNullOrEmpty(graphItem.Id))
                continue;

            graphIdsInGraph.Add(graphItem.Id);

            if (localByGraphId.TryGetValue(graphItem.Id, out var localItem))
            {
                // Match by GraphId - update existing local record, preserving local-only metadata
                localItem.Phrases = graphItem.Phrases ?? string.Empty;
                localItem.TargetKey = graphItem.TargetKey ?? string.Empty;
                localItem.Language = graphItem.Language ?? "en";
                localItem.Priority = graphItem.Priority;
                localItem.IsActive = graphItem.IsActive;
                // Preserve: CreatedBy, CreatedAt, TargetName (local-only fields)

                try
                {
                    await _pinnedResultService.UpdatePinnedResultAsync(
                        localItem.Id,
                        localItem.Phrases,
                        localItem.TargetKey,
                        localItem.TargetName,
                        localItem.Language,
                        localItem.Priority,
                        localItem.IsActive);
                }
                catch (Exception ex)
                {
                    // Log but continue if update fails - item will be retried on next sync
                }

                matchedLocalItems.Add(localItem.Id);
                syncedCount++;
            }
            else
            {
                // Try content-based matching: phrases + targetKey + language
                var contentMatchedItem = FindLocalItemByContent(
                    existingLocalResults.Where(x => !matchedLocalItems.Contains(x.Id)).ToList(),
                    graphItem.Phrases ?? string.Empty,
                    graphItem.TargetKey ?? string.Empty,
                    graphItem.Language ?? "en");

                if (contentMatchedItem != null && !matchedLocalItems.Contains(contentMatchedItem.Id))
                {
                    // Match by content - update existing and link GraphId
                    contentMatchedItem.Phrases = graphItem.Phrases ?? string.Empty;
                    contentMatchedItem.TargetKey = graphItem.TargetKey ?? string.Empty;
                    contentMatchedItem.Language = graphItem.Language ?? "en";
                    contentMatchedItem.Priority = graphItem.Priority;
                    contentMatchedItem.IsActive = graphItem.IsActive;
                    contentMatchedItem.GraphId = graphItem.Id; // Link the GraphId

                    try
                    {
                        await _pinnedResultService.UpdatePinnedResultAsync(
                            contentMatchedItem.Id,
                            contentMatchedItem.Phrases,
                            contentMatchedItem.TargetKey,
                            contentMatchedItem.TargetName,
                            contentMatchedItem.Language,
                            contentMatchedItem.Priority,
                            contentMatchedItem.IsActive);

                        // Update GraphId separately using the already-loaded entity to avoid concurrency issues
                        await _pinnedResultService.UpdatePinnedResultGraphIdAsync(contentMatchedItem, graphItem.Id);
                    }
                    catch (Exception ex)
                    {
                        // Log but continue if update fails - item will be retried on next sync
                        // This prevents one failure from blocking the entire sync
                    }

                    matchedLocalItems.Add(contentMatchedItem.Id);
                    syncedCount++;
                }
                else
                {
                    // No match found - create new local record for this Graph item
                    try
                    {
                        await _pinnedResultService.CreatePinnedResultAsync(
                            collectionId,
                            graphItem.Phrases ?? string.Empty,
                            graphItem.TargetKey ?? string.Empty,
                            null, // No local TargetName from Graph
                            graphItem.Language ?? "en",
                            graphItem.Priority,
                            graphItem.IsActive,
                            "Optimizely Graph Sync",
                            graphItem.Id);

                        syncedCount++;
                    }
                    catch (Exception ex)
                    {
                        // Log but continue if create fails - item will be retried on next sync
                    }
                }
            }
        }

        // Note: We do not delete local items that are not in Graph
        // This preserves local work-in-progress items and avoids data loss
        // Users can manually delete if they want to clean up

        return syncedCount;
    }

    /// <summary>
    /// Fetches all pinned results from Graph for a collection.
    /// Used for content-based matching.
    /// </summary>
    private async Task<IList<GraphPinnedResultResponse>> FetchAllGraphItemsAsync(string graphCollectionId)
    {
        if (string.IsNullOrWhiteSpace(graphCollectionId))
            throw new ArgumentException("graphCollectionId cannot be null or empty", nameof(graphCollectionId));

        var (gatewayUrl, hmacKey, hmacSecret) = GetAndValidateGraphConfiguration();

        var authenticationHeader = (hmacKey + ":" + hmacSecret).Base64Encode();
        var encodedCollectionId = Uri.EscapeDataString(graphCollectionId.Trim());
        var graphApiUrl = $"{gatewayUrl}/api/pinned/collections/{encodedCollectionId}/items";

        _logger.LogInformation($"Fetching all pinned results from Graph collection. CollectionId: {encodedCollectionId}");

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, graphApiUrl, authenticationHeader);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"Failed to fetch pinned results from Graph. URL: {graphApiUrl}, Status: {response.StatusCode}, Response: {errorContent}");
            throw new GraphSyncException($"Failed to fetch pinned results from Optimizely Graph: {response.StatusCode} - {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        return DeserializeGraphPinnedResultsResponse(responseContent);
    }

    /// <summary>
    /// Finds a Graph item by content matching (phrases, targetKey, language).
    /// Used to detect if an item already exists in Graph before creating a duplicate.
    /// </summary>
    private GraphPinnedResultResponse? FindGraphItemByContent(
        IList<GraphPinnedResultResponse> graphItems,
        string phrases,
        string targetKey,
        string language)
    {
        if (string.IsNullOrEmpty(phrases) || string.IsNullOrEmpty(targetKey))
            return null;

        // Normalize for comparison (trim whitespace)
        var normalizedPhrases = phrases.Trim();
        var normalizedTargetKey = targetKey.Trim();
        var normalizedLanguage = (language ?? "en").Trim();

        return graphItems.FirstOrDefault(item =>
            !string.IsNullOrEmpty(item.Phrases) &&
            !string.IsNullOrEmpty(item.TargetKey) &&
            item.Phrases.Trim().Equals(normalizedPhrases, StringComparison.OrdinalIgnoreCase) &&
            item.TargetKey.Trim().Equals(normalizedTargetKey, StringComparison.OrdinalIgnoreCase) &&
            (item.Language ?? "en").Trim().Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds a local item by content matching (phrases, targetKey, language).
    /// Used to detect if an item already exists locally before creating a duplicate from Graph.
    /// </summary>
    private PinnedResult? FindLocalItemByContent(
        IList<PinnedResult> localItems,
        string phrases,
        string targetKey,
        string language)
    {
        if (string.IsNullOrEmpty(phrases) || string.IsNullOrEmpty(targetKey))
            return null;

        // Normalize for comparison (trim whitespace)
        var normalizedPhrases = phrases.Trim();
        var normalizedTargetKey = targetKey.Trim();
        var normalizedLanguage = (language ?? "en").Trim();

        return localItems.FirstOrDefault(item =>
            !string.IsNullOrEmpty(item.Phrases) &&
            !string.IsNullOrEmpty(item.TargetKey) &&
            item.Phrases.Trim().Equals(normalizedPhrases, StringComparison.OrdinalIgnoreCase) &&
            item.TargetKey.Trim().Equals(normalizedTargetKey, StringComparison.OrdinalIgnoreCase) &&
            item.Language.Trim().Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase));
    }
}
