#!/usr/bin/env bash
# Writes one idempotent SQL script per module, numbered in the order they must be applied.
# Review them, then run them against the database before starting a new API version.
#
#   deploy/migration-scripts.sh [output folder]      (default: artifacts/migrations)
set -euo pipefail

cd "$(dirname "$0")/.."
out="${1:-artifacts/migrations}"
mkdir -p "$out"

dotnet tool restore >/dev/null

# The order the API itself migrates in (src/Host/AlMotaheda.Api/ModuleCatalog.cs): Platform first (every save writes
# its audit and outbox tables), then Access and Projects (other modules point to their tables), HR before Fleet
# (vehicles and claims point to hr.Employee), Office before Finance, Finance last (its migrations copy recorded
# payments into the ledger and link office custodies). Keep this list in step with ModuleCatalog.
modules=(Platform Access Projects HR Workforce Subcontractors Fleet Archive Office Procurement Reports Finance)

n=0
for module in "${modules[@]}"; do
  n=$((n + 1))
  file="$(printf '%s/%02d-%s.sql' "$out" "$n" "$module")"
  echo "-> $file"
  dotnet ef migrations script --idempotent --configuration Release \
    --project "src/Modules/$module/AlMotaheda.Modules.$module" --output "$file"
done
