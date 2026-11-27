# Configuration

## Settings a deployment must give

In Development, the app fills in local values. Anywhere else it refuses to start without these, and says which one is missing:

| Setting | What it is |
|---|---|
| `Ehr:Links:ClinicalBaseUrl` and `Ehr:Links:PortalBaseUrl` | The addresses people reach the clinical app and the portal at. Every link the app hands out (the emailed ones for password reset, confirmation and invitation, and the staff set-up link shown to an admin) is built from these and never from the request, whose Host header is its sender's to write. `PortalBaseUrl` also decides where `/` goes on that host: to `/portal`, when it isn't the clinical app's address. |
| `Ehr:Clinic:TimeZone` | The clinic's time zone, an IANA name such as `America/Chicago`. Clinician pages and the desktop app show and take times in it, labeled, and the patient portal shows them in it; everything is stored in UTC, and the audit log stays in UTC. Required outside Development, where it defaults to UTC. `Ehr:Clinic:TimeZoneAbbreviations` (optional, such as `CST,CDT`: standard, then daylight) is the label after a time; without it the label is the offset, such as `UTC-6`. The local stack sets both, from `EHR_CLINIC_TIME_ZONE` (default `America/Chicago`) and `EHR_CLINIC_TIME_ZONE_ABBREVIATIONS` (default `CST,CDT`) in `infra/compose/.env`: change one and change the other with it. |
| `Ehr:Enrollment:Key` | A secret of at least 32 characters that portal enrollment codes are hashed with, so a copy of the table can't be searched for codes. For the on-prem app only: the cloud app forwards registration and is not given it. Changing it makes codes already handed out stop working. |

The local server stack sets them from `infra/compose/.env` (`EHR_ENROLLMENT_KEY`; the addresses are in `compose.yaml`).

## Optional settings

`Ehr:Passkeys:Domain` and `Ehr:Passkeys:Origins`, for staff passkeys (the plan's step 19): the domain a passkey is tied to, the parent domain both staff sites share, such as `example.com`, and the staff sites' exact addresses under it, such as `https://ehr.example.com:8443` and `https://ehr-remote.example.com:9443` (never the portal's). Both or neither: without them, outside Development, there are no passkeys, and in Development they are `localhost` and the local address. A passkey is accepted only from those addresses, and changing the domain later means adding passkeys again; the authenticator app still works. The local stack sets them in `compose.yaml`. `Ehr:Lab:Key`, the key a lab's system calls the on-prem app with; without it there is no lab and its door isn't there (see [Lab interface](apis.md#lab-interface)). `RateLimits:LabRequestsPerMinute` (default 120) is how many requests with the lab's key that door takes a minute, and `RateLimits:LabKeylessRequestsPerMinute` (default 30) how many others, in a window of their own. `Ehr:Desktop:UpdatesFolder`, a folder of desktop releases the app hands to the desktop app at `/api/desktop/updates`, and `Ehr:Desktop:MinimumVersion`, the oldest desktop build that may sync, refused with a 426 below it (the plan's step 20; see "Desktop updates" in [infra/compose/README.md](../infra/compose/README.md#desktop-updates)); without them there are no updates, and every build syncs.
