#!/usr/bin/env bash
# Run Generate for one connection inside the running dev host container, as the host's own identity (HLD D-36).
# Stopgap until plan 4's bf_Generate. Regenerates from the design already published in the environment.
# Usage: tools/dev/generate-in-host.sh <Instance> <Solution> [--preview | --confirm <fingerprint>]
#   e.g. tools/dev/generate-in-host.sh WarehouseAPI BrokerForgeWarehouse --preview
# Exit codes follow the admin command: 0 done or previewed, 1 failed, 4 the changes need confirming (run again with
# the printed --confirm). The host role needs the generation privileges (tools/dev/setup_dataverse.py --host-user).
set -euo pipefail
INSTANCE="${1:?service instance}"
SOLUTION="${2:?solution unique name}"
shift 2
EXTRA=""
case "${1:-}" in
  "") ;;
  --preview) EXTRA="--preview" ;;
  --confirm) EXTRA="--confirm ${2:?fingerprint}" ;;
  *) echo "usage: $0 <Instance> <Solution> [--preview | --confirm <fingerprint>]" >&2; exit 2 ;;
esac
# shellcheck disable=SC1090
source "$HOME/.brokerforge/spike-ids.env"
# shellcheck disable=SC1090
source "$HOME/.brokerforge/dev-ids.env"
: "${ENVID:?} ${RG:?}"
COMMAND="dotnet /app/BrokerForge.Host.dll generate --environment $ENVID --instance $INSTANCE --solution $SOLUTION --publisher brokerforgeext --from-environment $EXTRA"
# exec needs a terminal; script(1) gives it one when this runs from another tool. `az containerapp exec` does not pass
# the command's exit status back, so the result is read from the command's own lines.
OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT
if [ -t 0 ]; then
  az containerapp exec -g "$RG" -n bf-dev-host --command "$COMMAND" | tee "$OUT" || true
else
  script -q /dev/null az containerapp exec -g "$RG" -n bf-dev-host --command "$COMMAND" | tee "$OUT" || true
fi
if grep -q "run again with --confirm" "$OUT"; then
  exit 4
elif grep -q "^$INSTANCE: Succeeded" "$OUT" || { [ "$EXTRA" = "--preview" ] && grep -Eq "^$INSTANCE: (changes|no changes)" "$OUT"; }; then
  echo "OK: generate $INSTANCE in the host ${EXTRA:+($EXTRA) }finished."
else
  echo "FAILED: the host did not report success for $INSTANCE (see its output above)." >&2
  exit 1
fi
