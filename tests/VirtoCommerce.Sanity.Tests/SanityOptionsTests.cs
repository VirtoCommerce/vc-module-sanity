using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VirtoCommerce.Sanity.Core.Models;
using Xunit;

namespace VirtoCommerce.Sanity.Tests;

[Trait("Category", "Unit")]
public class SanityOptionsTests
{
    [Theory]
    [InlineData("WebhookToleranceSeconds", "0")]
    [InlineData("WebhookToleranceSeconds", "86401")]
    [InlineData("WebhookMaxBodySize", "100")]
    public void OutOfRangeValue_FailsValidation(string key, string value)
    {
        var options = Build(new() { [$"Sanity:{key}"] = value });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void Defaults_PassValidation()
    {
        var options = Build([]).Value;

        Assert.Equal(300, options.WebhookToleranceSeconds);
        Assert.Equal(10 * 1024 * 1024, options.WebhookMaxBodySize);
        Assert.Empty(options.WebhookSecrets);
    }

    [Fact]
    public void SecretNames_AreCaseInsensitive()
    {
        var options = Build(new() { ["Sanity:Secrets:ProjectAToken"] = "sk-a" }).Value;

        Assert.Equal("sk-a", options.Secrets["projectatoken"]);
    }

    // Same binding and validation as Module.Initialize
    private static IOptions<SanityOptions> Build(Dictionary<string, string> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddOptions<SanityOptions>()
            .Bind(configuration.GetSection("Sanity"))
            .ValidateDataAnnotations();

        return services.BuildServiceProvider().GetRequiredService<IOptions<SanityOptions>>();
    }
}
