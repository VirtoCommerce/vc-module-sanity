using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Sanity.Core;
using VirtoCommerce.Sanity.Core.Models;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.SearchModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;

namespace VirtoCommerce.Sanity.Data.ContentProviders;

public partial class SanityContentProvider(
    ISanityApiClient apiClient,
    ISanityConverter sanityConverter,
    ISanityLinkResolver sanityLinkResolver,
    IStoreSearchService storeSearchService,
    ISettingsManager settingsManager,
    IOptions<SanityOptions> options,
    ILogger<SanityContentProvider> logger)
    : ISanityContentProvider
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

    /// <summary>
    /// Fetches documents from the Sanity sources of every configured store and converts them to page
    /// documents with internal links and asset URLs resolved.
    /// </summary>
    public Task<IList<PageDocument>> GetByIdsAsync(IList<string> ids)
    {
        return GetByIdsAsync(ids, projectId: null, dataset: null);
    }

    /// <summary>
    /// Fetches documents the same way as indexing does, optionally restricted to a single Sanity
    /// project and dataset. A webhook names its source in the sanity-project-id and sanity-dataset
    /// headers, which keeps the lookup to the source the notification came from instead of every
    /// configured one, and makes the same document id in another project irrelevant.
    /// </summary>
    public virtual async Task<IList<PageDocument>> GetByIdsAsync(IList<string> ids, string projectId, string dataset)
    {
        var result = new List<PageDocument>();
        var processedIds = new HashSet<string>();

        var validIds = GetValidIds(ids);
        if (validIds.Count == 0)
        {
            return result;
        }

        await ForEachStoreAsync(async (projects, sourceStoreId) =>
        {
            var remainingIds = validIds.Where(id => !processedIds.Contains(id)).ToList();
            var sources = FilterSources(projects, projectId, dataset);

            if (remainingIds.Count == 0 || sources.Count == 0)
            {
                return;
            }

            var documents = await GetDocumentsAsync(sources, sourceStoreId, remainingIds);

            // A document is taken from the first store that has it
            result.AddRange(documents
                .Where(doc => doc["_id"] != null && processedIds.Add(doc["_id"].ToString()))
                .Select(doc => ToPageDocument(doc, sourceStoreId))
                .Where(pageDocument => pageDocument != null));
        },
        // so the remaining stores need not be read once every id is found
        isDone: () => processedIds.Count >= validIds.Count,
        logConfigurationWarnings: string.IsNullOrEmpty(projectId));

        return result;
    }

    // Ids are interpolated into GROQ filters, so a value outside the identifier charset is dropped
    private List<string> GetValidIds(IList<string> ids)
    {
        var invalidIds = ids.Where(id => !SanityIdentifier.IsValid(id)).ToList();
        if (invalidIds.Count > 0)
        {
            logger.LogWarning("Sanity document ids {DocumentIds} are not valid Sanity identifiers and are skipped.", invalidIds);
        }

        return ids.Except(invalidIds).Distinct().ToList();
    }

    private PageDocument ToPageDocument(JObject document, string sourceStoreId)
    {
        var pageDocument = sanityConverter.GetPageDocument(sourceStoreId, null, Pages.Core.Events.PageOperation.Publish, document, null);

        if (pageDocument != null && pageDocument.StoreId.IsNullOrEmpty())
        {
            pageDocument.StoreId = sourceStoreId;
        }

        return pageDocument;
    }

    public virtual async Task<IList<string>> GetStoreIdsAsync(string projectId, string dataset)
    {
        var storeIds = new List<string>();

        if (string.IsNullOrEmpty(projectId))
        {
            return storeIds;
        }

        await ForEachStoreAsync((projects, storeId) =>
        {
            if (FilterSources(projects, projectId, dataset).Count > 0)
            {
                storeIds.Add(storeId);
            }

            return Task.CompletedTask;
        },
        logConfigurationWarnings: false);

        return storeIds;
    }

    /// <summary>
    /// Narrows the configured sources to the project and dataset a notification came from.
    /// Without a project the sources are returned as they are, which is what indexing needs.
    /// </summary>
    private static IList<SanityProject> FilterSources(IList<SanityProject> projects, string projectId, string dataset)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            return projects;
        }

        return projects
            .Where(x => x.ProjectId.EqualsIgnoreCase(projectId))
            .Select(x => new SanityProject
            {
                ProjectId = x.ProjectId,
                ApiToken = x.ApiToken,
                Datasets = string.IsNullOrEmpty(dataset)
                    ? x.Datasets
                    : x.Datasets.Where(d => d.Name.EqualsIgnoreCase(dataset)).ToList(),
            })
            .Where(x => x.Datasets.Count > 0)
            .ToList();
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

    /// <summary>
    /// Runs the action for every enabled store with Sanity sources. Problems in a store's Sanity settings
    /// are logged only when <paramref name="logConfigurationWarnings"/> is set: indexing reports them,
    /// while a webhook delivery reads every store's settings and would repeat them on each notification.
    /// </summary>
    private async Task ForEachStoreAsync(Func<IList<SanityProject>, string, Task> action, Func<bool> isDone = null, bool logConfigurationWarnings = true)
    {
        ILogger configurationLogger = logConfigurationWarnings ? logger : NullLogger.Instance;

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
                await TryProcessStoreAsync(store.Id, action, configurationLogger);

                if (isDone?.Invoke() == true)
                {
                    return;
                }
            }

            criteria.Skip += storeBatchSize;
        }
        while (criteria.Skip < storeCount);
    }

    private async Task TryProcessStoreAsync(string storeId, Func<IList<SanityProject>, string, Task> action, ILogger configurationLogger)
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

        var projects = GetProjects(settings, storeId, configurationLogger);
        if (projects.Count == 0)
        {
            return;
        }

        await action(projects, storeId);
    }

    private List<SanityProject> GetProjects(Dictionary<string, ObjectSettingEntry> settings, string storeId, ILogger configurationLogger)
    {
        var projects = new List<SanityProject>();

        var rawProjects = settings.TryGetValue(ModuleConstants.Settings.General.Projects.Name, out var entry)
            ? entry?.Value?.ToString()
            : null;

        var defaultApiToken = ResolveSecrets(GetSettingValue<string>(settings, ModuleConstants.Settings.General.ApiToken.Name), storeId, configurationLogger);

        if (!string.IsNullOrWhiteSpace(rawProjects))
        {
            try
            {
                var configuredProjects = JsonConvert.DeserializeObject<List<SanityProject>>(rawProjects) ?? [];

                projects.AddRange(configuredProjects.Where(project => TryPrepareProject(project, settings, storeId, defaultApiToken, configurationLogger)));
            }
            catch (JsonException ex)
            {
                configurationLogger.LogWarning(ex,
                    "Store '{StoreId}': the '{SettingName}' setting contains invalid JSON. Falling back to the single project from the '{ProjectIdSettingName}' setting.",
                    storeId, ModuleConstants.Settings.General.Projects.Name, ModuleConstants.Settings.General.ProjectId.Name);
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
    private bool TryPrepareProject(SanityProject project, Dictionary<string, ObjectSettingEntry> settings, string storeId, string defaultApiToken, ILogger configurationLogger)
    {
        // An entry may reference a configured secret instead of carrying the token itself;
        // an unresolved reference leaves the token empty, so the entry is skipped below
        project.ApiToken = string.IsNullOrEmpty(project.ApiToken)
            ? defaultApiToken
            : ResolveSecrets(project.ApiToken, storeId, configurationLogger);

        if (string.IsNullOrEmpty(project.ProjectId) || string.IsNullOrEmpty(project.ApiToken))
        {
            configurationLogger.LogWarning(
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

    /// <summary>
    /// Replaces "${name}" references with the matching value of the "Sanity:Secrets" configuration section,
    /// so that API tokens do not have to be stored in the settings JSON. Only names listed in that section
    /// can be resolved: a setting must not be able to read arbitrary configuration values.
    /// Returns null when a reference cannot be resolved, so the caller treats the token as missing
    /// instead of sending the literal placeholder to Sanity.
    /// </summary>
    private string ResolveSecrets(string value, string storeId, ILogger configurationLogger)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        var secrets = options.Value.Secrets;

        var missingSecretName = SecretReferenceRegex()
            .Matches(value)
            .Select(match => match.Groups[1].Value)
            .FirstOrDefault(name => !secrets.TryGetValue(name, out var secret) || string.IsNullOrEmpty(secret));

        if (missingSecretName != null)
        {
            configurationLogger.LogWarning(
                "Store '{StoreId}': secret '{SecretName}' referenced by the Sanity settings is not defined in the 'Sanity:Secrets' configuration section.",
                storeId, missingSecretName);
            return null;
        }

        return SecretReferenceRegex().Replace(value, match => secrets[match.Groups[1].Value]);
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
        // Types are interpolated into GROQ filters, so a value outside the identifier charset is dropped
        return documentTypes
            .Select(type => type?.Trim())
            .Where(SanityIdentifier.IsValid)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    [GeneratedRegex("[$][{]([^}]+)[}]")]
    private static partial Regex SecretReferenceRegex();

    private static T GetSettingValue<T>(Dictionary<string, ObjectSettingEntry> settings, string name)
    {
        return settings.TryGetValue(name, out var entry) && entry?.Value is T value ? value : default;
    }
}
