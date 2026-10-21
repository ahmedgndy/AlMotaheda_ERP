# Local demo environment

A development-only copy of the ERP with connected demo data: one API on `https://localhost:7200`, its own database
(`AlMotahedaErp_v2_Demo`) and its own file storage. It never touches another database: the scripts refuse to start
unless the environment is `Development`, the SQL Server is on this machine and the database name ends in `_Demo`.

Nothing here is for production. The demo password and the signing key live only in `.local/` (git-ignored).

## What you need

| Tool | Version used | Check |
| --- | --- | --- |
| .NET SDK | 10 | `dotnet --version` |
| Docker Desktop | any; 4 GB VM is enough for SQL Server + one test run | `docker ps` |
| SQL Server 2022 container | `almotaheda-sql` on port 1433 | `docker ps --filter name=almotaheda-sql` |
| Flutter | 3.47 | `flutter --version` |
| Android emulator (optional) | API 37 image | `flutter emulators` |

If the SQL container does not exist yet:

```bash
docker run -d --name almotaheda-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<sa-password>' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

If it exists but is stopped (for example after a reboot), start it again; its data is kept:

```bash
docker start almotaheda-sql
```

## 1. Configure (once)

```bash
mkdir -p .local && cp tools/dev/demo.env.example .local/demo.env
```

Fill in the `<...>` values in `.local/demo.env`: the SQL `sa` password, a random signing key
(`openssl rand -base64 48`), a bootstrap Executive password and absolute storage paths. Keep every value in quotes.

Trust the .NET development certificate so the browser and the seed accept `https://localhost:7200`:

```bash
dotnet dev-certs https --trust
```

## 2. Start the API

```bash
tools/dev/run-demo-api.sh
```

On first start it creates the database and applies every migration, then creates the bootstrap Executive. It is
ready when this answers `200`:

```bash
curl -sk -o /dev/null -w '%{http_code}\n' https://localhost:7200/health
```

Swagger UI: <https://localhost:7200/swagger>. Stop the API with Ctrl+C in its terminal.

## 3. Seed the demo data

With the API running, in a second terminal:

```bash
tools/dev/seed-demo.sh
```

The seed works only through the public API, signed in as the person who would really do each step, so every
permission and workflow rule applies. It is idempotent: each step first looks for what it would create (by e-mail,
code, plate, idempotency key or a `[DEMO …]` note) and a second run ends with `0 created`. It never deletes, resets
or overwrites anything. The first run takes several minutes, mostly waiting out the login rate limit (10 per minute).

It writes two files in `.local/`:

- `demo-accounts.env`: `DEMO_PASSWORD`, one password shared by every demo account, generated on the first run. Keep
  this file; without it the next run cannot sign in.
- `demo-credentials.md`: every account, its role and what to try with it.

Dates are counted from the first run (the treasury opening date + 100 days), so a later run does not shift them.

### What the seed creates

About 170 records, each in the state a real team would leave it (the run prints every one):

- **People:** 12 accounts covering every role (executive ×3 including the bootstrap account, manager, engineer ×2,
  accountant, office_admin, hr, purchasing, staff drivers ×2), all `@almotaheda.test`; 7 departments; 11 employees
  with salary structures.
- **Reference data:** 9 price-list items, 6 suppliers, 2 price proposals (one approved, one pending).
- **Projects:** 4 projects with items and site areas. Site areas have **no GPS positions**: no provider is
  configured, and nothing pretends to be a real position.
- **Money chain (reconciled):** treasury bank 1,000,000 → accountant custody 100,000 → Nile Tower engineer 50,000,
  settled by two weekly labor settlements (30,000 + 20,000) from approved daily sheets of 20 workers; Shorouk
  engineer 20,000 with site expenses; office petty cash 10,000; pickup custody 3,000; a 7,500 top-up approved but
  not yet paid out; funding request PAY-000001 (5,000) waiting for a decision; PAY-000002 rejected.
- **Site work:** labor sheets in every state (approved, submitted, returned, draft); material deliveries including
  one above the reference range waiting for the Executive; daily reports reviewed / returned / submitted; a report
  request; 3 archive documents; a subcontractor, contract and measurement; rented equipment with its cost; the
  Nile Tower budget approved.
- **Fleet:** 2 vehicles with drivers; claims VX-000001–4 (approved, evidence requested, submitted, rejected and
  referred to HR); a traffic violation.
- **Office:** expenses (approved, waiting), a repair request, a cash count, a funding request.
- **HR:** requests HRQ-000001–5 in different states, an HR case under investigation, September 2026 payroll input
  and salary sheet (submitted).
- **Procurement:** approvals on; a requisition taken through quotations to PO-000001 (partly received) and posted
  supplier invoice WC-INV-1101, plus requisitions left at earlier steps (submitted, manager-approved, quotation
  chosen without an order yet).

Balances after the seed (verified on 2026-10-21): bank 900,000; accountant custody 17,000 posted, 7,500 reserved,
9,500 available; Omar 0; office 8,550; PAY-000001 (5,000) pending.

## 4. Start again from an empty demo

Do not drop or truncate a database to reseed it. Point the demo at a **new** database and a new storage folder
instead, and keep the old ones until you decide to remove them yourself:

1. In `.local/demo.env` change `Database=AlMotahedaErp_v2_Demo` to, for example, `AlMotahedaErp_v3_Demo`, and the
   four `*_Storage__RootPath` values to a new folder (for example `.local/storage-v3/...`).
2. Restart the API (step 2), then run the seed (step 3). `demo-accounts.env` may stay; the password is reused.

Older demo databases on this machine (`AlMotahedaErp_Demo`, from an earlier attempt) can be removed by you when no
longer wanted. The seed and scripts never do it.

## 5. Run the Flutter app against the demo API

The app's default API is the deployed server, so pass the local one at build time. Debug builds accept the .NET
development certificate (`ALLOW_SELF_SIGNED` is on by default and ignored in release builds).

| Where the app runs | API URL to pass |
| --- | --- |
| Android emulator | `https://10.0.2.2:7200` (the emulator's name for this Mac) |
| Desktop (macOS, needs Xcode) | `https://localhost:7200` |
| iOS simulator (needs Xcode) | `https://localhost:7200` |
| Phone on the same Wi-Fi | `https://<this Mac's LAN IP>:7200`; also set `ASPNETCORE_URLS='https://0.0.0.0:7200'` in `.local/demo.env` and allow the port in the macOS firewall |

From the Flutter project:

```bash
flutter emulators --launch Medium_Phone_API_37.0
```

```bash
flutter run --dart-define=API_BASE_URL=https://10.0.2.2:7200
```

Optional: `--dart-define=MAP_TILE_URL=https://tiles.example.com/{z}/{x}/{y}.png` for your own map tile server
(the default is the public OpenStreetMap server, fine for light development use).

Sign in with any e-mail from `.local/demo-credentials.md` and the `DEMO_PASSWORD` from `.local/demo-accounts.env`.

### Trying offline

1. Sign in online and open the screens you want (projects, a custody, the work inbox): each read is saved,
   encrypted, on the device for that user.
2. Turn on airplane mode (emulator: Extended controls → Cellular → Network type "none", or
   `adb shell cmd connectivity airplane-mode enable`).
3. The banner says you are offline; saved screens show "saved copy" with the time they were fetched.
4. Create a daily report, a project expense, a document upload, an office record or a vehicle claim: it is queued
   with status *pending* in the sync center. Approvals, payments and other decisions are refused offline.
5. Turn the network back on: the queue is sent in order, each item with its own idempotency key, and shows *synced*
   (or *failed* / *conflict* with the server's reason).

## 6. Tests

Run the backend test projects **one at a time**: each starts its own SQL Server test container (~1 GB), and running
them all in parallel can run Docker out of memory and stop `almotaheda-sql`.

```bash
for p in $(find tests -name '*.csproj' | sort); do dotnet test "$p" || break; done
```

Flutter:

```bash
flutter analyze && flutter test
```

## Troubleshooting

- **`Refusing: ...`** from a script: `.local/demo.env` points at something that is not a local `_Demo` database.
  That is the guard working; fix the file.
- **Seed waits "login rate limit"**: normal; it waits and continues.
- **Seed stops with an API error**: the message names the request and the server's `code`. Fix the cause and run
  it again; what was already created is reused.
- **`almotaheda-sql` exited**: `docker start almotaheda-sql`, then check `docker logs almotaheda-sql`.
- **Emulator cannot connect**: use `10.0.2.2`, not `localhost`; check the API answers `/health` on the Mac.
