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

# Platform first (every save writes its audit and outbox tables), then Access and Projects (other modules point to
# their tables), Finance last (its second migration copies recorded payments into the ledger).
modules=(Platform Access Projects Workforce Subcontractors Fleet Archive HR Procurement Reports Finance)

n=0
for module in "${modules[@]}"; do
  n=$((n + 1))
  file="$(printf '%s/%02d-%s.sql' "$out" "$n" "$module")"
  echo "-> $file"
  dotnet ef migrations script --idempotent --configuration Release \
    --project "src/Modules/$module/AlMotaheda.Modules.$module" --output "$file"
done
