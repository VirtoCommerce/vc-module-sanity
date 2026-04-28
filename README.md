# Sanity

## Overview

The Sanity module integrates [Sanity](https://www.sanity.io/) CMS with Virto Commerce Pages. It exposes a webhook endpoint that receives page create, update, and delete events from Sanity and publishes them to the Pages module.

## Sanity Schema

Create a document type in your [Sanity Studio](https://www.sanity.io/docs/sanity-studio-quickstart/setting-up-your-studio) project. The type name must match the `Sanity.PageType` store setting (default: `page`).

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
| **Sanity.ApiToken** | API token (read access) | — |
| **Sanity.PageType** | Document type to index | `page` |

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
