#!/usr/bin/env python3
"""Disposable Jellyfin 12 integration test for Smart Match.

Starts a throwaway Jellyfin container with a synthetic library and a deterministic, offline
stub metadata provider, installs the packaged plugin ZIP, and drives the real HTTP API through
the PRD acceptance scenarios. Nothing touches a real server or real media.

Usage:
  tests/integration/run_integration.py --image jellyfin/jellyfin:12.0.20260908-012347 \
      --zip dist/smartmatch_1.0.0.0-jf12.zip --provider-dll tests/SmartMatch.TestProvider/bin/Release/net10.0/SmartMatch.TestProvider.dll \
      [--platform linux/arm64] [--smoke] [--catalog] [--update-zip dist2/smartmatch_1.0.0.1-jf12.zip] [--report out.json]

--smoke runs the reduced ARM64 scenario (AT-16): load, audit, apply one fixture.
--catalog additionally installs the plugin from a locally served repository manifest (AT-01).
--update-zip upgrades the plugin in place and checks settings/decisions survive (AT-15).
"""

from __future__ import annotations

import argparse
import hashlib
import http.server
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from smartmatch_build import md5, read_build_yaml, validate_manifest  # noqa: E402

PLUGIN_GUID = read_build_yaml()["guid"]
PROVIDER = "SmartMatch Test Provider"
AUTH_BASE = 'MediaBrowser Client="SmartMatchIT", Device="it", DeviceId="smartmatch-it", Version="1.0"'

TV = {
    "www.UIndex.org - The Fall and Rise of Reggie Dinkins": ["Season 01/www.UIndex.org - The.Fall.and.Rise.of.Reggie.Dinkins.S01E01.1080p.WEB-DL.mkv"],
    "Clean Show (2020)": ["Season 1/Clean.Show.S01E01.mkv"],
    "The Office": ["Season 1/The.Office.S01E01.mkv"],
    "www.UIndex.org - Severance (2022)": ["Season 1/Severance.S01E01.mkv"],
    "Provider Down Show": ["Season 1/Provider.Down.Show.S01E01.mkv"],
}
MOVIES = {
    "The.Dark.Knight.2008.1080p.BluRay.x264": "The.Dark.Knight.2008.1080p.BluRay.x264.mkv",
    "www.UIndex.org - Dune.2021.2160p.HEVC": "Dune.2021.2160p.HEVC.mkv",
    "www.UIndex.org - Arrival.2016.1080p": "Arrival.2016.1080p.mkv",
}


class Failure(Exception):
    pass


class Report:
    def __init__(self) -> None:
        self.results: list[dict] = []
        self.meta: dict = {}

    def check(self, test_id: str, description: str, condition: bool, detail: str = "") -> None:
        self.results.append({"id": test_id, "description": description, "passed": bool(condition), "detail": detail})
        mark = "PASS" if condition else "FAIL"
        print(f"[{mark}] {test_id}: {description}" + (f" ({detail})" if detail else ""), flush=True)
        if not condition:
            raise Failure(f"{test_id}: {description} {detail}")

    def summary(self) -> dict:
        return {
            "meta": self.meta,
            "passed": sum(r["passed"] for r in self.results),
            "failed": sum(not r["passed"] for r in self.results),
            "results": self.results,
        }


class Jellyfin:
    def __init__(self, base: str) -> None:
        self.base = base
        self.token: str | None = None
        self.user_id: str | None = None

    def request(self, method: str, path: str, body=None, token: str | None = "default", raw: bool = False, timeout: int = 60):
        url = self.base + path
        data = None
        headers = {"Accept": "application/json"}
        tok = self.token if token == "default" else token
        headers["Authorization"] = AUTH_BASE + (f', Token="{tok}"' if tok else "")
        if body is not None:
            data = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                payload = r.read()
                if raw:
                    return r.status, payload, dict(r.headers)
                return r.status, (json.loads(payload) if payload and r.headers.get("Content-Type", "").startswith("application/json") else payload.decode("utf-8", "replace"))
        except urllib.error.HTTPError as e:
            payload = e.read()
            try:
                parsed = json.loads(payload) if payload else None
            except json.JSONDecodeError:
                parsed = payload.decode("utf-8", "replace")
            if raw:
                return e.code, payload, dict(e.headers)
            return e.code, parsed

    def get(self, path, **kw):
        return self.request("GET", path, **kw)

    def post(self, path, body=None, **kw):
        return self.request("POST", path, body if body is not None else {}, **kw)

    def ok(self, method, path, body=None, expect=(200, 204), **kw):
        status, payload = self.request(method, path, body, **kw)
        if status not in expect:
            raise Failure(f"{method} {path} -> {status}: {payload}")
        return payload


def sha_tree(root: Path) -> dict:
    out = {}
    for p in sorted(root.rglob("*")):
        if p.is_file():
            out[str(p.relative_to(root))] = hashlib.sha256(p.read_bytes()).hexdigest()
    return out


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def docker(*args, check=True, capture=False):
    return subprocess.run(["docker", *args], check=check, text=True, capture_output=capture)


def wait_until(predicate, timeout: float, interval: float = 2.0, what: str = "condition"):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            last = predicate()
            if last:
                return last
        except (urllib.error.URLError, ConnectionError, OSError, Failure) as e:
            last = e
        time.sleep(interval)
    raise Failure(f"timed out waiting for {what} (last: {last})")


def make_media(media: Path) -> None:
    for folder, files in TV.items():
        for rel in files:
            p = media / "tv" / folder / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b"synthetic-not-a-video-" + folder.encode())
    for folder, name in MOVIES.items():
        p = media / "movies" / folder / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(b"synthetic-not-a-video-" + folder.encode())


def install_plugin_dir(config: Path, zip_path: Path, provider_dll: Path | None) -> None:
    plugins = config / "plugins"
    plugins.mkdir(parents=True, exist_ok=True)
    version = json.loads(zipfile.ZipFile(zip_path).read("meta.json"))["version"]
    target = plugins / f"Smart Match_{version}"
    if target.exists():
        shutil.rmtree(target)
    target.mkdir()
    zipfile.ZipFile(zip_path).extractall(target)
    if provider_dll:
        tp = plugins / "SmartMatch Test Provider_0.0.1.0"
        tp.mkdir(exist_ok=True)
        shutil.copy(provider_dll, tp / provider_dll.name)
        (tp / "meta.json").write_text(json.dumps({
            "guid": "5e1f0c84-7a73-4d6b-9a1c-6d3f0c1b2a99", "name": PROVIDER, "version": "0.0.1.0",
            "targetAbi": "12.0.0.0", "status": "Active", "autoUpdate": False, "assemblies": [provider_dll.name],
            "category": "Metadata", "description": "test", "overview": "test", "owner": "test", "changelog": "",
            "timestamp": "2026-01-01T00:00:00Z",
        }))
    seed_config(config)


def seed_config(config: Path) -> None:
    conf = config / "plugins" / "configurations"
    conf.mkdir(parents=True, exist_ok=True)
    target = conf / "Jellyfin.Plugin.SmartMatch.xml"
    if target.exists():
        return
    # Short delays so the test does not wait minutes; everything else is the shipped default.
    target.write_text("""<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <StartupDelaySeconds>5</StartupDelaySeconds>
  <SettleDelaySeconds>5</SettleDelaySeconds>
  <MinSearchIntervalMs>250</MinSearchIntervalMs>
</PluginConfiguration>
""")


def library_options(provider: str, savers: list[str] | None = None) -> dict:
    def type_opts(t, fetchers):
        return {"Type": t, "MetadataFetchers": fetchers, "MetadataFetcherOrder": fetchers, "ImageFetchers": [], "ImageFetcherOrder": [], "ImageOptions": []}
    return {
        "LibraryOptions": {
            "Enabled": True,
            "EnableRealtimeMonitor": False,
            "EnableInternetProviders": True,
            "SaveLocalMetadata": False,
            "MetadataSavers": savers or [],
            "EnableChapterImageExtraction": False,
            "EnableTrickplayImageExtraction": False,
            "ExtractChapterImagesDuringLibraryScan": False,
            "EnableEmbeddedTitles": False,
            "AutomaticRefreshIntervalDays": 0,
            "PreferredMetadataLanguage": "en",
            "MetadataCountryCode": "US",
            "TypeOptions": [
                type_opts("Series", [provider]),
                type_opts("Season", []),
                type_opts("Episode", []),
                type_opts("Movie", [provider]),
            ],
            "DisabledLocalMetadataReaders": ["Nfo"],
            "PathInfos": [],
        }
    }


class Runner:
    def __init__(self, args, work: Path | None = None) -> None:
        self.args = args
        self.report = Report()
        self.work = work or Path(args.workdir or tempfile.mkdtemp(prefix="smartmatch-it-"))
        self.config = self.work / "config"
        self.media = self.work / "media"
        self.cache = self.work / "cache"
        self.port = free_port()
        self.name = "smartmatch-it-" + uuid.uuid4().hex[:8]
        self.jf = Jellyfin(f"http://127.0.0.1:{self.port}")
        self.viewer_token = None
        self.libraries: dict[str, str] = {}

    # ------------------------------------------------------------------ container

    def start(self) -> None:
        cmd = ["run", "-d", "--name", self.name, "-p", f"127.0.0.1:{self.port}:8096",
               "-v", f"{self.config}:/config", "-v", f"{self.cache}:/cache", "-v", f"{self.media}:/media:ro",
               "--add-host=host.docker.internal:host-gateway"]
        if self.args.platform:
            cmd += ["--platform", self.args.platform]
        cmd.append(self.args.image)
        docker(*cmd)
        self.wait_ready()

    def wait_ready(self) -> None:
        def ready():
            # Jellyfin 12 answers /System/Info/Public from a temporary setup server while it boots;
            # /Branding/Configuration is only served by the real server.
            status, info = self.jf.get("/System/Info/Public", token=None)
            branding, _ = self.jf.get("/Branding/Configuration", token=None)
            return status == 200 and branding == 200 and isinstance(info, dict) and info
        info = wait_until(ready, timeout=self.args.start_timeout, interval=3, what="Jellyfin to start")
        self.report.meta["serverVersion"] = info.get("Version")
        self.report.meta["serverProduct"] = info.get("ProductName")

    def restart(self) -> None:
        docker("restart", self.name)
        self.wait_ready()
        self.login()

    def stop(self) -> None:
        if not self.args.keep:
            docker("rm", "-f", self.name, check=False, capture=True)

    def logs(self) -> str:
        return docker("logs", self.name, check=False, capture=True).stdout + docker("logs", self.name, check=False, capture=True).stderr

    # ------------------------------------------------------------------ setup

    def wizard(self) -> None:
        self.jf.ok("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}, token=None)
        self.jf.get("/Startup/User", token=None)
        self.jf.ok("POST", "/Startup/User", {"Name": "admin", "Password": "admin-pass-1"}, token=None)
        self.jf.ok("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False}, token=None)
        self.jf.ok("POST", "/Startup/Complete", token=None)
        self.login()
        self.jf.ok("POST", "/Users/New", {"Name": "viewer", "Password": "viewer-pass-1"})
        status, auth = self.jf.post("/Users/AuthenticateByName", {"Username": "viewer", "Pw": "viewer-pass-1"}, token=None)
        if status != 200:
            raise Failure(f"viewer login failed: {status} {auth}")
        self.viewer_token = auth["AccessToken"]

    def login(self) -> None:
        status, auth = self.jf.post("/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin-pass-1"}, token=None)
        if status != 200:
            raise Failure(f"admin login failed: {status} {auth}")
        self.jf.token = auth["AccessToken"]
        self.jf.user_id = auth["User"]["Id"]

    def add_libraries(self) -> None:
        tv = urllib.parse.urlencode({"name": "TV", "collectionType": "tvshows", "paths": "/media/tv", "refreshLibrary": "false"})
        mv = urllib.parse.urlencode({"name": "Movies", "collectionType": "movies", "paths": "/media/movies", "refreshLibrary": "false"})
        self.jf.ok("POST", "/Library/VirtualFolders?" + tv, library_options(PROVIDER))
        # Movies library deliberately has the NFO saver enabled (AT-12) while media is mounted read-only.
        self.jf.ok("POST", "/Library/VirtualFolders?" + mv, library_options(PROVIDER, ["Nfo"]))
        for f in self.jf.ok("GET", "/Library/VirtualFolders"):
            self.libraries[f["Name"]] = f["ItemId"]

    def scan(self) -> None:
        self.jf.ok("POST", "/Library/Refresh")
        time.sleep(3)

        def done():
            tasks = self.jf.ok("GET", "/ScheduledTasks")
            scan = next(t for t in tasks if t.get("Key") == "RefreshLibrary")
            return scan.get("State") == "Idle"
        wait_until(done, timeout=600, interval=3, what="library scan")

    def sm(self, method, path, body=None, expect=(200,)):
        return self.jf.ok(method, "/SmartMatch/" + path, body, expect=expect)

    def wait_idle(self, what="Smart Match queue to drain", timeout=300):
        def idle():
            s = self.sm("GET", "Status")
            return s["QueueDue"] == 0 and s["WorkerState"] in ("Idle",) and not s.get("ActiveAudit") and s
        return wait_until(idle, timeout=timeout, interval=3, what=what)

    def findings(self, **params):
        params.setdefault("take", 200)
        return self.sm("GET", "Reviews?" + urllib.parse.urlencode(params))["Items"]

    def finding(self, title_part: str):
        for f in self.findings():
            if title_part in f["OriginalName"]:
                return f
        return None

    def item(self, item_id: str) -> dict:
        return self.jf.ok("GET", f"/Items/{item_id}?userId={self.jf.user_id}")

    def items_by_path(self) -> dict:
        res = self.jf.ok("GET", f"/Items?recursive=true&includeItemTypes=Series,Movie&fields=Path,ProviderIds,Overview&userId={self.jf.user_id}")
        return {i["Path"]: i for i in res["Items"]}

    # ------------------------------------------------------------------ scenarios

    def run(self) -> int:
        r = self.report
        self.work.mkdir(parents=True, exist_ok=True)
        r.meta.update({"image": self.args.image, "platform": self.args.platform or "native", "zip": str(self.args.zip),
                       "zipMd5": md5(Path(self.args.zip)), "workdir": str(self.work)})
        make_media(self.media)
        media_before = sha_tree(self.media)
        install_plugin_dir(self.config, Path(self.args.zip), Path(self.args.provider_dll))
        try:
            self.start()
            self.wizard()
            self.check_bootstrap()
            self.check_auth()
            self.add_libraries()
            if self.args.smoke:
                self.smoke()
            else:
                self.full(media_before)
            r.check("AT-14", "media files and folders byte-identical after all operations", sha_tree(self.media) == media_before)
            if self.args.update_zip:
                self.update(Path(self.args.update_zip))
        except Exception as e:  # noqa: BLE001 - report every failure with logs
            traceback.print_exc()
            log_path = self.work / "jellyfin.log"
            log_path.write_text(self.logs())
            print(f"Jellyfin container log written to {log_path}")
            r.results.append({"id": "FATAL", "description": str(e), "passed": False, "detail": ""})
        finally:
            (self.work / "jellyfin.log").write_text(self.logs())
            self.stop()
        if self.args.catalog:
            try:
                self.catalog_install()
            except Exception as e:  # noqa: BLE001
                traceback.print_exc()
                r.results.append({"id": "AT-01", "description": str(e), "passed": False, "detail": ""})
        summary = r.summary()
        if self.args.report:
            Path(self.args.report).write_text(json.dumps(summary, indent=2))
        print(json.dumps({k: summary[k] for k in ("passed", "failed")}))
        return 0 if summary["failed"] == 0 else 1

    def check_bootstrap(self) -> None:
        r = self.report
        plugins = self.jf.ok("GET", "/Plugins")
        sm = next((p for p in plugins if p["Id"].replace("-", "") == PLUGIN_GUID.replace("-", "")), None)
        r.check("AT-01a", "plugin loaded and Active", sm is not None and sm.get("Status") == "Active", json.dumps(sm))
        r.meta["pluginVersion"] = sm.get("Version")
        status, html, headers = self.jf.request("GET", "/web/configurationpage?name=SmartMatch", raw=True)
        r.check("AT-01b", "configuration page served", status == 200 and b"SmartMatchPage" in html)
        status, js, headers = self.jf.request("GET", "/web/configurationpage?name=SmartMatchJs", raw=True)
        ctype = {k.lower(): v for k, v in headers.items()}.get("content-type", "")
        r.check("AT-01c", "UI controller module served as JavaScript", status == 200 and b"export default" in js and "javascript" in ctype, ctype)
        tasks = self.jf.ok("GET", "/ScheduledTasks")
        keys = {t.get("Key") for t in tasks}
        r.check("M0-tasks", "scheduled tasks registered", {"SmartMatchAudit", "SmartMatchReconcile"} <= keys)
        pages = self.jf.ok("GET", "/web/ConfigurationPages")
        r.check("M0-page", "dashboard page registered", any(p.get("Name") == "SmartMatch" for p in pages))
        status = wait_until(lambda: self.sm("GET", "Status").get("MonitoringActive") and self.sm("GET", "Status"), 90, what="monitoring to start")
        r.check("M0-compat", "server ABI reported compatible", status["Compatible"], status["ServerVersion"])
        r.check("AT-21a", "default mode is Report only", status["Mode"] == "ReportOnly")

    def check_auth(self) -> None:
        r = self.report
        rid = str(uuid.uuid4())
        endpoints = [
            ("GET", "Status"), ("GET", "Config"), ("PUT", "Config"), ("GET", "Reviews"), (f"GET", f"Reviews/{rid}"),
            ("POST", f"Reviews/{rid}/Apply"), ("POST", f"Reviews/{rid}/Ignore"), ("POST", f"Reviews/{rid}/Rollback"),
            ("POST", "ApplyBatch/Preview"), ("POST", f"ApplyBatch/{rid}/Commit"), ("POST", "Audits"), ("GET", "Export"),
            ("GET", "Decisions"), ("POST", "Normalize/Preview"), ("GET", "Coverage"), ("POST", "Reconcile"),
        ]
        anon = []
        viewer = []
        for method, path in endpoints:
            s, body = self.jf.request(method, "/SmartMatch/" + path, {} if method != "GET" else None, token=None)
            anon.append((path, s))
            s2, body2 = self.jf.request(method, "/SmartMatch/" + path, {} if method != "GET" else None, token=self.viewer_token)
            viewer.append((path, s2, body2))
        r.check("AT-11a", "anonymous calls rejected with 401 on every endpoint", all(s == 401 for _, s in anon), str([x for x in anon if x[1] != 401]))
        r.check("AT-11b", "playback-only user rejected with 403 on every endpoint", all(s == 403 for _, s, _ in viewer), str([x[:2] for x in viewer if x[1] != 403]))
        r.check("AT-11c", "no path leaked to non-admin responses", all("/media" not in json.dumps(b) for _, _, b in viewer if b))

    def full(self, media_before: dict) -> None:
        r = self.report
        # AT-17: ordinary scan -> events -> report entries without a manual audit.
        scan_started = time.time()
        self.scan()
        r.meta["scanSeconds"] = round(time.time() - scan_started, 1)
        self.wait_idle()
        status = self.sm("GET", "Status")
        r.check("AT-17", "scan events produced report entries without a manual audit", status["EventsReceived"] > 0 and len(self.findings()) >= 5, f"events={status['EventsReceived']}")

        reggie = self.finding("Reggie Dinkins")
        r.check("AT-02", "UIndex prefix removed and candidate found", reggie is not None and reggie["SearchTitle"] == "The Fall and Rise of Reggie Dinkins" and reggie["CandidateId"] == "tmdb:990001", json.dumps(reggie))
        r.check("AT-17b", "finding origin is the new scan", reggie["Origin"] == "NewScan", reggie["Origin"])
        items = self.items_by_path()
        clean = items["/media/tv/Clean Show (2020)"]
        r.check("AT-05", "correctly identified item skipped and unchanged", clean["ProviderIds"].get("Tmdb") == "990002" and self.finding("Clean Show") is None)
        office = self.finding("The Office")
        r.check("AT-06", "ambiguous The Office needs review, never applied", office["Status"] == "NeedsReview" and not office["AutoEligible"] and not items["/media/tv/The Office"]["ProviderIds"].get("Tmdb"))
        down = self.finding("Provider Down Show")
        r.check("AT-08", "provider outage reported as retriable error without modifying item", down["Status"] == "ProviderError" and not items["/media/tv/Provider Down Show"]["ProviderIds"], json.dumps(down))
        r.check("AT-25", "scan completed despite provider outage", "/media/tv/Provider Down Show" in items and len(items) >= 8)
        dk = self.finding("Dark.Knight")
        r.check("AT-13", "movie release name keeps title and year, ignores codec suffix", dk["SearchTitle"] == "The Dark Knight" and dk["TrustedYear"] == 2008 and dk["CandidateId"] == "tmdb:990101", json.dumps(dk))
        untouched = all(not items[p]["ProviderIds"].get("Tmdb") for p in items if "UIndex" in p or "Dark.Knight" in p)
        r.check("AT-03/AT-21", "report-only scan and audit changed no Jellyfin metadata", untouched)

        # AT-24: event storms -> one lookup per unchanged item.
        searches = self.logs().count("SmartMatchTestProvider search: The Fall and Rise of Reggie Dinkins")
        r.check("AT-24", "one effective provider lookup per unchanged item despite repeated events", searches == 1, f"searches={searches}")

        # AT-18-ish: a manual full audit re-checks existing media without new searches for unchanged items.
        run = self.sm("POST", "Audits", {"Force": False})
        self.wait_idle("audit to finish")
        run = self.sm("GET", f"Audits/{run['RunId']}")
        r.check("AT-18", "existing-library audit covers pre-indexed items, unchanged ones not re-searched", run["Status"] == "Completed" and run["Searches"] == 0 and run["Enqueued"] >= 8, json.dumps(run["Counts"]))
        cov = self.sm("GET", "Status")["Coverage"]
        r.check("AT-18b", "coverage reports every item checked", cov["CheckedItems"] == cov["TotalItems"] and cov["TotalItems"] >= 8, json.dumps(cov))

        # AT-12: sidecar warning for the NFO-saving Movies library.
        status = self.sm("GET", "Status")
        r.check("AT-12a", "NFO-saving library flagged with a warning", any("Movies" in w and "NFO" in w for w in status["Warnings"]), json.dumps(status["Warnings"]))

        # AT-20: Apply all eligible across the current filter (movies only).
        plan = self.sm("POST", "ApplyBatch/Preview", {"Scope": "AllEligible", "Filter": {"MediaType": "Movie"}})
        names = sorted(i["CandidateName"] for i in plan["Items"])
        r.check("AT-20a", "preview shows exact scope, count and items", "current filter" in plan["ScopeDescription"] and names == ["Arrival", "Dune", "The Dark Knight"], f"{plan['ScopeDescription']} {names}")
        r.check("AT-20b", "preview did not mutate", not self.items_by_path()["/media/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv"]["ProviderIds"].get("Tmdb"))
        bad = self.jf.request("POST", f"/SmartMatch/ApplyBatch/{plan['PlanId']}/Commit", {"PlanHash": "x" * 32, "IdempotencyKey": "k1"})
        r.check("AT-20c", "confirmation with a mismatched plan is refused", bad[0] == 409, str(bad))

        # AT-07 first: make Arrival stale by assigning a different id through Jellyfin itself.
        arrival_path = "/media/movies/www.UIndex.org - Arrival.2016.1080p/Arrival.2016.1080p.mkv"
        arrival_item = self.item(self.items_by_path()[arrival_path]["Id"])
        arrival_item["ProviderIds"] = {"Imdb": "tt2543164"}
        self.jf.ok("POST", f"/Items/{arrival_item['Id']}", arrival_item)
        key = str(uuid.uuid4())
        run = self.sm("POST", f"ApplyBatch/{plan['PlanId']}/Commit", {"PlanHash": plan["PlanHash"], "IdempotencyKey": key})
        replay = self.sm("POST", f"ApplyBatch/{plan['PlanId']}/Commit", {"PlanHash": plan["PlanHash"], "IdempotencyKey": key})
        r.check("AT-20d", "commit is idempotent", replay["RunId"] == run["RunId"])
        done = wait_until(lambda: (b := self.sm("GET", f"ApplyBatch/{run['RunId']}"))["Status"] != "Running" and b, 300, what="batch")
        outcomes = {i["Title"]: i["Outcome"] for i in done["Items"]}
        r.check("AT-20e", "only eligible records changed; per-item outcomes reported", outcomes.get("Arrival.2016.1080p") in ("Conflict",) and sum(o == "Applied" for o in outcomes.values()) == 2, json.dumps(outcomes))
        r.check("AT-07", "conflicting id appearing after preview aborts with conflict", outcomes.get("Arrival.2016.1080p") == "Conflict" and self.item(arrival_item["Id"])["ProviderIds"] == {"Imdb": "tt2543164"})
        items = self.items_by_path()
        dk_item = items["/media/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv"]
        r.check("AT-04", "applied provider id persisted, metadata refreshed, path unchanged", dk_item["ProviderIds"].get("Tmdb") == "990101" and (dk_item.get("Overview") or "").startswith("Synthetic overview") and dk_item["Name"] == "The Dark Knight", json.dumps({k: dk_item.get(k) for k in ("Name", "Overview", "ProviderIds")}))
        r.check("AT-12b", "identification succeeded although NFO saving was blocked by the read-only media mount", dk_item["ProviderIds"].get("Tmdb") == "990101" and not any(p.endswith(".nfo") for p in sha_tree(self.media)))

        # AT-04 (single apply) and AT-19 (selected with an ambiguous item).
        sev = self.finding("Severance")
        res = self.sm("POST", f"Reviews/{sev['ReviewId']}/Apply", {"CandidateId": sev["CandidateId"], "Version": sev["Version"]})
        r.check("AT-04b", "Apply one succeeds", res["Outcome"] == "Applied", json.dumps(res))
        stale = self.jf.request("POST", f"/SmartMatch/Reviews/{sev['ReviewId']}/Apply", {"CandidateId": sev["CandidateId"], "Version": sev["Version"]})
        r.check("M2-stale", "stale ETag rejected", stale[0] == 409, str(stale))
        reggie = self.finding("Reggie Dinkins")
        office = self.finding("The Office")
        arrival = self.finding("Arrival")
        plan = self.sm("POST", "ApplyBatch/Preview", {"Scope": "Selected", "ReviewIds": [reggie["ReviewId"], office["ReviewId"], arrival["ReviewId"]]})
        reasons = {x["Title"]: x["Reason"] for x in plan["Exclusions"]}
        r.check("AT-19a", "selected preview applies only valid items and lists exclusions with reasons", len(plan["Items"]) == 1 and "Ambiguous" in reasons.get("The Office", "") and len(reasons) == 2, json.dumps(reasons))
        run = self.sm("POST", f"ApplyBatch/{plan['PlanId']}/Commit", {"PlanHash": plan["PlanHash"], "IdempotencyKey": str(uuid.uuid4())})
        done = wait_until(lambda: (b := self.sm("GET", f"ApplyBatch/{run['RunId']}"))["Status"] != "Running" and b, 300, what="batch")
        reggie_item = self.items_by_path()["/media/tv/www.UIndex.org - The Fall and Rise of Reggie Dinkins"]
        r.check("AT-19b", "selected valid item applied; ambiguous untouched", done["Items"][0]["Outcome"] == "Applied" and reggie_item["ProviderIds"].get("Tmdb") == "990001" and reggie_item["Name"] == "The Fall and Rise of Reggie Dinkins" and not self.items_by_path()["/media/tv/The Office"]["ProviderIds"].get("Tmdb"))

        # Rollback of plugin-owned fields.
        reggie = self.finding("Reggie Dinkins")
        rb = self.sm("POST", f"Reviews/{reggie['ReviewId']}/Rollback", {"Version": reggie["Version"]})
        reggie_item = self.items_by_path()["/media/tv/www.UIndex.org - The Fall and Rise of Reggie Dinkins"]
        r.check("M2-rollback", "conditional rollback restores provider ids and title", rb["Outcome"] == "RolledBack" and not reggie_item["ProviderIds"].get("Tmdb") and reggie_item["Name"].startswith("www.UIndex.org"), json.dumps(rb))

        # Self-generated events do not loop.
        time.sleep(8)
        searches = self.logs().count("SmartMatchTestProvider search: The Fall and Rise of Reggie Dinkins")
        r.check("M3-noloop", "applying and rolling back caused no re-search loop", searches == 1, f"searches={searches}")

        # AT-22: enable auto-apply for TV only, add a new prefixed series, scan.
        config = self.sm("GET", "Config")
        config["Mode"] = "AutoApplyHighConfidence"
        config["AutoApplyLibraryIds"] = [self.libraries["TV"]]
        self.sm("PUT", "Config", config)
        bad = dict(config, MinSearchIntervalMs=1)
        status, err = self.jf.request("PUT", "/SmartMatch/Config", bad)
        r.check("M2-validation", "server-side configuration validation", status == 400 and "MinSearchIntervalMs" in json.dumps(err), str(status))
        self.add_series("www.UIndex.org - New Auto Show (2024)", "New.Auto.Show.S01E01.mkv")
        self.scan()
        self.wait_idle()
        auto = self.finding("New Auto Show")
        auto_item = self.items_by_path()["/media/tv/www.UIndex.org - New Auto Show (2024)"]
        decisions = self.sm("GET", "Decisions?take=200")["Items"]
        r.check("AT-22a", "high-confidence item in opted-in library auto-applied and refreshed", auto["Status"] == "Applied" and auto_item["ProviderIds"].get("Tmdb") == "990006" and any(d["Action"] == "Apply" and d["Origin"] == "Automatic" and d["ItemId"] == auto_item["Id"] for d in decisions), json.dumps(auto))
        office = self.finding("The Office")
        r.check("AT-22b", "ambiguous titles stay report-only with auto enabled", office["Status"] == "NeedsReview")

        # AT-23: disable auto, new item arrives -> stays proposed.
        config = self.sm("GET", "Config")
        config["Mode"] = "ReportOnly"
        self.sm("PUT", "Config", config)
        self.add_series("www.UIndex.org - Later Show (2023)", "Later.Show.S01E01.mkv")
        self.scan()
        self.wait_idle()
        later = self.finding("Later Show")
        r.check("AT-23", "after disabling auto-apply no new automatic commit happens", later["Status"] == "Proposed" and later["AutoEligible"] and not self.items_by_path()["/media/tv/www.UIndex.org - Later Show (2023)"]["ProviderIds"].get("Tmdb"))

        if self.args.ui:
            self.ui_check()

        # Export (admin only, no paths).
        status, csv, _ = self.jf.request("GET", "/SmartMatch/Export?format=csv", raw=True)
        r.check("M2-export", "CSV export works and contains no media paths", status == 200 and b"Reggie" in csv and b"/media/" not in csv)

        # AT-10: restart persistence and no re-application.
        before = self.sm("GET", "Decisions?take=200")["Total"]
        self.restart()
        self.wait_idle(timeout=300)
        after_findings = {f["OriginalName"]: f["Status"] for f in self.findings()}
        after = self.sm("GET", "Decisions?take=200")["Total"]
        r.check("AT-10", "applied outcomes and audit log persist across restart; nothing re-applied", after_findings.get("The.Dark.Knight.2008.1080p.BluRay.x264") == "Applied" and after == before, f"decisions {before}->{after}")

    def ui_check(self) -> None:
        """Render the dashboard page in the real Jellyfin web client (headless Chromium)."""
        shots = self.work / "screenshots"
        shots.mkdir(exist_ok=True)
        proc = subprocess.run(["node", str(ROOT / "tests/integration/ui_check.js"), self.jf.base, "admin", "admin-pass-1", str(shots)],
                              capture_output=True, text=True, timeout=600)
        out = proc.stdout.strip().splitlines()[-1] if proc.stdout.strip() else proc.stderr[-500:]
        self.report.check("UI", "dashboard page renders status, tiles, findings, detail, preview and settings in the Jellyfin 12 web client", proc.returncode == 0, out[:600])

    def add_series(self, folder: str, episode: str) -> None:
        # The container sees /media read-only; the host side creates the "new download".
        p = self.media / "tv" / folder / "Season 1" / episode
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(b"synthetic-not-a-video-" + folder.encode())

    def smoke(self) -> None:
        r = self.report
        self.scan()
        self.wait_idle()
        reggie = self.finding("Reggie Dinkins")
        r.check("AT-16a", "ARM64: plugin starts and reports a prefixed series", reggie is not None and reggie["CandidateId"] == "tmdb:990001")
        run = self.sm("POST", "Audits", {"Force": True})
        self.wait_idle("audit")
        run = self.sm("GET", f"Audits/{run['RunId']}")
        r.check("AT-16b", "ARM64: audit completes", run["Status"] == "Completed" and run["Searches"] >= 1, json.dumps(run["Counts"]))
        reggie = self.finding("Reggie Dinkins")
        res = self.sm("POST", f"Reviews/{reggie['ReviewId']}/Apply", {"CandidateId": reggie["CandidateId"], "Version": reggie["Version"]})
        item = self.items_by_path()["/media/tv/www.UIndex.org - The Fall and Rise of Reggie Dinkins"]
        r.check("AT-16c", "ARM64: one synthetic fixture applied and refreshed", res["Outcome"] == "Applied" and item["ProviderIds"].get("Tmdb") == "990001" and (item.get("Overview") or "").startswith("Synthetic"))

    def update(self, zip_path: Path) -> None:
        r = self.report
        # AT-15: replace the plugin with a newer version (same GUID), as the catalogue update would.
        self.start_if_stopped()
        before_cfg = self.sm("GET", "Config")
        before_findings = len(self.findings())
        before_decisions = self.sm("GET", "Decisions")["Total"]
        docker("stop", self.name)
        for old in (self.config / "plugins").glob("Smart Match_*"):
            shutil.rmtree(old)
        install_plugin_dir(self.config, zip_path, None)
        docker("start", self.name)
        self.wait_ready()
        self.login()
        plugins = self.jf.ok("GET", "/Plugins")
        sm = next(p for p in plugins if p["Id"].replace("-", "") == PLUGIN_GUID.replace("-", ""))
        new_version = json.loads(zipfile.ZipFile(zip_path).read("meta.json"))["version"]
        wait_until(lambda: self.sm("GET", "Status").get("MonitoringActive"), 90, what="monitoring after update")
        r.check("AT-15", "update keeps settings, findings and decisions", sm["Version"] == new_version and sm["Status"] == "Active"
                and self.sm("GET", "Config")["Mode"] == before_cfg["Mode"] and len(self.findings()) == before_findings
                and self.sm("GET", "Decisions")["Total"] == before_decisions, f"{sm['Version']} findings={before_findings}")

    def start_if_stopped(self) -> None:
        state = docker("inspect", "-f", "{{.State.Running}}", self.name, check=False, capture=True).stdout.strip()
        if state != "true":
            docker("start", self.name)
            self.wait_ready()
            self.login()

    def catalog_install(self) -> None:
        """AT-01: add a repository manifest, install from the catalogue, restart, plugin active."""
        r = self.report
        serve = self.work / "catalog"
        serve.mkdir(exist_ok=True)
        zip_path = Path(self.args.zip)
        shutil.copy(zip_path, serve / zip_path.name)
        port = free_port()
        handler = lambda *a, **kw: http.server.SimpleHTTPRequestHandler(*a, directory=str(serve), **kw)  # noqa: E731
        server = http.server.ThreadingHTTPServer(("0.0.0.0", port), handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            url = f"http://host.docker.internal:{port}/{zip_path.name}"
            subprocess.run([sys.executable, str(ROOT / "scripts/update-manifest.py"), "--manifest", str(serve / "manifest.json"),
                            "--zip", str(zip_path), "--url", url, "--allow-http", "--timestamp", "2026-10-28T00:00:00Z"], check=True)
            manifest = json.loads((serve / "manifest.json").read_text())
            r.check("AT-01-manifest", "generated manifest passes validation", not validate_manifest(manifest, PLUGIN_GUID, allow_http=True))

            fresh = Runner(self.args, self.work / "catalog-run")
            fresh.config.mkdir(parents=True, exist_ok=True)
            fresh.media.mkdir(parents=True, exist_ok=True)
            make_media(fresh.media)
            seed_config(fresh.config)
            fresh.start()
            fresh.wizard()
            fresh.jf.ok("POST", "/Repositories", [{"Name": "Smart Match local", "Url": f"http://host.docker.internal:{port}/manifest.json", "Enabled": True}])
            packages = wait_until(lambda: [p for p in fresh.jf.ok("GET", "/Packages") if p.get("guid", p.get("Guid", "")).replace("-", "") == PLUGIN_GUID.replace("-", "")], 60, what="catalogue listing")
            r.check("AT-01d", "plugin listed in the catalogue", bool(packages))
            q = urllib.parse.urlencode({"assemblyGuid": PLUGIN_GUID, "version": manifest[0]["versions"][0]["version"], "repositoryUrl": f"http://host.docker.internal:{port}/manifest.json"})
            fresh.jf.ok("POST", "/Packages/Installed/" + urllib.parse.quote("Smart Match") + "?" + q)
            wait_until(lambda: any(p["Id"].replace("-", "") == PLUGIN_GUID.replace("-", "") for p in fresh.jf.ok("GET", "/Plugins")) or (list((fresh.config / "plugins").glob("Smart Match*"))), 120, what="install")
            fresh.restart()
            plugins = fresh.jf.ok("GET", "/Plugins")
            sm = next((p for p in plugins if p["Id"].replace("-", "") == PLUGIN_GUID.replace("-", "")), None)
            r.check("AT-01", "installed from repository, restart activates plugin, page appears", sm is not None and sm["Status"] == "Active"
                    and fresh.jf.request("GET", "/web/configurationpage?name=SmartMatch", raw=True)[0] == 200, json.dumps(sm))
            (self.work / "jellyfin-catalog.log").write_text(fresh.logs())
            fresh.stop()
        finally:
            server.shutdown()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--zip", required=True)
    parser.add_argument("--provider-dll", required=True)
    parser.add_argument("--platform")
    parser.add_argument("--workdir")
    parser.add_argument("--report")
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--catalog", action="store_true")
    parser.add_argument("--update-zip")
    parser.add_argument("--keep", action="store_true")
    parser.add_argument("--ui", action="store_true", help="also drive the dashboard page with Playwright (needs node + playwright)")
    parser.add_argument("--start-timeout", type=int, default=240)
    return Runner(parser.parse_args()).run()


if __name__ == "__main__":
    sys.exit(main())
