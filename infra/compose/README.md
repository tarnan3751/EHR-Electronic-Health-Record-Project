# Local server stack

The whole server architecture on one machine, with Docker Compose: the on-prem primary, the cloud replica, the isolated backup host, and a simulated WAN between them. Run every command below from this folder (`infra/compose`). CI starts this same stack on every push and checks it (see "Continuous integration" in the root README).

## What runs

| Service | Stands in for | Notes |
|---|---|---|
| `onprem-db` | On-prem PostgreSQL 18 primary | Owns every write |
| `migrate` | A deploy's database step | One-shot job that applies `db/policies` and the EF Core migrations to the primary as `ehr_owner` each time the stack starts; "Exited (0)" means the database is up to date. Both apps start only after it. |
| `onprem-app` | On-prem app | On-site clinicians reach it directly |
| `cloud-db` | Cloud hot standby | Clones the primary over the WAN on first start, then streams from it |
| `cloud-app` | Cloud app | Reached only through the simulated internet |
| `barman` | Isolated backup host | Streams WAL and takes base backups; listens on nothing |
| `wan` | Tailscale and the internet | [Toxiproxy](https://github.com/Shopify/toxiproxy): the only path between sites, with latency on every link |
| `wan-latency` | | One-shot job that sets the latency each time the stack starts; "Exited (0)" means it worked. `cloud-app` starts only after it. |
| `llama` | Llama 3.1 on the on-prem server | Opt-in `ai` profile, on a network with no internet access |
| `restore-db` | A restored copy of the primary | Opt-in `restore` profile, for the restore drill |

The apps are the blank web app for now; both containers run the same image and don't yet behave differently. `Ehr__Site` is set for when write forwarding and the read-only data path are built. The MedASR sidecar joins the `ai` profile once its code exists.

## One-time setup

1. **Docker Desktop memory.** The core stack uses about 150 MB, so Docker's default is plenty. For the `ai` profile, give Docker at least 10 GB. On macOS: Settings → Resources. On Windows: `memory=` in `%UserProfile%\.wslconfig`.
2. **Settings file.** Copy `.env.example` to `.env`. The defaults work; `.env` is gitignored. If you already have a `.env`, copy over any lines it's missing from `.env.example`; Compose names the missing setting if you don't.
3. **Certificates.** Install [mkcert](https://github.com/FiloSottile/mkcert) (macOS: `brew install mkcert`; Windows: `winget install FiloSottile.mkcert`), then:
   ```
   mkcert -install
   mkcert -cert-file certs/example.com.pem -key-file certs/example.com-key.pem example.com "*.example.com"
   ```
   On macOS, also run `chmod 644 certs/example.com-key.pem`: the app runs as a non-root user and mkcert makes the key readable only by you. It's a local-only certificate, and `certs/` is gitignored.
4. **Hosts file.** Add this line to `/etc/hosts` (macOS, with `sudo`) or `C:\Windows\System32\drivers\etc\hosts` (Windows, as administrator), and remove it when you're done:
   ```
   127.0.0.1 ehr.example.com ehr-remote.example.com portal.example.com
   ```

## Start and stop

| Command | What it does |
|---|---|
| `docker compose up -d --build` | Builds and starts everything. The first start takes a few minutes. |
| `docker compose --profile "*" down` | Stops everything and keeps the data. |
| `docker compose --profile "*" down -v` | Stops everything and deletes all data, for a fresh cluster next time. |

`--profile "*"` makes sure `llama` and `restore-db` stop too; without it, `down -v` can't delete a volume they still use.

Add `--wait` to `up` to return only once every container is running and healthy, as CI does.

**After pulling changes to `postgres/primary/initdb`:** those scripts only run when the primary's volume is first created, so reset once with `docker compose --profile "*" down -v`. The change that added `ehr_owner` is one of these.

On a fresh database, the `migrate` log shows `Failed executing DbCommand` for `ehr.__ef_migrations_history`. That's harmless: EF Core reads its history table before creating it.

In VS Code, the same three are under Terminal → Run Task: "Server stack: start", "Server stack: stop" and "Server stack: reset".

## Addresses

| Address | Path it simulates |
|---|---|
| https://ehr.example.com:8443 | On-site clinician: the LAN, straight to the on-prem app |
| https://ehr-remote.example.com:9443 | Off-site clinician: the internet, to the cloud app |
| https://portal.example.com:9443 | Patient portal: the internet, to the cloud app |
| `127.0.0.1:15432` / `15433` | Primary / replica, database `ehr`: log in as `ehr_app` (primary only) or `ehr_read`, with the passwords in `.env` |
| http://127.0.0.1:8474 | Toxiproxy API, to change latency or cut links |

Everything binds to `127.0.0.1`. For a superuser shell, use `docker compose exec -u postgres onprem-db psql` (or `cloud-db`); superuser logins over the network are refused.

## Simulated latency

`.env` sets the latency added in each direction, so a round trip costs twice the value. The defaults are 30 ms for the internet (off-site browsers to the cloud) and 40 ms for the WAN (cloud and backup to on-prem). Change them in `.env` and run `docker compose up -d` again.

To change a link while the stack runs:

```
docker compose exec wan /toxiproxy-cli toxic update --toxicName latency_downstream --attribute latency=2000 wan_replication
docker compose up -d --force-recreate wan-latency     # back to the .env values
```

The links are `internet_to_cloud_app`, `wan_cloud_to_onprem_app`, `wan_replication` and `wan_backup`.

Two limits: Toxiproxy accepts each TCP connection locally, so a new connection's handshake costs no extra round trip, and it only carries TCP, so HTTP/3 can't be tested here.

## Failure drills

These follow the failure table in the project overview.

| Event | Start the drill | End it |
|---|---|---|
| On-prem internet link down | `docker compose exec wan /toxiproxy-cli toggle wan_cloud_to_onprem_app`, then the same for `wan_replication` and `wan_backup` | Run the same three toggles again |
| Primary down | `docker compose stop onprem-db` | `docker compose start onprem-db` |
| Replica lagging | Raise `wan_replication` latency (see above) | `docker compose up -d --force-recreate wan-latency` |
| Backup host down | `docker compose stop barman` | `docker compose start barman` |
| Cloud box compromised | `docker compose stop cloud-app cloud-db` | `docker compose start cloud-db cloud-app` |

To see the effect:

```
docker compose exec -u postgres onprem-db psql -c "select application_name, state, replay_lag from pg_stat_replication"
docker compose exec -u postgres onprem-db psql -c "select slot_name, active, wal_status, safe_wal_size from pg_replication_slots"
docker compose exec -u postgres barman barman check onprem
```

A stopped replica or Barman leaves its replication slot holding WAL on the primary, capped at 2 GB by `max_slot_wal_keep_size`.

## Backup and point-in-time restore

Barman takes a base backup about a minute after its first start, streams WAL continuously, and keeps a 7-day recovery window. Until that first backup finishes, `barman check onprem` reports `minimum redundancy requirements: FAILED`. To rehearse a restore:

1. Note the time you want to go back to:
   `docker compose exec -u postgres onprem-db psql -Atc "select now()"`
2. Make the changes you want to undo.
3. Ship the WAL that's still in progress:
   `docker compose exec -u postgres barman barman switch-wal --force --archive onprem`
4. Restore to that time:
   `docker compose exec -u postgres barman barman recover onprem latest /var/lib/barman/restore --target-time "<the time from step 1>"`
5. Start the restored copy and compare row counts. It pauses read-only at the target time:
   `docker compose --profile restore up -d restore-db`, then `docker compose exec -u postgres restore-db psql -d ehr`
6. Clean up:
   `docker compose --profile restore rm -sf restore-db`, then `docker compose exec -u postgres barman rm -rf /var/lib/barman/restore`

## AI profile

1. Put the Llama 3.1 8B Instruct 4-bit GGUF file in `infra/compose/models/`, and set `LLAMA_MODEL_FILE` and `LLAMA_API_KEY` in `.env`. The model is gitignored; `llama` refuses to start without an API key.
2. Start with `docker compose --profile ai up -d`.
3. On a machine with an NVIDIA GPU, add the override file:
   `docker compose -f compose.yaml -f compose.nvidia.yaml --profile ai up -d`

Docker can't use an Apple Silicon or AMD GPU, so there `llama` runs on the CPU. The root README's "Dictation server hardware" section covers the faster native options.

## What this doesn't reproduce

- **Tailscale:** the Docker networks copy who can reach whom, not Tailscale's device identity or access rules.
- **DNS and certificates:** the hosts file stands in for split-horizon DNS, and mkcert stands in for the ACME wildcard certificate.
- **Network timing:** the TCP handshake latency and HTTP/3 limits above.
