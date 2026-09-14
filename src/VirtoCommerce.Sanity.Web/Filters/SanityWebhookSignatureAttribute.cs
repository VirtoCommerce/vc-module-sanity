using System;
using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtoCommerce.Sanity.Core.Models;

namespace VirtoCommerce.Sanity.Web.Filters;

/// <summary>
/// Validates the Sanity webhook signature: the "sanity-webhook-signature" header carries
/// "t=&lt;unix ms&gt;,v1=&lt;signature&gt;", where the signature is a base64url encoded HMAC-SHA256 of
/// "&lt;t&gt;.&lt;raw body&gt;" keyed with the webhook secret. The signature authenticates the request,
/// so the endpoint needs no API key.
/// Implemented as a resource filter because it runs before model binding, while the body can still
/// be read in its raw form: re-serializing the parsed body would change the bytes and break the hash.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed partial class SanityWebhookSignatureAttribute : Attribute, IAsyncResourceFilter
{
    public const string SignatureHeaderName = "sanity-webhook-signature";

    // Sanity never issues timestamps before 2021-01-01, so anything earlier is malformed
    private const long MinimumTimestamp = 1609459200000;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var options = services.GetRequiredService<IOptions<SanityOptions>>().Value;
        var logger = services.GetRequiredService<ILogger<SanityWebhookSignatureAttribute>>();

        var rejectionReason = await GetRejectionReasonAsync(context.HttpContext.Request, options);
        if (rejectionReason != null)
        {
            logger.LogWarning("Sanity webhook request rejected: {Reason}.", rejectionReason);
            context.Result = new UnauthorizedResult();
            return;
        }

        await next();
    }

    private static async Task<string> GetRejectionReasonAsync(HttpRequest request, SanityOptions options)
    {
        if (options.WebhookSecrets.Count == 0)
        {
            return $"no secrets are configured in the 'Sanity:{nameof(SanityOptions.WebhookSecrets)}' configuration section";
        }

        var header = request.Headers[SignatureHeaderName].ToString();
        if (string.IsNullOrEmpty(header))
        {
            return $"the '{SignatureHeaderName}' header is missing";
        }

        var match = SignatureHeaderRegex().Match(header);
        if (!match.Success)
        {
            return $"the '{SignatureHeaderName}' header is malformed";
        }

        var timestampText = match.Groups[1].Value;
        if (!long.TryParse(timestampText, out var timestamp) || timestamp < MinimumTimestamp)
        {
            return "the signature timestamp is invalid";
        }

        var age = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp);
        if (age > options.WebhookToleranceSeconds * 1000L)
        {
            return "the signature timestamp is outside the allowed time window";
        }

        byte[] expectedSignature;
        try
        {
            expectedSignature = Base64Url.DecodeFromChars(match.Groups[2].Value);
        }
        catch (FormatException)
        {
            return "the signature is not valid base64url";
        }

        var signedPayload = await GetSignedPayloadAsync(request, timestampText);

        foreach (var secret in options.WebhookSecrets)
        {
            if (string.IsNullOrEmpty(secret))
            {
                continue;
            }

            var actualSignature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedPayload);

            if (CryptographicOperations.FixedTimeEquals(actualSignature, expectedSignature))
            {
                return null;
            }
        }

        // The body size helps tell a wrong secret apart from a body altered on the way to the module
        return $"the signature does not match any configured secret (signed body: {signedPayload.Length - timestampText.Length - 1} bytes)";
    }

    /// <summary>
    /// Builds "&lt;timestamp&gt;.&lt;raw body&gt;" from the bytes as received, then rewinds the body
    /// so that model binding can read it again.
    /// </summary>
    private static async Task<byte[]> GetSignedPayloadAsync(HttpRequest request, string timestampText)
    {
        request.EnableBuffering();
        request.Body.Position = 0;

        using var buffer = new MemoryStream();
        await buffer.WriteAsync(Encoding.UTF8.GetBytes($"{timestampText}."));
        await request.Body.CopyToAsync(buffer);

        request.Body.Position = 0;

        return buffer.ToArray();
    }

    [GeneratedRegex(@"^t=(\d+)[, ]+v1=([^, ]+)$")]
    private static partial Regex SignatureHeaderRegex();
}
