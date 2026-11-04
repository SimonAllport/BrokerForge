# Generator spike: forms, views, dependencies, removals and roles on a virtual table

Run 4 November 2026 against the dev environment as the signed-in az CLI user (System Administrator), with `tools/spike/generator.sh`. The scratch instance was `SpikeAPI`, table `ext_spikeitem`, a copy of Inventory made by `bf generate` into solution `BrokerForgeGeneratorSpike`. Everything below was observed, and every component was deleted afterwards. Plan: `docs/superpowers/plans/2026-11-04-portal-connections-3-generator.md`, task 1.

## 1. What the platform creates with a virtual table

| Component | Query | Notes |
| --- | --- | --- |
| Main form "Information" | `systemforms?$filter=objecttypecode eq '<table>'`, `type` 2 | One section holding the primary name only. `isdefault` reads false even though it is the only main form. |
| Card form, quick view form | `type` 11 and 6 | Not touched by the generator. |
| Default public view "All <collection>" | `savedqueries?$filter=returnedtypecode eq '<table>'`, `querytype` 0, `isdefault` true | Primary key and primary name. |
| Advanced find (1), associated (2), quick find (4), lookup (64), offline (8192), Copilot (1048576) views | same query | Primary name only. Not touched by the generator. |

The grid's `object` attribute is the table's object type code, which differs per environment. The generator keeps the platform's `layoutxml` and replaces only the `<row>` element, so it never has to know the code.

## 2. Showing every column

- **Form.** PATCH `systemforms(<id>)` with `formxml` whose first `<rows>` holds one `<row><cell id="{new guid}"><labels>…</labels><control id="<col>" classid="{…}" datafieldname="<col>" /></cell></row>` per column.
- **View.** PATCH `savedqueries(<id>)`:
  - `fetchxml` lists `<attribute name="<col>" />` for the key and each column, plus `<order attribute="<primary name>" />`;
  - `layoutxml` replaces the `<row name="result" id="<key>">` content with `<cell name="<col>" width="150" />`.
- **Publish.** `PublishXml` with `<entities><entity><table></entity></entities>`, then read back. Both PATCHes and the publish together took 11.3 s.
- **Control classids by type.** The probe used the text control for every column and the platform accepted it. The generator uses the type's own control:

  | Type | classid |
  | --- | --- |
  | string | `{4273EDBD-AC1D-40d3-9FB2-095C621B552D}` |
  | memo | `{E0DECE4B-6FC8-4a8f-A065-082708572369}` |
  | int | `{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}` |
  | decimal | `{C3EFE0C3-0EC6-42be-8349-CBD9079DFD8E}` |
  | bool | `{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}` |
  | datetime, date | `{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}` |
  | optionset | `{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}` |

## 3. Dependencies and removals

- `GET RetrieveDependenciesForDelete(ObjectId=<attribute MetadataId>,ComponentType=2)` returns one row per dependent. The fields are:
  - `dependentcomponenttype`: 60 for a form, 26 for a view;
  - `dependentcomponentobjectid`;
  - `dependencytype` 2.

  With a hand-made view using `ext_sku`, the column had three dependents: the main form, the hand-made view and the default view. A dependent's name comes from `systemforms(<id>)?$select=name` or `savedqueries(<id>)?$select=name`.
- After the layout dropped `ext_onhand`, it had no dependents, and `DELETE EntityDefinitions(LogicalName='<table>')/Attributes(LogicalName='ext_onhand')` succeeded in 7.7 s.
- DELETE of `ext_sku`, still used by the hand-made view, was refused with HTTP 400: "The Attribute(<id>) component cannot be deleted because it is referenced by 3 other components." The platform enforces the rule too. The generator checks first, so that it can name the dependents and apply nothing.
- **Custom API.** `RetrieveDependenciesForDelete(ObjectId=<customapiid>,ComponentType=10038)` returned no rows. `DELETE customapis(<id>)` took 9.7 s and removed its 3 request parameters with it.
- The table delete took 18 s.

## 4. Role

- `POST roles` with `businessunitid@odata.bind` to the root business unit, sent with the `MSCRM.SolutionUniqueName` header, puts the role in the solution (component type 20). No `AddSolutionComponent` is needed.
- A virtual table has `prvRead`, `prvCreate`, `prvWrite`, `prvDelete`, `prvAppend` and `prvAppendTo` `<table>`. There is no Assign or Share privilege, because the table is organisation-owned.
- `AddPrivilegesRole` with `Depth: "Global"` grants them; `roleprivilegescollection` shows `privilegedepthmask` 8.
- **A new role starts with nine platform privileges:**
  - `prvReadPluginAssembly`, `prvReadPluginType`;
  - `prvReadSdkMessage`, `prvReadSdkMessageProcessingStep`, `prvReadSdkMessageProcessingStepImage`;
  - `prvCreateSharePointData`, `prvReadSharePointData`, `prvWriteSharePointData`, `prvReadSharePointDocument`.

  `ReplacePrivilegesRole` removed five of them. The generator therefore never uses Replace. It adds the wanted table privileges and removes this table's unwanted ones one at a time, with `POST roles(<id>)/Microsoft.Dynamics.CRM.RemovePrivilegeRole` and body `{"Privilege": {"@odata.type": "Microsoft.Dynamics.CRM.privilege", "privilegeid": "<id>"}}`. That call worked. Its parameter is `Privilege`, not `PrivilegeId`.

## 5. Not verified

- **Whether a non-administrator can grant a privilege it doesn't hold.** The AddPrivilegesRole reference requires privileges on the Role table and access to the role record. It doesn't say whether the caller must also hold the privilege being granted. The new table's privileges can't already be held by the host role. Task 6 tests this as the host's application user. If the platform refuses, the finding goes to Simon before any change to the host's role.
- Every call above ran as System Administrator. The privileges a non-administrator host role needs are listed in task 6 and checked there.
