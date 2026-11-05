#!/usr/bin/env bash
# Install the BrokerForge core (out/BrokerForgeCore_managed.zip from tools/dev/export-core.sh) into another environment,
# the one-off install step of spec 11. Usage: tools/dev/install-core.sh <org url> <environment id>
set -euo pipefail
ORG_URL="${1:?org url, e.g. https://contoso-test.crm4.dynamics.com}"
ENV_ID="${2:?environment id}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PY="${PYTHON:-$( [ -x /usr/bin/python3 ] && echo /usr/bin/python3 || echo python3 )}"
PACKAGE="$ROOT/out/BrokerForgeCore_managed.zip"
[ -f "$PACKAGE" ] || { echo "run tools/dev/export-core.sh first" >&2; exit 1; }
DV_ORG="$ORG_URL" "$PY" "$ROOT/tools/dev/install_core.py" "$PACKAGE"
cat <<NEXT
Installed in $ORG_URL. Still to do outside Dataverse:
 1. A federated credential on the plug-in app for environment $ENV_ID (docs/findings/phase0.md section 4).
 2. A Trust:AllowedCallers entry for $ENV_ID with OrgUrl $ORG_URL in infra/containerApps.bicep, then tools/dev/deploy.sh.
 3. Assign the BrokerForge administrator role to whoever sets up connections there.
NEXT
