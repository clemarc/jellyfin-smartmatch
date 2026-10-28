# Compatibility matrix

| Plugin | Built against | targetAbi | Jellyfin server | Architecture | Evidence |
|---|---|---|---|---|---|
| 1.0.0.0 | Jellyfin.Controller / Jellyfin.Model 12.0.0, .NET SDK 10.0.403 | 12.0.0.0 | 12.0 (`jellyfin/jellyfin:12.0.20260908-012347`) | linux/amd64 | Full acceptance run (CI `integration` job; local run 2026-10-28) |
| 1.0.0.0 | same | 12.0.0.0 | 12.4 (`jellyfin/jellyfin:12.4.20261026-211555`) | linux/amd64 | Full acceptance run (CI `integration` job) |
| 1.0.0.0 | same | 12.0.0.0 | 12.0 (`jellyfin/jellyfin:12.0-arm64.20260908-012347`) | linux/arm64 | Smoke: load, audit, apply one fixture (QEMU locally; native `ubuntu-24.04-arm` runner in CI) |
| 1.0.0.0 | — | — | 10.x, 11.x, 13+ | any | Not supported. 10.x/11.x refuse the plugin (targetAbi); a 13.x server would load it but Smart Match stays idle and writes nothing. |

The plugin is a managed AnyCPU assembly with no native dependencies, so the same ZIP serves x64 and ARM64.

Not validated here: a physical Raspberry Pi 400, real TMDb/TVDb providers (integration uses an offline stub provider so CI is deterministic), and Jellyfin 12.1–12.3 (12.0 and 12.4 bracket them; no ABI break was found between them for the APIs used).
