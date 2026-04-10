using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.ContentProviders;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Sanity.Core;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.SearchModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;

namespace VirtoCommerce.Sanity.Data.ContentProviders;

public class SanityContentProvider(
    ISanityApiClient apiClient,
    ISanityConverter sanityConverter,
    IStoreSearchService storeSearchService,
    ISettingsManager settingsManager)
    : IPageContentProvider
{
    public string ProviderName => "Sanity";
    public bool SupportsReindexation => true;

    public async Task<long> GetTotalChangesCountAsync(DateTime? startDate, DateTime? endDate)
    {
        long totalCount = 0;
        var processedProjects = new HashSet<string>();

        await ForEachStoreAsync(async (projectId, dataset, apiToken, pageType, _) =>
        {
            if (!processedProjects.Add($"{projectId}:{dataset}:{pageType}"))
            {
                return;
            }

            var query = BuildCountQuery(pageType, startDate, endDate);
            var response = await apiClient.QueryAsync(projectId, dataset, apiToken, query);
            var count = response.Results.FirstOrDefault()?["count"]?.Value<long>() ?? 0;
            totalCount += count;
        });

        return totalCount;
    }

    public async Task<IList<IndexDocumentChange>> GetChangesAsync(DateTime? startDate, DateTime? endDate, long skip, long take)
    {
        var allChanges = new List<IndexDocumentChange>();
        var processedProjects = new HashSet<string>();

        await ForEachStoreAsync(async (projectId, dataset, apiToken, pageType, _) =>
        {
            if (!processedProjects.Add($"{projectId}:{dataset}:{pageType}"))
            {
                return;
            }

            var query = BuildChangesQuery(pageType, startDate, endDate);
            var response = await apiClient.QueryAsync(projectId, dataset, apiToken, query);

            allChanges.AddRange(response.Results.Select(doc => new IndexDocumentChange
            {
                DocumentId = doc["_id"]?.ToString(),
                ChangeDate = doc["_updatedAt"]?.ToObject<DateTime>() ?? DateTime.UtcNow,
                ChangeType = IndexDocumentChangeType.Modified,
            }));
        });

        var normalSkip = skip > int.MaxValue ? int.MaxValue : (int)skip;
        var normalTake = take > int.MaxValue ? int.MaxValue : (int)take;

        var safeSkip = skip < 0 ? 0 : normalSkip;
        var safeTake = take < 0 ? 0 : normalTake;

        return allChanges
            .OrderByDescending(x => x.ChangeDate)
            .Skip(safeSkip)
            .Take(safeTake)
            .ToList();
    }

    public async Task<IList<PageDocument>> GetByIdsAsync(IList<string> ids)
    {
        var result = new List<PageDocument>();
        var processedIds = new HashSet<string>();

        await ForEachStoreAsync(async (projectId, dataset, apiToken, pageType, storeId) =>
        {
            var remainingIds = ids.Where(id => !processedIds.Contains(id)).ToList();
            if (remainingIds.Count == 0)
            {
                return;
            }

            var idsFilter = string.Join(", ", remainingIds.Select(id => $"\"{id}\""));
            var query = $"*[_type == \"{pageType}\" && _id in [{idsFilter}]]";
            var response = await apiClient.QueryAsync(projectId, dataset, apiToken, query);

            foreach (var doc in response.Results)
            {
                var docId = doc["_id"]?.ToString();
                if (docId == null || !processedIds.Add(docId))
                {
                    continue;
                }

                var pageDocument = sanityConverter.GetPageDocument(storeId, null, Pages.Core.Events.PageOperation.Publish, doc, null);
                if (pageDocument == null)
                {
                    continue;
                }

                if (pageDocument.StoreId.IsNullOrEmpty())
                {
                    pageDocument.StoreId = storeId;
                }

                result.Add(pageDocument);
            }
        });

        return result;
    }

    private static string BuildCountQuery(string pageType, DateTime? startDate, DateTime? endDate)
    {
        var dateFilter = BuildDateFilter(startDate, endDate);
        return $"count(*[_type == \"{pageType}\"{dateFilter}])";
    }

    private static string BuildChangesQuery(string pageType, DateTime? startDate, DateTime? endDate)
    {
        var dateFilter = BuildDateFilter(startDate, endDate);
        return $"*[_type == \"{pageType}\"{dateFilter}]{{_id, _updatedAt}} | order(_updatedAt desc)";
    }

    private static string BuildDateFilter(DateTime? startDate, DateTime? endDate)
    {
        var filters = new List<string>();

        if (startDate.HasValue)
        {
            filters.Add($" && _updatedAt >= \"{startDate.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}\"");
        }

        if (endDate.HasValue)
        {
            filters.Add($" && _updatedAt <= \"{endDate.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}\"");
        }

        return string.Concat(filters);
    }

    private async Task ForEachStoreAsync(Func<string, string, string, string, string, Task> action)
    {
        const int storeBatchSize = 50;
        var criteria = AbstractTypeFactory<StoreSearchCriteria>.TryCreateInstance();
        criteria.Take = storeBatchSize;
        criteria.Skip = 0;

        int storeCount;
        do
        {
            var storesResult = await storeSearchService.SearchAsync(criteria);
            storeCount = storesResult.TotalCount;

            foreach (var store in storesResult.Results)
            {
                await TryProcessStoreAsync(store.Id, action);
            }

            criteria.Skip += storeBatchSize;
        }
        while (criteria.Skip < storeCount);
    }

    private async Task TryProcessStoreAsync(string storeId, Func<string, string, string, string, string, Task> action)
    {
        var settings = (await settingsManager.GetObjectSettingsAsync(
        [
            ModuleConstants.Settings.General.Enabled.Name,
            ModuleConstants.Settings.General.ProjectId.Name,
            ModuleConstants.Settings.General.Dataset.Name,
            ModuleConstants.Settings.General.ApiToken.Name,
            ModuleConstants.Settings.General.PageType.Name,
        ], "Store", storeId)).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        if (!GetSettingValue<bool>(settings, ModuleConstants.Settings.General.Enabled.Name))
        {
            return;
        }

        var projectId = GetSettingValue<string>(settings, ModuleConstants.Settings.General.ProjectId.Name);
        var apiToken = GetSettingValue<string>(settings, ModuleConstants.Settings.General.ApiToken.Name);

        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(apiToken))
        {
            return;
        }

        var dataset = GetSettingValue<string>(settings, ModuleConstants.Settings.General.Dataset.Name);
        var pageType = GetSettingValue<string>(settings, ModuleConstants.Settings.General.PageType.Name);

        await action(
            projectId,
            string.IsNullOrEmpty(dataset) ? "production" : dataset,
            apiToken,
            string.IsNullOrEmpty(pageType) ? "page" : pageType,
            storeId);
    }

    private static T GetSettingValue<T>(Dictionary<string, ObjectSettingEntry> settings, string name)
    {
        return settings.TryGetValue(name, out var entry) && entry?.Value is T value ? value : default;
    }
}
