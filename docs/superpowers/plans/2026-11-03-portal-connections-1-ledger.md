# SDD ledger — plan: docs/superpowers/plans/2026-11-03-portal-connections-1-config.md
Spec: docs/superpowers/specs/2026-11-03-portal-connections-design.md
Branch: portal-connections (from main a4ac969; plan commit d1c18a5)

## Pre-flight scan
| Rows | Produces vs consumes | Found |
|---|---|---|
| T1→T2 | carrier ruling → format doc step 5 | consistent |
| T1→T6 | exact reads → DataverseDesignSource | consistent |
| T2→T4 | ConnectionDesign, EnvironmentSettings → DesignConfigInput.Merge | AuthOverrides application unspecified |
| T3→T4 | ConfigInput, HostConfigStore(ConfigInput) → Merge | consistent |
| T4→T5 | store:<n> refs emitted in T4; SecretRef.Parse accepts only kv/env until T5 | conflict: T4 validation would reject store refs; T4 tests need a Static key to reach Inventory mock |
| T4→T6 | IDesignSource, CompositeConfigProvider → Dataverse source | consistent |
| T5→T7 | IInstanceSecretStore → set-instance-secret | consistent |
| T1 self | spike, findings | consistent |
| T2 self | tests vs types | consistent |
| T3 self | pure refactor, same messages | consistent |
| T4 self | tests vs interfaces | consistent after rulings |
| T5 self | | consistent after ruling |
| T6 self | | consistent |
| T7 self | | consistent |

Ruling: AuthOverrides keys override the DesignAuth string field of the same camelCase name (e.g. scope, headerName); an unknown key is a config problem naming it — makes per-env auth tweaks explicit — cost if wrong: small rework in Merge.
Ruling: Move into T4 the `store` scheme in SecretRef.Parse, the IInstanceSecretStore interface + InMemoryInstanceSecretStore, the instance-aware ISecretResolver.GetAsync(ref, instance, ct) overload and SecretResolverRouter delegating `store` to the store; T5 keeps the table store, missing-key message and its tests — T4's own tests need a Static design to reach the Inventory mock — cost if wrong: none, only reorders work.
Task 1: implementer DONE_WITH_CONCERNS f84e299 — carrier = web resource type 4 (XML, CDATA); env var JSON capped at 2000 chars.
Ruling: Task 1 concerns 1–4 (publish after write, keep dev env-var value out of solution, async import/targeted publish, CDATA `]]>`) belong to plan 3 (generator) and Task 2's writer: Task 2 serializer must refuse/escape `]]>` when wrapping; carry the others into plan 3's brief — cost if wrong: a stale or leaked env value in plan 3.
Ruling: Carrier = web resource type 4 (XML), design JSON in one CDATA section, name `<prefix>_/brokerforge/<Instance>/design.xml`; read = webresourceset filter by name, base64-decode content, strict unwrap (docs/findings/portal-carrier.md) — env var JSON capped at 2000 chars — cost if wrong: Task 6 reader swap.
Task 1: minor (deferred): `]]>` advice vague in findings (handled by Task 2 ruling below); limit bracketed to 8 KB not bisected; carrier.sh has no candidate-3 code path; managed-import publish not verified.
Task 1: complete (commits d1c18a5..f84e299, review clean)
Ruling: Task 2 adds `DesignCarrier.Wrap(string json) -> string xml` and `DesignCarrier.Unwrap(string xml, string source) -> string json` (root `<brokerforgeDesign formatVersion="1"><![CDATA[...]]></brokerforgeDesign>`, strict: any other node fails). Serializer uses relaxed escaping for readability and replaces every `]]>` with `]]>` (only possible inside JSON strings) — Task 1 concern 4 — cost if wrong: small.
Task 2: implementer DONE 5c1c76f; review: spec ❌ (doc `]]>` rule unreadable), Important: doc; re-graded Minor 1 (duplicate keys overwrite silently) to Important under principle 1.
Task 2: minor (deferred): auth secret scan not recursive (auth.oauth.clientSecret gets "unknown key"); XDocument DTD processing not prohibited; FormatVersion2Fails edits text with Replace; redundant \r\n replace.
Task 2: fix round 1/5 (2 addressed, 0 open — doc ]]> rule; duplicate keys; commits 5c1c76f..b5bfb8d)
Task 2: complete (commits f84e299..b5bfb8d, review clean)
Ruling: Task 3 keeps two problem lists (Problems for schemas path, DescriptorProblems for descriptors path) instead of one — preserves message order as the brief's 'messages identical' requires — cost if wrong: trivial.
Ruling: live acceptance scripts are run once in Task 7 after deploying the branch, not after every task — the deployed dev host runs main's image, so per-task runs would test nothing new — cost if wrong: a regression found later in Task 7.
Task 3: minor (deferred): FileConfigInput reads files for instances later dropped for bad Auth:Mode; bind runs before validation; O(instances×files) text scan.
Task 3: complete (commits b5bfb8d..20cb78a, review clean)
Task 4: implementer DONE_WITH_CONCERNS 498f545 (550 pass, +17). Added HLD D-34.
Ruling: OAuth confidential client secret in a design = instance secret named `clientSecret` listed in DesignAuth.SecretNames; Merge sets the OAuth resource's ClientSecretRef to `store:clientSecret` when OAuth is set, not UsesConnectApp — carried into Task 5 — cost if wrong: rename.
Ruling: design version logging on each call goes into Task 6 (host logs `design v<N>` with operation version when the instance came from a design) — spec 6 approval paragraph — cost if wrong: none.
Task 4: minor (deferred to plan 4): connect page for an environment whose designs fail to load returns bare 500, not a message page.
Task 4: review Needs fixes — Important: designs mapping same table overwrite; re-graded Important: connect route loads any GUID env (unbounded cache); Review Focus 1 test missing (cross-env per-user credential).
Task 4: minor (deferred → Task 6): failed builds not negatively cached; log lines lack environmentId; TokenEndpointClient uses plain resolver overload (fix with clientSecret in Task 5).
Task 4: minor (deferred): a bad design fails the env's file instances too (document in D-34); xUnit1013 warning on ConnectTests.TestSignIn (pre-existing).
Task 4: fix round 1/5 (3 addressed, 0 open — table clash; unknown env connect 404; cross-env credential test; commits 498f545..5022c44)
Task 4: minor (deferred): Guid.Parse in ConnectEndpoints.Find on malformed callback state → 500 not 404; TrustOptions via RequestServices locator.
Task 4: complete (commits 20cb78a..5022c44, review clean)
Task 5: review Needs fixes — Important: connect callback bare 500 on missing/unreadable clientSecret; re-graded Important: token-call assertion counts wrong client, env id logged before validation. StoreSecretTests.cs folded into EnvironmentConfigTests (accepted).
Task 5: fix round 1/5 (3 addressed + 2 extras, 0 open; commits 932f663..9bb12e2)
Task 5: minor (deferred): 503 unreadable-clientSecret branch on connect callback untested.
Task 5: complete (commits 5022c44..9bb12e2, review clean)
Task 6: implementer DONE fb20989 (Contract 208). Controller verified live (read-only, dev): both OData queries (webresourceset contains/endswith; environmentvariabledefinitions $filter + $expand values) return HTTP 200.
Task 6: review Needs fixes — Important: non-transient reader failures (4xx, off-org link) get warm grace; minors 1–2 folded into the fix round.
Task 6: minor (deferred): filter also matches unrelated `/brokerforge/` resources (fails env loudly; note in findings); GetProperty("value") relies on reader guarantee.
Task 6: fix round 1/5 (3 addressed, 0 open; commits fb20989..cc4c6a8)
Task 6: complete (commits 9bb12e2..cc4c6a8, review clean)
Task 7: implementer DONE_WITH_CONCERNS 4002966 — live: needs-key, 45 rows, missing-URL after TTL, acceptance both pass, log scan clean. Deviations: set-instance-secret runs in-container (no new RBAC), ext_AdjustStockItem rename, env var values read via definition, bicep settings moved.
Ruling: Review Focus 3 is not met — a failed design read/validation fails the environment's file-configured instances too (Task 4 deferred minor + Task 7 concern 2). Goes to the final review fix wave as Important: on a design failure the env store serves file instances; any request not resolved by a file instance gets the design failure (never "unknown table") — cost if wrong: Orders down in dev whenever Dataverse read fails.
Task 7: minor (deferred): one 75 s end-to-end call after TTL, delay before host received it (Dataverse/plug-in side), not reproduced.
Task 7: minor (deferred): set-instance-secret.sh trusts exit status through az exec (grep marker instead); confirm store messages never include value; --from can copy any readable kv/env into a secret (note in script header); host role reads all web resources/env var definitions (note in HLD); orgUrl param default ''; setup_dataverse argv IndexError.
Task 7: note: dev org URL default (pre-existing in dv.sh, generate.sh, setup_dataverse.py, findings) is in the public repo — predates this plan; raise with Simon.
Task 7: complete (commits cc4c6a8..4002966, review clean)
Final review: C1 design ServiceAccount mode + arbitrary scope/base URL exfiltrates host MI token; I1 Review Focus 3 not met; I2 `mode`/`asyncContext` overridable by env var. With fixes.
Ruling: C1 fix — designs may use ServiceAccount (and Impersonate) only with a scope listed for that environment in host settings (`Trust:AllowedCallers[i]:DesignScopes`); Azure management, Key Vault, Storage, Dataverse (*.dynamics.com, *.crm*.dynamics.com) and Graph audiences always refused; default empty list = no ServiceAccount/Impersonate designs — keeps the host operator as the only one who grants the host's identity — cost if wrong: an admin must add a scope to host settings once per environment.
Ruling: I2 — remove mode and asyncContext from AuthOverrides; overrides limited to shape fields, scope still subject to C1 allowlist — spec Approval rule — cost if wrong: none.
Final: fix wave commits 4002966..b55001c (C1, I1, I2, M1, M2, 503 test, script, docs); suite green incl. Auth 91.
Final: Ruling: second, narrowly scoped fix dispatch for residual C1 bypasses (Enum.TryParse mode spellings skip DesignScopeRules; design-supplied tokenUrl/authorizeUrl for usesConnectApp receives the connect app's client assertion) — a known Critical credential leak must not remain; skill normally allows one fix wave — cost if wrong: one extra review round.
Final: second fix 0e44a53 re-reviewed: mode bypass, connect-app endpoints, malformed scopes ADDRESSED. New Important: design SSRF (base URL/token URL to loopback, link-local IMDS, private ranges; static headers like Metadata:true).
Ruling: fix SSRF now (third scoped dispatch) — egress guard via SocketsHttpHandler.ConnectCallback for design instances (refuse loopback, link-local, private, CGNAT, IPv6 local, mapped forms), host allow list `Config:Designs:AllowedPrivateHosts` (dev lists the internal mock FQDN), merge-time check for literal IPs/scheme, reserve `Metadata` and `X-IDENTITY-HEADER` headers for all instances, loopback http for OAuth only in tests — design authors are less trusted than the operator — cost if wrong: an allow-list entry per private source.
Final: minor (deferred): Entra endpoint hard-codes public cloud (sovereign clouds fail at first token call) — document in D-34.
Final: SSRF fix 824924b re-reviewed: ADDRESSED, no new Critical/Important.
Final: minor (deferred): NAT64/IPv4-compatible/6to4 forms not blocked (unreachable on ACA); design Static/SSO HeaderName could be `Metadata` (guard already blocks IMDS) — reserve for auth header names too.
Final: live after redeploy (824924b): Orders + Inventory acceptance pass; ext_stockitems via WarehouseAPI design 200.
