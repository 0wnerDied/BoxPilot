# Architecture

BoxPilot separates the Avalonia presentation layer from all sing-box and subscription logic. `BoxPilot.App` owns desktop lifetime, localization, themes, tray integration, and view models. `BoxPilot.Core` has no Avalonia dependency and can be tested headlessly.

## Runtime flow

1. `AppRuntime` creates paths, stores, the sing-box process service, subscription services, and the shared session view model.
2. `SettingsStore` generates a private Clash API secret and persists user preferences atomically.
3. `ProfileRepository` stores metadata in `profiles.json` and each configuration in a separate restricted file.
4. `SingBoxService` discovers the installed binary, validates configurations, starts one managed process, captures both output streams asynchronously, and performs graceful shutdown. TUN configurations are delegated to the protected core service.
5. `ClashApiClient` supplies selector state, latency tests, and WebSocket traffic samples.

## Privileged TUN boundary

The Avalonia process always runs with the current user's permissions. On the first TUN start, BoxPilot calls macOS Authorization Services directly and identifies the operation as **BoxPilot TUN**; no auxiliary app, executable, or `osascript` process is shipped. Windows uses the existing BoxPilot executable for the corresponding UAC flow. The authorized operation can launch only BoxPilot's fingerprinted installer entry point, which installs an owner-scoped LaunchDaemon or Windows Service and copies the selected sing-box binary into a protected directory. Later TUN starts do not elevate the GUI or execute a user-selected path as root.

The GUI authenticates over a user-restricted Unix socket or Windows named pipe with a 256-bit local token. IPC accepts only bounded lifecycle messages and configuration content; the protected sing-box path is fixed at installation. Application and core fingerprints trigger a one-time service update after either binary changes. Losing the GUI connection stops sing-box and removes its temporary configuration, while the installed service remains idle for the next launch. Settings exposes explicit service removal.

sing-box 1.13 uses the Clash API for selectors, traffic, and connection state. The 1.14 gRPC API can replace those runtime queries after it becomes stable and is detected, but it does not replace the operating-system service that owns TUN privileges.

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

Core output enters a concurrent queue and is flushed to a bounded UI collection in batches. Traffic uses one cancellable WebSocket. Files are replaced atomically, service messages are length-bounded, process arguments never pass through a shell, API access is loopback-only, and subscription credentials are never committed or printed.

Unsaved configuration drafts stay in memory when users switch profiles and are removed only after a successful save or explicit profile deletion. This prevents accidental data loss without writing invalid JSON to disk.

## Release footprint

Release packages use full IL trimming and compressed, self-contained single-file
publishing. Persistence uses generated JSON metadata so the trimmer can remove
reflection-only serializer paths safely. The remaining payload is primarily the
.NET runtime, Skia, HarfBuzz, and the Avalonia native backend; keeping these in
the package lets BoxPilot run without a separately installed .NET runtime or an
operating-system WebView. macOS applies a second compression layer in the DMG,
while Windows keeps the result as one directly runnable executable.
The same trimmed executable contains the dormant service entry point, so release
packages do not carry a second .NET runtime or a duplicate helper binary.
