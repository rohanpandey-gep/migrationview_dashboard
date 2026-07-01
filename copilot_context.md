# Migration View Dashboard — Copilot Context

_Last updated: 2026-06-24_

---

## 1. Project Overview

**What it is:** An internal GEP tool for monitoring package migration status across environments (QC / UAT / PROD). It shows counts of migration packages in Published / Missed-Queue / Failed status, grouped by priority BPC (Buying Platform Client) domains and broken down by functional module rows.

**Target users:** GEP internal teams (thousands of MNC employees).

**Architecture:** Angular 19 (standalone) frontend + .NET 10 Web API backend. Config-driven layout via JSON files. Calls real GEP external APIs for data.

---

## 2. Repository Layout

```
migrationview_dashboard/          ← workspace root
├── copilot_context.md            ← this file
├── server/
│   └── MigrationViewDashboard/   ← .NET 10 Web API (port 5103)
│       ├── Program.cs
│       ├── appsettings.json
│       ├── Controllers/
│       │   └── DashboardController.cs
│       ├── Services/
│       │   └── LogsService.cs
│       ├── Models/
│       │   └── DashboardModels.cs
│       └── data/
│           ├── priorityGroups.json
│           ├── modules.json
│           └── appsConfig.json
└── client/                       ← Angular 19 app (port 4200)
    └── src/app/dashboard/
        ├── dashboard.component.ts
        ├── dashboard.component.html
        ├── dashboard.component.css
        ├── dashboard.service.ts
        └── models.ts
```

---

## 3. External APIs (GEP)

Base: `https://api-build.gep.com/leo-platform-portalorchestrator-api/api/v1/Setup/`

| API | Endpoint | Purpose |
|-----|----------|---------|
| Queue Count | `GetQueuedPublishCount` | GET — returns `{returnValue: <number>, isSuccess: true}` |
| Logs | `AllPublishQueueForLogs` | GET — paginated; returns `{returnValue: {publishQueueList: [...], totalCount: N}, isSuccess: true}` |

**Query params for Logs:** `pageNumber`, `pageSize=100`, `projectGroupId`, `timestamp` (ISO 8601, e.g. `2025-06-20T23:59:59.999Z`)

**Auth:** Both APIs require:
- `Authorization: Bearer <JWT>` header
- `Ocp-Apim-Subscription-Key: 1cbd622fdcfd4753b4b43be776fe8c3f` header

**LogEntry fields from API:** `status`, `timestamp`, `bpcCode` (int), `bpcName`, `environment`

**Status values:** `"Published"`, `"Failed"`, anything else → `MissedQueue`

---

## 4. Auth Flow

1. User pastes JWT into the UI (password input field).
2. Angular auto-prepends `"Bearer "` if not already present, trims whitespace.
3. JWT sent to .NET backend via custom `X-Auth-Token` header (Angular `getData()` / `getQueueCount()`).
4. .NET backend reads it from `[FromHeader(Name = "X-Auth-Token")]` and forwards as `Authorization: Bearer <token>` to GEP APIs.

**Why custom header:** Standard `Authorization` header is restricted in browsers for CORS preflight reasons.

---

## 5. Config Files

### `server/data/priorityGroups.json`
Keyed by environment (`QC` / `UAT` / `PROD`). Each env is an array of priority groups. A group with `bpcs: []` is the "Rest of the Domains" catch-all.

```json
{
  "UAT": [
    { "displayName": "Priority 1 Domains", "bpcs": [70022010, 70021828, 70022201] },
    { "displayName": "Rest of the Domains", "bpcs": [] }
  ],
  ...
}
```

### `server/data/modules.json`
`["Orders", "Catalog", "Invoice", "Requisition", "Receivables"]`

### `server/appsettings.json` — `ExternalApis` section
```json
{
  "QueueCountUrl": "...GetQueuedPublishCount",
  "LogsUrl": "...AllPublishQueueForLogs",
  "SubscriptionKey": "1cbd622fdcfd4753b4b43be776fe8c3f",
  "ModuleProjectGroups": {
    "Orders":       "0b138999-3ee1-46cb-a448-ca0f0ac3d86b",
    "Catalog":      "b4c4cd97-8550-47e8-ae78-90ca8aa18bcd",
    "Invoice":      "27e54b53-d552-4f34-9581-24ec34e34532",
    "Requisition":  "ae71933e-229a-4f51-8cae-33db5df633fb",
    "Receivables":  "PLACEHOLDER"
  }
}
```

---

## 6. Server — Key Files

### `Program.cs`
Registers `AddControllers`, `AddHttpClient`, `AddScoped<LogsService>`, CORS for `localhost:4200`. No Swagger, no auth middleware.

### `DashboardController.cs`
Routes: `GET /api/dashboard/seed`, `/api/dashboard/appsConfig`, `/api/dashboard/data`, `/api/dashboard/queue`

**`GET /api/dashboard/data` flow:**
1. Reads `priorityGroups.json` and `modules.json`.
2. Builds `priorityBpcCodes` HashSet for selected env.
3. Fires `LogsService.GetEntriesForDate()` for all modules in parallel (`Task.WhenAll`).
4. Catches `HttpRequestException` → returns actual HTTP status code.
5. Buckets each entry: if `bpcCode` ∈ `priorityBpcCodes` → goes into per-BPC dict; otherwise → `Rest` aggregation.
6. Only renders cards for BPCs that actually had entries (`seenPriorityCodes` HashSet).
7. BPC names resolved dynamically from API entries — no hardcoding.
8. Returns `DashboardResponse { QueueCount: 0, Sections: [...] }`.

**`GET /api/dashboard/queue`:** Proxies request to GEP queue count API, returns raw JSON body.

### `LogsService.cs`
`GetEntriesForDate(module, date, environment, authorization)`:
- Skips module if `projectGroupId` is empty or `"PLACEHOLDER"`.
- Paginates 100 entries/page, up to 50 pages.
- Stops pagination when an entry date is **before** target date (API returns newest-first).
- Filters matched entries by `environment`.
- **Debug output:** Writes raw page response to `logs/<module>_page<N>_raw.json` and matched-entry dump to `logs/<module>.json` (relative to server working directory).
- Throws `HttpRequestException` on non-200 responses (controller catches and propagates status code).

### `DashboardModels.cs`
`PriorityGroup`, `ModuleCounts`, `BpcCard`, `PrioritySection`, `DashboardResponse`, `SeedResponse`, `AppsConfig`/`AppCategory`/`AppDefinition`/`AppModule`.

---

## 7. Client — Key Files

### `dashboard.service.ts`
- `getSeed()` → `GET /api/dashboard/seed`
- `getData(env, date, auth)` → `GET /api/dashboard/data` with `X-Auth-Token` header
- `getQueueCount(auth)` → `GET /api/dashboard/queue` with `X-Auth-Token` header
- Proxy: Angular dev server proxies `/api` to `http://localhost:5103` via `proxy.conf.json`

### `dashboard.component.ts`
- `ngOnInit`: loads seed (environments, default date = today).
- `loadData()`: trims + prepends Bearer, calls `getData()`, calls `refreshQueue()`, starts 60s polling.
- `ngOnDestroy`: clears polling interval.
- State: `sections`, `loading`, `errorMessage`, `queueErrorMessage`, `queueCount`.

### `dashboard.component.html`
Top row: Queue count box → Environment dropdown → Date picker → JWT input → Load button (inline spinner).
Below: Loading overlay, error banners (red=data error, yellow=queue error), then priority sections.

**Column structure:**
- Priority groups (named BPCs): 4 columns — Module / Published / Missed/Queue / Failed
- "Rest of the Domains": 2 columns — Module / Missed/Queue (no Published/Failed)

### `models.ts`
`ModuleCounts`, `BpcCard`, `PrioritySection`, `DashboardResponse`, `PriorityGroup`, `SeedResponse`.

---

## 8. Key Architectural Decisions

| Decision | Choice | Reason |
|----------|--------|--------|
| Auth header | `X-Auth-Token` custom header | Browser CORS restrictions on `Authorization` header |
| Bearer auto-prepend | In Angular component before every call | Users paste raw tokens without "Bearer " |
| Pagination strategy | Stop when entry date < target date | API returns newest-first; avoids fetching all history |
| BPC name resolution | From API entries at runtime | No static BPC name list available; avoids hardcoding |
| Priority cards visibility | Only render BPCs with actual entries | Clean UI; no empty/zero cards for irrelevant BPCs |
| Rest of Domains | Catch-all group with `bpcs: []` in JSON | Config-driven; no special-casing in controller |
| Failed column | Only shown for priority groups | Rest of Domains shows only Missed/Queue (business decision) |
| Env-keyed priority groups | Different BPC codes per environment | QC/UAT/PROD may have different priority clients |
| Parallel module fetching | `Task.WhenAll` | Performance — 5 modules × N pages each |
| Debug file dump | `logs/` folder under server cwd | Diagnose deserialization issues without log config changes |

---

## 9. Known Bugs / Current Blockers

### 🔴 CRITICAL: Logs API returns 0 entries for all modules

**Symptom:** `AllPublishQueueForLogs` returns HTTP 200, but `publishQueueList` is empty for every module.

**Debugging added:**
- `LogsService` writes raw response body to `logs/<module>_page<N>_raw.json` in the server's working directory (`server/MigrationViewDashboard/logs/`).
- Logs body length per page.
- Logs "Page N empty - stopping" with an 800-char snippet of the response body.

**Suspected causes (in order of likelihood):**
1. `timestamp` parameter format — currently sending `{date}T23:59:59.999Z` (end of day). The API may interpret this differently. Try sending `{date}T00:00:00.000Z` or today's date with current time.
2. `projectGroupId` values may be incorrect — verify via direct Postman call to the GEP API.
3. Response deserialization mismatch — the JSON property name for `publishQueueList` might differ. Already using `PropertyNameCaseInsensitive = true`.
4. API genuinely has no data for selected date/env/module combo — test with a date known to have data.

**To diagnose:** Restart server (`dotnet run` from `server/MigrationViewDashboard/`), click Load, then check `server/MigrationViewDashboard/logs/Orders_page1_raw.json` for actual response shape.

---

## 10. Pending Tasks

| Priority | Task | Notes |
|----------|------|-------|
| 🔴 HIGH | Fix 0-entries bug | See Section 9 — check raw response files in `logs/` folder |
| 🔴 HIGH | Receivables module | "Umbrella category" with multiple `projectGroupId`s — user to provide real GUIDs and confirm multi-ID fetch strategy |
| 🟡 MED | `appsConfig.json` integration | File loaded via `/api/dashboard/appsConfig` but NOT used in the data pipeline yet |
| 🟡 MED | Seed endpoint env-awareness | Currently returns priorityGroups from first env only; client doesn't depend on this but it's inaccurate |
| 🟢 LOW | UI polish | Responsive layout improvements |
| 🟢 LOW | Remove manual JWT input | Eventually auth should come from session/cookie |

---

## 11. Running the Project

### Backend
```bash
cd server/MigrationViewDashboard
dotnet run
# Listens on http://localhost:5103
```

### Frontend
```bash
cd client
npm install        # only first time
ng serve
# Listens on http://localhost:4200
# Proxies /api → http://localhost:5103 (via proxy.conf.json)
```

---

## 12. Bugs Fixed (Historical)

| Bug | Fix |
|-----|-----|
| UTF-8 BOM in `priorityGroups.json` caused JSON parse crash | Rewrote file without BOM |
| 401 on queue API — JWT sent as query param (URL truncation) | Switched to `Authorization` header forwarding |
| `returnValue` was modelled as array, is actually an object | Fixed `LogsApiResponse` → `LogsReturnValue` wrapper class |
| Field name mismatches: `createdDate`→`timestamp`, `bpcId`→`bpcCode` | Updated `LogEntry` model properties |
| `LogsService` swallowed all API errors silently | Added `throw new HttpRequestException(...)` on non-200 |
| CORS block from Angular dev server | Added `WithOrigins("http://localhost:4200")` in `Program.cs` |
