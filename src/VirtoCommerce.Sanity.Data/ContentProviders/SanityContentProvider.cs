using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.ContentProviders;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Sanity.Core;
using VirtoCommerce.Sanity.Core.Models;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.Sanity.Data.Services;
using VirtoCommerce.SearchModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;

namespace VirtoCommerce.Sanity.Data.ContentProviders;

public class SanityContentProvider(
    ISanityApiClient apiClient,
    ISanityConverter sanityConverter,
    SanityLinkResolver sanityLinkResolver,
    IStoreSearchService storeSearchService,
    ISettingsManager settingsManager,
    ILogger<SanityContentProvider> logger)
    : IPageContentProvider
{
    public string ProviderName => "Sanity";
    public bool SupportsReindexation => true;

    private const string DefaultDatasetName = "production";

    public async Task<PageChangesSearchResult> SearchChangesAsync(PageChangesSearchCriteria criteria)
    {
        var allChanges = new List<IndexDocumentChange>();
        var processedProjects = new HashSet<string>();

        await ForEachStoreAsync(async (projects, _) =>
        {
            var seenDocumentIds = new HashSet<string>();

            foreach (var project in projects)
            {
                var projectKey = $"{project.ProjectId}:{string.Join("|", project.Datasets.Select(x => $"{x.Name}:{string.Join(",", x.DocumentTypes)}"))}";
                if (!processedProjects.Add(projectKey))
                {
                    continue;
                }

                foreach (var dataset in project.Datasets)
                {
                    var query = BuildChangesQuery(dataset.DocumentTypes, criteria.StartDate, criteria.EndDate);
                    var response = await apiClient.QueryAsync(project.ProjectId, dataset.Name, project.ApiToken, query);

                    foreach (var doc in response.Results)
                    {
                        var documentId = doc["_id"]?.ToString();

                        // The same document changed in several sources produces a single change;
                        // the winning content is chosen at indexing time
                        if (documentId == null || !seenDocumentIds.Add(documentId))
                        {
                            continue;
                        }

                        allChanges.Add(new IndexDocumentChange
                        {
                            DocumentId = documentId,
                            ChangeDate = doc["_updatedAt"]?.ToObject<DateTime>() ?? DateTime.UtcNow,
                            ChangeType = IndexDocumentChangeType.Modified,
                        });
                    }
                }
            }
        });

        var ordered = allChanges.OrderByDescending(x => x.ChangeDate).ToList();

        return new PageChangesSearchResult
        {
            TotalCount = ordered.Count,
            Results = ordered.Skip(criteria.Skip).Take(criteria.Take).ToList(),
        };
    }

    public async Task<IList<PageDocument>> GetByIdsAsync(IList<string> ids)
    {
        var result = new List<PageDocument>();
        var processedIds = new HashSet<string>();

        await ForEachStoreAsync(async (projects, storeId) =>
        {
            var remainingIds = ids.Where(id => !processedIds.Contains(id)).ToList();
            if (remainingIds.Count == 0)
            {
                return;
            }

            var documents = await GetDocumentsAsync(projects, storeId, remainingIds);

            foreach (var doc in documents)
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

    private async Task<IList<JObject>> GetDocumentsAsync(IList<SanityProject> projects, string storeId, IList<string> ids)
    {
        // Every source (project + dataset) is queried with the full id list so conflicts (the same
        // document id in several sources) are detected and logged; the higher-priority source wins.
        var winningDocuments = new Dictionary<string, (JObject Document, string ProjectId, string Dataset)>();
        var idsFilter = string.Join(", ", ids.Select(id => $"\"{id}\""));

        foreach (var project in projects)
        {
            foreach (var dataset in project.Datasets)
            {
                var query = $"*[{BuildTypeFilter(dataset.DocumentTypes)} && _id in [{idsFilter}]]";
                var response = await apiClient.QueryAsync(project.ProjectId, dataset.Name, project.ApiToken, query);

                var datasetDocuments = new List<JObject>();

                foreach (var document in response.Results)
                {
                    var documentId = document["_id"]?.ToString();
                    if (documentId == null)
                    {
                        continue;
                    }

                    if (winningDocuments.TryGetValue(documentId, out var winner))
                    {
                        logger.LogWarning(
                            "Sanity conflict in store '{StoreId}': document '{DocumentId}' exists in project '{WinningProjectId}' dataset '{WinningDataset}' and in project '{ConflictingProjectId}' dataset '{ConflictingDataset}'. The document from the higher-priority source wins.",
                            storeId, documentId, winner.ProjectId, winner.Dataset, project.ProjectId, dataset.Name);
                        continue;
                    }

                    winningDocuments.Add(documentId, (document, project.ProjectId, dataset.Name));
                    datasetDocuments.Add(document);
                }

                if (datasetDocuments.Count > 0)
                {
                    await sanityLinkResolver.ResolveLinksAsync(project.ProjectId, dataset.Name, project.ApiToken, datasetDocuments);
                }
            }
        }

        return winningDocuments.Values.Select(x => x.Document).ToList();
    }

    private static string BuildChangesQuery(IList<string> documentTypes, DateTime? startDate, DateTime? endDate)
    {
        var dateFilter = BuildDateFilter(startDate, endDate);
        return $"*[{BuildTypeFilter(documentTypes)}{dateFilter}]{{_id, _updatedAt}} | order(_updatedAt desc)";
    }

    private static string BuildTypeFilter(IList<string> documentTypes)
    {
        if (documentTypes.Count == 1)
        {
            return $"_type == \"{documentTypes[0]}\"";
        }

        var typesFilter = string.Join(", ", documentTypes.Select(type => $"\"{type}\""));
        return $"_type in [{typesFilter}]";
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

    private async Task ForEachStoreAsync(Func<IList<SanityProject>, string, Task> action)
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

    private async Task TryProcessStoreAsync(string storeId, Func<IList<SanityProject>, string, Task> action)
    {
        var settings = (await settingsManager.GetObjectSettingsAsync(
        [
            ModuleConstants.Settings.General.Enabled.Name,
            ModuleConstants.Settings.General.ProjectId.Name,
            ModuleConstants.Settings.General.Dataset.Name,
            ModuleConstants.Settings.General.ApiToken.Name,
            ModuleConstants.Settings.General.DocumentTypes.Name,
            ModuleConstants.Settings.General.Projects.Name,
            ModuleConstants.Settings.General.PageType.Name,
        ], "Store", storeId)).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        if (!GetSettingValue<bool>(settings, ModuleConstants.Settings.General.Enabled.Name))
        {
            return;
        }

        var projects = GetProjects(settings, storeId);
        if (projects.Count == 0)
        {
            return;
        }

        await action(projects, storeId);
    }

    private IList<SanityProject> GetProjects(Dictionary<string, ObjectSettingEntry> settings, string storeId)
    {
        var projects = new List<SanityProject>();

        var rawProjects = settings.TryGetValue(ModuleConstants.Settings.General.Projects.Name, out var entry)
            ? entry?.Value?.ToString()
            : null;

        var defaultApiToken = GetSettingValue<string>(settings, ModuleConstants.Settings.General.ApiToken.Name);

        if (!string.IsNullOrWhiteSpace(rawProjects))
        {
            try
            {
                var configuredProjects = JsonConvert.DeserializeObject<List<SanityProject>>(rawProjects) ?? [];

                foreach (var project in configuredProjects)
                {
                    if (TryPrepareProject(project, settings, storeId, defaultApiToken))
                    {
                        projects.Add(project);
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex,
                    "Store '{StoreId}': the '{SettingName}' setting contains invalid JSON. Falling back to the single project from the '{ProjectIdSettingName}' setting.",
                    storeId, ModuleConstants.Settings.General.Projects.Name, ModuleConstants.Settings.General.ProjectId.Name);
                projects.Clear();
            }
        }

        if (projects.Count == 0)
        {
            // Legacy single-project configuration from the ProjectId, Dataset and DocumentTypes settings
            var projectId = GetSettingValue<string>(settings, ModuleConstants.Settings.General.ProjectId.Name);
            if (!string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(defaultApiToken))
            {
                var dataset = GetSettingValue<string>(settings, ModuleConstants.Settings.General.Dataset.Name);
                projects.Add(new SanityProject
                {
                    ProjectId = projectId,
                    ApiToken = defaultApiToken,
                    Datasets =
                    [
                        new SanityDataset
                        {
                            Name = string.IsNullOrEmpty(dataset) ? DefaultDatasetName : dataset,
                            DocumentTypes = GetDocumentTypes(settings),
                        },
                    ],
                });
            }
        }

        return projects;
    }

    // Validates a deserialized project and normalizes it for querying: fills the default API token,
    // drops nameless datasets, fills inherited document types, and orders datasets so the ones
    // marked as priority come first
    private bool TryPrepareProject(SanityProject project, Dictionary<string, ObjectSettingEntry> settings, string storeId, string defaultApiToken)
    {
        if (string.IsNullOrEmpty(project.ApiToken))
        {
            project.ApiToken = defaultApiToken;
        }

        if (string.IsNullOrEmpty(project.ProjectId) || string.IsNullOrEmpty(project.ApiToken))
        {
            logger.LogWarning(
                "Store '{StoreId}': an entry in the '{SettingName}' setting is skipped because it has no project id or no API token.",
                storeId, ModuleConstants.Settings.General.Projects.Name);
            return false;
        }

        var datasets = (project.Datasets ?? [])
            .Where(x => !string.IsNullOrEmpty(x?.Name))
            // Stable sort: datasets marked as priority go first, the rest keep their configured order
            .OrderByDescending(x => x.IsPriority)
            .ToList();

        if (datasets.Count == 0)
        {
            datasets.Add(new SanityDataset { Name = DefaultDatasetName });
        }

        // A dataset without its own types inherits the store-wide DocumentTypes setting
        foreach (var dataset in datasets)
        {
            dataset.DocumentTypes = NormalizeDocumentTypes(dataset.DocumentTypes ?? []);

            if (dataset.DocumentTypes.Length == 0)
            {
                dataset.DocumentTypes = GetDocumentTypes(settings);
            }
        }

        project.Datasets = datasets;

        return true;
    }

    private static string[] GetDocumentTypes(Dictionary<string, ObjectSettingEntry> settings)
    {
        var documentTypes = GetSettingValue<string>(settings, ModuleConstants.Settings.General.DocumentTypes.Name);

        // Fall back to the legacy single-type setting for stores configured before DocumentTypes was introduced
        if (string.IsNullOrWhiteSpace(documentTypes))
        {
            documentTypes = GetSettingValue<string>(settings, ModuleConstants.Settings.General.PageType.Name);
        }

        var types = NormalizeDocumentTypes((documentTypes ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries));
        return types.Length > 0 ? types : ["page"];
    }

    private static string[] NormalizeDocumentTypes(IEnumerable<string> documentTypes)
    {
        return documentTypes
            .Select(type => type?.Trim())
            .Where(type => !string.IsNullOrEmpty(type))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static T GetSettingValue<T>(Dictionary<string, ObjectSettingEntry> settings, string name)
    {
        return settings.TryGetValue(name, out var entry) && entry?.Value is T value ? value : default;
    }
}
