using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Sanity.Core;
using VirtoCommerce.Sanity.Core.Services;
using VirtoCommerce.Sanity.Data.Services;

namespace VirtoCommerce.Sanity.Web;

public class Module : IModule
{
    public ManifestModuleInfo ModuleInfo { get; set; }

    public void Initialize(IServiceCollection serviceCollection)
    {
        serviceCollection.AddTransient<ISanityConverter, SanityConverter>();
    }

    public void PostInitialize(IApplicationBuilder appBuilder)
    {
        var serviceProvider = appBuilder.ApplicationServices;

        // Register permissions
        var permissionsRegistrar = serviceProvider.GetRequiredService<IPermissionsRegistrar>();
        permissionsRegistrar.RegisterPermissions(ModuleInfo.Id, "Sanity", ModuleConstants.Security.Permissions.AllPermissions);
    }

    public void Uninstall()
    {
        // Nothing to do here
    }
}
