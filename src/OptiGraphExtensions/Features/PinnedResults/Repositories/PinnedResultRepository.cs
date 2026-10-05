using Microsoft.EntityFrameworkCore;
using OptiGraphExtensions.Entities;

namespace OptiGraphExtensions.Features.PinnedResults.Repositories
{
    public class PinnedResultRepository : IPinnedResultRepository
    {
        private readonly IOptiGraphExtensionsDataContext _dataContext;

        public PinnedResultRepository(IOptiGraphExtensionsDataContext dataContext)
        {
            _dataContext = dataContext;
        }

        public async Task<IEnumerable<PinnedResult>> GetAllAsync(Guid? collectionId = null)
        {
            var query = _dataContext.PinnedResults.AsNoTracking();

            if (collectionId.HasValue)
            {
                query = query.Where(pr => pr.CollectionId == collectionId.Value);
            }

            return await query.OrderBy(pr => pr.Priority).ToListAsync();
        }

        public async Task<PinnedResult?> GetByIdAsync(Guid id)
        {
            return await _dataContext.PinnedResults.FindAsync(id);
        }

        public async Task<PinnedResult?> GetByIdAsNoTrackingAsync(Guid id)
        {
            return await _dataContext.PinnedResults
                .AsNoTracking()
                .FirstOrDefaultAsync(pr => pr.Id == id);
        }

        public async Task<PinnedResult?> GetByIdWithCollectionAsync(Guid id)
        {
            return await _dataContext.PinnedResults
                .Include(pr => pr.Collection)
                .FirstOrDefaultAsync(pr => pr.Id == id);
        }

        public async Task<PinnedResult> CreateAsync(PinnedResult pinnedResult)
        {
            _dataContext.PinnedResults.Add(pinnedResult);
            await _dataContext.SaveChangesAsync();
            return pinnedResult;
        }

        public async Task<PinnedResult> UpdateAsync(PinnedResult pinnedResult)
        {
            _dataContext.PinnedResults.Update(pinnedResult);
            await _dataContext.SaveChangesAsync();
            return pinnedResult;
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            var pinnedResult = await _dataContext.PinnedResults.FindAsync(id);
            if (pinnedResult == null)
            {
                return false;
            }

            try
            {
                _dataContext.PinnedResults.Remove(pinnedResult);
                await _dataContext.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Entity was modified since we loaded it
                // Re-fetch and try again
                var refetched = await _dataContext.PinnedResults.FindAsync(id);
                if (refetched == null)
                {
                    return false; // Already deleted
                }

                _dataContext.PinnedResults.Remove(refetched);
                await _dataContext.SaveChangesAsync();
                return true;
            }
        }

        public async Task<bool> DeleteByIdDirectAsync(Guid id)
        {
            System.Diagnostics.Debug.WriteLine($"[DeleteByIdDirectAsync] Preparing delete for ID: {id}");

            // If an instance with this key is already tracked, remove that one.
            // Otherwise remove a stub entity.
            var trackedEntity = _dataContext.PinnedResults.Local.FirstOrDefault(pr => pr.Id == id);
            if (trackedEntity != null)
            {
                System.Diagnostics.Debug.WriteLine($"[DeleteByIdDirectAsync] Found tracked entity. Removing tracked instance...");
                _dataContext.PinnedResults.Remove(trackedEntity);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[DeleteByIdDirectAsync] No tracked entity found. Removing stub instance...");
                _dataContext.PinnedResults.Remove(new PinnedResult { Id = id });
            }

            // Save changes
            try
            {
                System.Diagnostics.Debug.WriteLine($"[DeleteByIdDirectAsync] Calling SaveChangesAsync...");
                await _dataContext.SaveChangesAsync();
                System.Diagnostics.Debug.WriteLine($"[DeleteByIdDirectAsync] SUCCESS: SaveChangesAsync completed");
                return true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Entity was already deleted, that's fine
                System.Diagnostics.Debug.WriteLine($"[DeleteByIdDirectAsync] DbUpdateConcurrencyException caught (entity already deleted): {ex.Message}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeleteByIdDirectAsync] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> ExistsAsync(Guid id)
        {
            return await _dataContext.PinnedResults.AnyAsync(e => e.Id == id);
        }

        public async Task<bool> CollectionExistsAsync(Guid collectionId)
        {
            return await _dataContext.PinnedResultsCollections.AnyAsync(c => c.Id == collectionId);
        }

        public async Task<IEnumerable<PinnedResult>> GetByCollectionIdAsync(Guid collectionId)
        {
            return await _dataContext.PinnedResults
                .AsNoTracking()
                .Where(pr => pr.CollectionId == collectionId)
                .OrderBy(pr => pr.Priority)
                .ToListAsync();
        }

        public async Task<bool> DeleteByCollectionIdAsync(Guid collectionId)
        {
            var relatedPinnedResults = await _dataContext.PinnedResults
                .Where(pr => pr.CollectionId == collectionId)
                .ToListAsync();
            
            if (relatedPinnedResults.Any())
            {
                _dataContext.PinnedResults.RemoveRange(relatedPinnedResults);
                await _dataContext.SaveChangesAsync();
                return true;
            }

            return true;
        }
    }
}