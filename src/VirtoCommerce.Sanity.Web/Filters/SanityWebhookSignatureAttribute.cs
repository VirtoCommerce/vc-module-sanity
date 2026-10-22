using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // Bodies up to this size are buffered in memory, larger ones in a temporary file
    private const int BodyBufferThreshold = 64 * 1024;
    private const int BodyReadBufferSize = 16 * 1024;

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

        var timestampText = match.Groups["timestamp"].Value;
        if (!long.TryParse(timestampText, out var timestamp) || timestamp < MinimumTimestamp)
        {
            return "the signature timestamp is invalid";
        }

        var age = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp);
        if (age > TimeSpan.FromSeconds(options.WebhookToleranceSeconds).TotalMilliseconds)
        {
            return "the signature timestamp is outside the allowed time window";
        }

        byte[] expectedSignature;
        try
        {
            expectedSignature = Base64Url.DecodeFromChars(match.Groups["signature"].Value);
        }
        catch (FormatException)
        {
            return "the signature is not valid base64url";
        }

        if (request.ContentLength > options.WebhookMaxBodySize)
        {
            return $"the body is larger than {options.WebhookMaxBodySize} bytes";
        }

        var secrets = options.WebhookSecrets.Where(x => !string.IsNullOrEmpty(x)).ToList();
        var signatures = await ComputeSignaturesAsync(request, timestampText, secrets, options.WebhookMaxBodySize);
        if (signatures == null)
        {
            return $"the body is larger than {options.WebhookMaxBodySize} bytes";
        }

        if (signatures.Any(x => CryptographicOperations.FixedTimeEquals(x, expectedSignature)))
        {
            return null;
        }

        // The body size helps tell a wrong secret apart from a body altered on the way to the module
        return $"the signature does not match any configured secret (signed body: {request.Body.Length} bytes)";
    }

    /// <summary>
    /// Hashes "&lt;timestamp&gt;.&lt;raw body&gt;" with every secret while reading the bytes as received,
    /// then rewinds the body so that model binding can read it again. The body is buffered by the
    /// request itself, up to <paramref name="maxBodySize"/>; returns null when it is larger.
    /// </summary>
    private static async Task<IList<byte[]>> ComputeSignaturesAsync(HttpRequest request, string timestampText, IList<string> secrets, long maxBodySize)
    {
        var hashes = secrets.Select(x => IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(x))).ToList();

        try
        {
            var prefix = Encoding.UTF8.GetBytes($"{timestampText}.");
            hashes.ForEach(x => x.AppendData(prefix));

            request.EnableBuffering(BodyBufferThreshold, maxBodySize);
            request.Body.Position = 0;

            var buffer = new byte[BodyReadBufferSize];
            long totalRead = 0;
            int read;
            while ((read = await request.Body.ReadAsync(buffer)) > 0)
            {
                // Counted here as well: a body that is already seekable is not wrapped by EnableBuffering,
                // so its limit would not apply
                totalRead += read;
                if (totalRead > maxBodySize)
                {
                    return null;
                }

                hashes.ForEach(x => x.AppendData(buffer, 0, read));
            }

            request.Body.Position = 0;

            return hashes.Select(x => x.GetHashAndReset()).ToList();
        }
        catch (IOException)
        {
            // Thrown by the buffering stream when the body exceeds its limit
            return null;
        }
        finally
        {
            hashes.ForEach(x => x.Dispose());
        }
    }

    [GeneratedRegex(@"^t=(?<timestamp>\d+)[, ]+v1=(?<signature>[^, ]+)$")]
    private static partial Regex SignatureHeaderRegex();
}
