using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace VirtoCommerce.Sanity.Core.Models;

/// <summary>
/// Module configuration, bound from the "Sanity" section of appsettings.
/// Holds everything that must not be stored in the database: webhook secrets and API tokens.
/// </summary>
public class SanityOptions
{
    private const int DefaultWebhookToleranceSeconds = 5 * 60;
    private const int MaxWebhookToleranceSeconds = 24 * 60 * 60;
    private const long MinWebhookMaxBodySize = 1024;
    private const long DefaultWebhookMaxBodySize = 10 * 1024 * 1024;
    private const long MaxWebhookMaxBodySize = 100 * 1024 * 1024;

    /// <summary>
    /// Secrets accepted when validating the sanity-webhook-signature header. A request is accepted
    /// when it matches any of them, which allows several webhooks (one per project) and lets a secret
    /// be rotated without downtime. When empty, every webhook request is rejected.
    /// </summary>
    public List<string> WebhookSecrets { get; set; } = [];

    /// <summary>
    /// How far the webhook timestamp may be from the current time, in seconds. Guards against replay.
    /// </summary>
    [Range(1, MaxWebhookToleranceSeconds)]
    public int WebhookToleranceSeconds { get; set; } = DefaultWebhookToleranceSeconds;

    /// <summary>
    /// Largest webhook body accepted, in bytes. The body is buffered to check its signature before the
    /// request is authenticated, so this bounds what an anonymous caller can make the server hold.
    /// </summary>
    [Range(MinWebhookMaxBodySize, MaxWebhookMaxBodySize)]
    public long WebhookMaxBodySize { get; set; } = DefaultWebhookMaxBodySize;

    /// <summary>
    /// Named secrets referenced as "${name}" from the Sanity.Projects store setting.
    /// Only names listed here can be resolved, so a setting cannot reach arbitrary configuration values.
    /// </summary>
    public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
