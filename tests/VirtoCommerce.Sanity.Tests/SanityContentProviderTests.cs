using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Sanity.Core;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.Sanity.Data.ContentProviders;
using VirtoCommerce.Sanity.Data.Services;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;
using Xunit;

namespace VirtoCommerce.Sanity.Tests;

[Trait("Category", "Unit")]
public class SanityContentProviderTests
{
    private const string StoreId = "store1";

    [Fact]
    public async Task GetByIdsAsync_ConflictingDocument_PriorityDatasetWinsAndConflictIsLogged()
    {
        var apiClient = new FakeSanityApiClient();
        apiClient.ResultsByDataset["draft"] = [CreatePageDocument("page-1", "Draft title")];
        apiClient.ResultsByDataset["production"] = [CreatePageDocument("page-1", "Production title")];

        var logger = new InMemoryLogger();
        var provider = CreateProvider(apiClient, logger, new Dictionary<string, object>
        {
            [ModuleConstants.Settings.General.Projects.Name] = """
            [{ "projectId": "project1", "datasets": { "draft": "page", "production": "page" }, "priorityDataset": "production" }]
            """,
        });

        var result = await provider.GetByIdsAsync(["page-1"]);

        var pageDocument = Assert.Single(result);
        Assert.Equal("Production title", pageDocument.Title);

        // "production" is configured second but must be queried first as the priority dataset
        Assert.Equal(["production", "draft"], apiClient.Requests.Select(x => x.Dataset).ToArray());

        var conflict = Assert.Single(logger.Entries, x => x.Level == LogLevel.Warning);
        Assert.Contains("page-1", conflict.Message);
        Assert.Contains("production", conflict.Message);
        Assert.Contains("draft", conflict.Message);
    }

    [Fact]
    public async Task GetByIdsAsync_NoConflicts_NothingIsLogged()
    {
        var apiClient = new FakeSanityApiClient();
        apiClient.ResultsByDataset["production"] = [CreatePageDocument("page-1", "Page")];
        apiClient.ResultsByDataset["marketing"] = [CreatePageDocument("landing-1", "Landing")];

        var logger = new InMemoryLogger();
        var provider = CreateProvider(apiClient, logger, new Dictionary<string, object>
        {
            [ModuleConstants.Settings.General.Projects.Name] = """
            [{ "projectId": "project1", "datasets": { "production": "page", "marketing": "landing" } }]
            """,
        });

        var result = await provider.GetByIdsAsync(["page-1", "landing-1"]);

        Assert.Equal(2, result.Count);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task GetByIdsAsync_EachDatasetIsQueriedWithItsOwnDocumentTypes()
    {
        var apiClient = new FakeSanityApiClient();
        var provider = CreateProvider(apiClient, new InMemoryLogger(), new Dictionary<string, object>
        {
            [ModuleConstants.Settings.General.Projects.Name] = """
            [{ "projectId": "project1", "datasets": { "production": "page", "marketing": ["landing", "blog"] } }]
            """,
        });

        await provider.GetByIdsAsync(["page-1"]);

        var productionQuery = Assert.Single(apiClient.Requests, x => x.Dataset == "production").Query;
        Assert.Contains("_type == \"page\"", productionQuery);

        var marketingQuery = Assert.Single(apiClient.Requests, x => x.Dataset == "marketing").Query;
        Assert.Contains("_type in [\"landing\", \"blog\"]", marketingQuery);
    }

    [Fact]
    public async Task GetByIdsAsync_NoProjectsSetting_FallsBackToLegacySingleProjectSettings()
    {
        var apiClient = new FakeSanityApiClient();
        apiClient.ResultsByDataset["staging"] = [CreatePageDocument("page-1", "Page")];

        var provider = CreateProvider(apiClient, new InMemoryLogger(), new Dictionary<string, object>
        {
            [ModuleConstants.Settings.General.Dataset.Name] = "staging",
            [ModuleConstants.Settings.General.DocumentTypes.Name] = "page",
        });

        var result = await provider.GetByIdsAsync(["page-1"]);

        Assert.Single(result);
        var request = Assert.Single(apiClient.Requests);
        Assert.Equal("staging", request.Dataset);
    }

    [Fact]
    public async Task GetByIdsAsync_ProjectEntryWithoutDatasets_UsesDefaultDatasetAndTypes()
    {
        var apiClient = new FakeSanityApiClient();
        apiClient.ResultsByDataset["projA:production"] = [CreatePageDocument("page-1", "Page")];

        var provider = CreateProvider(apiClient, new InMemoryLogger(), new Dictionary<string, object>
        {
            [ModuleConstants.Settings.General.Projects.Name] = """[{ "projectId": "projA" }]""",
        });

        var result = await provider.GetByIdsAsync(["page-1"]);

        Assert.Single(result);
        var request = Assert.Single(apiClient.Requests);
        Assert.Equal("production", request.Dataset);
        Assert.Contains("_type == \"page\"", request.Query);
    }

    [Fact]
    public async Task GetByIdsAsync_MultipleProjects_EachProjectQueriedWithOwnCredentialsAndTypes()
    {
        var apiClient = new FakeSanityApiClient();
        apiClient.ResultsByDataset["projA:production"] = [CreatePageDocument("page-1", "Page")];
        apiClient.ResultsByDataset["projB:content"] = [CreatePageDocument("landing-1", "Landing")];

        var provider = CreateProvider(apiClient, new InMemoryLogger(), new Dictionary<string, object>
        {
            [ModuleConstants.Settings.General.Projects.Name] = """
            [
                { "projectId": "projA", "apiToken": "tokenA", "datasets": { "production": "page" } },
                { "projectId": "projB", "datasets": { "content": "landing" } }
            ]
            """,
        });

        var result = await provider.GetByIdsAsync(["page-1", "landing-1"]);

        Assert.Equal(2, result.Count);

        var requestA = Assert.Single(apiClient.Requests, x => x.ProjectId == "projA");
        Assert.Equal("production", requestA.Dataset);
        Assert.Equal("tokenA", requestA.ApiToken);
        Assert.Contains("_type == \"page\"", requestA.Query);

        // The second project has no apiToken of its own and must inherit the store-level Sanity.ApiToken
        var requestB = Assert.Single(apiClient.Requests, x => x.ProjectId == "projB");
        Assert.Equal("content", requestB.Dataset);
        Assert.Equal("token1", requestB.ApiToken);
        Assert.Contains("_type == \"landing\"", requestB.Query);
    }

    [Fact]
    public async Task GetByIdsAsync_ConflictAcrossProjects_FirstConfiguredProjectWinsAndConflictIsLogged()
    {
        var apiClient = new FakeSanityApiClient();
        apiClient.ResultsByDataset["projA:production"] = [CreatePageDocument("page-1", "Title from projA")];
        apiClient.ResultsByDataset["projB:production"] = [CreatePageDocument("page-1", "Title from projB")];

        var logger = new InMemoryLogger();
        var provider = CreateProvider(apiClient, logger, new Dictionary<string, object>
        {
            [ModuleConstants.Settings.General.Projects.Name] = """
            [
                { "projectId": "projA", "apiToken": "tokenA", "datasets": { "production": "page" } },
                { "projectId": "projB", "apiToken": "tokenB", "datasets": { "production": "page" } }
            ]
            """,
        });

        var result = await provider.GetByIdsAsync(["page-1"]);

        var pageDocument = Assert.Single(result);
        Assert.Equal("Title from projA", pageDocument.Title);

        var conflict = Assert.Single(logger.Entries, x => x.Level == LogLevel.Warning);
        Assert.Contains("page-1", conflict.Message);
        Assert.Contains("projA", conflict.Message);
        Assert.Contains("projB", conflict.Message);
    }

    [Fact]
    public async Task GetByIdsAsync_ProjectEntryWithoutProjectId_IsSkippedWithWarning()
    {
        var apiClient = new FakeSanityApiClient();
        apiClient.ResultsByDataset["projA:production"] = [CreatePageDocument("page-1", "Page")];

        var logger = new InMemoryLogger();
        var provider = CreateProvider(apiClient, logger, new Dictionary<string, object>
        {
            [ModuleConstants.Settings.General.Projects.Name] = """
            [
                { "apiToken": "tokenX", "datasets": { "production": "page" } },
                { "projectId": "projA", "datasets": { "production": "page" } }
            ]
            """,
        });

        var result = await provider.GetByIdsAsync(["page-1"]);

        Assert.Single(result);
        Assert.All(apiClient.Requests, x => Assert.Equal("projA", x.ProjectId));
        Assert.Single(logger.Entries, x => x.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SearchChangesAsync_SameDocumentChangedInSeveralDatasets_ProducesSingleChange()
    {
        var apiClient = new FakeSanityApiClient();
        apiClient.ResultsByDataset["production"] =
        [
            JObject.Parse("""{"_id": "page-1", "_updatedAt": "2026-09-01T00:00:00Z"}"""),
        ];
        apiClient.ResultsByDataset["draft"] =
        [
            JObject.Parse("""{"_id": "page-1", "_updatedAt": "2026-09-02T00:00:00Z"}"""),
            JObject.Parse("""{"_id": "page-2", "_updatedAt": "2026-09-03T00:00:00Z"}"""),
        ];

        var provider = CreateProvider(apiClient, new InMemoryLogger(), new Dictionary<string, object>
        {
            [ModuleConstants.Settings.General.Projects.Name] = """
            [{ "projectId": "project1", "datasets": { "draft": "page", "production": "page" }, "priorityDataset": "production" }]
            """,
        });

        var criteria = new PageChangesSearchCriteria { Take = 10 };
        var result = await provider.SearchChangesAsync(criteria);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(["page-2", "page-1"], result.Results.Select(x => x.DocumentId).ToArray());
    }

    private static SanityContentProvider CreateProvider(
        FakeSanityApiClient apiClient,
        InMemoryLogger logger,
        Dictionary<string, object> settingValues)
    {
        settingValues.TryAdd(ModuleConstants.Settings.General.Enabled.Name, true);
        settingValues.TryAdd(ModuleConstants.Settings.General.ProjectId.Name, "project1");
        settingValues.TryAdd(ModuleConstants.Settings.General.ApiToken.Name, "token1");

        var settingsManager = new Mock<ISettingsManager>();
        settingsManager
            .Setup(x => x.GetObjectSettingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((IEnumerable<string> names, string _, string _) => names
                .Select(name => new ObjectSettingEntry
                {
                    Name = name,
                    Value = settingValues.GetValueOrDefault(name),
                })
                .ToList());

        var storeSearchService = new Mock<IStoreSearchService>();
        storeSearchService
            .Setup(x => x.SearchAsync(It.IsAny<StoreSearchCriteria>(), It.IsAny<bool>()))
            .ReturnsAsync(new StoreSearchResult
            {
                TotalCount = 1,
                Results = [new Store { Id = StoreId }],
            });

        return new SanityContentProvider(
            apiClient,
            new SanityConverter(),
            new SanityLinkResolver(apiClient),
            storeSearchService.Object,
            settingsManager.Object,
            logger);
    }

    private static JObject CreatePageDocument(string id, string title)
    {
        return JObject.Parse($$"""
        {
            "_id": "{{id}}",
            "_type": "page",
            "_createdAt": "2026-09-01T00:00:00Z",
            "_updatedAt": "2026-09-01T00:00:00Z",
            "title": "{{title}}",
            "permalink": { "current": "{{id}}" }
        }
        """);
    }

    private sealed class FakeSanityApiClient : ISanityApiClient
    {
        public List<(string ProjectId, string Dataset, string ApiToken, string Query)> Requests { get; } = [];

        // Keyed by "projectId:dataset" or, for single-project tests, by dataset name alone
        public Dictionary<string, List<JObject>> ResultsByDataset { get; } = [];

        public Task<SanityQueryResponse> QueryAsync(string projectId, string dataset, string apiToken, string groqQuery)
        {
            Requests.Add((projectId, dataset, apiToken, groqQuery));
            var results = ResultsByDataset.GetValueOrDefault($"{projectId}:{dataset}")
                ?? ResultsByDataset.GetValueOrDefault(dataset)
                ?? [];
            return Task.FromResult(new SanityQueryResponse
            {
                Results = results,
                TotalCount = results.Count,
            });
        }
    }

    private sealed class InMemoryLogger : ILogger<SanityContentProvider>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
