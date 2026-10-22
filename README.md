# Sanity

## Overview

The Sanity module integrates [Sanity](https://www.sanity.io/) CMS with Virto Commerce Pages. It exposes a webhook endpoint that receives page create, update, and delete events from Sanity and publishes them to the Pages module.

## Sanity Schema

Create one or more document types in your [Sanity Studio](https://www.sanity.io/docs/sanity-studio-quickstart/setting-up-your-studio) project. The type names must be listed per dataset in the `Sanity.Projects` store setting (see [Multiple Projects and Datasets](#multiple-projects-and-datasets)) or in the `Sanity.DocumentTypes` setting (comma-separated, default: `page`), e.g. `page,siteSettings,footerNavigation`.

**`schemaTypes/pageType.ts`:**

```typescript
import { defineType, defineField } from 'sanity'

export const pageType = defineType({
  name: 'page',
  title: 'Page',
  type: 'document',
  fields: [
    defineField({ name: 'title', type: 'string', validation: (rule) => rule.required() }),
    defineField({ name: 'permalink', type: 'slug', options: { source: 'title' }, validation: (rule) => rule.required() }),
    defineField({ name: 'description', type: 'text' }),
    defineField({ name: 'body', type: 'array', of: [{ type: 'block' }] }),
    defineField({ name: 'storeId', type: 'string', title: 'Store ID' }),
    defineField({ name: 'cultureName', type: 'string', title: 'Culture Name' }),
    defineField({ name: 'visibility', type: 'string', options: { list: ['Public', 'Private'] } }),
    defineField({ name: 'userGroups', type: 'array', of: [{ type: 'string' }] }),
    defineField({ name: 'startDate', type: 'datetime' }),
    defineField({ name: 'endDate', type: 'datetime' }),
  ],
})
```

Register the schema in `schemaTypes/index.ts`:

```typescript
import { pageType } from './pageType'

export const schemaTypes = [pageType]
```

### Field Mapping

The following table shows how Sanity document fields map to VirtoCommerce `PageDocument` properties:

| Sanity Field | Type | PageDocument Property | Required | Notes |
|---|---|---|---|---|
| `_id` | system | `Id`, `OuterId` | auto | Set by Sanity. Prefix `drafts.` = Draft status |
| `_createdAt` | system | `CreatedDate` | auto | Set by Sanity |
| `_updatedAt` | system | `ModifiedDate` | auto | Set by Sanity |
| `title` | string | `Title` | yes | Page title |
| `permalink` | slug | `Permalink` | yes | Read from `permalink.current` |
| `description` | text | `Description` | no | Meta description |
| `body` | block[] | — | no | Stored as raw JSON in `Content` |
| `storeId` | string | `StoreId` | no | Overrides the store the document was found in; otherwise the store whose Sanity sources contain it |
| `cultureName` | string | `CultureName` | recommended | Fallback: webhook query param |
| `visibility` | string | `Visibility` | no | `Public` or `Private`. Default: `Private` |
| `userGroups` | string[] | `UserGroups` | no | Restrict access to specific user groups |
| `startDate` | datetime | `StartDate` | no | Scheduled publishing start |
| `endDate` | datetime | `EndDate` | no | Scheduled publishing end |

The entire Sanity document JSON is stored in `PageDocument.Content` with `MimeType = "application/json"` and `Source = "sanity"`.

**Draft detection:** Documents with `_id` starting with `drafts.` are indexed with `Status = Draft`. All other documents use `Status = Published`.

## Pages Module Integration

The module integrates with [Virto Pages](https://github.com/VirtoCommerce/vc-module-pages) as a content provider (`IPageContentProvider`), enabling:

* **Index Rebuild** — full reindex of all Sanity pages from the admin UI
* **Scheduled Sync** — periodic synchronization of modified pages using `_updatedAt` filter
* **Webhook Push** — real-time page updates via `POST /api/pages/sanity`

All three paths fetch the document from the Sanity API and enrich it the same way, so indexed content is identical regardless of what triggered the update.

The content provider uses the [Sanity Content API (GROQ)](https://www.sanity.io/docs/http-query) to query pages. Configure the following store-level settings:

| Setting | Description | Default |
|---|---|---|
| **Sanity.Enabled** | Enable/disable Sanity for the store | `false` |
| **Sanity.ProjectId** | Sanity project ID | — |
| **Sanity.Dataset** | Dataset name | `production` |
| **Sanity.ApiToken** | API token (read access); also the default token for projects that carry no `apiToken` of their own in **Sanity.Projects**. Supports `${name}` secret references | — |
| **Sanity.DocumentTypes** | Comma-separated list of document types to fetch and index | `page` |
| **Sanity.Projects** | Single JSON setting describing all Sanity sources of the store: projects, their datasets, document types, and priority (see below) | `[]` |
| **Sanity.PageType** | Legacy single document type; used only when **Sanity.DocumentTypes** is empty | `page` |

### Multiple Projects and Datasets

A store can fetch and index documents from several datasets and several Sanity projects at once. All sources are described by the single **Sanity.Projects** setting — a JSON array where each entry is a project with its own credentials and datasets, and each dataset has its own document types:

```json
[
  {
    "projectId": "abc12345",
    "apiToken": "${projectAToken}",
    "datasets": [
      { "name": "production", "documentTypes": ["page", "footerNavigation"], "isPriority": true },
      { "name": "marketing", "documentTypes": ["landing", "blog"] }
    ]
  },
  {
    "projectId": "xyz67890",
    "datasets": [
      { "name": "content", "documentTypes": ["landing", "blog"] }
    ]
  }
]
```

Project fields:

| Field | Required | Description |
|---|---|---|
| `projectId` | yes | Sanity project ID; entries without it are skipped with a warning |
| `apiToken` | no | Project API token, normally a `${name}` reference to a configured secret (see Configuration below); falls back to the **Sanity.ApiToken** setting |
| `datasets` | no | Array of the project's datasets; when omitted, the single `production` dataset is used |

Dataset fields:

| Field | Required | Description |
|---|---|---|
| `name` | yes | Dataset name; entries without it are skipped |
| `documentTypes` | no | Array of document types fetched from this dataset; when empty, inherits the **Sanity.DocumentTypes** setting |
| `isPriority` | no | Marks the dataset as the priority one: it is queried first, so its documents win on conflicts. Default: `false` |

When **Sanity.Projects** is empty, the module works with the single project from **Sanity.ProjectId**, **Sanity.Dataset**, and **Sanity.DocumentTypes** — existing configurations keep working unchanged.

**Conflicts.** The same document id may exist in several sources (e.g. cloned datasets or projects). Sources are processed in priority order: projects in their configured order, and within a project the datasets marked with `isPriority` first, the rest in their configured order. On a conflict, the document from the higher-priority source is indexed, and a warning is always written to the platform log:

```
Sanity conflict in store 'B2B-store': document 'page-about' exists in project 'abc12345' dataset 'production' and in project 'abc12345' dataset 'draft'. The document from the higher-priority source wins.
```

### Internal Link Resolution

Sanity stores internal links as raw references (`{"_type": "reference", "_ref": "<document-id>"}`), which are useless for building URLs on the frontend. When the module fetches or receives a document, it automatically resolves such references: it collects all document references at any nesting depth, queries the relative link of each referenced document in a single batch request, and injects it into the reference object as a `slug` property.

The relative link of a referenced document is resolved as:

```groq
coalesce(permalink.current, seo.slug.current, slug.current)
```

For example, a footer navigation link stored as:

```json
{ "link": { "label": "About", "internalLink": { "_type": "reference", "_ref": "page-about" } } }
```

is indexed as:

```json
{ "link": { "label": "About", "internalLink": { "_type": "reference", "_ref": "page-about", "slug": "about-us" } } }
```

References to documents without a permalink/slug are left untouched. Resolution applies to every indexing path — index rebuild, scheduled sync, and webhook push — because all three fetch the document from the Sanity API.

### Asset URL Resolution

Image and file references get the same treatment: every asset reference is enriched with a `url` property pointing to the Sanity CDN. Asset ids encode everything needed for the URL, so no extra API request is made:

| Asset id | Injected `url` |
|---|---|
| `image-<hash>-<width>x<height>-<format>` | `https://cdn.sanity.io/images/<projectId>/<dataset>/<hash>-<width>x<height>.<format>` |
| `file-<hash>-<extension>` | `https://cdn.sanity.io/files/<projectId>/<dataset>/<hash>.<extension>` |

For example, an image stored as:

```json
{ "logo": { "_type": "image", "asset": { "_type": "reference", "_ref": "image-abc123-800x600-jpg" } } }
```

is indexed as:

```json
{ "logo": { "_type": "image", "asset": { "_type": "reference", "_ref": "image-abc123-800x600-jpg", "url": "https://cdn.sanity.io/images/<projectId>/<dataset>/abc123-800x600.jpg" } } }
```

which matches the shape a GROQ `asset->{url}` dereference would produce (`logo.asset.url`). Malformed asset ids are left untouched.

### References

* [Best Practices](docs/best-practices.md) — content modelling conventions: identifying content from the theme, permalinks, links and assets, datasets, schema evolution
* [Sanity Content API (GROQ)](https://www.sanity.io/docs/http-query)
* [Sanity HTTP API](https://www.sanity.io/docs/reference/http)
* [Sanity Studio Quickstart](https://www.sanity.io/docs/sanity-studio-quickstart/setting-up-your-studio)

## Configuration

Webhook secrets and Sanity API tokens are read from configuration, not from the database, and are bound to `SanityOptions` from the `Sanity` section:

```json
{
  "Sanity": {
    "WebhookSecrets": [ "<secret from the Sanity webhook>" ],
    "WebhookToleranceSeconds": 300,
    "WebhookMaxBodySize": 10485760,
    "Secrets": {
      "projectAToken": "sk...",
      "projectBToken": "sk..."
    }
  }
}
```

| Key | Description | Default |
|---|---|---|
| `Sanity:WebhookSecrets` | Secrets accepted when validating a webhook signature. A request is accepted when it matches **any** of them, which covers several webhooks (one per project) and lets a secret be rotated without downtime: add the new one, switch Sanity over, then drop the old one. Empty means every webhook request is rejected. | `[]` |
| `Sanity:WebhookToleranceSeconds` | How far a webhook timestamp may be from the current time. Guards against replay. 1 to 86400. | `300` |
| `Sanity:WebhookMaxBodySize` | Largest webhook body accepted, in bytes. The body is read to check its signature before the request is authenticated, so this bounds what an anonymous caller can make the server buffer. 1 KB to 100 MB. | `10485760` (10 MB) |
| `Sanity:Secrets` | Named API tokens referenced as `${name}` from the **Sanity.Projects** store setting. Names are case-insensitive. | `{}` |

### Referencing secrets from store settings

**Sanity.Projects** is stored in the database and is editable in the admin UI, so it should not carry raw tokens. Reference a configured secret by name instead:

```json
[
  {
    "projectId": "abc12345",
    "apiToken": "${projectAToken}",
    "datasets": [ { "name": "production", "documentTypes": ["page"] } ]
  }
]
```

Only names defined in `Sanity:Secrets` can be resolved — a setting cannot reach arbitrary configuration values such as connection strings. An unresolved reference is never sent to Sanity: the module logs a warning naming the missing secret and skips the project. The same substitution applies to the **Sanity.ApiToken** store setting.

### Environment variables

Configuration keys map to environment variables with `__` (double underscore) instead of `:`, which is what you need on Linux and in containers, where `:` is not allowed in variable names. Array items are addressed by index, dictionary entries by key:

```bash
export Sanity__WebhookSecrets__0="whsec_current"
export Sanity__WebhookSecrets__1="whsec_previous"   # during rotation
export Sanity__WebhookToleranceSeconds="300"
export Sanity__Secrets__projectAToken="sk..."
export Sanity__Secrets__projectBToken="sk..."
```

The same in `docker-compose.yml`:

```yaml
services:
  platform:
    environment:
      - Sanity__WebhookSecrets__0=whsec_current
      - Sanity__Secrets__projectAToken=sk...
```

The options are validated at startup: an out-of-range value stops the platform from starting instead of failing the first webhook delivery.

Environment variables override `appsettings.json`, so keep the file free of real secrets and inject them per environment (or from a secret store such as Azure Key Vault). In local development use `dotnet user-secrets` instead of editing the file.

## Webhook Configuration

The module exposes a single endpoint:

```
POST /api/pages/sanity?cultureName={cultureName}
```

To connect Sanity to this endpoint, configure webhooks in [Sanity Manage](https://www.sanity.io/manage) → your project → **API** → **Webhooks**.

| Setting | Value |
|---|---|
| **URL** | `https://<your-domain>/api/pages/sanity?cultureName=<cultureName>` |
| **Secret** | any strong random string; the same value goes into `Sanity:WebhookSecrets` |
| **Trigger on** | `Create, Update, Delete` |
| **HTTP method** | `POST` |
| **Projection** | the recommended projection below |

Recommended projection:

```groq
{
  _id,
  storeId,
  "operation": delta::operation(),
  "projectId": sanity::projectId(),
  "dataset": sanity::dataset()
}
```

It is small, so deliveries stay well within Sanity's payload limits, and it puts every value that steers the module inside the signed body (see [Signed values take precedence over headers](#signed-values-take-precedence-over-headers)).

### The payload is a notification, not content

The webhook tells the module **which** document changed; it is never indexed as-is. The module reads the change from these values:

| Body property | Header | Used for |
|---|---|---|
| `_id` | `sanity-document-id` | the document to fetch |
| `projectId` | `sanity-project-id` | the project to fetch it from |
| `dataset` | `sanity-dataset` | the dataset to fetch it from |
| `operation` | `sanity-operation` | `create` / `update` / `delete` |
| `storeId` | — | the store of a deleted document (optional) |
| — | `sanity-webhook-signature` | authentication (see [Authorization](#authorization)) |

On create and update the module fetches that document from that project and dataset, and enriches it exactly as index rebuild does — resolved internal links and asset URLs included. The indexed content is therefore identical no matter what triggered it, and a partial or reordered webhook payload cannot put half-built content into the index.

Consequences for configuration:

* **Do not ship the document body.** The document is refetched anyway, so a projection of the whole document only makes deliveries larger and can hit Sanity's payload size limits on large documents. Use the recommended projection above.
* **One webhook serves every store.** The endpoint takes no `storeId`: the module looks the document up in the stores whose configuration contains the project and dataset from the headers, and the store assignment follows that configuration (or the document's own `storeId` field). A page document is identified by its Sanity `_id`, so a document reachable from several stores is indexed once, for the first store whose sources contain it — exactly as index rebuild treats it. To target a specific store, set the document's `storeId` field.
* **The notification's own project wins.** Because the lookup is scoped by `sanity-project-id` and `sanity-dataset`, the same document id living in another project or dataset is irrelevant here: no cross-source conflict resolution takes place, and no other project is queried. Index rebuild, which has no such hint, still resolves conflicts by priority.
* **The project and document type must be configured.** A webhook naming a project absent from every store's **Sanity.Projects**, or a document whose type is not listed for that dataset, fetches nothing; a warning naming the document, project and dataset is written to the platform log.
* **Deletes are the exception.** A deleted document can no longer be fetched, so the module publishes the delete straight from its id. The event carries the `storeId` from the payload when the projection includes it; otherwise the module publishes one delete event per store whose configuration contains the project and dataset, so delete handlers still see the store.

### Signed values take precedence over headers

The signature covers the request body only — Sanity does not sign its `sanity-*` headers. A captured delivery replayed within the tolerance window could therefore carry altered headers: another document id, or `delete` instead of `update`. To close this, values from the signed body take precedence:

* when the body carries `_id`, `operation`, `projectId` or `dataset`, that value is used;
* when the matching header is also present and differs, the request is rejected with `400 Bad Request` and a warning is logged;
* a header is used only for a value the body does not carry.

With the recommended projection every value that steers the module is signed, so the headers are not trusted at all. A webhook with an empty projection keeps working from the headers alone, but without this protection.

Independently of the signature, a document id that is not a valid Sanity identifier (letters, digits, `_`, `-`, `.`) is rejected with `400 Bad Request` before it reaches any GROQ query. The same check applies to referenced ids during link resolution and to document types from the settings.

### Authorization

The endpoint is **authenticated by the webhook signature**, not by an API key, so no secret has to be put in the webhook URL, where Sanity would show it in the UI and the attempt log.

Sanity signs every delivery with the secret from the webhook's **Secret** field and sends the result in the `sanity-webhook-signature` header as `t=<unix ms>,v1=<signature>`, where the signature is a base64url encoded HMAC-SHA256 of `<t>.<raw body>`. The module recomputes it over the raw request body and rejects the request with `401 Unauthorized` when:

* no secret is configured in `Sanity:WebhookSecrets` — **an unconfigured module accepts nothing**;
* the header is missing or malformed;
* the timestamp is outside `Sanity:WebhookToleranceSeconds` (5 minutes by default), which blocks replays;
* the body is larger than `Sanity:WebhookMaxBodySize`;
* the signature matches none of the configured secrets.

Every rejection is written to the platform log with its reason. See [Configuration](#configuration) for where to put the secret.

You can verify webhook delivery in Sanity Manage → Webhooks → **Your webhook** → **...** → **Show attempt log**.

## Upgrading from the API key webhook

Earlier versions authorized the webhook by a platform API key in its URL and took the store from a `storeId` query parameter. After the upgrade **the endpoint rejects every notification until a secret is configured**, and content is refreshed only by a full reindex. For each existing webhook:

1. Generate a strong random secret and set it in the webhook's **Secret** field in Sanity Manage.
2. Add the same value to `Sanity:WebhookSecrets` (environment variable `Sanity__WebhookSecrets__0`) and restart the platform.
3. Remove `api_key` and `storeId` from the webhook URL; keep `cultureName` if you use it.
4. Replace the projection with the [recommended one](#webhook-configuration).
5. Make sure every store that should receive the content lists the project and dataset in **Sanity.Projects** (or **Sanity.ProjectId** / **Sanity.Dataset**): the store is now resolved from that configuration or the document's `storeId` field.
6. Save a document in the Studio and check the attempt log in Sanity and the platform log for `Sanity webhook request rejected` warnings.

The platform API key used by the old webhook is no longer needed and can be revoked.

## License

Copyright (c) Virto Solutions LTD.  All rights reserved.

Licensed under the Virto Commerce Open Software License (the "License"); you
may not use this file except in compliance with the License. You may
obtain a copy of the License at

<https://virtocommerce.com/open-source-license>

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
implied.
