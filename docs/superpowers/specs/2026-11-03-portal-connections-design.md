# Connections set up in Dataverse: design

Status: draft for Simon's review, 3 November 2026. Replaces HLD D-05 and D-16, the section 14 row "Service instances and descriptors: config data, deployed by pipeline from Git", and the interim settings in D-10 and D-30.

## 1. Why

Today a new REST source means running `bf draft`, editing YAML, adding settings to Bicep, redeploying the host, running `bf generate` and adding columns to the generated form by hand. The how-to guide recorded on 2 November shows every step, and it is too much for the admins this is for.

Simon wants it to feel like adding a SQL connection in Power Platform, or a service instance in K2. An admin opens a BrokerForge app in Dataverse, adds a connection, fills in a name, base URL and authentication, pastes a Swagger (OpenAPI) definition into a text box, answers any questions BrokerForge has, and clicks Generate. The Swagger definition is the only thing anyone writes.

## 2. Goals

1. An admin onboards a REST source entirely inside a model-driven app: no YAML, no Bicep, no CLI, no host redeploy.
2. After Save, BrokerForge lists every endpoint it found and what each becomes (list, open, New, Save, Delete, or an action). The admin can untick endpoints.
3. Where the Swagger definition leaves a gap BrokerForge cannot fill without guessing, the admin answers a question on screen. Generate stays disabled until every ticked endpoint is ready.
4. Generate creates the virtual table, every column, a view and a main form that show every column, the Custom APIs, and a security role for the source.
5. "Read again" with a changed Swagger definition shows what would change before anything is updated.
6. The connection's design travels dev → test → prod in the source's solution. Each environment supplies its own base URL and secret.
7. Orders, Inventory and the two Tickets sources move onto this route, and their host settings in Bicep are removed.

## 3. Non-goals

- A second, code-first onboarding route in BrokerForge. The code route is DataverseToCode (Simon's VS Code product), which keeps solution components as files in Git. Teaching DataverseToCode to show a BrokerForge connection as a file is work in that project. This design only commits to a stored format that is readable text, stable and documented (section 7).
- Non-REST adapters (phase 4) on these screens. The tables allow a broker type so they can be added later.
- Editing descriptors as YAML on screen. Admins never see YAML.
- Changes to `BrokerForge.Contracts` or the descriptor schema (`docs/descriptor-spec.md`). The design produces the same descriptors and schema files the engine reads today.

## 4. Anti-goals: what would make this a failure even if it works

- **BrokerForge guessing.** If it fills a gap with a guess, such as assuming a filter works or picking a paging style, the source can return wrong or partial rows. That breaks principle 1. Every gap is a question the admin answers, or the endpoint stays out.
- **A secret in Dataverse.** No secret goes into a Dataverse row, an environment variable, a solution, a Custom API parameter that is stored, a plug-in trace or a log.
- **A move to test that fails silently.** If a connection arrives in an environment with no URL or secret, it must say "needs URL" or "needs key" on the connection screen. Calls to its tables must throw a typed exception that names what is missing. They must never call the dev URL.
- **Admins having to repair what Generate made.** Hand-fixing a form or adding a role after Generate counts as a defect.

## 5. The admin's experience

These are the mockups Simon approved on 3 November (`.superpowers/brainstorm/*/content/connection-flow.html`).

**Screen 1, New connection.** It is a main form on the connection table, with these fields:

- Name, the service instance name, for example `WarehouseAPI`.
- Base URL.
- Authentication: Service Account, Static (API key, basic), OAuth, Impersonate or SSO, with the fields that mode needs. OAuth asks for the authorise URL, token URL, scopes and client ID, Impersonate for the source API's scope, and SSO for how the credential is sent.
- Table prefix.
- Swagger / OpenAPI, a large text box that takes YAML or JSON.

A secret (API key, basic password, OAuth client secret) is never typed into the form. Beside the field, a "Set key" button opens the host's secret page, where the key is entered and stored (section 8). The form only shows "key set" or "needs key".

"Save and read the definition" saves the row and asks the host to read the Swagger definition.

**Screen 2, Review.** It shows the table BrokerForge proposes: display name, logical name, key, primary name and columns, with names the admin can edit. One row per endpoint: ticked or not, what it becomes, and a status of Ready, N questions, or Not used.

Questions appear under the endpoint they belong to, in plain words with fixed choices. They cover every point where SwaggerForge's draft report says "review", plus anything the validator would reject. The builder decides the exact set, starting from today's draft report:

- how results are paged;
- which filters and operators the source really supports (anything left unticked fails loudly if used);
- whether the default order is stable;
- which property is the key or the primary name when it can't be told;
- number precision;
- which collection becomes the table when there are several;
- how errors map to BrokerForge exceptions, where the definition lists error responses.

Generate is enabled only when every ticked endpoint is Ready.

**Screen 3, Done.** It lists what was created, with links to open the table, add it to an app, and read again.

How the screens are built is the builder's call. My call: the connection table's main form for screen 1, plus a custom page for screen 2. Command buttons call Custom APIs, which go through the existing plug-in to the host. This reuses the trusted plug-in → host path (HLD 9.2) rather than adding a browser → host path for admin actions.

## 6. How it works

SwaggerForge (draft), the validator and the generator are libraries today, but only the CLI references SwaggerForge and the generator, and the generator runs with the CLI user's token. Moving them into the host, with the generator running as the host's own application user, is new work in this design.

1. **Read the definition.** A `bf_ReadDefinition` Custom API sends the connection's ID. The host reads the row through the Web API, runs SwaggerForge, and writes back the proposed table and endpoints, plus the open questions with their choices. Where SwaggerForge marks something "review" or "not drafted" today, it now raises a question, and the answer goes into the descriptor. SwaggerForge may be extended for this: sort mappings, `orStyle: in` and nested response paths such as `$.state.code` are needed to reproduce today's Orders descriptors.
2. **Answer.** The admin's answers and edits are saved on Dataverse rows. Each save, or an explicit "Check" button, re-runs the draft with the answers applied, plus the validator. The status per endpoint follows from that.
3. **Generate.** A `bf_Generate` Custom API starts generation on the host and returns at once. Generation can take longer than a plug-in's 2-minute limit, so it runs in the background and writes progress and a final result (or a typed failure) to the connection row, which the form shows. The generator adds:
   - every column on the view and the main form;
   - a security role named `<Instance> user`, with the privileges the endpoints allow (Read for list and open, Create, Write, Delete as present) at organisation depth;
   - the solution parts in section 7.

   Everything goes into a solution named for the source, which the generator creates if it doesn't exist.
4. **Read again.** It re-runs step 1 against the generated design and shows the difference as a list the admin confirms, for example "add column Colour", "remove column SKU", "change Price from whole number to decimal", "change Reserve's inputs". Generate then applies exactly that list (Simon, 3 November). The current generator only adds and refuses anything else (`GenerationRefusedException` in `src/BrokerForge.Generator/GenerationPlan.cs`); this design extends it:
   - **Removing a column or Custom API:** it is first taken off the generated view and form, then deleted. Virtual tables hold no data, so nothing is lost in Dataverse. Before the admin confirms, the list shows anything else that depends on it (other forms, views, flows, apps), using Dataverse's dependency check. If something outside BrokerForge's generated parts still depends on it, that item is blocked and named, and nothing in the list is applied until the admin removes the dependency. BrokerForge never removes someone else's component.
   - **Changing a column's type:** Dataverse cannot change a column's type in place, so this is a remove followed by an add under the same name, with the same dependency rule.
   - **Changing a Custom API's parameters:** the same, applied to the parameters, or to the whole API where the platform requires it.
   - **Renames** shown on screen 2 (display names) are updates in place, not removals.
   - Generate runs only in the environment the design belongs to. Other environments get the change by importing the new solution version as an upgrade, which removes deleted components the normal way.
5. **Run time.** The host loads each environment's connections from Dataverse instead of host settings and files: the stored design (section 7) plus the environment variable values. It caches them and reloads after Generate, or within a fixed time (builder's call, no more than 5 minutes) when a solution import changes them. A call for a connection that is missing its URL or secret throws a typed exception naming what is missing, with the correlation ID.

**Host identity in Dataverse.** The host needs an application user in each environment it serves, holding a BrokerForge host role. To read: the connection tables and environment variables. To generate: create tables, columns, forms, views, Custom APIs, roles and solutions. The setup script creates it. One host serving several environments needs each environment's URL. The host already maps the plug-in's `azp` to one environment ID (D-09); it gains the URL in the same mapping.

**Approval.** Clicking Generate is the approval. The design document replaces HLD section 6's `bf_operation` and `bf_objectmap` rows: there are no separately approved descriptor versions and no draft descriptors at run time. The design carries a version number that Generate increments; the host logs it with every call. `bf_keymap` and `bf_userconnection` are unaffected (phase 5 and D-30 respectively). Section 6 of the HLD is rewritten to match. (My call.)

**Who may set up connections.** Only users with a "BrokerForge administrator" role in Dataverse. The Custom APIs check the caller's privilege, so the platform enforces it, and the host does not rely only on the plug-in's word. The builder chooses the mechanism, for example a privilege on the connection table that the Custom API steps require.

## 7. What travels in the solution

Rows in ordinary Dataverse tables do not travel in solutions. A connection that existed only as rows would leave its generated tables behind it in test, with no design to run. So Generate also writes the connection's design as solution parts:

| Part | Holds | Per environment? |
| --- | --- | --- |
| Design document | The Swagger definition as pasted, the admin's answers, and the generated descriptors and table schema file in today's formats (descriptor spec 1–14) | No: the same everywhere |
| Environment variable `bf_<instance>_BaseUrl` | Base URL | Yes: dev's value as the default, each environment sets its own |
| Environment variable `bf_<instance>_Auth` | Authentication mode and its non-secret settings, such as the OAuth URLs and client ID | Yes |
| Generated table, columns, view, form, Custom APIs, role, data source record | As today | Data source record as today |

The secret is never part of the solution. On arrival in a new environment the connection shows "needs key" until an admin sets it on the host's page.

**Unverified (first task).** Which solution part carries the design document. Candidates, in order:

1. an environment variable of type JSON;
2. a web resource;
3. the data source record.

The first task measures each one's size limit against the largest real design (Orders with all its descriptors), checks that the value round-trips through export and import unchanged, and checks that it unpacks as readable text, which is what DataverseToCode needs. If none of them work, stop and report under principle 6. Do not fall back to a pipeline from Git.

The connection rows the admin works on (screens 1–2) hold the same information while it is being edited. In an environment where the solution was imported, the connection screen is built from the solution parts. Whether the design can be edited there, or only in the environment it came from, is the builder's call. The default is read-only outside its source environment, like a managed solution.

## 8. Secrets

The host serves a page at `{host}/admin/{environmentId}/{instance}/secret`; the "Set key" button builds that URL from the environment it runs in. It uses Entra sign-in through the existing connect app (D-26). The host then checks, through its own application user in that environment, that the signed-in user holds the BrokerForge administrator role there (looked up by Entra object ID). An Entra app role is not used, so Dataverse stays the one place admin rights are granted. Instance names need only be unique within an environment. (My call.) The secret is stored encrypted in the host's existing table store (D-25), in a partition for instance secrets, keyed by environment and instance. `secret` bindings and Static mode read it from there through `ISecretResolver`. Today's Key Vault references (`kv:`) stay supported, so a client who wants Key Vault can still point at it.

Nothing on this path logs the value. The no-token log check from phase 3 findings 5 is repeated for instance secrets.

## 9. Moving dev → test → prod

1. Export the source's solution from dev, or let DataverseToCode or a pipeline do it.
2. Import into test. The tables, actions and role arrive, and so does the connection's design.
3. Open the connection in test: set the base URL if it differs, and set the key.
4. The connection reads "ready". Users given the role can use the tables.

## 10. Migration of today's sources

Orders, Inventory, TicketsAPI and TicketsEntraAPI are re-onboarded through the screens from their OpenAPI files, answering the questions. "Matches" means the same behaviour: the acceptance scripts and contract tests pass. The YAML does not have to be identical. Orders' OpenAPI file is `tests/SwaggerForge.Tests/fixtures/orders-openapi.yaml`; it moves next to the mock in `tools/mock-api/`, with the others, and stays the source of truth. (My call.) The existing acceptance scripts must still pass. Then their `Instances__*`, `ObjectMaps__*` and `OAuthResources__*` settings come out of `infra/containerApps.bicep`, along with the `descriptors/` and `schemas/` copies in the image. The repo keeps the OpenAPI files and the mock.

The contract tests keep loading descriptors from files, so they stay independent of Dataverse. The host gains a second configuration source (Dataverse) next to the file source, used in production.

`bf validate` stays for the contract-test fixtures. `bf draft` and `bf generate` stay as developer tools but drop out of the guide.

## 11. Constraints

- **Setting up an environment stays a one-off install step,** like installing a K2 server. That means: the plug-in app registration, the host's `Trust:AllowedCallers` entry (now also carrying the environment URL), the host's application user and role, and the core solution, all through the setup scripts and a host deploy. Only onboarding a source moves into the portal. Packaging the core as a managed solution in `solution/` is part of this work, because done criterion 4 needs a second environment installed the same way. Moving environment trust out of Bicep is not part of it. (My call.)

- The fail-loudly, no-fallback, no-secrets and thin plug-in principles (CLAUDE.md) apply in full. The plug-in gains two Custom API routes and nothing else.
- Dataverse limits:
  - a plug-in call has 2 minutes, hence background Generate;
  - virtual tables have no Money columns (D-15);
  - provider errors surface as `0x80040265` (section 15).
- One host serves every environment in the tenant (HLD 14), so connection config is always looked up by environment.
- No contract or descriptor schema change (section 3). If the builder finds one is needed, they ask Simon first, as the working agreement requires.

## 12. Done when

1. In a dev environment where BrokerForge is installed (section 11) and no source exists, an admin onboards the Inventory mock from its OpenAPI file through the screens, and lists, opens, creates, updates and deletes items. Nothing runs from a terminal except setting up the mock.
2. An undeclared filter still throws `UnsupportedQueryException`, and an unmapped 500 still throws `SourceException`.
3. A gap in the Swagger definition (paging not stated) shows a question, and Generate is disabled until it's answered.
4. The Inventory solution, imported into a second environment, works after only setting the URL and key. Before the key is set, a read throws the typed "needs key" exception. No call reaches the source.
5. Read again after removing a property from the Swagger definition and changing another's type lists both changes. After confirmation, Generate removes the first column and recreates the second with the new type, and the view and form are updated. A column that a hand-made view still uses is blocked and named, and nothing is applied.
6. A non-administrator gets Read through the generated role with no hand-made role.
7. No secret appears in any Dataverse row, solution export, environment variable or log. Checked by searching an export and the logs for the test key.
8. Orders, Inventory and both Tickets sources run from Dataverse config, their acceptance scripts pass, and their Bicep settings are gone.
9. HLD decisions D-05, D-10, D-16 and D-30, and section 14, are updated in the same change, and the how-to guide and video are redone for the new route.

## 13. Left to the builder

- Table and column names for the connection, endpoint and question rows, within the `bf_` prefix.
- The exact question set and wording, starting from SwaggerForge's draft report.
- How the screens are built.
- The cache reload interval, up to 5 minutes.
- How the administrator check is enforced.
- Whether the design can be edited in an environment it was imported into.
- How the work splits into phases and tasks.
