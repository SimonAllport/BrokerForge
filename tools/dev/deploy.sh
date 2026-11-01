#!/usr/bin/env bash
# Deploy the BrokerForge dev environment (infra/main.bicep) and wait for the host to be healthy.
# Reads the app registration and environment IDs from ~/.brokerforge/spike-ids.env (phase 0) and the phase 3
# connect app IDs (tools/dev/setup-connect-app.sh) from ~/.brokerforge/dev-ids.env, and writes the deployed
# names back to dev-ids.env, keeping the IDs it does not own. Generated secrets go straight into Key Vault and are
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
# Phase 3 IDs from setup-connect-app.sh; empty until it has run, which leaves the per-user sources out.
devid() { [ -f "$OUT" ] && sed -n "s/^$1=//p" "$OUT" | tail -1 || true; }
CONNECT_APP="$(devid CONNECT_APP)" TICKETS_API_APP="$(devid TICKETS_API_APP)"
TICKETS_ALICE_OID="$(devid TICKETS_ALICE_OID)" TICKETS_BOB_OID="$(devid TICKETS_BOB_OID)"

az bicep version >/dev/null 2>&1 || az bicep install
for ns in Microsoft.App Microsoft.ContainerRegistry Microsoft.KeyVault Microsoft.Storage Microsoft.OperationalInsights Microsoft.Insights Microsoft.ManagedIdentity; do
  az provider register --namespace "$ns" --wait -o none
done

echo "== resource group $RG ($LOCATION)"
az group create -n "$RG" -l "$LOCATION" -o none
DEPLOYER="$(az ad signed-in-user show --query id -o tsv)"

echo "== base: identity, registry, Key Vault, monitoring"
base="$(az deployment group create -g "$RG" -n bf-base -f "$ROOT/infra/main.bicep" \
  -p deployApps=false deployerObjectId="$DEPLOYER" \
  --query "[properties.outputs.registryName.value, properties.outputs.vaultName.value]" -o tsv)"
ACR="$(sed -n 1p <<< "$base")"
VAULT="$(sed -n 2p <<< "$base")"

echo "== secrets in $VAULT (generated once, never printed)"
secret_file="$(mktemp)"
trap 'rm -f "$secret_file"' EXIT
for name in orders-api-key inventory-api-key page-token-key tickets-client-secret tickets-alice-password tickets-bob-password; do
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

# A dirty tree gets a tag from its diff: reusing one "-dirty" tag would leave the old revision running.
TAG="$(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet || echo "-dirty-$(git -C "$ROOT" diff | shasum | cut -c1-8)")"
echo "== images $TAG"
az acr build -r "$ACR" -t "brokerforge-host:$TAG" -f "$ROOT/src/BrokerForge.Host/Dockerfile" "$ROOT" --no-logs -o none
az acr build -r "$ACR" -t "brokerforge-orders-mock:$TAG" "$ROOT/tools/mock-api" --no-logs -o none
SERVER="$(az acr show -n "$ACR" --query loginServer -o tsv)"

echo "== apps"
HOST_URL="$(az deployment group create -g "$RG" -n bf-apps -f "$ROOT/infra/main.bicep" \
  -p deployApps=true deployerObjectId="$DEPLOYER" \
     hostImage="$SERVER/brokerforge-host:$TAG" mockImage="$SERVER/brokerforge-orders-mock:$TAG" \
     tenantId="$TENANT" hostAudience="$HOST_APP" pluginClientId="$PLUGIN_APP" environmentId="$ENVID" \
     connectClientId="$CONNECT_APP" ticketsApiAppId="$TICKETS_API_APP" ticketsAliceOid="$TICKETS_ALICE_OID" ticketsBobOid="$TICKETS_BOB_OID" \
  --query properties.outputs.hostUrl.value -o tsv)"

keep="$(mktemp)"
[ -f "$OUT" ] && grep -Ev '^(RG|ACR|VAULT|DEV_HOST_URL|IMAGE_TAG)=' "$OUT" > "$keep" || true
{ printf 'RG=%s\nACR=%s\nVAULT=%s\nDEV_HOST_URL=%s\nIMAGE_TAG=%s\n' "$RG" "$ACR" "$VAULT" "$HOST_URL" "$TAG"; cat "$keep"; } > "$OUT"
rm -f "$keep"

echo "== waiting for $HOST_URL/healthz, and for host and mock to run image $TAG alone"
# Healthy is not enough: the outgoing revision keeps answering until it is deactivated (phase 1 findings 5 and 8,
# phase 2 finding 6 for the mock).
active() { az containerapp revision list -g "$RG" -n "$1" --query "[?properties.active].properties.template.containers[0].image" -o tsv | sort -u; }
for attempt in $(seq 1 30); do
  code="$(curl -s -o /dev/null -w '%{http_code}' "$HOST_URL/healthz" || true)"
  if [ "$code" = 200 ] && [ "$(active bf-dev-host)" = "$SERVER/brokerforge-host:$TAG" ] \
     && [ "$(active bf-dev-orders-mock)" = "$SERVER/brokerforge-orders-mock:$TAG" ]; then
    echo "healthy: $HOST_URL (ids in $OUT)"
    exit 0
  fi
  sleep 10
done
echo "host did not become healthy; check: az containerapp logs show -g $RG -n bf-dev-host --tail 100" >&2
exit 1
