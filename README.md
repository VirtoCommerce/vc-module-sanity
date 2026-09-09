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
| `storeId` | string | `StoreId` | recommended | Required for index rebuild. Fallback: webhook query param |
| `cultureName` | string | `CultureName` | recommended | Required for index rebuild. Fallback: webhook query param |
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
* **Webhook Push** — real-time page updates via `POST /api/pages/sanity` (existing functionality)

The content provider uses the [Sanity Content API (GROQ)](https://www.sanity.io/docs/http-query) to query pages. Configure the following store-level settings:

| Setting | Description | Default |
|---|---|---|
| **Sanity.Enabled** | Enable/disable Sanity for the store | `false` |
| **Sanity.ProjectId** | Sanity project ID | — |
| **Sanity.Dataset** | Dataset name | `production` |
| **Sanity.ApiToken** | API token (read access); also the default token for projects that carry no `apiToken` of their own in **Sanity.Projects** | — |
| **Sanity.DocumentTypes** | Comma-separated list of document types to fetch and index | `page` |
| **Sanity.Projects** | Single JSON setting describing all Sanity sources of the store: projects, their datasets, document types, and priority (see below) | `[]` |
| **Sanity.PageType** | Legacy single document type; used only when **Sanity.DocumentTypes** is empty | `page` |

### Multiple Projects and Datasets

A store can fetch and index documents from several datasets and several Sanity projects at once. All sources are described by the single **Sanity.Projects** setting — a JSON array where each entry is a project with its own credentials and datasets, and each dataset has its own document types:

```json
[
  {
    "projectId": "abc12345",
    "apiToken": "sk...",
    "datasets": {
      "production": "page,footerNavigation",
      "marketing": ["landing", "blog"]
    },
    "priorityDataset": "production"
  },
  {
    "projectId": "xyz67890",
    "datasets": { "content": "landing,blog" }
  }
]
```

Entry fields:

| Field | Required | Description |
|---|---|---|
| `projectId` | yes | Sanity project ID; entries without it are skipped with a warning |
| `apiToken` | no | Project API token; falls back to the **Sanity.ApiToken** setting |
| `datasets` | no | JSON object mapping a dataset name to its document types — a comma-separated string or a JSON array; a dataset with an empty type list inherits the **Sanity.DocumentTypes** setting |
| `dataset` | no | Single dataset name (default `production`), used only when `datasets` is omitted |
| `priorityDataset` | no | Name of the dataset that wins when the same document exists in several datasets of this project |

When **Sanity.Projects** is empty, the module works with the single project from **Sanity.ProjectId**, **Sanity.Dataset**, and **Sanity.DocumentTypes** — existing configurations keep working unchanged.

**Conflicts.** The same document id may exist in several sources (e.g. cloned datasets or projects). Sources are processed in priority order: projects in their configured order, and within a project the dataset named in `priorityDataset` first, the rest in their configured order. On a conflict, the document from the higher-priority source is indexed, and a warning is always written to the platform log:

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

References to documents without a permalink/slug are left untouched. Resolution applies when documents are fetched from the Sanity API (index rebuild and scheduled sync); webhook payloads are indexed as received.

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

* [Sanity Content API (GROQ)](https://www.sanity.io/docs/http-query)
* [Sanity HTTP API](https://www.sanity.io/docs/reference/http)
* [Sanity Studio Quickstart](https://www.sanity.io/docs/sanity-studio-quickstart/setting-up-your-studio)

## Webhook Configuration

The module exposes a single endpoint:

```
POST /api/pages/sanity?storeId={storeId}&cultureName={cultureName}
```

To connect Sanity to this endpoint, configure webhooks in [Sanity Manage](https://www.sanity.io/manage) → your project → **API** → **Webhooks**.

| Setting | Value |
|---|---|
| **URL** | `https://<your-domain>/api/pages/sanity?storeId=<StoreId>&cultureName=<cultureName>&api_key=<your-api-key>` |
| **Trigger on** | `Create, Update, Delete` |
| **HTTP method** | `POST` |

### Authorization

The endpoint requires an API key for a VirtoCommerce user with the following permissions:

- `sanity:update` — for create and update operations
- `sanity:delete` — for delete operations

You can verify webhook delivery in Sanity Manage → Webhooks → **Your webhook** → **...** → **Show attempt log**.

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
