# folder_exporter

A Prometheus exporter for **Windows file and folder metrics**: folder size, file
count, folder age, oldest/newest file, and detection of files being **added** and
**removed**.

Single `.exe`, ~70 KB, configured by one **YAML file**, **no runtime to install** —
it builds with the C# compiler that already ships inside Windows, and runs on any
Windows 10/11 or Server 2016+ machine. Typical resident memory is **4–10 MB**; a
Prometheus scrape performs **zero disk I/O**.

**Scope: one instance per server.** It monitors local folders only. UNC paths and
mapped network drives are rejected at config load, so every metric is truthfully
attributed to the host Prometheus scraped it from. To watch a file server's
storage, run an instance on that file server.

---

## Contents

- [Quick start](#quick-start)
- [Why it is cheap to run](#why-it-is-cheap-to-run)
- [Configuration](#configuration)
- [Running it](#running-it)
  - [Foreground](#foreground)
  - [As a Windows service](#as-a-windows-service)
  - [Firewall and URL binding](#firewall-and-url-binding)
- [Connecting it to Prometheus](#connecting-it-to-prometheus)
- [Metrics reference](#metrics-reference)
- [Useful PromQL](#useful-promql)
- [Alerting](#alerting)
- [Performance](#performance)
- [Operational notes and limits](#operational-notes-and-limits)
- [Troubleshooting](#troubleshooting)
- [Command-line reference](#command-line-reference)

---

## Quick start

```powershell
# 1. Build - nothing to install, uses the in-box .NET Framework compiler.
#    Produces the deployable bundle in .\releases
.\build.ps1

# 2. Add the folders you want monitored
notepad .\releases\folder_exporter.yml

# 3. Check the config is valid
.\releases\folder_exporter.exe --check-config

# 4. Run it
.\releases\folder_exporter.exe

# 5. Look at the metrics
curl http://localhost:9847/metrics
```

Then open <http://localhost:9847/> in a browser for a status page listing every
watched folder.

To deploy: copy the whole `releases\` folder to the server, edit the YAML there,
and run `folder_exporter.exe --install` from an elevated prompt.
`releases\INSTALL.md` is a short server-side version of these instructions.

To verify the whole thing end to end on your machine:

```powershell
.\selftest.ps1        # builds a temp tree, asserts 40 behaviours, cleans up
```

### Requirements

| | |
|---|---|
| **To run** | Windows 10 / 11 / Server 2016+ with .NET Framework 4.x (present by default on all of them) |
| **To build** | The same — `csc.exe` lives in `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`. No SDK, no NuGet, no internet |
| **Privileges** | None to run on `127.0.0.1`. Administrator only to bind all interfaces or install the service |

Copy `folder_exporter.exe` + `folder_exporter.yml` to any target machine; that is
the entire deployment.

---

## Why it is cheap to run

Folder walking is the expensive part of any exporter like this, so the design
attacks that directly:

| Technique | Effect |
|---|---|
| Scans run on a **background timer**, scrapes serve a cached snapshot | A Prometheus scrape does no disk I/O at all and returns in microseconds. A hung or slow volume can never stall Prometheus |
| `FindFirstFileEx` with `FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH` | Size, timestamps and attributes arrive *with* the directory listing. The exporter never opens a file, and never issues a per-file `stat` — which is what makes `Get-ChildItem`-based scripts slow |
| **Background priority mode** (`low_priority`) | Lowers CPU *and* disk I/O priority for the whole process, so scans yield to real workloads on the box |
| Iterative traversal with an explicit stack | Constant managed-stack usage regardless of tree depth |
| 64-bit path hashes for change detection | ~24 bytes per tracked file instead of a full path string |
| Working-set trim after each scan cycle | Transient scan memory is returned to the OS; RSS stays flat instead of ratcheting up |
| `HttpListener` on `http.sys` | Kernel-mode HTTP; no thread spins waiting on a socket while idle |
| Optional throttling (`throttle_every_files`) | Paces I/O on huge or high-latency (SMB) trees |

Measured on the machine this was built on — see [Performance](#performance) for
the full numbers:

```
Flat folder, 30,000 files      0.04 s      (~750,000 files/sec)
C:\Windows, 166k files/88k dirs  14.5 s    (3.6x faster than Get-ChildItem -Recurse)
Resident memory, steady state   8-10 MB
CPU while idle                  0%
```

---

## Configuration

The exporter reads `folder_exporter.yml` from next to the `.exe`, or
`--config <path>`.

The file is **watched**: saving it reloads the configuration in place, with no
restart and without losing the add/remove counters. `POST /-/reload` does the
same on demand. If the new file is invalid, the previous configuration keeps
running and the error is logged.

A minimal config:

```yaml
listen_address: 0.0.0.0:9847
scan_interval_seconds: 60

folders:
  - name: inbound
    path: D:\data\inbound
```

Windows paths need no escaping or quoting — `D:\data\inbound` is a plain YAML
scalar. Quote a path only if it starts with `%` (environment variables) or
contains a `#`.

`folder_exporter.yml` in this repo is a fully commented reference. The two tables
below list every setting.

> **YAML subset.** The parser supports nested mappings, block sequences, flow
> sequences (`[a, b]`), quoted and plain scalars, and `#` comments — everything
> this config needs. It does not support anchors, aliases, multi-document files
> or block scalars (`|`, `>`). Errors are reported with a line number.

### Global settings

| Key | Default | Meaning |
|---|---|---|
| `listen_address` | `0.0.0.0:9847` | Bind address. Use `127.0.0.1:9847` to avoid needing admin rights |
| `metrics_path` | `/metrics` | Path Prometheus scrapes |
| `scan_interval_seconds` | `60` | How often folders are walked. This — not the scrape interval — controls data freshness and cost |
| `scan_timeout_seconds` | `900` | Abandon a single folder after this long and report partial data (`folder_scan_timed_out`) |
| `max_concurrent_scans` | `1` | Folders scanned simultaneously. Keep at 1 for one physical disk; raise it when targets sit on different volumes |
| `low_priority` | `true` | Run at low CPU **and** low disk I/O priority |
| `trim_working_set` | `true` | Return freed memory to the OS after each scan cycle |
| `throttle_every_files` | `0` | Sleep every N files (`0` disables). Use on huge or SMB trees |
| `throttle_sleep_ms` | `5` | How long to sleep when throttling |
| `scan_on_startup` | `true` | Scan immediately at startup instead of waiting one interval |
| `log_level` | `info` | `error`, `warn`, `info`, `debug` |
| `log_file` | `""` | Log file path. Empty means console only (the service writes nothing unless this is set) |
| `log_max_bytes` | `8388608` | Rotate to `<file>.1` past this size |
| `basic_auth` | disabled | Nested `username:` / `password:` keys |
| `file_age_buckets_seconds` | `[300, 3600, 21600, 86400, 604800, 2592000]` | Buckets for the file-age histogram |

### Per-folder settings

Every folder to monitor is an entry under `folders:`. Anything in `defaults:`
applies to all of them; anything inside a folder entry overrides it.

| Key | Default | Meaning |
|---|---|---|
| `name` | derived from path | Value of the `target` label. Must be unique |
| `path` | *required* | Folder to watch. Must be an **absolute local path**; environment variables such as `%ProgramData%` are expanded. UNC paths and mapped network drives are rejected — see [scope](#folder-exporter) |
| `recursive` | `true` | Descend into subdirectories |
| `max_depth` | `0` | `0` = unlimited; `1` = the folder itself only |
| `include` | `[]` | Wildcard patterns; empty means every file. e.g. `["*.csv", "*.xml"]` |
| `exclude` | `[]` | Wildcard patterns applied to file names |
| `exclude_directories` | `[]` | Directory names that are skipped entirely — a large saving on trees like `node_modules` |
| `follow_reparse_points` | `false` | Junctions and symlinks are skipped by default, which prevents infinite loops |
| `skip_hidden` / `skip_system` | `false` | Ignore hidden / system files |
| `track_changes` | `true` | Enable add/remove detection. Turn off for large read-only archives to save memory |
| `change_tracking_mode` | `hash` | `hash` keeps a 64-bit hash per file. `name` also keeps the relative path, which is what lets the exporter report the **name of a removed file** |
| `max_tracked_files` | `5000000` | Safety cap on the index; if hit, `folder_tracking_truncated` goes to 1 |
| `expose_filename_labels` | `false` | Emit `*_file_info` metrics carrying filenames as labels. Off by default because filenames are high-cardinality |
| `disk_metrics` | `true` | Also export free/total bytes of the containing volume |
| `extension_metrics` | `false` | Per-extension file counts and sizes |
| `top_extensions` | `10` | How many extensions to keep (largest by bytes) |
| `age_basis` | `write` | `write` uses last-write time, `create` uses creation time, for ages and oldest/newest |
| `scan_interval_seconds` | inherit | Per-folder override — e.g. scan a cold archive every 15 minutes |
| `count_initial_files_as_added` | `false` | If true, files present at startup count as additions. Normally you want `false` so the first scan just primes the baseline |
| `labels` | `{}` | Extra static labels merged into every metric for this folder |

---

## Running it

### Foreground

```powershell
.\releases\folder_exporter.exe                   # uses folder_exporter.yml beside the exe
.\releases\folder_exporter.exe --config C:\etc\fe.yml
```

`Ctrl+C` stops it cleanly.

### As a Windows service

The binary is a real `ServiceBase` service — no NSSM, srvany or wrapper needed.

The easy way is `install-service.bat` (right-click → **Run as administrator**),
which validates the config *before* installing anything, starts the service,
verifies `/metrics` responds and offers to open the firewall port. Pass a name as
its first argument to install a second instance:
`install-service.bat folder_exporter_archive`. `uninstall-service.bat` removes it.

By hand, from an **elevated** PowerShell:

```powershell
# Install (registers with automatic start and restart-on-failure)
.\folder_exporter.exe --install --config "C:\Program Files\folder_exporter\folder_exporter.yml"

sc.exe start folder_exporter
sc.exe query folder_exporter

# Remove
.\folder_exporter.exe --uninstall
```

`--install` registers the service with `start= auto` and a failure policy of
restart after 5 s, 10 s, then every 60 s.

Notes for service deployments:

- The service runs as `LocalSystem` by default. If a folder sits on storage only a
  domain account can read, run the service as that account instead:
  `sc.exe config folder_exporter obj= "DOMAIN\svc_exporter" password= "..."`.
- Set `log_file` in the config — a service has no console to log to.
- Use `--service-name <name>` to run several instances side by side.

Prefer Task Scheduler? Create a task that runs the exe at startup as
`SYSTEM` with "Run whether user is logged on or not"; no other change is needed.

### Firewall and URL binding

Binding `0.0.0.0` (or `+`) requires administrator rights, which a service has.
To run non-elevated on all interfaces, reserve the URL once:

```powershell
netsh http add urlacl url=http://+:9847/ user="DOMAIN\username"
```

Then allow Prometheus through the firewall:

```powershell
New-NetFirewallRule -DisplayName "Prometheus folder_exporter" -Direction Inbound `
    -Protocol TCP -LocalPort 9847 -Action Allow `
    -RemoteAddress 10.0.0.0/8          # restrict to your Prometheus subnet
```

If you would rather not open a port at all, bind `127.0.0.1` and scrape through
an existing agent or reverse proxy on the host.

---

## Connecting it to Prometheus

Add this to the `scrape_configs:` section of `prometheus.yml`
(a copy-paste-ready version is in `prometheus/prometheus-scrape-config.yml`):

```yaml
scrape_configs:
  - job_name: folder_exporter
    scrape_interval: 60s
    static_configs:
      - targets:
          - fileserver01.example.com:9847
          - fileserver02.example.com:9847
```

Reload Prometheus:

```bash
curl -X POST http://prometheus:9090/-/reload     # or restart the service
```

Confirm it worked:

1. **Prometheus → Status → Targets** — `folder_exporter` should be `UP`.
2. Run `folder_files` in the expression browser; you should get one series per
   watched folder.

Then load the alerting and recording rules:

```yaml
rule_files:
  - folder_exporter_rules.yml     # from the prometheus/ directory here
```

```bash
promtool check rules folder_exporter_rules.yml
```

### Picking intervals

`scan_interval_seconds` controls both freshness and cost; the scrape interval
only controls how often Prometheus copies the cached values. Scraping every 15 s
against a 60 s scan just stores the same number four times.

**Set `scrape_interval` equal to `scan_interval_seconds`.**

| Folder | Suggested scan interval |
|---|---|
| Hot inbox you alert on within minutes | 30–60 s |
| General data folder | 60–300 s |
| Very large archive | 900 s, plus `throttle_every_files` |

### Service discovery

`static_configs` is fine for a handful of servers. For a fleet, point
`file_sd_configs` at a generated JSON file, or use `consul_sd_configs` /
`azure_sd_configs` — the exporter is an ordinary HTTP endpoint and needs nothing
special.

---

## Metrics reference

Every folder metric carries `target` (the configured name) and `path` labels,
plus any static `labels` you defined.

### Folder state

| Metric | Type | Description |
|---|---|---|
| `folder_up` | gauge | 1 if the last scan completed successfully |
| `folder_exists` | gauge | 1 if the path exists and is a directory |
| `folder_size_bytes` | gauge | **Total size** of all matching files |
| `folder_files` | gauge | **Number of files** contained |
| `folder_directories` | gauge | Number of subdirectories |
| `folder_created_timestamp_seconds` | gauge | When the folder was created |
| `folder_age_seconds` | gauge | **Folder age** — now minus its creation time |
| `folder_modified_timestamp_seconds` | gauge | Folder's last-write time |

### File timestamps and ages

| Metric | Type | Description |
|---|---|---|
| `folder_oldest_file_timestamp_seconds` | gauge | Timestamp of the oldest file |
| `folder_oldest_file_age_seconds` | gauge | Age of the oldest file — the classic "stuck file" signal |
| `folder_newest_file_timestamp_seconds` | gauge | Timestamp of the newest file |
| `folder_newest_file_age_seconds` | gauge | Age of the newest file — rises when a feed stops |
| `folder_largest_file_bytes` | gauge | Size of the largest file |
| `folder_file_age_seconds` | histogram | Distribution of file ages (`_bucket`, `_sum`, `_count`) |

### Files added and removed

| Metric | Type | Description |
|---|---|---|
| `folder_files_added_total` | counter | **Files observed being added** since start |
| `folder_files_removed_total` | counter | **Files observed being removed** since start |
| `folder_files_added_last_scan` | gauge | Additions between the last two scans |
| `folder_files_removed_last_scan` | gauge | Removals between the last two scans |
| `folder_last_file_added_timestamp_seconds` | gauge | **When a file was last added** (0 = never observed) |
| `folder_seconds_since_last_file_added` | gauge | Same thing, as an age |
| `folder_last_file_removed_timestamp_seconds` | gauge | **When a file was last removed** |
| `folder_seconds_since_last_file_removed` | gauge | Same thing, as an age |
| `folder_tracked_files` | gauge | Files currently in the change-tracking index |
| `folder_tracking_truncated` | gauge | 1 if `max_tracked_files` was hit (counts unreliable) |

### Filenames — only with `expose_filename_labels: true`

| Metric | Description |
|---|---|
| `folder_last_added_file_info{file="..."}` | Name of the most recently added file |
| `folder_last_removed_file_info{file="..."}` | Name of the most recently removed file (needs `change_tracking_mode: "name"`) |
| `folder_newest_file_info{file="..."}` | Name of the newest file |
| `folder_largest_file_info{file="..."}` | Name of the largest file |

> These put filenames into label values. On a folder with high churn that
> creates a new time series per distinct filename, so leave it off unless the
> folder holds a stable, small set of names, or you genuinely need the name in
> an alert.

### Volume and per-extension

| Metric | Type | Description |
|---|---|---|
| `folder_volume_free_bytes{volume="C:\\"}` | gauge | Free space on the containing volume |
| `folder_volume_total_bytes{volume="C:\\"}` | gauge | Total size of that volume |
| `folder_extension_files{extension="csv"}` | gauge | File count per extension (`extension_metrics: true`) |
| `folder_extension_size_bytes{extension="csv"}` | gauge | Bytes per extension |

### Scan health

| Metric | Type | Description |
|---|---|---|
| `folder_last_scan_timestamp_seconds` | gauge | When the folder was last scanned |
| `folder_last_scan_duration_seconds` | gauge | How long that scan took |
| `folder_scans_total` | counter | Scans performed |
| `folder_scan_errors_total` | counter | Access-denied subdirectories, timeouts, vanished paths |
| `folder_scan_timed_out` | gauge | 1 if the last scan hit `scan_timeout_seconds` |

### Exporter self-monitoring

`folder_exporter_build_info`, `folder_exporter_start_time_seconds`,
`folder_exporter_uptime_seconds`, `folder_exporter_targets`,
`folder_exporter_scrapes_total`, `folder_exporter_scan_cycles_total`,
`folder_exporter_config_reloads_total`,
`folder_exporter_config_reload_failures_total`,
`folder_exporter_resident_memory_bytes`, `folder_exporter_private_memory_bytes`,
`folder_exporter_cpu_seconds_total`, `folder_exporter_open_handles`,
`folder_exporter_managed_heap_bytes`.

---

## Useful PromQL

```promql
# Folder size, in GiB
folder_size_bytes / 1024^3

# Folders where nothing new has arrived in the last hour
folder_seconds_since_last_file_added > 3600

# File arrival rate, files per minute
rate(folder_files_added_total[5m]) * 60

# Backlog that is growing rather than draining
folder_files > 1000 and deriv(folder_files[30m]) > 0

# Oldest file, in hours - the "is something stuck?" query
folder_oldest_file_age_seconds / 3600

# Share of files older than a day, from the histogram
1 - (
  folder_file_age_seconds_bucket{le="86400"}
  / ignoring(le) folder_file_age_seconds_count
)

# Mean file size
folder_size_bytes / clamp_min(folder_files, 1)

# Projected days until the volume fills, from the last 6 hours of growth
folder_volume_free_bytes / clamp_min(deriv(folder_size_bytes[6h]) * 86400, 1)

# Top 5 largest watched folders in the estate
topk(5, folder_size_bytes)

# Bulk-deletion detector
increase(folder_files_removed_total[10m]) > 500
```

---

## Alerting

`prometheus/folder_exporter_rules.yml` ships with rules covering the situations
these metrics exist to catch:

| Alert | Fires when |
|---|---|
| `FolderExporterDown` | Exporter unreachable for 5 min |
| `FolderMissing` | A watched path disappeared |
| `FolderScanStale` | No successful scan for 15 min |
| `NoNewFilesArriving` | No additions for over an hour |
| `NewestFileTooOld` | Newest file older than 2 h (works from the first scrape) |
| `FolderBacklogGrowing` | More than 1000 files and still rising |
| `StuckFilesInFolder` | A file has sat there for over a day |
| `TooManyAgingFiles` | Over half the files are older than an hour |
| `FolderTooLarge` / `FolderGrowthSpike` | Size crosses 100 GiB / grows 10 GiB in an hour |
| `VolumeAlmostFull` | Containing volume over 90% full |
| `MassFileDeletion` | Over 500 files removed in 10 minutes |
| `FolderTrackingTruncated` | The tracking cap was hit |

Thresholds are deliberately generic — tune them per folder, most simply by
adding a `labels` entry in the config and matching on it in the rule.

---

## Performance

Measured on Windows 11, NVMe SSD, with `--once`:

| Tree | Entries | Cold | Warm | Rate (warm) |
|---|---|---|---|---|
| Flat data folder | 30,000 files | — | **0.04 s** | ~750,000 files/sec |
| `C:\Windows` (deep, WinSxS-heavy) | 166,556 files + 88,569 dirs | 27.7 s | **14.5 s** | ~11,500 files/sec |

For reference, `Get-ChildItem -Recurse -File` over that same `C:\Windows` tree
took **51.8 s** warm — the exporter is about **3.6× faster** while also computing
sizes, ages, histograms and change detection in the same pass.

Steady-state process cost:

| | |
|---|---|
| Resident memory | 8–10 MB (7.7 MB scanning a 30k-file folder) |
| CPU when idle | 0% — it sleeps between scans |
| Threads / handles | ~12 / ~320 |
| Scrape cost | Cached render only; no disk I/O |

Memory for change tracking scales with file count: roughly **24 bytes per file**
in `hash` mode, or that plus the relative path string in `name` mode. A million
files in hash mode is around 25 MB; set `track_changes: false` on huge archives
where you only care about size.

---

## Operational notes and limits

**Change detection is sampled, not eventful.** The exporter compares consecutive
scans. A file created and deleted between two scans is never seen, and a file
replaced in place counts as neither an add nor a remove. If you need true
per-event fidelity, you need a `ReadDirectoryChangesW` watcher — a different and
much more stateful tool. For monitoring and alerting, interval sampling is the
right trade: it is bounded, restartable and cannot fall behind under load.

**"Last added" is observation time, not file birth time.** The
`folder_last_file_added_timestamp_seconds` value is when the scan first saw the
file, so it lags by up to one scan interval. Use
`folder_newest_file_timestamp_seconds` if you want the file's own timestamp.

**Counters reset on restart.** `folder_files_added_total` and
`folder_files_removed_total` count from process start; the first scan primes a
baseline without counting existing files as additions (set
`count_initial_files_as_added: true` if you want the opposite). `rate()` and
`increase()` handle the reset correctly.

**Hash collisions.** In `hash` mode two paths in the same folder colliding on a
64-bit FNV-1a hash would hide one file from the add/remove counts. At a million
files, the odds are on the order of 1 in 10^7. Size and count metrics are never
affected. Use `name` mode if you want no collision risk at all.

**Junctions and symlinks are skipped** unless `follow_reparse_points: true`,
which prevents traversal loops. Hardlinked files (common in `C:\Windows\WinSxS`)
are counted once per link, so `folder_size_bytes` there exceeds real disk usage.

**One instance per server, by design.** Network paths are rejected rather than
supported. A share scanned remotely would carry the *scanning* host's `instance`
label, so a full disk on `fileserver01` would show up as a problem on
`appserver07`; the traversal would also be an order of magnitude slower and would
fail differently depending on the service account. Running an instance on the
machine that owns the storage keeps attribution correct and scans fast. If you
genuinely cannot install on a file server (an appliance or NAS), this exporter is
the wrong tool — use a NAS-native exporter or SNMP.

**Cardinality.** Each folder produces roughly 30 series, plus one per histogram
bucket, per extension, and per filename label if enabled. A hundred targets is
comfortable; ten thousand distinct filenames per hour is not.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `cannot bind ... access denied` | Binding all interfaces without admin rights. Run elevated, install as a service, add a `netsh http add urlacl` reservation (command is printed in the error), or bind `127.0.0.1` |
| `port 9847 is already in use` | Another process owns it. `netstat -ano \| findstr :9847` then pick a different port |
| Prometheus target is `DOWN` / connection refused | Firewall. Add the inbound rule above and confirm with `Test-NetConnection <host> -Port 9847` from the Prometheus host |
| `folder_exists` is 0 | Path typo, or the service account cannot read the folder. Run `--check-config` to see exactly which paths are resolved and which are missing |
| `is a network (UNC) path` at startup | By design — see [scope](#folder-exporter). Install an instance on the server that owns the storage |
| `folder_scan_errors_total` climbing | Access-denied subdirectories. Set `log_level: "debug"` to see which, then exclude them or grant read access |
| `folder_scan_timed_out` is 1 | Tree is too big or too slow for `scan_timeout_seconds`. Raise it, narrow `include`/`exclude_directories`, or lengthen the scan interval |
| Add/remove counts look wrong | Check `folder_tracking_truncated`; if 1, raise `max_tracked_files`. Also confirm `track_changes` is on for that folder |
| Removed *filenames* missing | Requires `change_tracking_mode: "name"` — `hash` mode cannot recover a name it no longer holds |
| Service starts then stops immediately | Bad config or a bind failure. Set `log_file` and read it; also check Event Viewer → Windows Logs → Application |
| Memory grows over time | Almost always the tracking index on a folder with millions of files. Set `track_changes: false` or lower `max_tracked_files` |

Turn on `"log_level": "debug"` to get a line per scan showing file count, byte
total, additions, removals and duration.

---

## Command-line reference

```
folder_exporter.exe [--config <path>]      Run in the foreground (Ctrl+C to stop)
folder_exporter.exe --once                 Scan once, print metrics to stdout, exit
folder_exporter.exe --check-config         Validate the config file and exit
folder_exporter.exe --install              Install as a Windows service (elevated)
folder_exporter.exe --uninstall            Remove the Windows service (elevated)
folder_exporter.exe --version              Print the version

  --config <path>        Config file. Default: folder_exporter.yml next to the .exe
  --service-name <name>  Service name for --install/--uninstall
  --console              Force console mode even when not interactive
```

### HTTP endpoints

| Path | Purpose |
|---|---|
| `/metrics` | Prometheus exposition (gzip when the client accepts it) |
| `/healthz` | Liveness probe, returns `ok` |
| `/-/reload` | `POST` to reload the configuration |
| `/` | HTML status page listing every watched folder |

### Project layout

```
folder-exporter/
├── build.ps1                  Compiles the exe and assembles releases/
├── selftest.ps1               End-to-end test: 40 assertions, self-cleaning
├── folder_exporter.yml        Fully commented reference config
├── install-service.bat        Guided service install (validates config first)
├── uninstall-service.bat      Removes the service
├── INSTALL.md                 Server-side deployment guide (shipped in releases/)
├── src/
│   ├── Program.cs             Entry point, CLI, Windows service host
│   ├── App.cs                 Scan scheduling, hot reload, status page
│   ├── Scanner.cs             Directory walker and change detection
│   ├── Metrics.cs             Prometheus exposition rendering
│   ├── HttpServer.cs          HttpListener front end
│   ├── Config.cs              Config mapping and local-path enforcement
│   ├── Yaml.cs                YAML subset parser
│   ├── Logger.cs              Level-filtered logging with rotation
│   └── Win32.cs               FindFirstFileEx and other native interop
├── prometheus/
│   ├── prometheus-scrape-config.yml
│   └── folder_exporter_rules.yml
└── releases/                  ← the deployable bundle, produced by build.ps1
    ├── folder_exporter.exe
    ├── folder_exporter.yml
    ├── install-service.bat
    ├── uninstall-service.bat
    ├── INSTALL.md
    └── prometheus/
```

`build.ps1` never overwrites an existing `releases\folder_exporter.yml` — on a
rebuild it writes `folder_exporter.yml.example` alongside it instead, so a
server's edited configuration is safe.

Port **9847** is not in the official Prometheus port registry; change it if it
clashes with something in your environment.
