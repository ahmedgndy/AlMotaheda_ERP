# Fleet GPS: vehicle tracking from the driver's phone

Module `AlMotaheda.Modules.Fleet` (schema `fleet`), Flutter `lib/features/tracking` and `lib/features/fleet`.
Read `05-fleet.md` and `12-treasury-custody.md` first (vehicles, drivers, custody, claims).

## 1. What it is

- The position source is **the app on the assigned driver's company phone**. No external tracker is integrated
  (`Fleet:Gps:Provider` must be `DriverPhone`). The former `IVehicleLocationProvider` / `NullVehicleLocationProvider`
  stub (always "not configured") was removed; the same routes now return stored positions.
- One Android foreground service (`TrackingService.kt`, type `location`, persistent notification) serves both the
  site engineers' tracking and vehicle tracking. It writes each position to a durable inbox; Dart moves it into the
  encrypted device database (`vehicle_points`, schema v2) and uploads it. A position leaves the phone only after the
  server confirmed it.
- No daily start button. The driver accepts the **vehicle tracking notice** once on the phone; from then on, while
  they have an open assignment on a vehicle whose tracking is on (in service or under maintenance), the phone tracks
  and uploads by itself. A fleet manager stops it by ending the assignment or turning the vehicle's tracking off.

## 2. Tables (migration `VehicleGpsTracking`)

| Table | Purpose |
|---|---|
| `VehicleGpsPoint` | Raw positions. Capture time (`RecordedAt`, UTC, never changed) and server time (`ReceivedAt`), lat/lng (9,6), accuracy, speed, heading, altitude, the phone's sequence number. Vehicle, assignment and driver fixed at ingestion from the assignment that covered the capture day. Unique `(UserId, ClientPointId)`: a queue sent twice is stored once. Index `(VehicleId, RecordedAt)`. Not audited (personal data, every minute); deleted after `RetentionDays`. |
| `VehicleTrackingDevice` | The driver's phone: notice version accepted and when, last heartbeat (battery, permission, GPS on/off, positions still waiting), last upload. |
| `VehicleTrackingState` | Each vehicle's latest position and `SilentSince`, kept off the `Vehicle` row so positions never touch its audit trail or row version. |
| `VehicleTrackingAlert` | One row per Executive per silence (Open → Recovered/Ended). Audited, so the Platform rules turn it into notifications `VehicleTrackingLost` / `VehicleTrackingBack`. |
| `Vehicle.TrackingEnabled` | On by default; `PUT /api/vehicles/{id}/tracking`. |

Trips, stops and daily summaries are **computed from the stored positions on every read** (`TripSegmenter`), not
stored. A position that arrives days late therefore rebuilds its day by itself, and no trip can be stored twice.

## 3. Rules (`TripSegmenter`, `TrackingHealth`; thresholds in `Fleet:Gps`)

| Setting | Default | Meaning |
|---|---|---|
| `SampleSeconds` | 60 | The phone takes one position a minute. |
| `MinDistanceMeters` | 0 | No distance filter, so a parked vehicle still has samples (stops are backed by data). Raising it saves battery but leaves stops without samples. |
| `MaxAccuracyMeters` | 100 | Rougher positions are stored but never used for routes, trips or stops. |
| `StopRadiusMeters` | 50 | Positions within this distance (widened by their accuracy) of their running centre are "not moving": GPS jitter is not movement. |
| `StopMinutes` | 5 | Not moving this long is a confirmed stop, one interval. Shorter halts (traffic lights) stay in the trip. |
| `MinTripMeters` | 300 | Less movement than this between two stops at the same place is drift: one stop. |
| `GapMinutes` | 10 | No accepted position for longer is a data gap: never measured, drawn dashed and labelled. If the place before and after is the same, the stop continues (marked "positions missing"). |
| `MaxSpeedKmh` | 180 | A lone jump faster than this is dropped; three positions in a row that agree on the new place are believed. |
| `LiveMinutes` / `StaleMinutes` | 3 / 20 | Live; Delayed; then stale (alert once). |
| `MaxLateDays` | 14 | Older queued positions are refused (`tooOld`) and shown on the phone as expired. |
| `MaxBatchPoints`, `HeartbeatMinutes`, `RetentionDays`, `PolicyVersion`, `MonitorEnabled`, `CheckEverySeconds` | 500, 5, 90, 1, true, 60 | |

- Distance = sum of great-circle distances between consecutive accepted positions, not across gaps, **not
  map-matched**: every screen and report says "estimated". Straight lines across gaps are reported apart
  (`unmeasuredKm`) and never added.
- Days follow the company time zone (`Projects:TimeZone`, Africa/Cairo); stored times are UTC. A trip across
  midnight is one trip, shown on both days with "began the day before" / "continues the next day"; each day's totals
  count only the stretches that end inside it.
- A trip never runs from one driver to the next, nor through more than 2 hours without a position
  (`TripSegmenter.TripBreakMinutes`): the positions are cut there and each piece is segmented on its own, so a
  route is always the route of the driver who recorded it.
- A position is filed under the stay that covers its capture time. On the day a stay ends, it covers only what was
  recorded before the moment it was ended (`EndedAt`): after a hand-over the former driver's phone no longer moves
  the vehicle, while what it queued offline before the hand-over still belongs to that stay.
- Motion now (`Moving`, `SuspectedStop`, `Stopped`) is computed only from live positions; otherwise `Unknown`.
- Health: `NotTracked`, `NoDriver`, `NotSetUp`, `NoData`, `Live`, `Delayed`, then once stale `PermissionBlocked`,
  `GpsOff`, `TrackingStopped`, `NoFix` **only when the phone reported it after its last position** (source `Device`),
  otherwise `Offline` with "reason unknown" (source `Server`). A silence never claims a cause.

## 4. Endpoints and permissions

| Route | Permission | |
|---|---|---|
| `GET /api/fleet/tracking/me?deviceId` | fleet View | Is this phone tracking a vehicle, which assignment, notice and settings. |
| `PUT /api/fleet/tracking/me/policy` | fleet View | The driver accepted the notice on this phone. |
| `POST /api/fleet/tracking/me/batch` | fleet View | Queue upload; 409 `vehicle_tracking_notice_not_accepted` until accepted. Each refused position comes back with a reason: `id`, `position` (out of range or 0,0), `accuracy`, `recordedAt`, `future` (> 2 min ahead), `tooOld`, `noAssignment`, `assignment` (another driver's), `trackingOff`, `notADriver`. |
| `GET /api/fleet/gps-status` | fleet View | Source, settings, vehicles expected and reporting (no position). |
| `GET /api/fleet/monitor?search&state&driverEmployeeId&includeRetired` | **fleet Edit** | Every vehicle's latest position, age, health, motion, phone report. |
| `GET /api/vehicles/{id}/location` | **fleet Edit** | One vehicle's. |
| `GET /api/vehicles/{id}/location-history?from&to` | **fleet Edit** | Raw positions, ≤ 48 h, ≤ 5000. |
| `GET /api/vehicles/{id}/trips?date` | **fleet Edit** | The company day: trips (segments), stops, gaps, totals, drivers. |
| `PUT /api/vehicles/{id}/tracking` | **fleet Edit** | Tracking on/off (closes an open alert silently). |

Positions are visible to **fleet Edit only** (Executives by default; grant fleet Edit to a fleet manager). fleet
View, which every driver and the accountant have, never returns a position. The vehicle a position belongs to always
comes from the caller's own assignments, never from the client.

## 5. Android

- Permissions (manifest, unchanged): `ACCESS_FINE/COARSE_LOCATION`, `ACCESS_BACKGROUND_LOCATION`,
  `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_LOCATION`, `POST_NOTIFICATIONS`, `RECEIVE_BOOT_COMPLETED`,
  `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`, `WAKE_LOCK`. targetSdk follows Flutter (36 in this build).
- The setup screen (`/tracking`) walks through: notice → location → "Allow all the time" (Android 11+ opens the
  app's location settings) → GPS on → notifications → battery unrestricted. Each step says what is missing.
- Limits (not hidden): force-stop stops everything until the app is opened again; after a reboot the service restarts
  only with "all the time"; aggressive manufacturer battery managers may still pause it; iOS, macOS and the web do not
  track in the background (the driver's Home says so). The server then reports `Offline` with the real last time.

## 6. Tests

Backend: `TripSegmenterTests` (A–F, K), `TrackingHealthTests` (L, M, N, K), `VehicleGpsTrackingTests` on SQL
Server (G–J, N, P, F), `VehicleApiSecurityTests` (every route's permission, validation). Flutter:
`vehicle_tracking_test.dart` (queue, inbox routing, offline → online upload with a lost answer),
`gps_pages_test.dart` (stale labelling, timeline order, gaps, empty day, driver states).
