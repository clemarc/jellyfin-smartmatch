# Installing and updating Smart Match

Smart Match is a native Jellyfin 12 plugin (.NET 10, AnyCPU). The same ZIP runs on Linux x64 and ARM64, including a Raspberry Pi 400 running 64-bit Raspberry Pi OS with Jellyfin in Docker.

| Requirement | Value |
|---|---|
| Jellyfin server | 12.0 or later 12.x (tested: 12.0 and 12.4). Not 10.x. |
| Target ABI | `12.0.0.0` |
| Network | Outbound HTTPS to GitHub (install/update) and to whichever metadata providers your libraries enable |
| Inbound ports | None beyond your existing Jellyfin access |

> The catalogue below goes live when the repository owner publishes the first release tag. Until then use a ZIP built by CI (see "Manual install").

## Install from the plugin catalogue (recommended)

1. **Dashboard → Plugins → Repositories → +** (in Jellyfin 12: *Dashboard → Plugins → Manage repositories*).
2. Name: `Smart Match`. URL:

   ```text
   https://raw.githubusercontent.com/clemarc/jellyfin-smartmatch/catalog/manifest.json
   ```

3. **Dashboard → Plugins → Catalogue**, open **Smart Match** (category *Metadata*), click **Install**.
4. Restart Jellyfin (Docker: `docker restart <your-jellyfin-container>`).
5. **Dashboard → Plugins → Smart Match** (or the *Smart Match* entry in the dashboard menu) shows the overview. The status line should read *Compatible (ABI 12.0.0.0)* and *Report only*.

Jellyfin verifies the MD5 checksum from the manifest when it downloads the ZIP. The release page also carries `SHA256SUMS.txt` if you want to check the file yourself:

```bash
sha256sum -c SHA256SUMS.txt
```

## What happens after the first start

- After a two-minute delay Smart Match runs a **report-only audit** of the films and series Jellyfin already knows. It searches one title at a time, 0.5 s apart, and does not read or probe video files. On a Pi 400 with ~1,000 records expect it to take a few minutes plus provider latency; playback is unaffected.
- From then on it watches library scans. New or changed films and series appear in the report shortly after a scan finishes.
- Nothing in Jellyfin changes until you click **Apply**, confirm a bulk preview, or enable auto-apply for a library.

## Update

Catalogue installs: **Dashboard → Plugins → My plugins → Smart Match → Update** (or let Jellyfin's automatic plugin updates do it), then restart. Settings, the findings report and the change history live in `/config/plugins/configurations/SmartMatch/` and `/config/plugins/configurations/Jellyfin.Plugin.SmartMatch.xml`, outside the versioned plugin folder, so they survive updates (tested: AT-15).

## Manual install (no catalogue)

1. Download `smartmatch_<version>-jf12.zip` from the GitHub release (or the `plugin` artifact of a CI run).
2. Find where your container's `/config` lives on the host. In Docker Compose or Portainer look for the volume mapped to `/config`, for example `- /srv/jellyfin/config:/config`. **Check this before running commands; host and container paths differ.**
3. Stop Jellyfin, then extract into a new folder under `plugins`:

   ```bash
   docker stop jellyfin
   sudo mkdir -p "/srv/jellyfin/config/plugins/Smart Match_1.0.0.0"
   sudo unzip smartmatch_1.0.0.0-jf12.zip -d "/srv/jellyfin/config/plugins/Smart Match_1.0.0.0"
   sudo chown -R "$(stat -c %u:%g /srv/jellyfin/config)" "/srv/jellyfin/config/plugins/Smart Match_1.0.0.0"
   docker start jellyfin
   ```

   The ZIP contains only `Jellyfin.Plugin.SmartMatch.dll` and `meta.json`. Keep `meta.json`: without it Jellyfin assigns the plugin a made-up id.

4. To update manually, stop Jellyfin, delete the old `Smart Match_<old version>` folder, extract the new ZIP into `Smart Match_<new version>`, start Jellyfin.

## Downgrade

Downgrading is not supported. Jellyfin refuses to install an older catalogue version over a newer one, and Smart Match refuses to read journal files written by a newer schema: it moves them aside as `*.corrupt-<timestamp>` and starts with an empty report (applied metadata in Jellyfin is unaffected). If you must downgrade, uninstall, delete `/config/plugins/configurations/SmartMatch/`, then install the older ZIP manually.

## Uninstall

**Dashboard → Plugins → My plugins → Smart Match → Uninstall**, restart. Provider ids Smart Match applied stay on your items (they are ordinary Jellyfin metadata). Delete `/config/plugins/configurations/SmartMatch/` and `Jellyfin.Plugin.SmartMatch.xml` to remove its report and history.

## Recommended Docker setup

Mount media read-only so nothing in the Jellyfin container can write next to files Plex or qBittorrent use:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:12.4
    volumes:
      - /srv/jellyfin/config:/config
      - /srv/jellyfin/cache:/cache
      - /mnt/media:/media:ro
```

Smart Match never writes to media paths itself. With a read-only mount, Jellyfin's own NFO/artwork savers (if enabled) fail harmlessly while identification still succeeds (tested: AT-12b).

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Status shows *Unsupported server version* | Jellyfin is not 12.x. Smart Match stays idle and writes nothing. |
| Findings show *Provider error: NETWORK* | The container cannot reach the metadata provider (DNS, proxy, CA certificates). Fix the container's network or CA trust; Smart Match never disables TLS validation. It retries with back-off. |
| *No remote metadata providers are enabled* | Enable a provider (e.g. TheMovieDb) for that library: Dashboard → Libraries → library → Metadata downloaders. |
| Nothing appears after a scan | Check *Monitoring new scans* on the overview; check the library is selected under Settings; run **Reconcile now**. |
| Catalogue shows no Smart Match | The repository URL must be the raw `catalog/manifest.json` URL above, and the first release must exist. |
