#!/usr/bin/env bash
# Set one instance secret of a connection design (HLD D-34) in the dev host's store. Stopgap until plan 4's page.
# Usage: tools/dev/set-instance-secret.sh <Instance> <name> [<Key Vault secret name>]
#   e.g. tools/dev/set-instance-secret.sh WarehouseAPI apiKey inventory-api-key
# The value never leaves Azure and is never printed: the host's admin command runs inside the running host container
# (az containerapp exec), so it uses the host's own identity, table and Data Protection key ring, and it reads the
# value itself from the Key Vault reference. Only the reference is on the command line.
set -euo pipefail
INSTANCE="${1:?service instance}"
NAME="${2:?secret name}"
KV_SECRET="${3:-$(printf '%s' "$INSTANCE" | tr '[:upper:]' '[:lower:]')-$(printf '%s' "$NAME" | tr '[:upper:]' '[:lower:]')}"
# shellcheck disable=SC1090
source "$HOME/.brokerforge/spike-ids.env"
# shellcheck disable=SC1090
source "$HOME/.brokerforge/dev-ids.env"
: "${ENVID:?} ${RG:?} ${VAULT:?}"
VAULT_URI="$(az keyvault show -n "$VAULT" --query properties.vaultUri -o tsv)"
# Fail here, by name, rather than in the container: the secret must exist (its value is not fetched).
[ -n "$(az keyvault secret list --vault-name "$VAULT" --query "[?name=='$KV_SECRET'].id" -o tsv)" ] \
  || { echo "Key Vault $VAULT has no secret $KV_SECRET" >&2; exit 1; }
COMMAND="dotnet /app/BrokerForge.Host.dll set-instance-secret --environment $ENVID --instance $INSTANCE --name $NAME --from kv:${VAULT_URI}secrets/$KV_SECRET"
# exec needs a terminal; script(1) gives it one when this runs from another tool.
if [ -t 0 ]; then
  az containerapp exec -g "$RG" -n bf-dev-host --command "$COMMAND"
else
  script -q /dev/null az containerapp exec -g "$RG" -n bf-dev-host --command "$COMMAND"
fi
