# Deploying SwiftChart

**This guide has never been run against a server.** Every command and setting in it was written from the local server stack (`infra/compose`) and CI, not from a real host. Three parts of it were exercised locally, against the stack: the restore drill (`infra/compose/restore-drill.cs`), an alert emailed when the replica stops (`infra/compose/README.md`, "Monitoring"), and the site check (`check-sites.cs`, beside this file). Nothing else was: installing the servers, Tailscale, the certificates, the mail relay, the first start, backups and restores on real hosts, and upgrades are all untested. The Tailscale rules (`infra/tailscale/policy.hujson`) are checked for syntax only and have never been applied to a tailnet. Nothing is hosted, no account exists and nothing has been spent (the expansion plan's step 21: prepare only). All data in this project is synthetic. **Real patient data would need a hosting provider that signs a Business Associate Agreement (BAA) for the cloud server and the backups, and the clinic's own HIPAA obligations met; none of the providers below has been asked.**

The plan is two machines: the **clinic server**, on the clinic's network, which owns every write, and one low-cost **cloud server**, which serves off-site staff and the portal from a read-only replica and keeps the backups away from the clinic. They reach each other only over Tailscale. Any Ubuntu LTS server with Docker will do; no provider's own tooling is used.

## What runs where

Each server runs these services of `infra/compose/compose.yaml`, with the settings below in place of the stack's. The stack's `wan`, `wan-latency`, `mail` (Mailpit) and `lab` (the simulated lab) are stand-ins and run nowhere.

| Server | Services | Reached by |
|---|---|---|
| Clinic | `onprem-db` (the primary), `migrate` (on every start, before the app), `onprem-app`; `node-exporter` for monitoring; the `ai` profile's `llama` and `dictation` if dictation is offered | On-site staff over the clinic's network, on 443. Over Tailscale: staff devices on 443, and the cloud server on 5432, 443 and 9100 |
| Cloud | `cloud-db` (the hot standby), `cloud-app`, `barman`, `restore-db` (only during a restore), and the monitoring profile: `prometheus`, `alertmanager`, `blackbox`, `postgres-exporter` (logging in to the clinic's primary over Tailscale), `node-exporter` | Patients and off-site staff on its public address, 443 only. Over Tailscale: staff devices on 443, administrators on 22 |

**Why Barman is on the cloud server.** Backups belong away from what they protect: a fire, theft or ransomware at the clinic mustn't take them too. The cloud server already holds the whole record in its replica, so keeping backups there lets no one new read it; Barman runs in its own container and volume, connects out to the primary and listens on nothing. A third, separate machine for backups would isolate them further, at the cost of another server.

**Why monitoring is on the cloud server.** It has to notice when the clinic goes down, and send email when it does. While the cloud server itself is down, nothing sends an alert (see "When the cloud server or the link is down").

## Sizes and cost

**Clinic server** (the clinic's own hardware). Without dictation: 4 cores, 8 GB of memory, two SSDs mirrored, sized for the database and its WAL (the primary keeps up to 2 GB of WAL for a replica or Barman that falls behind, `max_slot_wal_keep_size`). With dictation: 32 GB of memory (`llama` about 7 GB, `dictation` capped at 10 GB, which with Whisper for Spanish comes to about 18 GB with the rest), and optionally an NVIDIA GPU with 8 GB for K2 Horizon (the root README's "Dictation server hardware").

**Cloud server.** 2 vCPUs, 4 GB of memory and 80 GB of disk is the size the table prices: the replica is a full copy of the database, Barman keeps about a week of base backups and WAL beside it (`infra/compose/README.md`, "Backup and point-in-time restore"), and the monitoring profile needs a few hundred MB more. Measure the database at hosting time and size the disk from it: the replica, seven base backups (compressed) and six days of WAL, with room to restore one copy.

Prices as each provider's public page showed them on 2026-11-25, read without an account. They change; check them again before choosing. Taxes are extra where the page says so.

| Provider | Plan (shared vCPU) | vCPU | Memory | Disk | Transfer | Monthly | Page, read 2026-11-25 | Signs a BAA |
|---|---|---|---|---|---|---|---|---|
| DigitalOcean | Basic Droplet, 4 GiB | 2 | 4 GiB | 80 GiB SSD | 4,000 GiB | $24.00 | https://www.digitalocean.com/pricing/droplets | to check |
| Akamai (Linode) | Shared CPU, Linode 4 GB | 2 | 4 GB | 80 GB | 4 TB | $24.00 | https://www.akamai.com/cloud/pricing/cpu | to check |
| Hetzner | Regular Performance, CPX21, US locations (Ashburn, Hillsboro) | 3 | 4 GB | 80 GB NVMe | 2 TB | €32.49, excl. VAT | https://www.hetzner.com/cloud/regular-performance | to check |

Hetzner's page also lists the same size in Europe (CPX22, from €16.99 a month, excl. VAT), and showed every plan as "currently unavailable" on that day. A clinic in the United States would keep its data in the United States.

NFR-16's budget is roughly $50. The cloud server alone is about half of that each month. Other costs, each the owner's to check: Tailscale's plan for the number of staff and devices, the domain, and the outside mail relay.

## The operating system and Docker

1. Ubuntu Server LTS, the current release, with unattended security upgrades on.
2. Docker Engine and the Compose plugin from Docker's own apt repository (docs.docker.com, "Install Docker Engine on Ubuntu"), not a provider's image or marketplace.
3. Disk encryption: LUKS on the clinic server; on the cloud server, the provider's encrypted volumes or LUKS. PostgreSQL, Barman and the cloud app's audit queue don't encrypt their files (`infra/compose/README.md`, "Encryption").
4. A firewall (`ufw`): on the cloud server, only 443 open to the internet; on both, SSH only on the Tailscale address (OpenSSH's `ListenAddress`), with keys, no passwords.
5. Check out the repository at the commit being deployed: the services' configuration files (`infra/compose/postgres`, `infra/barman`, `infra/monitoring`) come from it.

## Tailscale

Install Tailscale on both servers and on the staff devices the clinic manages, and apply `infra/tailscale/policy.hujson` to the tailnet with the real administrators' addresses in place of the example ones. Its header says who reaches what, in words. An administrator tags each server (`tag:clinic-server`, `tag:cloud-server`) and each managed staff device (`tag:staff-device`). Run `dotnet run infra/tailscale/check.cs` after any change: it checks the file's syntax and references, nothing more.

## Images

| Image | Where it comes from |
|---|---|
| `ghcr.io/tarnan3751/ehr-web:<commit>` | Published by CI for each commit on `main`, for `linux/amd64` and `linux/arm64` (the root README, "Published images"). Both apps run it |
| `postgres:18`, `prom/*`, `quay.io/prometheuscommunity/postgres-exporter`, `ghcr.io/ggml-org/llama.cpp` | Their public registries, at the versions `infra/compose/compose.yaml` pins |
| `ehr-migrate`, `ehr-barman`, `ehr-dictation` | **Not published yet.** CI builds them but pushes only `ehr-web`. Publishing them the same way is work before the first deployment (below); images are built in CI, never on a laptop or a server (AGENTS.md) |

## Secrets

Each server keeps its settings in one file, `/etc/swiftchart/<server>.env`, owned by root, mode 600, which Compose reads (`--env-file`); a container gets only the variables its service names. Make each secret with a random generator (`openssl rand -hex 32`), never type one on a command line the shell keeps, and never put one in the repository.

| Setting | Clinic | Cloud | What it is |
|---|---|---|---|
| `POSTGRES_SUPERUSER_PASSWORD` | yes | | The primary's superuser, which can't log in over the network |
| `REPLICATOR_PASSWORD` | yes | yes | The replica's stream |
| `BARMAN_PASSWORD` | yes | yes | Barman's stream and backups |
| `MONITOR_PASSWORD` | yes | yes | The `monitor` role, which reads the replication slots' state and no table |
| `EHR_OWNER_PASSWORD` | yes | | Migrations only |
| `EHR_APP_PASSWORD` | yes | | The on-prem app |
| `EHR_READ_PASSWORD` | yes | yes | The cloud app, on the replica (the migration job sets it on the primary, and the replica takes it from there) |
| `EHR_CLOUD_AUDIT_KEY` | yes | yes | The same on both: the cloud app ships the audit events of off-site reads with it |
| `EHR_ENROLLMENT_KEY` | yes | | Portal enrollment codes' key (the root README, "Settings a deployment must give") |
| `EHR_BOOTSTRAP_ADMIN_EMAIL`, `EHR_BOOTSTRAP_ADMIN_PASSWORD` | first start only | | The first administrator; delete the password from the file once they have signed in |
| `LLAMA_API_KEY`, `DICTATION_API_KEY` | with dictation | | The dictation services' keys |

Certificate keys live in `/etc/swiftchart/certs/`, mode 600, owned by the user the container reading them runs as (`ehr-web` runs as UID 1654). The relay's password for alerts is a file of its own (below).

## Certificates

- **The sites.** The clinic's on-site name and the cloud's two names (staff off-site, and the portal) need certificates browsers and the desktop app trust: an ACME certificate authority such as Let's Encrypt, which needs the clinic's own domain. The clinic's on-site name isn't reachable from the internet, so it takes the DNS challenge. The cloud app reaches the on-prem app by the clinic's name over Tailscale (`Ehr__OnPrem__Url`), and trusts its certificate as the system does, so a renewed certificate needs no copy on the cloud server.
- **The databases.** PostgreSQL's certificates on both servers, and the root the apps, the replica, Barman and the exporter verify them against (`SSL Mode=VerifyFull`, `sslmode=verify-full`), from a small certificate authority of the clinic's own, made once on an administrator's machine and kept offline. The names in each must be the ones the clients connect to, such as the clinic server's Tailscale name.

> **Warning: the Data Protection certificate must never be the sites' certificate.** `Ehr__DataProtection__Certificate` and its key encrypt the keys that protect sign-in cookies and form tokens, which in turn encrypt every staff member's and patient's authenticator secret and recovery codes (`ProtectedTokenStore`). The app decrypts that key ring with the certificate it is given now, and no earlier one (`Program.cs`). If it were an ACME certificate, the first renewal would leave the key ring unreadable: everyone signed out, and every authenticator enrollment lost. Use a certificate made for this alone, valid for years, held by both apps, and kept with the backups' keys.

## First start

1. **The clinic server first.** Start `onprem-db`: on its first start it creates the infrastructure roles (`replicator`, `barman`, `monitor`), their slots, and `ehr_owner` (`infra/compose/postgres/primary/initdb`). Its `pg_hba.conf` must take the replica's, Barman's and the exporter's logins from the cloud server's Tailscale address alone (`hostssl`, that address with `/32`), where the stack's takes them from its Docker network (`samenet`). Then `migrate`, which must exit 0, then `onprem-app`, which creates the first administrator from the bootstrap settings. Sign in as them at once: a new password, an authenticator app, recovery codes.
2. **Then the cloud server.** `cloud-db` clones the primary over Tailscale on its first start and then streams from it; then `cloud-app`; then `barman`, whose first base backup takes minutes; then the monitoring profile.
3. From an administrator's machine: `dotnet run infra/deploy/check-sites.cs -- --clinic https://<clinic name> --remote https://<off-site name> --portal https://<portal name>`.
4. Rehearse a restore on the cloud server (below) before anyone relies on the backups.

## Backups

Barman on the cloud server streams the primary's WAL as it is written and takes a base backup every 23 hours, keeping what restoring to any moment of the last 6 days needs, and nothing older than 7 days can be restored (`infra/compose/README.md`, "Backup and point-in-time restore", has the arithmetic). The backups are on the cloud server's disk, unencrypted by Barman. Before an upgrade, take one more: `barman backup --wait onprem`.

## Restoring

- **To rehearse,** on the cloud server: restore the newest base backup that ended before a chosen time, with `barman recover` into the Barman volume, start `restore-db` on it, and compare, as `infra/compose/README.md` does by hand. `infra/compose/restore-drill.cs` automates this against the local stack only: it writes marker rows on the primary through its superuser's local socket, which a real primary doesn't allow from the cloud server.
- **When one is needed** (the record damaged at a known time, or the clinic server lost): find the moment by restoring to a throwaway copy first; restore that moment onto the clinic server (`barman recover` with `--remote-ssh-command`, over Tailscale, or copied there); start it as the new primary; clone the replica again, since the primary's history has forked; and take a new base backup at once, since Barman's earlier ones are on the old timeline.

## Upgrades and migrations

Deploy only a commit on `main` whose CI passed. Take a base backup first. Pull the new `ehr-web` (and the other images), run `migrate` before either app, which applies the new migrations and `db/policies` (migrations only go forward), then restart `onprem-app`, then `cloud-app`, then run `check-sites.cs`. A schema change reaches the replica by replication.

## Monitoring

On the cloud server, the monitoring profile as the stack runs it (`infra/compose/README.md`, "Monitoring", which says what each alert means and what to do), with these differences:

- `prometheus.yml`: the sites by their real addresses; the clinic's app by the name the cloud app uses for it; the clinic's `node-exporter` at its Tailscale address, port 9100, which the clinic server publishes on that address alone.
- `postgres-exporter`: the clinic's primary at its Tailscale name, verified against the clinic's database certificate authority.
- `node-exporter` on each server reads the host's filesystems (the host's root mounted read-only, `--path.rootfs`), not the container's.
- Prometheus and Alertmanager publish on the cloud server's 127.0.0.1 only. An administrator reaches them through SSH: `ssh -L 9090:127.0.0.1:9090 -L 9093:127.0.0.1:9093 <cloud server>`.
- Alertmanager sends through the outside mail relay: `alertmanager.example.yml`, beside this file.

## Mail

- **The app's email** (portal confirmations, password resets, notices): plain SMTP without a password to a relay on the clinic's network (`Ehr__Smtp__Host`, `Ehr__Smtp__Port`, `Ehr__Smtp__From`; `SmtpEmailSender`), which holds the outside mail service's credentials and delivers. The messages carry an address and a link, never health information.
- **Alerts**, from the cloud server: straight to an outside relay, with STARTTLS and a password, as `alertmanager.example.yml` shows. They don't go through the clinic, so a clinic outage can still be reported.

## Dictation

On the clinic server only, as the `ai` profile: `llama` (about 7 GB) and `dictation`, capped at 10 GB (`compose.yaml` says how the cap was measured). The models come from `uv run src/Ehr.Dictation/setup_models.py`, run on the clinic server (MedASR's weights are gated: accept its terms on Hugging Face first), never from the repository. Record the dictation eval's baseline on that machine (the root README, "Dictation").

## When the cloud server or the link is down

- **The link from the cloud to the clinic** (Tailscale or the clinic's internet): on-site work goes on. Off-site, pages are read from the replica and every page says the system is read-only; saves are refused with what was typed kept (UC 2). The off-site reads' audit events wait in the cloud app's queue and ship when the link is back. The replica and Barman stop streaming; the primary keeps their WAL up to 2 GB, past which the replica has to be cloned again and a new base backup taken. Alerts: `ClinicUnreachableFromCloud`, `ReplicaNotStreaming`, `BackupStreamDown`.
- **The cloud server:** patients and off-site staff can't reach anything; on-site work goes on; nothing is backed up; and **no alert is sent**, since monitoring runs there. Someone has to notice. A watchdog outside both servers would close this gap; it would need an account.
- **The clinic server:** everything off-site is read-only from the replica, as above; `SiteDown` and `ClinicUnreachableFromCloud` are emailed.

## Day-two checklist

- **Daily:** someone reads the alert inbox. Silence means either all is well or monitoring itself is down, so now and then check that its pages answer.
- **Weekly:** `check-sites.cs`; `barman check onprem` and `barman list-backups onprem` on the cloud server; disk space on both.
- **Monthly:** a rehearsed restore on the cloud server; OS and Docker updates; `ehr-web` updated to a newer passing commit if one is wanted.
- **Before a certificate's renewal date:** the sites' certificates renew themselves; `CertificateExpiring` warns 14 days ahead if one didn't.

## Before the first deployment

The owner's, when he chooses to host (accounts and money):

- A cloud server at a provider, one that signs a BAA if real patient data is ever involved.
- A Tailscale account, the tailnet, and the policy file applied to it.
- An outside mail relay for alerts and the app's email.
- A domain, and certificates for its names.
- The full page time with its commit, measured on the clinic server: the 20 ms target CI's runners can't check (on macOS the flush waits for the drive; on the Linux server it will differ).

Work still to do, before a first deployment can follow this guide:

- **Compose files for each server**, from the services and settings above, with the clinic's `pg_hba.conf` naming the cloud server's address. They need two things made settings first: the primary's address that `infra/compose/postgres/replica/bootstrap-standby.sh` clones from and `infra/barman/onprem.conf` connects to, both hard-coded to the stack's `wan` today.
- **Publishing `ehr-migrate`, `ehr-barman` and `ehr-dictation` from CI**, for both architectures, as `ehr-web` is. CI's publish job runs only on `main`, and `ehr-barman`'s build needs an arm64 runner or emulation.
- **A Data Protection certificate** made for that alone, valid for years, held by both apps and kept with the backups' keys (the warning under "Certificates").
- **The clinic's own certificate authority** for the databases, and the database certificates it signs.
