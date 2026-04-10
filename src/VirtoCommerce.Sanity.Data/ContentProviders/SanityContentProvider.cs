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

        return allChanges
            .OrderByDescending(x => x.ChangeDate)
            .Skip(Convert.ToInt32(skip))
            .Take(Convert.ToInt32(take))
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

                var pageDocument = sanityConverter.GetPageDocument(storeId, string.Empty, Pages.Core.Events.PageOperation.Publish, doc, null);
                if (pageDocument == null)
                {
                    continue;
                }

                pageDocument.Status = PageDocumentStatus.Published;

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

        int totalStores;
        do
        {
            var storesResult = await storeSearchService.SearchAsync(criteria);
            totalStores = storesResult.TotalCount;

            foreach (var store in storesResult.Results)
            {
                var settings = (await settingsManager.GetObjectSettingsAsync(new[]
                {
                    ModuleConstants.Settings.General.Enabled.Name,
                    ModuleConstants.Settings.General.ProjectId.Name,
                    ModuleConstants.Settings.General.Dataset.Name,
                    ModuleConstants.Settings.General.ApiToken.Name,
                    ModuleConstants.Settings.General.PageType.Name,
                }, "Store", store.Id)).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

                settings.TryGetValue(ModuleConstants.Settings.General.Enabled.Name, out var enabledSetting);
                if (enabledSetting?.Value is not bool enabled || !enabled)
                {
                    continue;
                }

                settings.TryGetValue(ModuleConstants.Settings.General.ProjectId.Name, out var projectIdSetting);
                settings.TryGetValue(ModuleConstants.Settings.General.Dataset.Name, out var datasetSetting);
                settings.TryGetValue(ModuleConstants.Settings.General.ApiToken.Name, out var apiTokenSetting);
                settings.TryGetValue(ModuleConstants.Settings.General.PageType.Name, out var pageTypeSetting);
                if (enabledSetting?.Value is not bool enabled || !enabled)
                {
                    continue;
                }

                storeSettings.TryGetValue(ModuleConstants.Settings.General.ProjectId.Name, out var projectIdSetting);
                storeSettings.TryGetValue(ModuleConstants.Settings.General.Dataset.Name, out var datasetSetting);
                storeSettings.TryGetValue(ModuleConstants.Settings.General.ApiToken.Name, out var apiTokenSetting);
                storeSettings.TryGetValue(ModuleConstants.Settings.General.PageType.Name, out var pageTypeSetting);

                var projectId = projectIdSetting?.Value as string;
                var apiToken = apiTokenSetting?.Value as string;

                if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(apiToken))
                {
                    continue;
                }

                var dataset = datasetSetting?.Value as string;
                var pageType = pageTypeSetting?.Value as string;

                await action(
                    projectId,
                    string.IsNullOrEmpty(dataset) ? "production" : dataset,
                    apiToken,
                    string.IsNullOrEmpty(pageType) ? "page" : pageType,
                    store.Id);
            }

            criteria.Skip += storeBatchSize;
        }
        while (criteria.Skip < totalStores);
    }
}
