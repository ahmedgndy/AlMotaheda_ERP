# Access module

Sign-in, users, roles and permissions. The design and the decisions behind it are in the
"Login and Users: Design for Approval" page.

## What it does

- **Sign-in** with email and password. Returns a 15-minute access token and sets a refresh-token cookie
  (HttpOnly, Secure, SameSite=Strict, path `/api/auth`). A sign-in lasts 12 hours, or 30 days with "Remember me".
- **Refresh** swaps the cookie for a new one every time. A cookie that was already swapped coming back ends the
  whole sign-in (replay detection).
- **Lockout:** 5 wrong passwords in a row lock the account for 15 minutes. Parallel guesses count too.
- **First sign-in:** new users, and users whose password was reset, must change their password before anything else.
- **Users:** list (search, filter, paging), view, create, edit, disable, enable, reset password.
- **Permissions:** 17 modules × 5 actions. Each of the six roles has permissions (seeded from the prototype);
  a user can have their own setting per module, which replaces the role's for that module.

## The rule that protects everything else

**Only an active Executive can create users or change anyone's role, status, password or permissions.**

- It is a role check, not a permission someone can be given, because whoever can edit permissions could give
  themselves everything.
- It is checked twice: first from the token, then in the database inside the change's transaction. The caller must
  still be an active Executive, have no pending password change, and still be signed in with the same sign-in.
  So a copied token stops working for user management as soon as its owner signs out, changes their password,
  has it reset, or is disabled or demoted.
- Nobody can change their own status, role or permissions, and at least one active Executive always remains.
- Changes run one at a time (an application lock), so two Executives can't disable each other at the same moment.
- The Executive role's permissions can't be edited, and Executives can't have their own permission settings.
- Every change is written to the security log with who did it, to whom, and what changed (field names, never values).

## Endpoints

| Endpoint | Who may call |
|---|---|
| `POST /api/auth/login` | Anyone (rate-limited) |
| `POST /api/auth/refresh` | Holder of the cookie (rate-limited) |
| `POST /api/auth/logout` | Anyone |
| `POST /api/auth/change-password` | Signed in, also while a change is required |
| `GET /api/me` | Signed in, also while a change is required |
| `GET /api/users`, `GET /api/users/{id}` | `accounts` View, or yourself for your own record |
| `POST /api/users`, `PUT /api/users/{id}` | Executive |
| `POST /api/users/{id}/disable`, `/enable`, `/reset-password` | Executive |
| `GET /api/users/{id}/permissions` | `accounts` View, or yourself |
| `PUT /api/users/{id}/permissions/{module}`, `POST /api/users/{id}/permissions/reset` | Executive |
| `GET /api/roles` | `accounts` View |
| `PUT /api/roles/{code}/permissions` | Executive (not for the Executive role) |

Writes that change an existing record take the `rowVersion` from your last read and return 409
`concurrency_conflict` if someone saved first. Disable and enable accept it but don't require it, so an emergency
disable always goes through.

## Tables (schema `access`)

`Role`, `Permission`, `RolePermission`, `User`, `UserSignInState`, `UserPermissionOverride`, `RefreshToken`.
Users are disabled, never deleted. Foreign keys never cascade.

`UserSignInState` holds the failed-attempt count, lockout end and last sign-in. It is kept apart from `User` so
that signing in doesn't change the user's `RowVersion`.

## Configuration

| Setting | Default | Notes |
|---|---|---|
| `ConnectionStrings:Erp` | none, required | Secret |
| `Access:Tokens:SigningKey` | none, required | Base64 of at least 32 random bytes. Secret |
| `Access:Tokens:Issuer`, `Audience` | `AlMotaheda.Api`, `AlMotaheda.Erp` | |
| `Access:Tokens:Lifetime` | 15 minutes | |
| `Access:Sessions:Duration`, `PersistentDuration` | 12 hours, 30 days | |
| `Access:Lockout:MaxFailedAttempts`, `Duration` | 5, 15 minutes | |
| `Access:Bootstrap:Email`, `Password`, `NameAr`, `NameEn` | empty | Creates the first Executive when no users exist |
| `Database:ApplyMigrationsOnStartup` | `false` | Development convenience only |

## Code layout

- `Features/` one folder per area, one file per use case (`Auth`, `Users`, `Roles`, `UserPermissions`).
- `Security/` what several slices share: tokens, sessions, password hashing, lockout, the user-management gate,
  permission resolution.
- `Common/` validation, error responses, the security log, small shared helpers.
- `Data/` entities, the `AccessDbContext`, table configuration, seed data (`PermissionCatalog`) and migrations.
- `Startup/` applies migrations (when enabled) and creates the first Executive.
