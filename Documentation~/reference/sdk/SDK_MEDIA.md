---
title: Media
category: Services
order: 120
---

# Media

The Media feature loads remote images, videos, and documents with caching and versioning, so screens
can show backend-hosted media without each one re-implementing download, cache, and decode. The entry
point is the `MediaLoader` subsystem; `MediaInfo` models a single media item.

## MediaLoader

`MediaLoader` is a `RuntimeSubsystem` — resolve it and request media by URL. Each call is `Awaitable`,
cached, and versioned (bump `version` to invalidate a stale cache entry; pass a `cacheId` to key the
cache explicitly):

```csharp
var media = RuntimeManager.GetSubsystem<MediaLoader>();

Texture2D tex   = await media.GetTexture(imageUrl);
string videoPath = await media.GetVideoPath(videoUrl, version: 2);
string docPath   = await media.GetDocumentPath(pdfUrl, cacheId: "manual-v3");
```

| Method | Returns | Purpose |
|---|---|---|
| `GetTexture(url, version = 1, cacheId = null)` | `Awaitable<Texture2D>` | Download + decode an image. |
| `GetVideoPath(url, version = 1, cacheId = null)` | `Awaitable<string>` | Local path for a cached video (feed a `VideoPlayer`). |
| `GetDocumentPath(url, version = 1, cacheId = null)` | `Awaitable<string>` | Local path for a cached document. |

## MediaInfo

`MediaInfo` describes one media item (`name`, `id`, `Type` — image / video / document — and `url`) and
carries its own load/unload lifecycle for screens that manage media directly:

| Member | Purpose |
|---|---|
| `Init()` / `Unload()` | Prepare / release the item's resources. |
| `GetTexture()` | `Awaitable<Texture2D>` for an image item. |
| `PrepareVideo(VideoPlayer vp)` | `Awaitable<bool>` — prime a `VideoPlayer` for playback. |
| `GetDocumentUrl()` | `Awaitable<string>` for a document item. |

Type-specific handling lives in `ImageHandler`, `VideoHandler`, and `DocumentHandler` (implementations
of `IMediaHandler`). Media items pair naturally with the [SDK UI](SDK_UI.md) preview widgets
(`MediaPreviewUI`, `MediaInfoCycle`).

## See also

- [SDK UI](SDK_UI.md)
- [SDK Utilities](SDK_UTILITIES.md)
- [SDK Overview](SDK_OVERVIEW.md)
- [Networking: HttpClient & Requests](NETWORKING.md)
