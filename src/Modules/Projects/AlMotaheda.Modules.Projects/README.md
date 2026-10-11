# Projects module

Construction projects, who is on each project, and who may open which project. The decisions behind it are in the
"Project Domain Analysis" document (decisions D1 to D17); this module applies its recommendations.

## What it does

- **Projects:** list (search, status filter, paging, counters), view, create, edit. Projects are never deleted;
  a finished project is set to Completed. No money is stored in this version: no budget, contract value or
  opening figures (D1). The client is text (D7).
- **Team (membership):** one table, `ProjectMember`, replaces the prototype's four lists (`managerId`,
  `engineerIds`, `team` and the user's `projects`). Each row is one person on one project in one responsibility:
  - **ProjectManager:** exactly one active per project. An Executive or a Manager.
  - **SiteEngineer:** an Engineer.
  - **TeamMember:** a Manager, an Engineer or Staff (for example drivers).
- **History:** memberships are ended, never deleted. A replaced manager's row keeps who ended it, when and why.
- Workers and guards are not members and need no login. They will be assigned to projects by the Workforce module.

## Who may do what

| Action | Who | Checked how |
|---|---|---|
| See the project list and a project | projects View, and in scope | Scope in the SQL query; out of scope answers 404 |
| Scope | Executive and Accountant: every project. Manager, Engineer, Staff: projects with an active membership | `ProjectScopes`, `ProjectAccess.VisibleProjects` |
| Create a project | Executive only (D2) | Role in the token, then `IExecutiveCheck` in the database |
| Edit a project | projects Edit, and the Executive or the project's active manager (D3) | `ProjectRules.Edit`, under the project lock |
| Change the code | Executive only (D14) | `ProjectRules.CanChangeCode` |
| Change a completed project or reopen it | Executive only (D8). A manager may set Completed | `ProjectRules.Edit` |
| Add, end or replace members | Executive only (D4) | Role in the token, then `IExecutiveCheck` in the database |
| Read a user's project list | Yourself, or anyone with accounts View; only projects you may see | `ListUserProjects` |

**The rule behind it:** membership decides who may open a project, so changing it is a change of access. It follows
the same rule as permissions: only an active Executive, re-checked in the database (still active, still an Executive,
no pending password change, still signed in with the same sign-in). A token can be 15 minutes old; membership is
read fresh on every request, so ending someone's membership takes effect at once.

A responsibility never adds a permission. A project manager without projects Edit can't edit; an engineer on project A
gets nothing on project B.

## Endpoints

| Endpoint | Who may call |
|---|---|
| `GET /api/projects` | projects View (in scope) |
| `GET /api/projects/{id}` | projects View (in scope) |
| `POST /api/projects` | Executive |
| `PUT /api/projects/{id}` | projects Edit, Executive or the project's manager |
| `GET /api/projects/{id}/members` | projects View (in scope) |
| `PUT /api/projects/{id}/manager` | Executive |
| `POST /api/projects/{id}/members` | Executive |
| `POST /api/projects/{id}/members/{memberId}/end` | Executive |
| `GET /api/projects/memberships?userId=` | projects View; yourself, or accounts View |
| `GET /api/projects/assignable-users?responsibility=` | Executive |

`GET /api/projects` parameters: `search` (code, names, client), `status` (repeat for several), `assignedTo=me`,
`sort` (`code`, `name`, `startDate` newest first, `endDate`, `status`), `page`, `pageSize` (at most 100).
The response includes `countsByStatus` for the counters.

`GET /api/projects/{id}` also returns `myResponsibility` and `can` (`edit`, `changeCode`, `manageMembers`), so the
frontend shows only the buttons that will work. The server checks again on every call.

Dates are `yyyy-MM-dd`. Statuses and responsibilities are sent and returned by name (`OnHold`; any letter case is
accepted). Edits take the `rowVersion` from your last read and answer 409 `concurrency_conflict` if someone saved first.

## Errors

| Status | Code | When |
|---|---|---|
| 400 | `validation_failed` | Field errors, all together |
| 400 | `user_not_assignable` | The user doesn't exist, is disabled, or their role doesn't fit the responsibility |
| 403 | `forbidden` | Missing permission, not an Executive, not the project's manager, or changing the code |
| 404 | `not_found` | The project doesn't exist or is outside your scope; the membership isn't on this project |
| 409 | `project_code_taken`, `project_completed`, `already_member`, `manager_required`, `membership_already_ended`, `concurrency_conflict` | Rule conflicts |
| 503 | `busy` | Another change to the same project held its lock for more than 10 seconds; retry |

## Tables (schema `projects`)

- `Project`: code (unique, upper-case), Arabic and English names, client names, location, contract number, address,
  site centre and radius (geofence), start and end dates, status, notes. Check constraints mirror the form's rules.
- `ProjectMember`: project, user, responsibility, start date, end date (empty while active), who assigned it, who
  ended it and why. Filtered unique indexes: one active membership per person per project, and one active manager
  per project.
- `ProjectMember.UserId` is a foreign key to `access.User`, written by hand in the migration. **Apply the Access
  migrations before the Projects migrations.**

## Configuration

| Setting | Default | Notes |
|---|---|---|
| `Projects:TimeZone` | `Africa/Cairo` | The company's time zone. "Today" for membership dates is the date there. Use `UTC` on a server without time zone data. |
| `Database:ApplyMigrationsOnStartup` | `false` | Development only; applies the Access migrations, then these |

## Known limits (accepted)

- The checks that the Executive is still active and that an assigned user is still active and of a fitting role
  read the Access tables just before the Projects change, in a separate transaction. A demotion or a disable that
  lands in the same few milliseconds is not seen. User management itself keeps the stricter, locked gate. A member
  whose account is later disabled, or whose role no longer fits, shows `fitsResponsibility: false` for the Executive
  to replace or end.
- Phone and e-mail of team members are shown to everyone who can open the project, as in the prototype. The end
  reason of a past membership is shown to Executives only.
- Until the platform AuditLog table exists, the audit trail is the structured log (`ProjectsLog`, event ids 2001 to
  2301): who did what to which project and user, by id, with field names but never field values.

## Code layout

- `Features/Projects`: `ListProjects`, `GetProject`, `CreateProject`, `UpdateProject`, and the shared form rules
  (`ProjectFields`) and response (`ProjectDetails`).
- `Features/Members`: `ListProjectMembers`, `SetProjectManager`, `AddProjectMember`, `EndProjectMember`,
  `ListUserProjects`, `ListAssignableUsers`.
- `Security`: the caller, role scopes, `ProjectAccess` and `ProjectRules` (the one place the access rules live),
  `ExecutiveGate`, and the per-project lock.
- `Common`: validation, error responses, the audit log, the business calendar.
- `Data`: entities, `ProjectsDbContext`, table configuration and migrations.
- `Startup`: applies migrations when enabled.
