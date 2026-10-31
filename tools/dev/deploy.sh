#!/usr/bin/env bash
# Phase 1: deploy the BrokerForge dev environment (infra/main.bicep) and wait for the host to be healthy.
# Reads the app registration and environment IDs from ~/.brokerforge/spike-ids.env (phase 0) and writes
# the deployed names to ~/.brokerforge/dev-ids.env. Generated secrets go straight into Key Vault and are
# never printed or written to the repo.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IDS="$HOME/.brokerforge/spike-ids.env"
OUT="$HOME/.brokerforge/dev-ids.env"
RG="${BF_RG:-rg-brokerforge-dev}"
LOCATION="${BF_LOCATION:-switzerlandnorth}"
# shellcheck disable=SC1090
source "$IDS"
: "${HOST_APP:?} ${PLUGIN_APP:?} ${TENANT:?} ${ENVID:?}"

az bicep version >/dev/null 2>&1 || az bicep install
for ns in Microsoft.App Microsoft.ContainerRegistry Microsoft.KeyVault Microsoft.OperationalInsights Microsoft.Insights Microsoft.ManagedIdentity; do
  az provider register --namespace "$ns" --wait -o none
done

echo "== resource group $RG ($LOCATION)"
az group create -n "$RG" -l "$LOCATION" -o none
DEPLOYER="$(az ad signed-in-user show --query id -o tsv)"

echo "== base: identity, registry, Key Vault, monitoring"
read -r ACR VAULT VAULT_URI < <(az deployment group create -g "$RG" -n bf-base -f "$ROOT/infra/main.bicep" \
  -p deployApps=false deployerObjectId="$DEPLOYER" \
  --query "[properties.outputs.registryName.value, properties.outputs.vaultName.value, properties.outputs.vaultUri.value]" -o tsv | tr '\n' ' ')

echo "== secrets in $VAULT (generated once, never printed)"
secret_file="$(mktemp)"
trap 'rm -f "$secret_file"' EXIT
for name in orders-api-key page-token-key; do
  if az keyvault secret show --vault-name "$VAULT" -n "$name" --query id -o tsv >/dev/null 2>&1; then
    echo "   $name exists"
    continue
  fi
  (umask 077; openssl rand -hex 32 | tr -d '\n' > "$secret_file")
  # Role assignments can take a minute to reach Key Vault.
  for attempt in 1 2 3 4 5 6 7 8 9 10; do
    if az keyvault secret set --vault-name "$VAULT" -n "$name" --file "$secret_file" -o none 2>/dev/null; then
      echo "   $name created"
      break
    fi
    [ "$attempt" = 10 ] && { echo "could not write $name to $VAULT" >&2; exit 1; }
    sleep 15
  done
done

TAG="$(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet || echo -dirty)"
echo "== images $TAG"
az acr build -r "$ACR" -t "brokerforge-host:$TAG" -f "$ROOT/src/BrokerForge.Host/Dockerfile" "$ROOT" --no-logs -o none
az acr build -r "$ACR" -t "brokerforge-orders-mock:$TAG" "$ROOT/tools/mock-api" --no-logs -o none
SERVER="$(az acr show -n "$ACR" --query loginServer -o tsv)"

echo "== apps"
HOST_URL="$(az deployment group create -g "$RG" -n bf-apps -f "$ROOT/infra/main.bicep" \
  -p deployApps=true deployerObjectId="$DEPLOYER" \
     hostImage="$SERVER/brokerforge-host:$TAG" mockImage="$SERVER/brokerforge-orders-mock:$TAG" \
     tenantId="$TENANT" hostAudience="$HOST_APP" pluginClientId="$PLUGIN_APP" environmentId="$ENVID" \
  --query properties.outputs.hostUrl.value -o tsv)"

cat > "$OUT" <<IDS_EOF
RG=$RG
ACR=$ACR
VAULT=$VAULT
DEV_HOST_URL=$HOST_URL
IMAGE_TAG=$TAG
IDS_EOF

echo "== waiting for $HOST_URL/healthz"
for attempt in $(seq 1 30); do
  code="$(curl -s -o /dev/null -w '%{http_code}' "$HOST_URL/healthz" || true)"
  if [ "$code" = 200 ]; then
    echo "healthy: $HOST_URL (ids in $OUT)"
    exit 0
  fi
  sleep 10
done
echo "host did not become healthy; check: az containerapp logs show -g $RG -n bf-dev-host --tail 100" >&2
exit 1
