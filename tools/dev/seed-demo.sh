#!/usr/bin/env bash
# Fills the local demo database with connected demo data through the running demo API (tools/dev/run-demo-api.sh).
#
#   tools/dev/seed-demo.sh
#
# Development only. Safe to run again: it creates what is missing and never deletes or resets anything.
# It refuses unless .local/demo.env points at a "*_Demo" database on this machine.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
env_file="$root/.local/demo.env"
[[ -f "$env_file" ]] || { echo "Missing $env_file (see docs/local-demo.md)." >&2; exit 1; }
set -a
# shellcheck disable=SC1090
source "$env_file"
set +a
database="$(sed -nE 's/.*Database=([^;]+).*/\1/p' <<<"$ConnectionStrings__Erp")"
[[ "$database" == *_Demo ]] || { echo "Refusing: the demo database name must end in _Demo (got '$database')." >&2; exit 1; }

dotnet run --project "$root/tools/dev/DemoSeed" -- "$@"
