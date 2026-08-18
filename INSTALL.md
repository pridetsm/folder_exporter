# folder_exporter — install on a server

This folder is the complete deployment. Copy it to the server you want to
monitor. Nothing needs to be installed first: the exporter runs on the .NET
Framework that ships with Windows 10/11 and Server 2016+.

**One instance per server.** The exporter reports on local folders only —
network (UNC) paths and mapped drives are rejected on purpose, so that every
metric is truthfully attributed to the host Prometheus scraped it from.

```
folder_exporter.exe                      the exporter (single file, ~70 KB)
folder_exporter.yml                      configuration - edit this
install-service.bat                      installs it as a Windows service
uninstall-service.bat                    removes the service
prometheus\prometheus-scrape-config.yml  scrape config for the Prometheus server
prometheus\folder_exporter_rules.yml     alerting and recording rules
```

**Fast path:** edit `folder_exporter.yml`, then right-click `install-service.bat`
and choose **Run as administrator**. It validates the config, installs and starts
the service, checks the metrics endpoint responds and offers to open the firewall
port. The steps below explain what it does and how to do it by hand.

## 1. Put it somewhere permanent

```powershell
New-Item -ItemType Directory C:\Program Files\folder_exporter -Force
Copy-Item .\* 'C:\Program Files\folder_exporter\' -Recurse
cd 'C:\Program Files\folder_exporter'
```

## 2. Add the folders you want monitored

Edit `folder_exporter.yml`. The only thing most installs need to change is the
list at the bottom:

```yaml
folders:
  - name: inbound
    path: D:\data\inbound

  - name: outbound
    path: D:\data\outbound
    include: ["*.csv"]
```

`name` becomes the `target` label in Prometheus and must be unique. `path` must
be an absolute local path.

Then check it:

```powershell
.\folder_exporter.exe --check-config
```

That prints every folder it will watch and flags any that do not exist, and
every job it will run.

## 2b. (Optional) Add scheduled jobs

If you have scripts that currently run under Task Scheduler and keep getting
disturbed by preventive maintenance or configuration-management baselines,
move them into the `jobs:` section instead - they then run inside this same
service, which nothing that resets scheduled tasks touches. See
[README.md - Job scheduling](README.md#job-scheduling) for the full
reference; the short version:

```yaml
jobs:
  - name: nightly_reconciliation
    cron: "30 2 * * *"
    command: C:\scripts\Reconcile.ps1
```

Test it immediately without waiting for 2:30am:

```powershell
.\folder_exporter.exe --run-job nightly_reconciliation
```

## 3. Run it as a service

Right-click **`install-service.bat`** → **Run as administrator**. It will:

1. check the exe and YAML are present and that the .NET runtime works;
2. run `--check-config` and **stop before installing anything** if the YAML is bad;
3. warn if `log_file` is unset (a service has no console to log to);
4. offer to reinstall if the service already exists;
5. register the service with automatic start and restart-on-failure, and start it;
6. confirm `/metrics` actually responds;
7. offer to add the inbound firewall rule for the port in your YAML.

To install a second instance under a different name, pass one:
`install-service.bat folder_exporter_archive`.

Or do it by hand from an **elevated** prompt:

```powershell
.\folder_exporter.exe --install
sc.exe start folder_exporter
sc.exe query folder_exporter
```

To remove it: run `uninstall-service.bat` as administrator, or
`.\folder_exporter.exe --uninstall`. Neither touches the exe, the YAML or the logs.

Set `log_file` in the YAML before installing — a service has no console:

```yaml
log_file: C:\ProgramData\folder_exporter\exporter.log
```

If a folder lives on storage only a domain account can read, run the service as
that account:

```powershell
sc.exe config folder_exporter obj= "DOMAIN\svc_exporter" password= "..."
```

## 4. Open the port to Prometheus

```powershell
New-NetFirewallRule -DisplayName "Prometheus folder_exporter" -Direction Inbound `
    -Protocol TCP -LocalPort 9847 -Action Allow `
    -RemoteAddress 10.0.0.0/8      # narrow this to your Prometheus server
```

Check locally first: `curl http://localhost:9847/metrics`, or open
<http://localhost:9847/> for a status page listing every watched folder.

## 5. Point Prometheus at it

On the **Prometheus server**, add to `prometheus.yml`:

```yaml
scrape_configs:
  - job_name: folder_exporter
    scrape_interval: 60s
    static_configs:
      - targets:
          - thisserver.example.com:9847
```

Add one entry per server running the exporter. Then load the rules:

```yaml
rule_files:
  - folder_exporter_rules.yml
```

Reload Prometheus (`curl -X POST http://prometheus:9090/-/reload`) and confirm
under **Status → Targets** that the job is `UP`. Query `folder_files` to see one
series per watched folder.

## Changing the configuration later

Just edit `folder_exporter.yml` and save it. The exporter notices within a few
seconds and reloads — no restart, and the added/removed counters are preserved.
If the edited file is invalid, the previous configuration keeps running and the
error is written to the log.

## If something is wrong

| Symptom | Fix |
|---|---|
| `cannot bind ... access denied` | Run elevated or as the service. The error message prints the exact `netsh http add urlacl` command if you need to run non-elevated |
| Prometheus target is `DOWN` | Firewall. Check with `Test-NetConnection <host> -Port 9847` from the Prometheus server |
| `folder_exists` is 0 | Path typo, or the service account cannot read it |
| Service starts then stops | Set `log_file`, restart, and read the log. Also check Event Viewer → Windows Logs → Application |

Full documentation — every metric, PromQL examples, tuning and troubleshooting —
is in `README.md` in the source repository.
