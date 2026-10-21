#!/usr/bin/env bash
# Starts the API against the local demo database, with the settings in .local/demo.env (git-ignored).
#
#   tools/dev/run-demo-api.sh
#
# Development only. It refuses to start unless the environment is Development and the connection string points at a
# SQL Server on this machine and a database whose name ends in "_Demo", so it can never migrate or seed anything else.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
env_file="$root/.local/demo.env"
[[ -f "$env_file" ]] || { echo "Missing $env_file (see docs/local-demo.md)." >&2; exit 1; }

set -a
# shellcheck disable=SC1090
source "$env_file"
set +a

[[ "${ASPNETCORE_ENVIRONMENT:-}" == "Development" ]] || { echo "Refusing: ASPNETCORE_ENVIRONMENT must be Development." >&2; exit 1; }
server="$(sed -nE 's/.*Server=([^;]+).*/\1/p' <<<"$ConnectionStrings__Erp")"
database="$(sed -nE 's/.*Database=([^;]+).*/\1/p' <<<"$ConnectionStrings__Erp")"
[[ "$server" =~ ^(localhost|127\.0\.0\.1)(,[0-9]+)?$ ]] || { echo "Refusing: the demo API only uses a SQL Server on this machine (got '$server')." >&2; exit 1; }
[[ "$database" == *_Demo ]] || { echo "Refusing: the demo database name must end in _Demo (got '$database')." >&2; exit 1; }

mkdir -p "$Archive__Storage__RootPath" "$Fleet__Storage__RootPath" "$Hr__Storage__RootPath" "$Office__Storage__RootPath"
echo "Demo API: $ASPNETCORE_URLS  database: $database on $server"
cd "$root"
exec dotnet run --project src/Host/AlMotaheda.Api --no-launch-profile "$@"
