# Portal connections: which solution part carries a design

Verified 3 November 2026 against the dev environment (shown here as `https://contoso.crm.dynamics.com`), as the signed-in az CLI user, in the scratch solution `BrokerForgeCarrierSpike` (publisher `brokerforgeext`, prefix `ext`). Everything below was observed, not inferred, unless marked **Not verified**. Design background: [portal connections spec, section 7](../superpowers/specs/2026-11-03-portal-connections-design.md).

Probe: [tools/spike/carrier.sh](../../tools/spike/carrier.sh). It creates everything it tests, runs every check and deletes what it made (`KEEP=1` keeps it). Run 3 completed with nothing left behind.

## Ruling

**Carrier = WebResource.** One XML web resource (`webresourcetype` 4) per connection holds the design JSON in a single CDATA section. It passes every check at 1× and 4× the Orders design. The environment variable of type JSON fails on size; the third candidate (a `bf_brokerdatasource` column) was not needed, so it was not tried.

**Size limit:** the decoded file is capped by `organization.maxuploadfilesize`, 5,242,880 bytes (5 MB) here. A 5,238,676-byte file saved and a 5,246,868-byte file was refused (`Webresource content size is too big.`). That is over 570 times the Orders design. The cap is an org setting, so it can differ in other environments; a writer should read it rather than assume 5 MB.

## Test design

The Orders design: the five `descriptors/OrdersAPI/*.yaml` files, `schemas/OrdersAPI.yaml`, `tests/SwaggerForge.Tests/fixtures/orders-openapi.yaml` and a small answers object, wrapped as indented JSON.

| Copy | Bytes (UTF-8) |
| --- | --- |
| 1× (Orders as it is) | 9,094 |
| 4× (padded, for headroom) | 36,791 |

## Results

| Candidate | Check | 1× | 4× |
| --- | --- | --- | --- |
| Environment variable, type JSON (`type` 100000003), design as `defaultvalue` | Saves | **Fail**: `The length of the 'defaultvalue' attribute ... exceeded the maximum allowed length of '2000'` | **Fail** (same) |
| | Measured limit | 2,000 characters saves, 2,001 is refused. `environmentvariablevalue.value` has the same 2,000 cap (metadata `MaxLength`). | |
| Web resource, XML (`webresourcetype` 4), JSON in CDATA | Saves and reads back byte-identical | Pass | Pass |
| | Exported unmanaged zip holds the JSON as plain text | Pass | Pass |
| | Re-import of the same zip leaves the value unchanged | Pass | Pass |
| | Edit the value in the environment, then import the zip as a new solution (`BrokerForgeCarrierSpike2`): the exported value comes back byte-identical | Pass | Pass |
| `bf_brokerdatasource` column | | Not tried (candidate 2 passed) | |

Type 4 was accepted, so type 3 (script) was not needed.

### Environment variables for the base URL and auth (spec 7, step 3)

| Check | Result |
| --- | --- |
| A String variable (`type` 100000000) holds a 300-character URL as `defaultvalue` and reads it back unchanged | Pass |
| With no `environmentvariablevalue`, the effective value is the default | Pass |
| With an `environmentvariablevalue`, the effective value is the current value | Pass |
| `RetrieveEnvironmentVariableValue(DefinitionSchemaName='...')` returns the current value as `Value` | Pass |
| The export carries the default as text (`environmentvariabledefinitions/<schema>/environmentvariabledefinition.xml`) | Pass |
| The export carries the current value (`environmentvariabledefinitions/<schema>/environmentvariablevalues.json`) | **Yes**, when the value row was created in the solution (see Surprises) |

## Exact reads (for task 6)

**Design document.** Name convention used in the spike: `<prefix>_/brokerforge/<instance>/design.xml`, where `<prefix>` is the customization prefix of the solution's publisher.

```
GET /api/data/v9.2/webresourceset?$filter=name eq 'ext_/brokerforge/OrdersAPI/design.xml'&$select=webresourceid,name,webresourcetype,content
```

- Exactly one row is expected. Zero rows or more than one is an error that names the instance (fail loudly).
- `content` is base64. Decode to UTF-8, which gives the wrapper below. Check `webresourcetype` is 4.
- The wrapper the spike wrote and parsed, byte for byte:

  ```
  <?xml version="1.0" encoding="utf-8"?>
  <design format="json"><![CDATA[{ ...design JSON... }]]></design>
  ```

  The JSON is the text between `<![CDATA[` and the final `]]>`. Parse the wrapper strictly; anything else is a typed error, not a best guess.
- GET returns the **published** content (see Surprises), which is what the host must run.

**Writes (for the Generate side).** `POST webresourceset` with `name`, `displayname`, `webresourcetype: 4`, `content` (base64 of the wrapper) and header `MSCRM.SolutionUniqueName: <solution>`; then `PATCH webresourceset(<id>)` for later changes. Each write must be followed by:

```
POST /api/data/v9.2/PublishXml
{"ParameterXml": "<importexportxml><webresources><webresource>{<id>}</webresource></webresources></importexportxml>"}
```

**Base URL and auth variables.**

```
GET /api/data/v9.2/environmentvariabledefinitions?$filter=schemaname eq 'ext_OrdersAPI_BaseUrl'&$select=schemaname,type,defaultvalue&$expand=environmentvariabledefinition_environmentvariablevalue($select=value)
```

Effective value = the expanded `value` if there is a row, else `defaultvalue`. The function `GET RetrieveEnvironmentVariableValue(DefinitionSchemaName='ext_OrdersAPI_BaseUrl')` returns the same as `{"Value": ...}`. Either value is capped at 2,000 characters.

## Surprises

1. **Updates are invisible until published.** After a `PATCH` of `content`, GET still returned the old content. It returned the new content only after `PublishXml`. A new web resource is readable at once. Consequences:
   - Generate must publish the web resource after each write.
   - In a target environment, an unmanaged import must be published (`pac solution import --publish-changes`, or `PublishXml`/`PublishAllXml` afterwards) before the host sees a changed design. Managed imports are documented to publish as part of the import (**not verified** here). Also **not verified**: whether an unmanaged import alone, with no publish, shows the new content to GET. The probe always published after importing.
   - `RetrieveUnpublished` is not available through the Web API (404), so the host cannot read the unpublished version that way.
2. **A current value created inside the solution travels with it.** The `environmentvariablevalue` row was created with the `MSCRM.SolutionUniqueName` header, so it joined the solution and was exported in `environmentvariablevalues.json`. If dev's value travels, it overwrites the target's value on import. Generate should create dev's value without the solution header (or remove it with `RemoveSolutionComponent`), so that only the default travels and each environment sets its own value (spec 7).
3. **Long synchronous calls hung.** A synchronous `ImportSolution` never answered within 600 s, although its import job showed it completed in 5 s. In the next run, `PublishAllXml` also hung past 600 s. The probe now uses `ImportSolutionAsync` and polls `asyncoperations(<id>)` (`statecode` 3, `statuscode` 30), and calls `PublishXml` for its own web resources only. Code that imports or publishes should do the same.
4. **CDATA cannot hold `]]>`.** None of the Orders texts contain it, but YAML or OpenAPI text could. The writer must refuse it or write it so the JSON keeps its meaning (for example `]]>` inside JSON strings, which parses to the same value). The probe refuses it.
5. **Export file names.** In the raw `ExportSolution` zip the file is `WebResources/<name with punctuation removed><webresourceid>`, for example `WebResources/ext_brokerforgeordersapidesignxml<GUID>`. `customizations.xml` maps it back to the name. The content is the wrapper as plain text, so it diffs as text. **Not verified:** the `pac solution unpack` layout, because `pac` is not installed here. The SolutionPackager convention is `WebResources/<name path>`.
6. **Not tried:** a name whose prefix does not match the solution publisher's prefix. The spike used `ext_` with an `ext` publisher.

## Clean-up

The probe deletes its web resources, environment variable definitions and values, and both scratch solutions, then lists by name prefix (`ext_/brokerforge/carrierspike/`, `ext_CarrierSpike`, `BrokerForgeCarrierSpike`). After run 3 it reported 0 left behind.
