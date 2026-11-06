"""Remove the InventoryAPI made by `bf generate` from dev (plan 5 task 6), so done criterion 1 starts with no source.

Deletes the Custom API ext_AdjustStock (its parameters go with it), the virtual table ext_item and the
BrokerForgeInventory solution. Virtual tables hold no data: the items stay in the mock. Runs only against the dev
org in ~/.brokerforge/spike-ids.env (DV_ORG), as the az CLI user, and stops at the first refusal, printing what
Dataverse said (a dependency such as an app that still shows the table, which is then removed by hand).
"""
import os, sys

sys.path.insert(0, os.path.dirname(__file__))
from acceptance import API, ORG, call, error_message  # noqa: E402

DEV_ORG = next((l.split("=", 1)[1].strip().strip('"') for l in open(os.path.expanduser("~/.brokerforge/spike-ids.env")) if l.startswith("DV_ORG=")), None)


def gone(what, status, body, ok=(204,)):
    if status not in ok:
        sys.exit(f"{what}: HTTP {status}: {error_message(body)}")
    print(f"removed {what}", flush=True)


def main():
    if not DEV_ORG or ORG.rstrip("/") != DEV_ORG.rstrip("/"):
        sys.exit("remove_old_inventory.py runs only against the dev org (DV_ORG in ~/.brokerforge/spike-ids.env)")
    print(f"Dataverse: {API}", flush=True)
    _, body, _ = call("GET", "customapis?$select=customapiid&$filter=uniquename eq 'ext_AdjustStock'")
    for api in body.get("value", []):
        gone("Custom API ext_AdjustStock", *call("DELETE", f"customapis({api['customapiid']})")[:2])
    status, body, _ = call("GET", "EntityDefinitions(LogicalName='ext_item')?$select=MetadataId")
    if status == 200:
        gone("table ext_item", *call("DELETE", "EntityDefinitions(LogicalName='ext_item')")[:2])
    _, body, _ = call("GET", "solutions?$select=solutionid&$filter=uniquename eq 'BrokerForgeInventory'")
    for s in body.get("value", []):
        gone("solution BrokerForgeInventory", *call("DELETE", f"solutions({s['solutionid']})")[:2])
    status, _, _ = call("GET", "ext_items?$top=1")
    print(f"GET ext_items: HTTP {status}", flush=True)


if __name__ == "__main__":
    main()
