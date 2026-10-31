"""Phase 1: register the BrokerForge provider and the ext_order virtual table in the dev environment.

Idempotent: every step looks for its component first. Follows the phase 0 metadata rules
(docs/findings/phase0.md section 1) and reads back TableType, DataProviderId and DataSourceId,
exiting non-zero on a mismatch. Uses the az CLI user's token; no secret is read or written.

Usage: python3 setup_dataverse.py <signed plug-in dll>
"""
import base64, json, os, subprocess, sys, urllib.error, urllib.parse, urllib.request

ORG = os.environ.get("DV_ORG", "https://orgeb8d0d95.crm17.dynamics.com")
API = ORG + "/api/data/v9.2/"
DATA_SOURCE_PROVIDER = "b2112a7e-b26c-42f7-9b63-9a809a9d716f"
ASSEMBLY_VERSION = "0.1.0.0"
PUBLIC_KEY_TOKEN = "359afdf5b289a42f"


def ids(path):
    out = {}
    for line in open(os.path.expanduser(path)):
        if "=" in line and not line.startswith("#"):
            k, v = line.strip().split("=", 1)
            out[k] = v
    return out


SPIKE = ids("~/.brokerforge/spike-ids.env")
DEV_PATH = os.path.expanduser("~/.brokerforge/dev-ids.env")
DEV = ids(DEV_PATH)
TOKEN = subprocess.check_output(["az", "account", "get-access-token", "--resource", ORG, "--query", "accessToken", "-o", "tsv"], text=True).strip()


def call(method, path, body=None, solution=None, ok=(200, 201, 204)):
    headers = {"Authorization": "Bearer " + TOKEN, "Accept": "application/json", "Content-Type": "application/json; charset=utf-8",
               "OData-Version": "4.0", "OData-MaxVersion": "4.0", "Prefer": "return=representation"}
    if solution:
        headers["MSCRM.SolutionUniqueName"] = solution
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(API + urllib.parse.quote(path, safe="/?$=&'(),:@"), data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            text = r.read().decode()
            return r.status, (json.loads(text) if text else {}), dict(r.headers)
    except urllib.error.HTTPError as e:
        text = e.read().decode()
        if e.code in ok:
            return e.code, {}, dict(e.headers)
        if e.code == 404 and 404 in ok:
            return 404, {}, {}
        sys.exit(f"{method} {path} -> HTTP {e.code}: {text[:1500]}")


def first(path):
    status, body, _ = call("GET", path, ok=(200, 404))
    rows = body.get("value", []) if status == 200 else []
    return rows[0] if rows else None


def label(text):
    return {"LocalizedLabels": [{"Label": text, "LanguageCode": 1033}]}


def mp(value, name):
    return {"Value": value, "CanBeChanged": False, "ManagedPropertyLogicalName": name}


def string_attr(schema, display, length, external, primary=False):
    a = {"@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "SchemaName": schema, "DisplayName": label(display),
         "RequiredLevel": {"Value": "None"}, "MaxLength": length, "FormatName": {"Value": "Text"}, "ExternalName": external}
    if primary:
        a["IsPrimaryName"] = True
    return a


VIRTUAL_FLAGS = {
    "OwnershipType": "OrganizationOwned", "HasActivities": False, "HasNotes": False, "IsActivity": False,
    "ChangeTrackingEnabled": False, "CanChangeTrackingBeEnabled": mp(False, "canchangetrackingbeenabled"),
    "CanCreateCharts": mp(False, "cancreatecharts"), "IsAuditEnabled": mp(False, "canmodifyauditsettings"),
    "IsDuplicateDetectionEnabled": mp(False, "canmodifyduplicatedetectionsettings"),
}


def step(text):
    print("==", text, flush=True)


def ensure_solution(unique, friendly, publisher_id):
    if not first(f"solutions?$select=solutionid&$filter=uniquename eq '{unique}'"):
        call("POST", "solutions", {"uniquename": unique, "friendlyname": friendly, "version": "1.0.0.0",
                                   "publisherid@odata.bind": f"/publishers({publisher_id})"})


def ensure_publisher(unique, prefix, option_prefix):
    row = first(f"publishers?$select=publisherid&$filter=uniquename eq '{unique}'")
    if row:
        return row["publisherid"]
    _, body, _ = call("POST", "publishers", {"uniquename": unique, "friendlyname": unique, "customizationprefix": prefix,
                                             "customizationoptionvalueprefix": option_prefix})
    return body["publisherid"]


def entity(logical):
    status, body, _ = call("GET", f"EntityDefinitions(LogicalName='{logical}')?$select=MetadataId,TableType,DataProviderId,DataSourceId", ok=(200, 404))
    return body if status == 200 else None


def ensure_attribute(table, payload, solution):
    logical = payload["SchemaName"].lower()
    status, _, _ = call("GET", f"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='{logical}')?$select=LogicalName", ok=(200, 404))
    if status == 404:
        call("POST", f"EntityDefinitions(LogicalName='{table}')/Attributes", payload, solution)


def set_primary_key_external_name(table, external):
    path = f"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='{table}id')"
    _, attr, _ = call("GET", path)
    if attr.get("ExternalName") == external:
        return
    attr = {k: v for k, v in attr.items() if not k.startswith("@odata.") or k == "@odata.type"}
    attr["@odata.type"] = "Microsoft.Dynamics.CRM.AttributeMetadata"
    attr["ExternalName"] = external
    call("PUT", path, attr)


def main(dll_path):
    core, orders = "BrokerForgeCore", "BrokerForgeOrders"

    step("publishers and solutions")
    bf_publisher = ensure_publisher("brokerforge", "bf", 10001)
    ext_publisher = ensure_publisher("brokerforgeext", "ext", 10000)
    ensure_solution(core, "BrokerForge Core", bf_publisher)
    ensure_solution(orders, "BrokerForge Orders", ext_publisher)

    step(f"plug-in assembly {ASSEMBLY_VERSION}")
    content = base64.b64encode(open(dll_path, "rb").read()).decode()
    asm = first(f"pluginassemblies?$select=pluginassemblyid&$filter=name eq 'BrokerForge.Plugin' and version eq '{ASSEMBLY_VERSION}'")
    if asm:
        asm_id = asm["pluginassemblyid"]
        call("PATCH", f"pluginassemblies({asm_id})", {"content": content})
    else:
        _, body, _ = call("POST", "pluginassemblies", {"name": "BrokerForge.Plugin", "content": content, "version": ASSEMBLY_VERSION,
                                                       "culture": "neutral", "publickeytoken": PUBLIC_KEY_TOKEN,
                                                       "isolationmode": 2, "sourcetype": 0}, core)
        asm_id = body["pluginassemblyid"]
    call("PATCH", f"pluginassemblies({asm_id})", {"managedidentityid@odata.bind": f"/managedidentities({SPIKE['MI']})"})

    step("plug-in type BrokerForge.Plugin.ProviderPlugin")
    pt = first(f"plugintypes?$select=plugintypeid&$filter=typename eq 'BrokerForge.Plugin.ProviderPlugin' and _pluginassemblyid_value eq {asm_id}")
    if pt:
        pt_id = pt["plugintypeid"]
    else:
        _, body, _ = call("POST", "plugintypes", {"typename": "BrokerForge.Plugin.ProviderPlugin", "name": "BrokerForge.Plugin.ProviderPlugin",
                                                  "friendlyname": "BrokerForge provider", "pluginassemblyid@odata.bind": f"/pluginassemblies({asm_id})"}, core)
        pt_id = body["plugintypeid"]

    step("data source table bf_brokerdatasource")
    if not entity("bf_brokerdatasource"):
        call("POST", "EntityDefinitions", dict(VIRTUAL_FLAGS, **{
            "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": "bf_brokerdatasource",
            "DisplayName": label("BrokerForge Data Source"), "DisplayCollectionName": label("BrokerForge Data Sources"),
            "ExternalName": "brokerdatasource", "ExternalCollectionName": "brokerdatasources", "DataProviderId": DATA_SOURCE_PROVIDER,
            "Attributes": [string_attr("bf_name", "Name", 100, "name", primary=True)]}), core)
    for schema, display, length, external in [("bf_hosturl", "Host URL", 500, "hosturl"), ("bf_scope", "Token scope", 200, "scope"),
                                              ("bf_serviceinstance", "Service instance", 100, "serviceinstance")]:
        ensure_attribute("bf_brokerdatasource", string_attr(schema, display, length, external), core)
    set_primary_key_external_name("bf_brokerdatasource", "id")

    step("data provider BrokerForge")
    dp = first("entitydataproviders?$select=entitydataproviderid&$filter=name eq 'BrokerForge'")
    plugins = {k: pt_id for k in ("retrieveplugin", "retrievemultipleplugin", "createplugin", "updateplugin", "deleteplugin")}
    if dp:
        dp_id = dp["entitydataproviderid"]
        call("PATCH", f"entitydataproviders({dp_id})", plugins)
    else:
        _, body, _ = call("POST", "entitydataproviders", dict(plugins, name="BrokerForge", datasourcelogicalname="bf_brokerdatasource"), core)
        dp_id = body["entitydataproviderid"]

    step("data source record Orders host")
    record = {"bf_name": "Orders host", "bf_hosturl": DEV["DEV_HOST_URL"], "bf_scope": f"api://{SPIKE['HOST_APP']}/.default", "bf_serviceinstance": "OrdersAPI"}
    ds = first("bf_brokerdatasources?$select=bf_brokerdatasourceid&$filter=bf_name eq 'Orders host'")
    if ds:
        ds_id = ds["bf_brokerdatasourceid"]
        call("PATCH", f"bf_brokerdatasources({ds_id})", record)
    else:
        _, body, _ = call("POST", "bf_brokerdatasources", record)
        ds_id = body["bf_brokerdatasourceid"]

    step("virtual table ext_order")
    if not entity("ext_order"):
        call("POST", "EntityDefinitions", dict(VIRTUAL_FLAGS, **{
            "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": "ext_order",
            "DisplayName": label("Order"), "DisplayCollectionName": label("Orders"),
            "DataProviderId": dp_id, "DataSourceId": ds_id, "ExternalName": "order", "ExternalCollectionName": "orders",
            "Attributes": [string_attr("ext_name", "Reference", 100, "ext_name", primary=True)]}), orders)
    ensure_attribute("ext_order", {
        "@odata.type": "Microsoft.Dynamics.CRM.PicklistAttributeMetadata", "SchemaName": "ext_status", "DisplayName": label("Status"),
        "RequiredLevel": {"Value": "None"}, "ExternalName": "ext_status",
        "OptionSet": {"@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata", "IsGlobal": False, "OptionSetType": "Picklist",
                      "Options": [{"Value": 100000000, "Label": label("Open")}, {"Value": 100000001, "Label": label("Shipped")},
                                  {"Value": 100000002, "Label": label("Cancelled")}]}}, orders)
    ensure_attribute("ext_order", string_attr("ext_customerref", "Customer reference", 100, "ext_customerref"), orders)
    ensure_attribute("ext_order", {
        "@odata.type": "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata", "SchemaName": "ext_placed", "DisplayName": label("Placed"),
        "RequiredLevel": {"Value": "None"}, "Format": "DateAndTime", "ExternalName": "ext_placed"}, orders)
    ensure_attribute("ext_order", {
        "@odata.type": "Microsoft.Dynamics.CRM.MemoAttributeMetadata", "SchemaName": "ext_lines_json", "DisplayName": label("Lines (JSON)"),
        "RequiredLevel": {"Value": "None"}, "MaxLength": 100000, "Format": "TextArea", "ExternalName": "ext_lines_json"}, orders)
    call("POST", "PublishXml", {"ParameterXml": "<importexportxml><entities><entity>ext_order</entity><entity>bf_brokerdatasource</entity></entities></importexportxml>"})

    step("read back")
    table = entity("ext_order")
    checks = {"TableType": (table["TableType"], "Virtual"), "DataProviderId": (table["DataProviderId"], dp_id), "DataSourceId": (table["DataSourceId"], ds_id)}
    for name, (actual, expected) in checks.items():
        print(f"   {name}: {actual}")
        if str(actual).lower() != str(expected).lower():
            sys.exit(f"ext_order {name} is {actual}, expected {expected}")

    values = dict(DEV, DEV_ASM=asm_id, DEV_PT=pt_id, DEV_DP=dp_id, DEV_DS=ds_id)
    with open(DEV_PATH, "w") as f:
        f.writelines(f"{k}={v}\n" for k, v in values.items())
    print(f"done: assembly {asm_id}, plug-in type {pt_id}, provider {dp_id}, data source {ds_id}")


if __name__ == "__main__":
    main(sys.argv[1])
