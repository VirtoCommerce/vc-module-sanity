using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VirtoCommerce.Sanity.Core.Models;
using VirtoCommerce.Sanity.Web.Filters;
using Xunit;

namespace VirtoCommerce.Sanity.Tests;

[Trait("Category", "Unit")]
public class SanityWebhookSignatureAttributeTests
{
    private const string Secret = "secret1";
    private const string Body = """{"_id":"page-1","operation":"update"}""";

    [Fact]
    public async Task ValidSignature_RequestPassesAndBodyCanBeReadAgain()
    {
        var context = CreateContext(Body, Sign(Body, Secret, DateTimeOffset.UtcNow), new SanityOptions { WebhookSecrets = [Secret] });

        var nextCalled = await ExecuteAsync(context);

        Assert.True(nextCalled);
        Assert.Null(context.Result);

        using var reader = new StreamReader(context.HttpContext.Request.Body);
        Assert.Equal(Body, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignatureMatchesSecondSecret_RequestPasses()
    {
        var context = CreateContext(Body, Sign(Body, Secret, DateTimeOffset.UtcNow), new SanityOptions { WebhookSecrets = ["old", Secret] });

        Assert.True(await ExecuteAsync(context));
    }

    [Fact]
    public async Task WrongSecret_RequestIsRejected()
    {
        var context = CreateContext(Body, Sign(Body, "other", DateTimeOffset.UtcNow), new SanityOptions { WebhookSecrets = [Secret] });

        Assert.False(await ExecuteAsync(context));
        Assert.IsType<UnauthorizedResult>(context.Result);
    }

    [Fact]
    public async Task StaleTimestamp_RequestIsRejected()
    {
        var context = CreateContext(Body, Sign(Body, Secret, DateTimeOffset.UtcNow.AddMinutes(-10)), new SanityOptions { WebhookSecrets = [Secret] });

        Assert.False(await ExecuteAsync(context));
    }

    [Fact]
    public async Task NoSecretsConfigured_RequestIsRejected()
    {
        var context = CreateContext(Body, Sign(Body, Secret, DateTimeOffset.UtcNow), new SanityOptions());

        Assert.False(await ExecuteAsync(context));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BodyLargerThanLimit_RequestIsRejected(bool sendContentLength)
    {
        var body = $$"""{"_id":"page-1","padding":"{{new string('x', 4096)}}"}""";
        var options = new SanityOptions { WebhookSecrets = [Secret], WebhookMaxBodySize = 1024 };
        var context = CreateContext(body, Sign(body, Secret, DateTimeOffset.UtcNow), options, sendContentLength);

        Assert.False(await ExecuteAsync(context));
        Assert.IsType<UnauthorizedResult>(context.Result);
    }

    private static async Task<bool> ExecuteAsync(ResourceExecutingContext context)
    {
        var nextCalled = false;

        await new SanityWebhookSignatureAttribute().OnResourceExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ResourceExecutedContext>(null);
        });

        return nextCalled;
    }

    private static string Sign(string body, string secret, DateTimeOffset timestamp)
    {
        var t = timestamp.ToUnixTimeMilliseconds();
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        return $"t={t},v1={Base64Url.EncodeToString(hash)}";
    }

    private static ResourceExecutingContext CreateContext(string body, string signature, SanityOptions options, bool sendContentLength = true)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(options))
            .BuildServiceProvider();

        var bytes = Encoding.UTF8.GetBytes(body);
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = "POST";
        httpContext.Request.Body = new MemoryStream(bytes);
        httpContext.Request.ContentLength = sendContentLength ? bytes.Length : null;
        httpContext.Request.Headers[SanityWebhookSignatureAttribute.SignatureHeaderName] = signature;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
    }
}
