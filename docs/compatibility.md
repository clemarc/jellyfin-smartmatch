# Compatibility matrix

| Plugin | Built against | targetAbi | Jellyfin server | Architecture | Evidence |
|---|---|---|---|---|---|
| 1.0.0.0 | Jellyfin.Controller / Jellyfin.Model 12.0.0, .NET SDK 10.0.403 | 12.0.0.0 | 12.0 (`jellyfin/jellyfin:12.0.20260908-012347`) | linux/amd64 | Full acceptance run (CI `integration` job; local run 2026-10-28) |
| 1.0.0.0 | same | 12.0.0.0 | 12.4 (`jellyfin/jellyfin:12.4.20261026-211555`) | linux/amd64 | Full acceptance run (CI `integration` job; local run 2026-10-28) |
| 1.0.0.0 | same | 12.0.0.0 | 12.0 (`jellyfin/jellyfin:12.0` arm64) | linux/arm64 | CI `arm64-smoke` job on a native `ubuntu-24.04-arm` runner (load, audit, apply one fixture). **Not yet executed**: it runs on the first CI run of the PR. Locally only the binary was checked: the assembly is IL-only AnyCPU (PE32, `ILONLY`, no 32-bit flags, no native dependencies). |
| 1.0.0.0 | — | — | 10.x, 13+ | any | Not supported. 10.x refuses the plugin (its version is below targetAbi 12.0.0.0); if a later major loads it at all, Smart Match stays idle and writes nothing. |

The plugin is a managed AnyCPU assembly with no native dependencies, so the same ZIP serves x64 and ARM64.

Why ARM64 was not run locally: under QEMU user-mode emulation both the official `jellyfin/jellyfin:12.0-arm64` image (`NullReferenceException` in EF Core `FindCompiledModel` during Jellyfin's own startup migration, before any plugin loads) and the `mcr.microsoft.com/dotnet/sdk:10.0` arm64 image (abort inside MSBuild) crash. That is an emulator limitation, which is why CI uses a native ARM64 runner.

Not validated here: a physical Raspberry Pi 400, real TMDb/TVDb providers (integration uses an offline stub provider so CI is deterministic), and Jellyfin 12.1–12.3 (12.0 and 12.4 bracket them; no ABI break was found between them for the APIs used).
