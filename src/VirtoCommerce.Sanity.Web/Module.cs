using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.Pages.Core.ContentProviders;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Sanity.Core;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.Sanity.Data.ContentProviders;
using VirtoCommerce.Sanity.Data.Services;
using VirtoCommerce.StoreModule.Core.Model;

namespace VirtoCommerce.Sanity.Web;

public class Module : IModule
{
    public ManifestModuleInfo ModuleInfo { get; set; }

    public void Initialize(IServiceCollection serviceCollection)
    {
        serviceCollection.AddHttpClient("Sanity");
        serviceCollection.AddTransient<ISanityConverter, SanityConverter>();
        serviceCollection.AddTransient<ISanityApiClient, SanityApiClient>();
        serviceCollection.AddTransient<IPageContentProvider, SanityContentProvider>();
    }

    public void PostInitialize(IApplicationBuilder appBuilder)
    {
        var serviceProvider = appBuilder.ApplicationServices;

        // Register settings
        var settingsRegistrar = serviceProvider.GetRequiredService<ISettingsRegistrar>();
        settingsRegistrar.RegisterSettings(ModuleConstants.Settings.AllSettings, ModuleInfo.Id);
        settingsRegistrar.RegisterSettingsForType(ModuleConstants.Settings.StoreLevelSettings, nameof(Store));

        // Register permissions
        var permissionsRegistrar = serviceProvider.GetRequiredService<IPermissionsRegistrar>();
        permissionsRegistrar.RegisterPermissions(ModuleInfo.Id, "Sanity", ModuleConstants.Security.Permissions.AllPermissions);

    }

    public void Uninstall()
    {
        // Nothing to do here
    }
}
