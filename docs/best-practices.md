# Sanity Best Practices

Conventions for modelling content in Sanity Studio so it indexes cleanly into Virto Commerce Pages and stays addressable from the storefront theme. Written for this module's behavior: it fetches documents over the Content API (GROQ), resolves internal links and assets, and indexes them as `PageDocument` records.

## Identifying content from the theme

The theme must address content by a **stable contract the editor controls** — never by a generated UUID. A generated `_id` (`a1b2c3d4-…`) is meaningless when read, does not survive content being recreated, and differs between datasets and projects.

**One-of-a-kind documents (site settings, header, footer, sidebar): fixed `_id`.** Create the document with a deterministic, readable id, by convention equal to the type name:

```groq
*[_id == "leftSidebar"][0]
```

A draft of that document has `_id` = `drafts.leftSidebar`, so an exact-id query returns only the published version with no extra filtering.

**Several instances of one type: a `key` field.** As soon as there can be more than one sidebar — per section, per store — the type stops being an identifier. Add a slug field and let the theme hardcode the key, not the id:

```groq
*[_type == "sidebar" && key.current == "catalog-left"][0]
```

**Maximum flexibility: reference from a root singleton.** The theme knows one id (`siteSettings`); which sidebar renders is a reference inside it, so editors swap it without a theme deploy.

**Querying by `_type` alone** (`*[_type == "footerNavigation"][0]`) is the simplest option and fine when the type is a true singleton, but `[0]` picks an arbitrary document if a second one appears, and drafts are included — filter them out with `!(_id in path("drafts.**"))`.

Whatever you choose, treat the identifier as a published API: renaming a `key` or a fixed `_id` breaks the storefront, so it belongs in review like any other contract change.

### Lock singletons down in the Studio

A fixed `_id` only stays unique if the Studio enforces it. Present the document as a single structure entry:

```typescript
S.document().schemaType('leftSidebar').documentId('leftSidebar')
```

and remove `duplicate`, `delete`, and `unpublish` from `document.actions` for that type. Without this, an editor creates a second copy and the storefront silently renders the wrong one.

## Document types and schema design

**Keep indexable documents shallow at the top level.** The module maps a fixed set of fields — `title`, `permalink`, `description`, `storeId`, `cultureName`, `visibility`, `userGroups`, `startDate`, `endDate` — off the document root. Nest freely below that, but do not hide these behind an object wrapper.

**Model one concept per type.** Prefer `page`, `landing`, `footerNavigation` over one `content` type with a mode switch. Types are the module's fetch unit: every type is listed per dataset in the `Sanity.Projects` setting, so distinct types let you index only what the storefront needs.

**Use objects, not documents, for embedded content.** A navigation column or a hero block that only exists inside its parent should be an object type, not a document — documents are separately queryable, separately publishable, and appear in type filters where you do not want them.

**Name fields for the consumer, not the editor.** The document JSON is stored verbatim in `PageDocument.Content`, so field names are the theme's API. Renaming a field is a breaking change for the storefront.

## Permalinks and SEO

**Every document the storefront routes to needs a `permalink` slug**, sourced from the title and validated as required. The module reads `permalink.current` into `PageDocument.Permalink`, the storefront's lookup key. Documents without it are still indexed but are not addressable by URL.

**Keep permalinks unique per store and culture.** Sanity does not enforce cross-document uniqueness by default; add a custom `isUnique` validation rule so editors are warned in the Studio rather than discovering a collision in production.

**Do not rename a permalink casually.** Treat it as a URL change, with a redirect on the storefront side.

## Links and assets

**Always use references for internal links** — never a hand-typed URL string. The module walks every reference at any nesting depth and injects the target's relative link as `slug`, resolved as `coalesce(permalink.current, seo.slug.current, slug.current)`. A hardcoded `/about-us` in content silently rots when the page moves; a reference does not.

Model a link as an object with both options and let the editor pick one:

```typescript
defineField({ name: 'internalLink', type: 'reference', to: [{ type: 'page' }] }),
defineField({ name: 'externalUrl', type: 'url' }),
```

**Keep a slug-producing field on every referenceable type.** A reference to a document with no permalink or slug resolves to nothing, and the link renders dead. If a type is a link target, it needs a slug.

**Use Sanity's image and file types for media.** The module injects a CDN `url` into every asset reference, derived from the asset id — `image-<hash>-<w>x<h>-<fmt>` becomes `https://cdn.sanity.io/images/<projectId>/<dataset>/<hash>-<w>x<h>.<fmt>`. Pasting an external image URL into a string field gives up CDN delivery, transformations, and the automatic `url`.

**Fill alt text on images.** Add an `alt` field to the image object and make it required — it travels in the document JSON and is the only accessibility signal the storefront gets.

## Drafts and publishing

Sanity drafts live as separate documents with a `drafts.` id prefix. The module indexes them with `Status = Draft`; everything else is `Published`. Two consequences:

- **Never query drafts for storefront content** without filtering: `!(_id in path("drafts.**"))`, or query by exact `_id` for singletons.
- **Publishing is the deploy step.** A change is invisible to the storefront until published, so scheduled campaigns should use the `startDate` / `endDate` fields rather than holding content in draft.

Use `visibility` (`Public` / `Private`) and `userGroups` for access control instead of leaving content unpublished — those are indexed and enforced, and they keep the content reviewable.

## Datasets and projects

**Datasets are environments, not versions.** `production` for live content, a separate dataset for staging or a content migration rehearsal. Do not use datasets as a "v2 of the site" — that is what publishing and scheduling are for.

**Keep document ids identical across datasets.** Conflict resolution in this module matches documents by `_id` across all datasets and projects of a store, so a fixed id is what makes a `staging` copy recognizable as the same document as its `production` original. With generated ids the two never meet and both get indexed.

**Mark exactly one dataset per project as `isPriority`.** When the same document exists in several datasets, the priority one wins and the conflict is written to the platform log as a warning. Watch that log after a dataset clone — a burst of conflict warnings usually means a dataset was cloned by accident, not intentionally overridden.

**Split by project only for genuinely separate content estates** — a different brand, a different editorial team, a different billing account. Several datasets in one project are cheaper to operate than several projects: one token, one Studio, one set of schemas.

## Schema evolution

**Add fields, do not repurpose them.** The document JSON reaches the storefront verbatim, so a field that changes meaning breaks consumers with no error. Add the new field, migrate content, then remove the old one in a later release.

**Migrate with the CLI, not by hand.** `sanity documents query` plus a patch script (or `sanity migration` in newer CLI versions) keeps changes reproducible across datasets. Rehearse on a non-production dataset first.

**Reindex after a bulk migration.** Webhooks fire per document and can be throttled or missed during a mass patch; a full index rebuild from the admin UI is the reliable way to bring Pages back in sync.

## Queries and performance

**Project only the fields you need.** `*[_type == "page"]` pulls whole documents including every nested block. For storefront reads, name the fields explicitly — it cuts payload size and makes the contract visible in the query.

**Watch the URL length.** GROQ queries are sent as GET query strings; long `_id in [...]` lists hit request-size limits. This module batches reference lookups at 100 ids for that reason — apply the same ceiling in custom integrations.

**Resolve references in one batch, not per item.** One query with `_id in [...]` beats N queries in a loop, and the CDN caches it as a single entry.

**Read through `apicdn.sanity.io` for public content** in high-traffic storefront paths; use the uncached `api.sanity.io` host only when you need the freshest data, as this module's indexer does.

## Tokens and security

**Use read-only tokens for indexing.** This integration never writes to Sanity; a token with write or deploy scope is unnecessary exposure.

**Keep tokens out of the JSON settings where you can.** Store the shared token in the `Sanity.ApiToken` secure setting and let project entries inherit it; put a token inside `Sanity.Projects` only when a project genuinely needs a different one.

**Do not expose project tokens to the browser.** Content reaches the storefront through the indexed `PageDocument`, so the theme never needs a Sanity token. CDN asset URLs are public by design — a private dataset's assets are not, and are not a fit for storefront rendering.

**Restrict webhook delivery.** The webhook endpoint acts under a Virto Commerce API key with `sanity:update` and `sanity:delete` permissions; give that account nothing else.

## Studio ergonomics

These do not affect the integration but decide whether editors use the system correctly.

- **Set `preview` on every document type** with a title, subtitle, and media, so the document list is scannable.
- **Group long schemas into fieldsets or tabs** — SEO, scheduling, and visibility fields belong together and out of the way.
- **Set `initialValue`** for `visibility`, `storeId`, and `cultureName` so new documents are born valid rather than failing validation at publish.
- **Write `description` on non-obvious fields**, especially `key` and `permalink`, stating that the storefront depends on the exact value.
