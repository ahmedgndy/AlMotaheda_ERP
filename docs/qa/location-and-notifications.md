# Location / GPS and notifications

## Location today

| Piece | Where | What it does |
| --- | --- | --- |
| Site position | `Project.SiteLatitude`, `SiteLongitude`, `SiteRadiusMeters` (default 500 m) | Stored with the project; set on the project form. |
| Site map | Flutter `SiteLocationCard` (flutter_map, OpenStreetMap tiles; `MAP_TILE_URL` overrides the tile server) | Shows the site and its radius on the project overview. |
| "Use my location" / "How far am I" | Flutter `LocationService` (geolocator) | One foreground fix, only when the user taps. Asks for permission, explains a refusal, links to settings. |

Permissions requested: Android fine and coarse location (foreground only, no background permission), iOS "when
in use", macOS location entitlement. The phone's position is sent to the server only when the user chooses "use my location" on the project
form and saves it as the site's position. It is not otherwise stored or shared, and there is no tracking.

### Not built, and why

**GPS attendance** (check-in only inside the site radius) and **continuous tracking** of engineers or vehicles
are not implemented. Before building them the company has to decide:

1. Who is located, when (working hours only?), and how often.
2. How long positions are kept and who may see them (data-protection law and staff consent).
3. What happens when the phone has no signal or the user refuses permission (manual check-in with a reason?).
4. Whether a spoofed position must be detected.

With those answers the backend needs an attendance table (user, project, time, position, accuracy, inside
radius) and an endpoint that checks the radius on the server; the app already has the one-shot fix and the radius.

## Notifications today: the work inbox

`GET /api/inbox` answers "what is waiting for me" across the caller's projects:

| Reason | When | Who sees it |
| --- | --- | --- |
| Review | An expense/supply, labor sheet, equipment cost, measurement or manpower record is recorded; a daily report is submitted; a report request is submitted back; a price change is requested | Users with Approve in that module, never the recorder; price changes go to the Executive |
| Returned | A reviewer returned the record with a note | The recorder |
| Requested | A report was asked of the caller and isn't submitted | The recipient |

Design choices:

- **Computed from the records on every call**, not stored. Nothing can be missed or go stale, an item disappears
  the moment someone acts on it, and there is no read/unread table to clean up.
- **Same rules as the actions**: each module checks its own permission, only the caller's visible projects are
  included (completed projects only for the Executive), and amounts appear only with See money in that module.
- **Bounded**: each source returns at most 100 items per kind and the answer says when it was cut (`truncated`).
- The app polls it every 60 seconds and when it comes back to the foreground, shows a badge on the bell and a
  "My work" page whose items open the record.

### Not built, and why

- **Push notifications** (a phone alert while the app is closed) need Firebase Cloud Messaging and Apple Push
  credentials, a device-token table and a sender. They can be added on top of the inbox: the inbox stays the
  source of truth and a push only says "something new is waiting".
- **Real-time updates** (SignalR/WebSocket) were not added; a one-minute poll is enough for review work.
- **Email notifications** need an SMTP or mail-service account.
