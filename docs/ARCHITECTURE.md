# Architecture

BoxPilot separates the Avalonia presentation layer from all sing-box and subscription logic. `BoxPilot.App` owns desktop lifetime, localization, themes, tray integration, and view models. `BoxPilot.Core` has no Avalonia dependency and can be tested headlessly.

## Runtime flow

1. `AppRuntime` creates paths, stores, the sing-box process service, subscription services, and the shared session view model.
2. `SettingsStore` generates a private Clash API secret and persists user preferences atomically.
3. `ProfileRepository` stores metadata in `profiles.json` and each configuration in a separate restricted file.
4. `SingBoxService` discovers the installed binary, validates configurations, starts one managed process, captures both output streams asynchronously, and performs graceful shutdown.
5. `ClashApiClient` supplies selector state, latency tests, and WebSocket traffic samples.

## Subscription pipeline

`SubscriptionClient` uses a pooled `SocketsHttpHandler`, decompression, conditional requests, strict timeouts, and a 16 MiB response limit. `SubscriptionParser` detects:

- native sing-box JSON,
- Clash YAML,
- Base64 URI lists,
- plain URI lists.

The Clash converter maps supported protocols into sing-box outbounds, preserves selector and URL-test groups, translates common routing rules, and reports lossy mappings. Every generated document is checked by the actual installed core before replacing a profile. Updates therefore cannot overwrite a working profile with an invalid response.

## Full feature coverage

Structured import is intentionally complemented by the raw JSON studio. Unknown top-level objects and fields are retained, allowing endpoints, services, certificate providers, network namespaces, custom DNS, rule sets, experimental APIs, and future sing-box additions to be used immediately. The toolbox exposes every non-runtime sing-box CLI family; managed `run` remains under lifecycle control.

## Performance and safety

Core output enters a concurrent queue and is flushed to a bounded UI collection in batches. Traffic uses one cancellable WebSocket. Files are replaced atomically, process arguments never pass through a shell, API access is loopback-only, and subscription credentials are never committed or printed.

Unsaved configuration drafts stay in memory when users switch profiles and are removed only after a successful save or explicit profile deletion. This prevents accidental data loss without writing invalid JSON to disk.
