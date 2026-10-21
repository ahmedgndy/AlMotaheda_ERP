#!/usr/bin/env bash
# Serves the Flutter app in the browser against the local demo API (tools/dev/run-demo-api.sh must be running).
#
#   tools/dev/run-web-demo.sh [path/to/flutter/project]
#
# Open http://localhost:8100/accounts for one link per demo account (a1.localhost … a12.localhost: each keeps its own
# sign-in). Development only: it listens on this machine and forwards only to an API on this machine.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
flutter_project="${1:-${FLUTTER_PROJECT:-$HOME/StudioProjects/al_motaheda_erp_flutter}}"
web="$flutter_project/build/web"

if [[ ! -f "$web/index.html" || "${REBUILD:-0}" == "1" ]]; then
  echo "Building the Flutter web app in $flutter_project ..."
  (cd "$flutter_project" && flutter build web --release --no-web-resources-cdn --dart-define=API_BASE_URL=/)
fi

accounts=()
if [[ -f "$root/.local/demo-credentials.md" ]]; then
  # One host name per account, in the order of the seed's account list.
  python3 - "$root/.local/demo-credentials.md" "$root/.local/web-accounts.json" <<'EOF'
import json, sys
rows = [line for line in open(sys.argv[1], encoding="utf-8") if line.startswith("| ") and "@" in line]
accounts = []
for index, line in enumerate(rows, 1):
    cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
    accounts.append({"host": f"a{index}", "name": cells[0], "role": cells[1], "email": cells[2], "try": cells[3]})
json.dump(accounts, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
EOF
  accounts=(--accounts "$root/.local/web-accounts.json" --accounts-env "$root/.local/demo-accounts.env")
fi

exec dotnet run --project "$root/tools/dev/WebDemo" -- --web "$web" "${accounts[@]}"
