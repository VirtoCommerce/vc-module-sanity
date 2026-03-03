# Sanity

## Overview

The Sanity module integrates [Sanity](https://www.sanity.io/) CMS with Virto Commerce Pages. It exposes a webhook endpoint that receives page create, update, and delete events from Sanity and publishes them to the Pages module.

## Sanity Schema

Create a `virtoPage` document type in your [Sanity Studio](https://www.sanity.io/docs/sanity-studio-quickstart/setting-up-your-studio) project.

**`schemaTypes/virtoPageType.ts`:**

```typescript
import { defineType, defineField } from 'sanity'

export const pageSchema = defineType({
  name: 'virtoPage',
  title: 'Virto Page',
  type: 'document',
  fields: [
    defineField({name: 'title', type: 'string', validation: (rule) => rule.required()}),
    defineField({name: 'permalink', type: 'slug', options: {source: 'title'}, validation: (rule) => rule.required()}),
    defineField({name: 'description', type: 'text'}),
    defineField({name: 'content', type: 'array', of: [{type: 'block'}]}),
    defineField({name: 'visibility', type: 'string', options: {list: ['Public', 'Private']}}),
    defineField({name: 'userGroups', type: 'array', of: [{type: 'string'}]}),
    defineField({name: 'startDate', type: 'datetime'}),
    defineField({name: 'endDate', type: 'datetime'}),
  ],
})
```

Register the schema in `schemaTypes/index.ts`:

```typescript
import {virtoPageType} from './virtoPageType'

export const schemaTypes = [virtoPageType]
```

## Webhook Configuration

The module exposes a single endpoint:

```
POST /api/pages/sanity?storeId={storeId}&cultureName={cultureName}
```

To connect Sanity to this endpoint, configure webhooks in [Sanity Manage](https://www.sanity.io/manage) → your project → **API** → **Webhooks**.

| Setting | Value |
|---|---|
| **URL** | `https://<your-domain>/api/pages/sanity?storeId=<StoreId>&cultureName=<cultureName>` |
| **Trigger on** | `Create, Update, Delete` |
| **HTTP method** | `POST` |
| **HTTP headers** | `Name: api_key, Value: <your-api-key>` |

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
