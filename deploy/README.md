# Docker, CI and releases

## What is here

| File | What it does |
| --- | --- |
| `Dockerfile` | Builds the API into a small Linux image (non-root, port 8080, documents in `/data/archive`). |
| `.dockerignore` | Keeps tests, docs, `bin/obj` and every `.env` file out of the image. |
| `deploy/compose.yaml` | Runs the API with its own SQL Server 2022, for a trial or a small Linux server. |
| `deploy/.env.example` | The settings for `compose.yaml`. Copy to `deploy/.env` (ignored by git). |
| `deploy/migration-scripts.sh` | Writes the reviewed-in-order SQL migration scripts, one per module. |
| `.github/workflows/ci.yml` | CI: build and all 13 test projects on every push and pull request. |
| `.github/workflows/release.yml` | CD: on a `v*` tag, the Docker image, a Windows build and the migration scripts. |
| `.github/dependabot.yml` | Weekly update pull requests for NuGet, the Docker base images and the CI actions. |

## Run it in Docker

You need Docker. SQL Server needs about 2 GB of memory; with Docker Desktop at 4 GB, stop other SQL Server
containers first.

```bash
cp deploy/.env.example deploy/.env
```

Fill in `deploy/.env`: `SQL_SA_PASSWORD`, `JWT_SIGNING_KEY` (`openssl rand -base64 48`) and, for the first start,
`BOOTSTRAP_EMAIL` / `BOOTSTRAP_PASSWORD` and `APPLY_MIGRATIONS_ON_STARTUP=true`. Then:

```bash
docker compose -f deploy/compose.yaml up -d --build
```

- `http://localhost:8080/health/live` answers when the API runs; `http://localhost:8080/health` also checks the
  database.
- Set `SWAGGER_ENABLED=true` to get `http://localhost:8080/swagger`.
- After the first start, clear the bootstrap settings and set `APPLY_MIGRATIONS_ON_STARTUP=false`.
- Data lives in the Docker volumes `almotaheda-erp_sql-data` (database) and `almotaheda-erp_archive-data`
  (uploaded documents). Back both up.

Only the image, without compose:

```bash
docker build -t almotaheda-erp-api .
docker run -p 8080:8080 -v erp-archive:/data/archive -e ConnectionStrings__Erp="..." -e Access__Tokens__SigningKey="..." almotaheda-erp-api
```

The container listens on plain HTTP (8080). Put HTTPS in front of it (IIS, nginx, Caddy, a cloud load balancer)
and list that proxy in `ForwardedHeaders__KnownProxies__0` when it is on another machine.

## CI (every push and pull request)

GitHub Actions runs `.github/workflows/ci.yml`:

1. **Build** the whole solution in Release, and warn about vulnerable NuGet packages.
2. **Test**: each of the 13 test projects runs in its own job, on its own machine, so the SQL Server test containers
   never fight for memory. The test results (`.trx`) are kept for 14 days under the run's artifacts.
3. **Docker**: checks that the image still builds.

To block merging a red build, turn on branch protection for `main` in GitHub (Settings → Branches) and require
the CI checks.

## CD (releases)

Pushing a tag that starts with `v` runs `.github/workflows/release.yml`:

```bash
git tag v1.0.0
git push origin v1.0.0
```

It runs the full CI first, then produces:

- the image `ghcr.io/ahmedgndy/almotaheda-erp-api:1.0.0` (also `:1.0` and `:sha-…`), in GitHub Packages;
- `almotaheda-erp-api-1.0.0-win-x64.zip`: the API built for Windows (needs the ASP.NET Core 10 Hosting Bundle);
- `almotaheda-erp-migrations-1.0.0.zip`: one idempotent SQL script per module, numbered in apply order;

and attaches the two zips to a GitHub Release with generated release notes.

Nothing is installed on a server automatically. The database must be upgraded with reviewed scripts before the new
version starts, and that needs a person.

### Installing a release

1. Back up the database (Settings → Backup → Run backup now).
2. Review and run the migration scripts in number order (`01-Platform.sql` first, `11-Finance.sql` last), for
   example in SQL Server Management Studio. They are idempotent: already applied steps are skipped.
3. Install the new version:
   - **Windows server (IIS or a Windows service):** stop the site, replace its folder with the zip's content (keep
     your own settings), start it.
   - **Docker:** set `API_IMAGE=ghcr.io/ahmedgndy/almotaheda-erp-api:1.0.0` in `deploy/.env`, then
     `docker compose -f deploy/compose.yaml up -d`. A private package needs `docker login ghcr.io` first.
4. Check `/health` and sign in.

Making the scripts locally (needs the .NET 10 SDK): `deploy/migration-scripts.sh` writes them to
`artifacts/migrations/`.

## Good to know

- **Database backup feature and Docker.** The built-in backup (Settings → Backup) expects the API and SQL Server on
  the same Windows machine: SQL Server writes the `.bak` into a folder and the API zips the documents next to it
  and removes old files. In the Docker setup the two run in separate containers, so that folder would have to be
  a volume shared by both, with the same path. Until that is set up, back up the Docker volumes instead. For the
  Windows server, use the Windows build from the release.
- **Security patches.** The image uses the newest .NET 10 patch at build time. Rebuild and redeploy regularly
  (Dependabot opens a pull request when the base image changes).
- **Secrets** are never in the image or the repository: they come from `deploy/.env`, environment variables, or
  GitHub Actions secrets. The workflows need none besides the built-in `GITHUB_TOKEN`.
