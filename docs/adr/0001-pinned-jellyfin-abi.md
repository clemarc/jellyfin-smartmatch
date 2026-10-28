# ADR 0001: Build against Jellyfin 12.0.0 and fail closed on other majors

**Status:** accepted · **Date:** 2026-10-28

## Context

Jellyfin 12 moved plugins to .NET 10 and changed the plugin ABI. NuGet carries `Jellyfin.Controller`/`Jellyfin.Model` 12.0.0 through 12.4.0; Docker Hub carries `jellyfin/jellyfin:12.0` through `12.4`. Jellyfin's plugin loader refuses a plugin whose `targetAbi` is newer than the server, but it loads older-ABI plugins on newer servers.

## Decision

- Compile against the **12.0.0** packages (runtime assets excluded) and declare `targetAbi: 12.0.0.0`. This is the widest compatible baseline: 12.0 refuses nothing and 12.1–12.4 load it.
- CI runs the full acceptance suite against pinned `12.0.20260908-012347` and `12.4.20261026-211555` images, so "later 12.x" support is verified rather than assumed.
- At runtime the plugin checks `IServerApplicationHost.ApplicationVersion.Major == 12`. On anything else it stays idle: no event subscription, no background work, every write path returns `UNSUPPORTED_SERVER`.

## Consequences

Moving the baseline (for example to use a 12.2-only API) means bumping `JellyfinPackageVersion`, `targetAbi`, the release workflow guard and the compatibility matrix together.
