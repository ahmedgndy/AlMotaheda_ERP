# Caching step (brief Phase 6)

Date: 2026-10-16. Design: `04-signalr-and-cache-maps.md` §4 (this file records what was built and where it differs).

## 1. Choice

**`HybridCache`** (`Microsoft.Extensions.Caching.Hybrid` 10.7.0), registered once in the host (`AddErpCache`).
Why: one API for every module; **tag removal**, which matches the dependency map; **stampede protection** (many
users opening the same screen run the query once); in memory today, and a Redis second level can be added in the
host later with no code change. `IMemoryCache` has no tags or stampede protection; `IDistributedCache` alone adds
serialization everywhere and gives nothing on one server.

## 2. One removal path, shared with SignalR

```
a module saves ─► AuditTrailInterceptor ─► IChangeObserver (right after the save) ─► CacheInvalidation
                                       └─► outbox ─► dispatcher, after its commit ─► CacheInvalidation (again)
CacheInvalidation: LiveScopes.CacheTagsFor(change) ─► HybridCache.RemoveByTagAsync
```

- The tags come from **the same dependency map** as the live signals (`LiveScopes`), so a screen that gets a
  "changed" signal never refetches a stale cached copy.
- A change on a project removes `agg:{Aggregate}:{projectId}`; a change with no project (a worker payment, a labor
  allocation) removes `agg:{Aggregate}` on every project. Project entries carry both tags.
- Removal right after the save makes the next read fresh (tested). When a handler owns the transaction, the save
  comes just before its commit; the outbox removes the tags again after the commit, which closes that gap.
- If removal fails, it is logged and the entry expires on its own (short lifetimes are only a backstop).

## 3. What is cached

| Cached item | Key | Lifetime | Removed by (tag) | Who can read |
|---|---|---|---|---|
| Project cost figures (overview, cost summary, budget comparison, obligations, `PRJ-COST`) | `projects:costs:{p}:{from}:{to}:v1` | 60 s | `agg:ProjectOverview[:p]`: every expense, adjustment, supplier/worker/subcontract payment, labor sheet or allocation, subcontract work, equipment cost, budget, purchase order or supplier invoice change | `projects` See money + project scope, checked before the cache is read |
| Worker trades | `workforce:trades:{all\|active}:v1` | 30 min (local 10 min) | `agg:Trade` | `workers` View; daily rates hidden after reading, per caller |
| Roles and their permissions (the permission matrix screen) | `access:roles:all:v1` | 5 min (local 1 min) | `agg:Role`, `agg:RolePermission` | `accounts` View |

**Not cached, on purpose** [Company]: balances or outstanding amounts used to approve or pay; approval checks;
idempotency checks; payment request status; the permission re-check before money steps (`IPermissionCheck`);
the project-scope check; a trade's rate when a sheet is priced. Those read the database inside their transaction,
as before.

**Differences from the plan:** the price catalog and the company dashboards are not cached yet. The catalog's
view mixes per-caller money rules with pending price changes (little to gain, more risk); the company dashboards
don't exist yet. Code lists (units, work types, cost groups) and the report catalog are code, so they need no cache.

**No leak between roles:** cached values hold data, never decisions. Permission and scope checks run on every
request before the cache is read; project entries are keyed by project; money is filtered after reading by the
same code as before.

## 4. Changed

| Where | Change |
|---|---|
| BuildingBlocks.Data | `CacheTags`, `IChangeObserver`, `AddErpCache`; `AuditTrailInterceptor` tells the observers after each save |
| Platform | `CacheInvalidation` (the observer); `LiveScopes.CacheTagsFor`; the outbox dispatcher removes tags again after its commit; worker payments now also refresh `ProjectOverview` (live signals too) |
| Projects | `ProjectCostLoader` reads through the cache |
| Workforce | `GET /api/trades` reads through the cache |
| Access | `GET /api/roles` reads through the cache |
| Host | `AddErpCache()` |

No API contract change, no migration.

## 5. Tests

Platform tests 60 → 66: tag mapping (project vs every project, reference data, silent detail rows, both tags on
project entries), and two SQL Server flows: the project cost figures are served from the cache (a failing factory
isn't called) and the overview is fresh right after an approval and after a payment through Finance; the trades
list and the role matrix are fresh right after a change.

## 6. Scale-out

With two or more servers: add the Redis second level (`AddStackExchangeRedisCache`) in the host. Local copies on
other servers then expire within their local lifetime (≤ 60 s for project figures), or immediately once a Redis
tag-removal channel is used; live signals already reach every server through the SignalR backplane.

## 7. Results

2,085 of 2,086 tests pass, the 12 projects run one after another: Api 33, Access 180, Archive 215/216, Finance 57,
Fleet 193, HR 99, Platform 66, Procurement 92, Projects 638, Reports 24, Subcontractors 234, Workforce 254. The one
failure is the known flaky `Other_endpoints_keep_the_server_default_request_size` (no cache or database involved;
fixed in commit 2c7a689 on another branch, not brought in yet).
