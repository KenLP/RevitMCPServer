# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.8.24] — 2026-08-09: `spatial_get_paths_of_travel` — read Revit's own Path of Travel elements

Requested by AutomatedSpatialQC (`revit_addin/HANDOFF_get_paths_of_travel.md`, branch
`feat/pot-parity`): the READ side of `bim-nav benchmark-pot` (SPEC_pot-parity.md Block C). A client
who trusts Revit's native `Analyze > Path of Travel` drops a few `PathOfTravel` elements into the
model by hand; the consumer reads them back, reruns the same (from, to) pair through its own
occupancy-grid router, and prints both distances side by side — a credibility benchmark, not a
verdict. Ships independently of the WRITE side (`spatial_create_path_of_travel`, still pending).

### Added

- **`spatial_get_paths_of_travel`** (HTTP-only spatial-QC pack; not an MCP tool) — every
  `PathOfTravel` element with `levelName`, `from`/`to` (route-curve endpoints, world metres,
  Revit frame), `lengthMeters`, `timeSeconds`, all read verbatim from the element.

### Notes — the handoff's two flagged unknowns, resolved against RevitAPI.dll (2027 metadata)

- Route geometry: `PathOfTravel.GetCurves()` (the sketch's `GetCurve()`/`NumberOfCurveLoops` does
  not exist). `from`/`to` = first curve's start / last curve's end; elements whose route failed to
  compute (`GetCurves()` empty) are **skipped** — a 0-length row would read as a real measurement.
- Parameters: there is **no** `PATH_OF_TRAVEL_LENGTH` and no "Actual Length"/"Actual Time".
  Length is `CURVE_ELEM_LENGTH` (the UI "Length"), with a curve-length-sum fallback; time is
  `PATH_OF_TRAVEL_TIME` (internal unit seconds), emitted as `null` when absent — never a fake 0.
  Level comes from `PATH_OF_TRAVEL_LEVEL_NAME` (the UI "Level"), falling back to the owning view's
  `GenLevel`. `HANDOFF_create_path_of_travel.md` §2 asks for exactly these names when the WRITE
  side gets picked up.

## [0.8.23] — 2026-08-05: `configure_schedule` can filter on a numeric field

Reported by bim-orchestrator, which auto-creates the native Revit schedules a reviewer uses to
re-check a compliance run. Four of its five schedules worked; the one carrying a threshold —
`Width less than 900 mm` — produced a schedule listing **every** door.

### Fixed

- **`configure_schedule` filters accept a number** (`filters[].value` is now
  `string | number` on the bridge, was `string` only). A JSON number previously failed MCP input
  validation *above* the bridge, and the SDK returns that as plain text, so the caller got no
  `{ok,error}` envelope at all — just an unparseable result and a schedule left unconfigured.
- **A numeric filter now actually reaches Revit.** `ConfigureScheduleCommand` read the value with
  `GetValue<string>()` *outside* the per-filter `try` (a number threw and failed the whole
  command), then only ever called the `string` `ScheduleFilter` constructor — which Revit refuses
  on a Double/Integer field. The refusal was caught and demoted to a `warnings` entry, so the
  response stayed `ok:true` and the filter was **silently absent**. The value is now read as a raw
  `JsonNode` and offered to Revit as an ordered list of overloads (`double` → `int` → `string` for
  a JSON number; `string` → `double` → `int` for a JSON string), letting Revit's own validation
  pick the one that fits. Measured on R27 Snowdon: `Width < 2.9527559055118114` went from 100 rows
  (the unfiltered read cap over 149 doors) to **13**, identical for the number and the string form.

  The retry wraps `ScheduleDefinition.AddFilter`, **not** the `ScheduleFilter` constructor. The
  constructor happily builds a string filter for a Double field; Revit only refuses it on add —
  its message ends `Parameter name: filter`, naming `AddFilter`'s argument. A ctor-level ladder
  compiles, looks right, and never reaches its second rung (measured: it fixed the number form and
  left the string form failing exactly as before). Each rung also catches `Exception` rather than
  `ArgumentException`: Revit throws from `Autodesk.Revit.Exceptions`, whose `ArgumentException`
  does not derive from the BCL one, so a type-filtered ladder stops at rung one. A rung can only
  succeed by actually adding the filter, and the last refusal is rethrown verbatim — a genuinely
  invalid value still becomes the same `warnings` entry it does today (verified).

  The `int` overload is offered only when the value is integral: truncating 2.95 → 2 on an Integer
  field would yield a filter that applies cleanly and quietly means something else.

Two things that look like bugs and are not, now documented and covered by tests:

- The value is in Revit **internal units** and passes through untouched — callers convert
  (900 mm → 2.9527559055118114 ft) and an addin-side conversion would corrupt every filter.
- Numeric strings parse with **invariant culture**. Current-culture parsing on a de-DE machine
  reads `"2.9527559055118114"` as ~2.95×10¹⁶ — the filter would apply and match nothing.

Filters on text fields are unchanged: the ladder tries the `string` overload first, so
`Mark equals "S10"` still resolves as text and not as a number.

## [0.8.22] — 2026-08-03: resolve elements by `UniqueId`, never by a derived id

Follow-up to 0.8.21. Exposing `uniqueId` let us finally *verify* how callers were turning an
ACC `externalId` back into a Revit element — and the answer was: incorrectly.

### Measured

Against the live Revit 2027 add-in and the Autodesk Model Derivative API (project
"Ken - MCP Testing", models Architectural / Structural / HVAC):

- The trailing 8 hex characters of a `UniqueId`, parsed as hex, **are** the `ElementId`:
  27/27 across three models, 9/9 on a random host sample spanning 9 distinct episode GUIDs.
- The XOR heuristic circulating downstream (`hex[37..45] ^ hex[28..36]`) matched **0/27**.
  It is not a stale formula — it was never correct. Autodesk documents the layout as
  `EpisodeId(8-4-4-4-12) + "-" + hex(ElementId)`, with no XOR anywhere.
- ACC `externalId` is byte-identical to Revit `UniqueId` for the same element (verified on
  ids 619340 and 619404 against the running add-in).

### Added

- **`find_element_by_unique_id`** (MCP tool `revit_find_element_by_unique_id`) — resolves an
  element through `Document.GetElement(string)`, so no id derivation happens at all.
  - `linkId` searches exactly one `RevitLinkInstance`; `searchLinks=true` sweeps every loaded
    link when the host has no match. Returns `foundIn` ("host"/"link") plus link context, and
    transforms link bounding boxes into host coordinates.
  - Guards a sharper hazard than the bad formula: `ElementId` is numbered **per document**, so
    an id lifted from a linked model can silently resolve to a *different* real element in the
    host. (In the Snowdon sample the 5 ids tested happened to miss — chance, not safety: the
    host id range 593k–2.8M fully overlaps the HVAC and Structural ranges.)
- **`get_linked_elements` now returns `uniqueId`** per element. Without it, elements inside a
  link — where clash-driven tools do most of their work — had no cross-document identity at all.

### Changed

- Tool surface 89 → **90** (94 C# commands, 7 hidden). `revit_find_element_by_unique_id` joins
  the `core` profile.

### Fixed

- **CI published unusable release archives.** The `release` job kept its own hand-written copy
  list instead of calling `scripts/build-release.ps1`, and that list was never updated after the
  0.8.15 packaging fix. Because the job only runs on a tag, and no tag was cut between 0.8.19 and
  0.8.22, the drift sat undetected until v0.8.22 was tagged. The 64 KB per-version zips it
  produced were missing:
  - `RevitMCP.Core.dll` — the entire command kernel, so the add-in had no commands at all;
  - `dist/revitClient.js` and `dist/recipes.js` — both imported by `index.js`, so the MCP server
    died at startup with `ERR_MODULE_NOT_FOUND`;
  - the three WebView2 assemblies the AutoAudit panel loads.

  The job now runs `scripts/build-release.ps1`, which already gates on 26 required files *and*
  resolves every relative import in the emitted JS before zipping — so an incomplete bundle now
  fails the build instead of being published. The v0.8.22 assets were replaced with the verified
  1.08 MB bundle (SHA-256 `19C24DFF…3386`); the broken per-version zips were deleted. The same
  broken zips are still attached to **v0.8.19** alongside its good combined bundle.

## [0.8.21] — 2026-07-27: `get_element_info` exposes `uniqueId`

### Added

- **`get_element_info` now returns `uniqueId`** — the element's stable Revit `UniqueId`
  (45-char `<guid-36>-<8 hex>`). Purely additive: no field removed or renamed, envelope
  `{ok,data}` unchanged, input schema unchanged, tool/command count unchanged (gate stays green).
  - **Why:** downstream consumers that receive an ACC / BIM 360 Model Coordination `externalId`
    (which *is* the Revit UniqueId) previously had to reverse-engineer the `ElementId` with the
    XOR heuristic (`parseInt(uid[37:45],16) ^ parseInt(uid[28:36],16)`) and could not verify the
    result — a wrong guess silently targets a different element. Returning `UniqueId` lets a caller
    match the mover element exactly before any mutation. (Handoff: ClashDetection v1.2, 2026-07-27.)
  - Adds ~45 bytes/element to the response; nowhere near the 1 MB envelope ceiling.

## [0.8.20] — 2026-07-25: AutoAudit dockable panel lands on main (installer no longer wipes it)

### Added

- **AutoAudit DockablePane (WebView2) is now part of `main` and the installer.** The panel — a thin
  embedded browser onto the AutoAudit UI (`http://127.0.0.1:8601/ui/`, configurable via
  `revit-mcp-panel.json`) — previously lived only on the unmerged `feat/spatialqc-panel` branch, so
  every run of the v0.8.18/v0.8.19 one-shot installer (built from `main`) overwrote the deployed
  add-in with a panel-less DLL. That regression class is closed: the panel ships in the DLL the
  installer installs.
  - Ported selectively from the branch: `Panel/{AutoAuditPaneProvider, AutoAuditPanelView,
    PanelConfig, ShowAutoAuditPanelCommand}.cs` + registration in `App.cs` + WebView2 refs in the
    csproj — **without** taking the branch's stale `App.cs`/csproj (which predate the 0.8.17
    security hardening and the build-truth stamp; a straight merge would have reverted both).
  - Behaviours preserved from the live-verified branch build: panel registration in its own
    try/catch (a panel failure can never take down the MCP server), the visual-tree-before-init
    WebView2 fix, suspend/resume around document transitions (archi-lab WebView2 gotcha), and the
    AssemblyLoadContext resolver for the loose WebView2 assemblies.
  - New ribbon tab "AutoAudit" with a show-panel button; browser fallback when WebView2 is absent.
  - The SpatialQC pane (:8602) stays on the private branch — deliberately not ported (the handoff
    allows splitting it out).
- **Installer/bundle ship the WebView2 runtime pieces** (`Microsoft.Web.WebView2.Core.dll`,
  `Microsoft.Web.WebView2.Wpf.dll`, `WebView2Loader.dll`) per Revit version, and the artifact gate
  now fails the build if any is missing. `install.ps1` copies them; `uninstall.ps1` removes them.
  **`revit-mcp-panel.json` (the user's panel-URL config) is never written or removed** by either
  script.

Counts unchanged: 89 MCP tools, 91 C# commands (the panel is UI, not an MCP command).
Addresses `MultiAIagents-main/docs/handoff_addin_dockable_panel.md` (AU 2026 demo path).

---

## [0.8.19] — 2026-07-23: Installer configures Codex / Gemini / Cursor too

### Added

- **`install.ps1 -Client <list>`** configures MCP clients beyond Claude Desktop. Accepts one or
  more of `claude` (default), `gemini`, `cursor`, `codex` — e.g. `-Client codex,gemini` or
  `-Client claude,gemini,cursor,codex`. Each client's config is merged in place, backed up first,
  and every other server it already has is preserved:
  - `claude` -> `%APPDATA%\Claude\claude_desktop_config.json` (JSON `mcpServers`)
  - `gemini` -> `%USERPROFILE%\.gemini\settings.json` (JSON `mcpServers`)
  - `cursor` -> `%USERPROFILE%\.cursor\mcp.json` (JSON `mcpServers`)
  - `codex`  -> `%USERPROFILE%\.codex\config.toml` (**TOML** `[mcp_servers.NAME]` tables)
  The three JSON clients share one merge path; Codex gets a TOML writer that strips and re-appends
  only the `[mcp_servers.revit-*]` tables, leaving all other TOML content untouched.
- `-NoClaudeConfig` is kept as an alias of the new `-NoClientConfig`.

The server itself is unchanged and client-agnostic (stdio `node dist/index.js`); this only teaches
the installer where each client keeps its config. Cloud-only clients that can't run a local stdio
process (e.g. web ChatGPT) still can't reach the loopback add-in — use a local client (Codex CLI,
Claude, Cursor, Gemini CLI) on the same machine as Revit.

No functional change to the server: 89 MCP tools, 91 C# commands.

---

## [0.8.18] — 2026-07-19: One-shot installer for all three Revit versions

### Added

- **Single self-contained install bundle.** `RevitMCPServer-v<ver>.zip` now carries the add-in for
  **all three Revit versions** (`addin/2025`, `addin/2026`, `addin/2027`) plus one shared MCP server.
  `install.ps1` with no arguments:
  - **auto-detects** which Revit versions are installed (`Program Files\Autodesk\Revit <year>`) and
    deploys the matching add-in to each `%APPDATA%\Autodesk\Revit\Addins\<ver>`;
  - copies the MCP server to a **stable location** (`%LOCALAPPDATA%\RevitMCPServer`) and runs
    `npm install` there, so the extracted folder can be deleted afterwards;
  - **merges** a `revit-<ver>` entry per version into the Claude Desktop config — backing it up
    first and leaving every other MCP server entry untouched (JSON-parsed, not string-spliced).
  Flags: `-RevitVersions`, `-AllVersions`, `-NoClaudeConfig`, `-ClaudeConfigPath`,
  `-ServerInstallDir`, `-SkipNpm`. Missing Node.js is a warning, not a failure — the add-in works
  without it; only the Claude bridge needs it. `uninstall.ps1` mirrors all of this.
- `build-release.ps1` produces the combined bundle (replacing the per-version ZIPs) and the artifact
  gate now checks the per-version `addin/<ver>/` layout.

### Fixed

- **Installer/build scripts are now pure ASCII.** They contained em-dashes and box-drawing
  characters; Windows PowerShell 5.1 (the default on a clean Windows box) reads a UTF-8 `.ps1` as the
  ANSI code page, so an em-dash inside a string (`E2 80 94`) decoded to `â€"` whose trailing byte is a
  `"` in CP1252 — silently terminating the string and breaking the parse **on the end user's machine**,
  not just ours. All non-ASCII was transliterated to ASCII and both scripts re-verified with the PS
  parser.

No functional change to the server: 89 MCP tools, 91 C# commands. Addresses `project_review_findings`
"ready-to-run installer" for the MCP-only path (no ribbon/.bundle, which are Design-&-Make-marketplace
requirements we are not targeting).

---

## [0.8.17] — 2026-07-17: Security hardening — loopback clamp, unconditional auth, audit clean

### Changed (BREAKING for dev-only escape hatches)

- **`REVIT_MCP_AUTH=false` is removed on both sides.** The add-in always generates and
  requires the bearer token; the Node client always sends one when available and ignores the
  env var. Rationale: the listener is loopback-only, but an *unauthenticated* loopback port
  would still let any local process drive Revit. Verified before removal that nothing on the
  single deployment machine sets it — no `.env`, no system env, no Claude config does
  (Cad2BIM and bim-orchestrator *support* the variable in their clients but do not enable
  it; they are being notified to drop that branch).
- **`REVIT_MCP_HOST` is clamped to loopback** (`127.0.0.1`, `localhost`, `::1`). Any other
  value makes the Node client refuse to start with a clear error. The add-in's HttpListener
  prefix is hard-coded to `http://127.0.0.1:<port>/`, so a non-loopback host could never
  reach a real add-in — the only thing that setting could ever do is hand the bearer token
  and the full command stream to an arbitrary host over plaintext HTTP.

### Fixed

- **`npm audit --omit=dev` is clean: 5 → 0 vulnerabilities** (hono, fast-uri,
  express-rate-limit, ip-address, qs — 2 high / 3 moderate). All five were transitive
  dependencies of `@modelcontextprotocol/sdk`'s **HTTP transports**, which this stdio-only
  server never imports (`server/mcp.js` + `server/stdio.js` are the only SDK entry points),
  so reachability was effectively nil — fixed for hygiene and marketplace review, within
  semver ranges, no code change.

Counts unchanged: 89 MCP tools, 91 C# commands. Addresses `project_review_findings_2026-07-16.md`
P1 (dependency audit + runtime security escape hatches, resolved as B2: remove rather than
document).

---

## [0.8.16] — 2026-07-17: Release package actually runs; MCP submission docs land

### Fixed

- **The release package shipped an add-in and an MCP server that could not start.**
  Three separate omissions, all in the packaging path only:
  - `RevitMCPAddin.dll` was packaged **without `RevitMCP.Core.dll`** — the class-lib that
    carries the command kernel the add-in type-loads against. Revit would throw
    `FileNotFoundException` on start-up. The csproj's dev-deploy target *always* copied Core,
    so every local deploy worked and the bug lived only in the artifact users download —
    which is why it survived so many versions.
  - Only `dist/index.js` was packaged, but it imports `./revitClient.js` and `./recipes.js`
    at run time, so the packaged server died on first import.
  - `uninstall.ps1` left `RevitMCP.Core.dll` orphaned in the Addins folder.
- **`install.ps1`** now copies `RevitMCP.Core.dll` and fails fast with a clear message if it
  is not next to the add-in dll. **`uninstall.ps1`** removes `RevitMCP.Core.dll`/`.pdb` and
  reports the two things it deliberately leaves behind (metadata-only logs, the MCP server
  folder) so they can be deleted in one step.

### Added

- **Artifact completeness gate in `build-release.ps1`.** Before zipping, it asserts the 10
  required files are present and statically resolves every relative import in the packaged
  JS. A missing runtime file now fails the build instead of shipping silently. Verified on
  all three packages (R2025/R2026/R2027), plus an end-to-end check: the ZIP was extracted,
  prod deps installed, and the packaged server booted clean.
- **MCP Publisher submission artifacts** — `mcp-manifest.json` (89 tools, cross-checked
  against `index.ts`) and the filled Publisher Declaration content.

### Changed

- **Publisher Declaration corrected to match the code.** The earlier draft overclaimed:
  it said the server reads only the open Revit model (`revit_load_family` reads a `.rfa`
  from any local path, and PNG/PDF/CSV exports write to disk); it said uninstall removes the
  add-in *and* MCP server (it does not remove the server folder or logs); it implied auth is
  unconditional (token auth is the default but a local env var can disable it). Data
  retention now states plainly that diagnostic logs are append-only daily files with no
  automatic rotation, kept until the user deletes them. Also records that the add-in's HTTP
  listener is hard-bound to `http://127.0.0.1:<port>/` and is therefore unreachable from the
  network under any configuration.
- `mcp-manifest.json` declares `mcp_spec_version: 2025-11-25` — the SDK's
  `LATEST_PROTOCOL_VERSION` and the value in Autodesk's own example (was `2025-06-18`).

Counts unchanged: 89 MCP tools, 91 C# commands. Addresses `project_review_findings_2026-07-16.md`
P0-1 and P0-2.

---

## [0.8.15] — 2026-07-15: `find_elements` view scoping (`view_id`) — closes the last fork gap

### Added

- **`find_elements` accepts `view_id`** — scopes the query to elements visible in that view
  (must be a non-template `View`; a bad id returns a clear `invalid_parameter` error instead
  of a raw collector exception). Ported from the `feat/extract-revit-mcp-core` fork; with this,
  `main` is a strict superset of that branch and downstream submodules can re-pin to `main`.
  Exposed on the MCP tool surface (`revit_find_elements.view_id`).

### Fixed

- **`find_elements` docstring caught up with reality** — it still described the pre-pagination,
  instance-only-parameter behaviour, which misled an external audit
  (`HANDOFF_revitmcp-find-elements-fix.md`) into re-reporting bugs that `main` had already
  fixed: offset pagination landed in v0.8.6 (P2-C) and instance→type parameter fallback in
  v0.8.11. Verified against fresh `origin/main`: both fixes present; only `view_id` was missing.

Counts unchanged: 89 MCP tools, 91 C# commands.

---

## [0.8.14] — 2026-07-13: Build-truth `/health`, hosted placement actually lands

### Added

- **`/health` now reports build-truth and stays auth-exempt.** `version` comes from the
  compiled `AssemblyInformationalVersion` — single-sourced from the csproj `<Version>`, no
  hand-typed literal — plus `gitCommit`, `gitBranch`, `gitState`, `buildTimestampUtc`,
  `commandCount`, and a `capabilityHash` over the live registry. A consumer can verify the
  actual capability without a token, and a build can no longer advertise a version that
  outranks the command surface it really ships. The add-in also logs its build line on
  start-up so the Revit journal shows exactly which dll loaded.

### Fixed

- **Hosted `revit_place_family_instance` is committed for real.** The [0.8.11] entry below
  documented it, but the code was never committed — the RevitMCP.Core class-lib extraction
  carried the non-hosted version, so builds through 0.8.13 placed doors/windows with
  `Host = -1` and no wall cut. Restored the hosted overload (host-phase copy,
  `flipFacing`/`flipHand`) and added a wall-only guard: a `hostId` that is not a `Wall` falls
  back to non-hosted placement with a `hostWarning` instead of throwing.
- **Two-point tool schemas no longer collapse the second point to an unusable `{}`.**
  `revit_create_wall`, `revit_create_beam`, `revit_create_grid`, and the mirror-plane tool
  referenced the shared `xyz` Zod object twice, so `zod-to-json-schema` emitted the second
  point as a `$ref` the MCP bridge flattened away — a direct `create_wall` call rejected
  `end` with *"expected object, received string"*. Giving the second point a distinct
  instance (`.describe(...)`) inlines its `{x,y,z}` schema.

Counts unchanged: 89 MCP tools, 91 C# commands.
Addresses cad2bim gap: `HANDOFF_revitmcp_hosted_family_instance.md`.

---

## [0.8.13] — 2026-07-03: Spatial-QC command pack (HTTP-only, `spatial_*`)

### Added

- **Four pure-geometry commands forward-ported from AutomatedSpatialQC's add-in fork** so
  `spatial-qc check-revit` runs against the live `main`-based add-in again (it was aborting at
  `get_room_boundary` because only `get_doors` had been ported earlier):
  - **`spatial_get_room_boundary`** — room boundary loops (outer ring + holes) at the finish face as
    world-XY polylines in metres (net clear area, matches `IfcSpace`).
  - **`spatial_clearance_envelope`** — volumetric MEP-aware clear-height check over a room footprint,
    boolean-intersecting every overhead element in host **and every linked RVT**, naming each
    obstruction with the clear height it leaves.
  - **`spatial_clearance_envelope_batch`** — the same check for many rooms in one call, extracting
    candidate geometry once over the union of footprints.
  - **`spatial_raycast_headroom`** — vertical headroom raycast returning the lowest overhead soffit
    per `(x,y)` point.

### Namespacing decision

- The four are **registered in C# (HTTP-callable via `/mcp`) but NOT exposed as MCP tools** — they are
  consumed programmatically by the spatial-qc Python client, not by LLM tool routing, so surfacing
  them would only dilute the tool list. Prefixed `spatial_` to keep them clearly apart from the
  curated command surface and avoid any future name collision.
- **`get_doors` was deliberately left unprefixed.** It already shipped (v0.8.12) as the general-purpose
  MCP tool `revit_get_doors` (ADA/egress door-swing, useful to any consumer); renaming it would break
  that tool name and re-churn docs for no benefit. It and the spatial pack are different layers.
- Added `P.LongOrNull` helper (used by `spatial_get_room_boundary`).

Counts: **91 C# commands** registered (86 exposed + 5 hidden: `create_spot_elevation` + the 4 spatial
commands), still **89 MCP tools** (surface unchanged).

---

## [0.8.11] — 2026-06-27: Hosted family-instance placement (doors/windows)

> **Correction (0.8.14):** the code described below was *not* actually committed at 0.8.11 —
> it was lost in the RevitMCP.Core class-lib extraction and only truly landed in [0.8.14].
> Builds 0.8.11–0.8.13 still placed non-hosted (`Host = -1`).

### Changed

- **`revit_place_family_instance`** — adds `hostId`, `flipFacing`, and `flipHand` parameters
  (all optional, fully backward-compatible). When `hostId` is supplied the handler uses the
  Revit API hosted overload `NewFamilyInstance(XYZ, FamilySymbol, Element host, Level, StructuralType)`
  so the door/window is wall-hosted, Revit auto-cuts the opening, and `Host Id ≠ -1`.
  Phase Created is copied from the host wall to avoid the *"infilling wall"* warning.
  Without `hostId` behaviour is unchanged (non-hosted free-standing placement).

Counts: 88 MCP tools (unchanged — no new tool, existing tool extended).
Addresses cad2bim gap: `revit_mcp_hosted_instance_gap.md`.

---

## [0.8.12] — 2026-06-28: get_doors — door swing geometry

### Added

- **`revit_get_doors`** — all placed doors with nominal width (m), plan location (world XY, m),
  level, and **swing geometry**: `facingX/Y` (FacingOrientation — the normal / pull-swing side),
  `handX/Y` (HandOrientation — along the wall), and `facingFlipped/handFlipped`. Orientation is
  geometry, not a parameter, so `find_elements` cannot return it — this command exposes door swing
  for ADA/egress maneuvering-clearance and door-swing checks (consumer: spatial-qc).

  Ported from the `feat/extract-revit-mcp-core` line (commit `0668cf9`) onto `main` — that branch
  was 23 commits behind `main` and building it would have regressed the live add-in, so the command
  was brought forward instead. Read-only; additive; `find_elements`/`list_elements` unchanged.

---

## [0.8.11] — 2026-06-27: find_elements projects type parameters

### Fixed

- **`find_elements` now resolves TYPE parameters, not just instance parameters.** Both the
  `fields` projection and the `filters` matcher used `Element.LookupParameter`, which is
  instance-only, so type-level BIM parameters (Fire Rating, door Width, assembly codes,
  materials…) came back empty and filters on them never matched. They now fall back to the
  element's Type when the instance lookup misses, cached per `(typeId, name)` so N elements
  sharing a type cost one type lookup. Response `fields` shape is unchanged — type values
  simply start appearing. (Reported via a downstream QA rig; a stale copy of this command in
  another repo also reported the pre-P2-C "offset ignored" bug — that one was already fixed
  here in 0.8.6/P2-C.)

---

## [0.8.10] — 2026-06-26: Workflow recipe — clash review (P4)

### Added

- **`revit_recipe_clash_review`** (read-only) — runs a coordination clash sweep across many
  element-set pairs and returns a consolidated, prioritized report (hard clashes first, then
  clearance violations by smallest gap, counted per pair). Each pair is a `check_clearance`
  input, so **linked RVTs are supported** (`setB.source='link'` + `linkId` from
  `get_linked_files`) — the real host-MEP × linked-Arch/Struct coordination case. Composes
  `check_clearance`; a pair that errors is recorded and the sweep continues.

Counts: 88 MCP tools (86 C# commands, 1 hidden, 2 Node-only recipes). Synthesis unit-tested
(Vitest 20/20). **Verified live e2e on Revit 2027** against a federated model: HVAC-link ducts ×
Structural-link framing returned 20 hard clashes (link × link), correctly aggregated and
prioritized per pair.

---

## [0.8.9] — 2026-06-25: Workflow recipes — pilot (P4)

### Added

- **Workflow recipe layer** (`src/McpServer/src/recipes.ts`) — the P4 orchestration layer.
  Recipes live in the Node bridge ABOVE the deterministic C# kernel; they compose verified
  atomic commands into goal-oriented workflows (preconditions, synthesis, and — for writes —
  dry-run/verification). They never touch the Revit API directly.
- **`revit_recipe_model_health_triage`** (read-only pilot) — runs a model-health scan and
  returns a **prioritized, actionable triage list** (each issue + severity + recommended fix),
  instead of raw metrics. Composes `get_model_health`. New `recipes` tool profile.
- `check-version.mjs` now accounts for Node-only `revit_recipe_*` tools (excluded from the
  C#-parity invariant): `server.tool == registered − hidden + 1 batch + recipes`.

Counts: 87 MCP tools (86 C# commands, 1 hidden, 1 Node-only recipe). Synthesis unit-tested
(Vitest); underlying `get_model_health` already verified live.

---

## [0.8.8] — 2026-06-25: Family management + detailing (P3 packs 2 & 3)

### Added

- **`revit_load_family`** — load a family (`.rfa`) from disk into the project with an
  overwrite policy (`IFamilyLoadOptions`). Returns family id, category, and its types.
- **`revit_duplicate_family_type`** — duplicate a FamilySymbol under a new name (set
  parameters afterwards with `set_parameter` on the returned `typeId`).
- **`revit_create_detail_line`** — view-specific detail line in a 2D view; endpoints
  projected onto the view plane (rejects 3D / sheet / schedule views).
- **`revit_create_filled_region`** — filled region from a closed boundary in a 2D view;
  points projected onto the view plane, loop closed automatically, default FilledRegionType.

Counts: 82 → 86 MCP tools (86 C# commands registered, 1 hidden). Compiles for R2025/26/27;
C# 132/132; check-version green. **Verified live on Revit 2027** (24/24 smoke incl. family &
detailing): `load_family` loaded a real .rfa inside the dispatcher transaction; detail-line
and filled-region view-plane projection confirmed; `duplicate_family_type` dry-run confirmed.

---

## [0.8.7] — 2026-06-25: Schedule data reading (P3 pack 1)

### Added

- **`revit_get_schedule_data`** — read the rendered cell text of a ViewSchedule
  (calculated fields, units, and formatting applied — exactly what the user sees).
  Uses `ViewSchedule.GetTableData()` + `GetCellText(SectionType.Body, …)`. Paginated by
  row (`offset`/`limit`; returns `totalRows`, `totalColumns`, `hasMore`, `nextOffset`).
  The first row is normally the column headers. Complements `create_schedule` /
  `configure_schedule` (which author schedules and export CSV) by returning the data
  inline to the agent. Inspection profile.

---

## [0.8.6] — 2026-06-25: Pagination for large element lists (P2-C)

### Added

- **Pagination** for `revit_list_elements` and `revit_find_elements`: new `offset` param,
  and the response now carries `total` (all matches — for `find_elements`, after filters),
  `offset`, `limit`, `hasMore`, and `nextOffset`. Page through arbitrarily large sets by
  passing `offset = nextOffset` — the previous 5000-element ceiling no longer caps total
  reach (per-page `limit` stays ≤ 5000 so each response remains token-bounded).
- `truncated` is kept as an alias of `hasMore` for backward compatibility; `offset`
  defaults to 0, so existing callers are unaffected and simply gain the new fields.

### Changed

- **`create_spot_elevation` re-hidden** from the MCP surface. Live testing on Revit 2027
  showed the `ReferenceIntersector` raycast returns no face hit for floors even at the
  bbox centre (`doc.Regenerate()` on the temporary 3D view did not help), and the earlier
  solid-face approach failed with "Spot Dimension does not lie on its reference". The C#
  command stays registered (HTTP-callable) with a 2D-view guard for future work, but is off
  the tool surface until a reliable face-reference approach lands. `create_aligned_dimension`
  remains live and verified (grid+grid dimension).

---

## [0.8.5] — 2026-06-24: Un-hide create_aligned_dimension + create_spot_elevation

### Fixed

- **`create_aligned_dimension`**: Grid references now use `new Reference(grid)` (element
  reference) instead of the grid curve's geometry reference, which does not resolve in
  `NewDimension`. Wall centreline / core now uses the undocumented `:-9999:` stable
  representation (index 1 = overall centreline, 2 = core exterior, 3 = core interior,
  4 = core centre), confirmed working on Revit 2027. `GetSideFaces` kept as a fallback
  for explicit exterior/interior face requests.
- **`create_spot_elevation`**: Replaced manual solid-face iteration + user-supplied Z
  (caused "Spot Dimension does not lie on its reference") with `ReferenceIntersector`
  downward raycast on a temporary isometric 3D view. The hit gives both the face reference
  and a point guaranteed to lie on it. Temporary view is deleted after placement.

### Changed

- Both tools are now **exposed on the MCP surface** (`revit_create_aligned_dimension`,
  `revit_create_spot_elevation`) and added to the `documentation` profile. Total MCP
  tools: **80 → 82** (81 C# commands + 1 batch; 0 hidden).
- Version bumped: 0.8.4 → 0.8.5.

## [0.8.4] — 2026-06-23: Truth gate + observability & limits

### Fixed

- **Version drift**: `package.json` / `index.ts` / `McpHttpServer.cs` `/health` all
  lagged at `0.8.0` while docs claimed `0.8.3`; README health examples still showed
  `0.7.0`. All version strings now converge (single value, gated by CI).
- **Tool-count drift**: docs claimed **74** MCP tools while the code exposed **80**
  (79 commands + 1 batch; 81 C# commands registered, 2 hidden). README/COMMANDS/
  API_COVERAGE corrected; `revit_override_element_graphics` was missing from the
  README tool list.

### Added (P0 — truth gate)

- **`scripts/check-version.mjs`** now also verifies the tool/command inventory:
  counts `server.tool(...)` (TS) and `Register(new ...)` (C#), enforces the invariant
  *exposed commands − hidden + 1 batch = MCP tools*, and fails CI if the README
  headline counts disagree. Counts can no longer silently drift.

### Added (P1 — observability & limits)

- **Request correlation ID** on every HTTP request (`X-Request-Id`, generated if the
  client doesn't supply one) — returned as a response header and included in logs.
- **Structured request log** (one JSON line per request): timestamp, requestId,
  method, path, command, ok, errorCode, HTTP status, durationMs. Written under
  `%LOCALAPPDATA%\RevitMCP\logs\`.
- **Limits / backpressure**: max request body size, max batch steps, and a cap on
  concurrent in-flight requests (returns `overloaded` → HTTP 503 with retry hint).
- **`GET /stats`** endpoint: total/success/failed request counts, in-flight count,
  average and peak duration.

### Added (P1.5 — live-Revit smoke suite)

- **`scripts/smoke-test.ps1`** — drives the running addin over HTTP and asserts
  end-to-end behaviour unit tests can't (real Revit API). Covers connectivity, the
  read surface, observability, dry-run vs real writes (self-cleaning create→delete),
  batch, and the new limits. Supports `-Snapshot`/`-Golden` fingerprint compare against
  a fixed fixture `.rvt`. See [`docs/SMOKE_TESTING.md`](docs/SMOKE_TESTING.md).
  Verified live on Revit 2027 (18/18 checks; golden compare 23/23).

### Added (P2-A — tool profiles)

- **`REVIT_MCP_PROFILE`** env var — expose only selected tool groups instead of all 80,
  cutting token cost and tool-selection errors. Groups: `core` (always on), `inspection`,
  `model-health`, `coordination`, `architecture`, `documentation`, `editing`, `view`.
  Unset = all tools (default, backward compatible). Implemented as a runtime gate over
  `server.tool` (no change to the registration call sites). Verified: `documentation`
  exposes 20 tools, hides 60.

---

## [0.8.3] — 2026-06-22: Model health — worksets, imports/links, warning ratio

### Added

- **`revit_get_worksets`** — list user worksets with per-workset element counts.
  Flags empty worksets (no instances) and the un-renamed default `"Workset1"`.
  Returns `{ isWorkshared, count, emptyCount, worksets: [{id, name, elementCount,
  isEmpty, isOpen, isEditable, isVisibleByDefault, owner, isDefaultName}] }`.
  Non-workshared models return `isWorkshared=false`.
  Tested live on Revit 2027 (4 worksets, "Workset1" correctly flagged).

### Changed

- **`revit_get_model_health`** enhanced:
  - New **`imports`** section: imported vs linked CAD (with "in views" subcount),
    imported PDFs + raster images (`imagesAndPdfs`), RVT link instances/types,
    point clouds. Imported (non-linked) CAD is flagged (any > 0, warning); imported
    images/PDFs are flagged as info (any > 0).
  - **`warnings.perThousandElements`** — warnings-per-1000-elements ratio, reported
    for context (no published industry standard, so not scored).
  - **`file.worksets` / `file.emptyWorksets`** — workset summary; empty worksets flagged.
  - **`file.isModelInCloud`** — makes it explicit when file size is `null` because the
    model is cloud-hosted (the Revit API exposes no on-disk size for cloud models).
  - Thresholds aligned to published guidance: warnings high **300** / critical 1000;
    file size flag at **~500 MB** (recommended max 400-500); any imported CAD flagged.
  - Per-family file sizes are intentionally **not** measured (no Revit API for a loaded
    family's size; would require EditFamily+save per family) — noted in `notes`.

---

## [0.8.2] — 2026-06-22: Model health report

### Added

- **`revit_get_model_health`** — one-shot, read-only model quality report. Aggregates
  the metrics a BIM manager checks when judging a model, in a single call:
  - **Warnings**: total, error count, and top-N groups by description.
  - **File**: size (MB), worksharing status, workset count. Size is `null` for cloud
    models (`IsModelInCloud`) — reported in `notes`.
  - **Elements**: total count, distinct categories, top-N categories by instance count.
  - **Families**: loadable vs in-place, imported vs linked CAD instances, raster images.
  - **Groups**: model/detail group instances, single-instance group types.
  - **Views**: total views, sheets, placeable views, views not placed on any sheet.
  - **Complexity**: levels, grids, design options, reference planes (+ unnamed).
  - **Purgeable**: `Document.GetUnusedElements` count — only when `deep=true`
    (single-pass estimate; skipped by default as it is slow on large models).
  - **Scorecard**: letter grade (A–F), 0–100 score, and a list of flagged issues
    with severity. Thresholds are tunable constants in `GetModelHealthCommand`.

  Params: `deep` (bool, default false), `topN` (int, default 10).
  Tested live on Revit 2027 (Snowdon Towers sample): grade A/95, 90 warnings,
  42,901 elements, 633 purgeable.

---

## [0.8.1] — 2026-06-21: Annotation — tagging

### Added

- **`revit_tag_all_in_view`** — tag all untagged elements of a category in a view
  (mirrors Revit's "Tag All Not Tagged"). Accepts `category` (display name, e.g. `"Doors"`),
  optional `viewId`, `leader` (bool), `skipTagged` (bool, default true).
  Returns `{ tagged, skipped, failed, tags: [{tagId, elementId}] }`.
  Tested live on Revit 2026 (tagged 3 Doors in L1 - Architectural).
- **`revit_get_tags_in_view`** — list all `IndependentTag` elements in a view.
  Optional `category` filter. Returns `{ viewId, count, tags: [{tagId, elementId,
  category, hasLeader, tagText, location}] }`.
  Tested live: correctly returned 3 Door tags with tagText `"100"`, `"101"`, `"102"`.

### Notes

- `create_aligned_dimension` and `create_spot_elevation` are implemented in C# but
  hidden from the MCP tool surface pending Revit API reference fixes. They remain
  callable via direct HTTP if needed. Dimension works wall-to-wall; grid references
  in mixed `ReferenceArray` are not yet resolved.

---

## [0.8.0] — 2026-06-19: Type change, view templates, parameter copy, schedule/PDF/level, room containment

### Added

- **`revit_change_element_type`** — swap the type of any element (wall type, floor
  type, family symbol) using `Element.ChangeTypeId`. Validates against the element's
  own allowed types; returns old and new type info. Error code `wrong_element_type` → 400.
- **`revit_apply_view_template`** — apply or remove a view template from a view.
  Accepts `templateId` (ElementId) or `templateName` (case-insensitive lookup). Pass
  `templateId: -1` to remove the current template. Use `revit_list_view_templates` to
  discover available templates.
- **`revit_copy_parameters`** — copy parameter values from a source element to N target
  elements in one call. Matches by name and StorageType; only writable, non-None
  parameters are copied. Returns per-target success/failure detail.
- **`revit_configure_schedule`** — add filters and sort/group fields to an existing
  ViewSchedule; supports `clearFilters` / `clearSortFields` to reset first. Optional
  `exportCsv: true` exports the schedule and returns CSV content in the response.
  Uses `ScheduleField.FieldId` for phase-aware resolution; fields are added as hidden
  columns automatically when first referenced.
- **`revit_set_level_elevation`** — change the elevation of a Level element. Supports
  `"meters"`, `"feet"`, `"mm"`, and `"internal"` units. Returns old and new elevation
  in both m and ft.
- **`revit_export_view_pdf`** — export any view or sheet to PDF on disk via
  `Document.Export(folder, viewIds, PDFExportOptions)`. Accepts `outputFolder`,
  `fileName`, `rasterQuality` (Low / Medium / High / Presentation), `colorMode`
  (Color / Grayscale / BlackLine). Returns output path and file size.
- **`revit_get_element_rooms`** — get room containment for one or more family instances
  in a single batch call. Uses `FamilyInstance.get_Room(Phase)` / `get_FromRoom(Phase)`
  / `get_ToRoom(Phase)` — phase-dependent and authoritative, not centroid-in-bbox.
  `fromRoom` + `toRoom` for boundary connectors (Doors, Windows); `room` for
  point-located elements (Furniture, Fixtures, lighting, plumbing, …). Each room is
  `{ id, name, number }` or null. Phase resolved from the element's `PHASE_CREATED`
  parameter. Verified live on Revit 2027 Snowdon Towers Architectural.
- **`RevitMCP.Core` class library** extracted from `RevitMCPAddin` — all
  `IRevitCommand` implementations now live in a separate classlib for cleaner project
  boundaries and faster incremental builds.
- `wrong_element_type` added to the 400 group in `StatusForResult()`.

---

## [0.7.0] — 2026-06-11: Linked-file clash detection, view image export, R2025

### Added

- **`revit_get_linked_elements`** — read elements from inside a linked RVT file.
  Bounding boxes are automatically transformed to host-model coordinates.
  Accepts `linkId` (from `revit_get_linked_files`), optional `category`, optional `limit`.
- **`revit_check_clearance`** — detect hard clashes or clearance violations between two
  element sets. Supports host-vs-host (uses Revit's native `ElementIntersectsElementFilter`
  for exact solid-based detection) and cross-linked-file checks (AABB with clearance inflation).
  Parameters: `setA`, `setB` (each specifying `source: host|link`, optional `categories`),
  `clearanceMm` (0 = hard clash), `maxResults`.
- **`revit_get_view_image`** — export any Revit view (or the active view) to PNG and return
  it as an MCP `Image` content block. Accepts optional `viewId` and `dpi` (72/150/300).
- **Revit 2025 support** — added Nice3point reference assemblies for R2025 (`net8.0-windows`,
  port 7890). CI test matrix and release artifacts now include R2025 alongside R2026/R2027.
  Build/deploy works without installing R2025 locally.
- **7 new `CheckClearanceCommand.BboxIntersects` unit tests** covering overlap, separation,
  face-touch, containment, single-axis separation, and clearance inflation.

---

## [0.6.0] — 2026-06-11: Correctness, API hardening, CI R2027

### Breaking

- **Batch policy**: batches that mix `ModelWrite` and `UiAction` commands are
  now rejected with `bad_request`. Previously, UI actions could silently run
  inside a model transaction. Submit model writes and UI actions as separate
  batches.
- **Unit conversion extended**: `set_parameter` / `set_parameter_batch` now
  require spec-matched unit strings for area and volume parameters.
  - Length: `"meters"` / `"feet"` (unchanged)
  - Area: `"square_meters"` / `"square_feet"` (new)
  - Volume: `"cubic_meters"` / `"cubic_feet"` (new)
  - Passing `"meters"` for an area parameter now returns `invalid_parameter`
    instead of silently applying the wrong conversion.

### Added

- `revit_apply_view_filter` MCP schema now exposes `reuseExisting` (boolean,
  optional) — the C# command already supported it but the field was missing
  from the TypeScript schema.
- RGB channel validation in MCP schema: `r`, `g`, `b` now bounded `[0, 255]`
  for both `revit_apply_view_filter` and `revit_color_override_by_param`.
- `executionKind` field added to every entry in `GET /commands` response
  (`ReadOnly` / `ModelWrite` / `UiAction`). Clients can now distinguish model
  mutation from UI mutation.
- `RevitCommandException(code, message)` — commands now throw typed domain
  exceptions; the dispatcher preserves the `code` field in the error envelope
  instead of collapsing everything to `command_failed`.
- New HTTP status mappings in `StatusForResult`:
  - `invalid_parameter`, `read_only_parameter`, `unsupported_view` → 400
  - `ambiguous_selection` → 409
- CI matrix: `csharp` CI job now builds and tests both R2026 (.NET 8) and
  R2027 (.NET 10) on every pull request.
- `BatchPolicy.ValidateBatchKinds` — pure static helper, testable without Revit.
- New xUnit tests: `StatusForResultTests` (14 cases), `BatchPolicyTests` (7),
  `RevitCommandExceptionTests` (4), `CommandRegistryTests` extended with
  `executionKind` assertions. Total: **83 C# tests** (up from 46).

### Fixed

- README version badge updated from `v0.5.0` to `v0.6.0`; health example
  updated from `0.5.0` to `0.6.0`.
- `check-version.mjs` now validates the README badge and health example so
  future version drift is caught by CI.
- `vitest run` test script now uses explicit `--config ./vitest.config.ts
  --root .` to avoid path-resolution failures on Windows paths with spaces.
- MSB3277 warning suppressed in test project (harmless .NET 8 / Revit assembly
  version conflict that polluted CI output).
- Nice3point reference packages pinned to exact versions (`2026.4.10`,
  `2027.0.20`) for reproducible CI builds.
- Old stale review files (`project_review.md`, `docs/PROJECT_REVIEW.md`) removed.

## [0.5.0] — 2026-06-10: Safety, tests, CI, and production hardening

### Added — Command execution classification
- New `ExecutionKind` enum: `ReadOnly`, `ModelWrite`, `UiAction`.
- `open_view`, `select_elements`, `zoom_to_elements` marked `UiAction` —
  no longer wrapped in a model transaction; dry-run returns a no-op instead
  of silently reverting UI state.

### Added — Unit conversion for numeric parameters
- `set_parameter` and `set_parameter_batch` accept `units:"meters"|"feet"|"internal"`.
  When a Double parameter has measurable units (length, area, volume …),
  `UnitUtils.ConvertToInternalUnits` is called automatically.
  Dimensionless parameters (ratio, slope, etc.) are never converted.
  Response echoes `inputUnits` and `unitConversionApplied`.

### Added — View filter hardening
- `apply_view_filter` checks `AreGraphicsOverridesAllowed()` before
  creating the filter; raises a clear error for schedule/legend views.
- Duplicate filter names now detected before `ParameterFilterElement.Create()`
  — error includes the conflicting filter id. Set `reuseExisting:true` to
  re-apply an existing filter to the view instead.

### Added — `color_override_by_param` view guard
- `AreGraphicsOverridesAllowed()` check with view type in the error message.

### Added — Unambiguous family instance placement
- `place_family_instance` returns `placed:false` + a candidate list when
  both `familyName` and `familyTypeName` are omitted and multiple types
  match. When a partial filter still yields >1 match, places the first but
  includes a `warning` field and `familyTypeId` in the response.

### Added — Auth token auto-refresh
- `revitClient.ts` promotes `AUTH_TOKEN` to a mutable `_authToken` and
  re-reads the token file on `unauthorized` responses — handles Revit
  generating a new token on restart without requiring a server restart.

### Added — Startup health check
- On startup `index.ts` probes `/health` and logs: Revit not reachable,
  auth mismatch (enabled but no token), or successful connection with version.

### Added — Test suite (Phase 2)
- **13 TypeScript tests** (Vitest): `callRevit`, `callRevitBatch`,
  `envelopeToToolResult`, error codes, auth header.
- **46 C# tests** (xUnit): `JsonResult`, `ParamUtil` (all methods + bounds),
  `CommandRegistry` (register, replace, TryGet, ExecutionKind, RiskLevel).
- `Nice3point.Revit.Api` NuGet stubs used as fallback so `dotnet build`
  succeeds on CI machines without a Revit install.
- `scripts/check-version.mjs` — exits non-zero when version strings drift
  across `package.json`, `index.ts`, `McpHttpServer.cs`, and `CHANGELOG.md`.

### Added — GitHub Actions CI
- Three jobs: TypeScript build+test (ubuntu-latest), C# build+test
  (windows-latest, `DeployToRevit=false`), version consistency check.
- Release job on `v*` tags: builds R2026 + R2027 artifacts and uploads
  versioned zip files as GitHub Release assets.

### Added — Release tooling
- `scripts/build-release.ps1` — full release pipeline: version check,
  tests, multi-version C# build, TypeScript build, zip packaging.
- `scripts/install.ps1` — copies addin DLL + manifest to the per-user
  Revit Addins folder; runs `npm install --production` for the MCP server.
- `scripts/uninstall.ps1` — removes addin files from Revit Addins folder.

### Added — Compatibility matrix & troubleshooting guide
- `docs/COMPATIBILITY.md` — Revit × .NET × Node.js matrix with known limits.
- `docs/TROUBLESHOOTING.md` — diagnostic checklist for common failure modes.

### Fixed — HTTP error semantics
- `StatusForResult()` maps error codes to proper HTTP statuses:
  `bad_request`/`bad_json` → 400, `unauthorized` → 401,
  `unknown_command` → 404, name collision → 409, timeout → 408, else 500.

### Fixed — `set_parameter_batch` partial failure
- `partialFailure:true` added to top-level response when any element fails.
- New `atomic` option: `true` → any element failure rolls back the entire batch.

### Fixed — RGB color validation
- `P.ColorByte()` rejects values outside 0-255 with a clear error instead
  of silently wrapping via `(byte)` cast.

### Fixed — Version drift
- `package.json`, `index.ts`, `McpHttpServer.cs` all report `0.5.0`.

## [0.4.2] — 2026-06-09: Revit 2027 support + Family rename

### Added
- **Revit 2027 build** — csproj auto-selects `net10.0-windows` when
  `RevitVersion >= 2027`, `net8.0-windows` for R2025–2026.
  Build with `dotnet build -p:RevitVersion=2027`.
- **Auto-port assignment** — each Revit version gets its own port
  automatically (R2026=7891, R2027=7892, ...). No manual port config
  needed for side-by-side use.
- **Family & FamilySymbol rename** — `revit_rename_element` now handles
  `Family.Name` and `FamilySymbol.Name` (direct property setters), not
  just parameter-based renames. Validates system families, illegal
  characters, and name collisions. Returns `instancesAffected` count.
- Multi-version Claude Desktop config guide.
- Beginner-friendly installation walk-through in README (Step 0–6 +
  troubleshooting table).

### Changed
- `Revit_MCP_Server_Build_Plan.md` moved to `docs/internal/` (gitignored).
- README + docs fully updated for R2026/R2027 dual support.

## [0.4.1] — 2026-05-27

### Fixed — UTF-8 request decoding
- `McpHttpServer.ReadJsonObjectAsync` was decoding incoming bodies with
  `HttpListenerRequest.ContentEncoding`, which falls back to
  `Encoding.Default` when the client omits `charset` in `Content-Type`.
  Under Revit's hosting that produced mojibake for non-ASCII characters
  (e.g. em-dash `—` arrived as `â€"`, section sign `§` as `Â§`),
  corrupting audit-trail Comments written by the bim-orchestrator.
- Force `Encoding.UTF8` on the read path — JSON is canonically UTF-8
  per RFC 8259, so the client's charset declaration is moot.
- Also set `response.ContentEncoding = Encoding.UTF8` on the write
  path for symmetry; the actual bytes were already UTF-8.

## [0.4.0] — Phase 5a: Security + Preview

### Added — Dry-run mode
- Every write command and batch accepts `dryRun: true` (body field or
  `?dryRun=true` query param). The transaction runs normally then rolls
  back — the model is unchanged, but the full result data is returned so
  the AI can preview what *would* happen before committing.

### Added — Structured diffs
- Write commands now return a `changeSummary` one-liner in their data
  payload (e.g. `"Set 'Comments' on element 184239: '' → 'Reviewed'"`).
- Modify commands (`set_parameter`, `rename_element`, `move_element`)
  also return a `changes` object with `before`/`after` values.
- The AI shows the concise summary by default and can expand the full
  diff on request.

### Added — Auth token
- On startup the addin generates a 32-byte random token and writes it to
  `%APPDATA%\Autodesk\Revit\Addins\<version>\revit-mcp-token.txt`.
- All HTTP endpoints (except `GET /health`) require
  `Authorization: Bearer <token>`.
- The TypeScript MCP server reads the token file automatically.
- Disable auth with `REVIT_MCP_AUTH=false` or override with
  `REVIT_MCP_AUTH_TOKEN=<token>`.

### Added — Per-tool risk levels
- `IRevitCommand` now exposes a `RiskLevel` property with default interface
  implementation: `read` (read-only), `low` (creates), `medium` (modifies),
  `high` (deletes/destructive).
- `GET /commands` returns `riskLevel` alongside `isReadOnly` for each
  command, enabling clients to build per-tool permission policies.
- 13 commands classified as `medium` risk, 3 as `high`, rest default to
  `read` or `low`.

### Changed
- `McpHttpServer` version bumped to `0.4.0`.
- TypeScript MCP server v0.4.0 — all write tools now accept `dryRun`
  param; HTTP client sends `Authorization` header when token is available.
- `package.json` version updated to `0.4.0`.

## [0.3.0] — Phase 4: 60 Commands

### Added — Inspection / Introspection (+12 commands)
- `get_document_info` — project metadata, path, worksharing status, active view.
- `find_elements` — generic query DSL: category + parameter filters (equals,
  not_equals, contains, greater, less, etc.) + optional field projections.
- `get_parameter` — single parameter value from one element.
- `get_views` — all non-template views with type, level, scale, detail level.
- `get_active_view` — current active view.
- `get_selected_elements` — elements selected by user in Revit UI.
- `list_families` — loaded Families, optionally by category.
- `list_family_types` — FamilySymbols, optionally by family or category.
- `list_sheets` — all ViewSheets with number, viewport count.
- `list_rooms` — placed rooms with area, perimeter, department.
- `list_materials` — materials with class, category, color.
- `list_phases` — project phases.
- `list_view_templates` — all view templates.
- `get_linked_files` — RevitLinkInstances and their load status.
- `get_element_geometry` — bounding box, centroid, volume, surface area,
  solid/face counts.

### Added — Creation: Architecture (+6 commands)
- `create_room` — NewRoom at a point with optional name/number.
- `create_column` — structural column placement with family type resolution.
- `create_beam` — structural beam between two points.
- `create_ceiling` — Ceiling from closed polygonal profile.
- `create_opening_in_wall` — rectangular opening in a wall.
- `place_family_instance` — generic FamilyInstance placement (non-hosted).

### Added — Creation: Documentation (+8 commands)
- `create_sheet` — ViewSheet with optional title block.
- `place_view_on_sheet` — Viewport placement.
- `create_floor_plan_view` — ViewPlan for a level.
- `create_section_view` — ViewSection with configurable bounding box.
- `create_3d_view` — isometric View3D.
- `create_schedule` — ViewSchedule with optional field columns.
- `tag_element` — IndependentTag on an element in a view.
- `create_text_note` — TextNote in a view.

### Added — Edit: Parameters (+2 commands)
- `set_parameter_batch` — set one parameter on many elements in one call.
- `rename_element` — set Element.Name.

### Added — Edit: Transform (+4 commands)
- `rotate_element` — rotate around vertical axis.
- `copy_element` — copy with translation.
- `mirror_element` — mirror across a plane (copy or move).
- `array_linear` — copy N times along a vector.

### Added — Edit: Grouping (+2 commands)
- `group_elements` — NewGroup.
- `ungroup_elements` — UngroupMembers.

### Added — View Manipulation (+8 commands)
- `open_view` — switch active view.
- `set_view_detail_level` — Coarse / Medium / Fine.
- `hide_elements_in_view` — View.HideElements.
- `unhide_elements_in_view` — View.UnhideElements.
- `select_elements` — set UI selection.
- `zoom_to_elements` — ShowElements.
- `apply_view_filter` — create ParameterFilterElement + apply with overrides.
- `color_override_by_param` — color-code elements by parameter value in a view.

### Summary
- **60 registered C# commands** + 1 batch MCP tool = **61 MCP tools total**.
- C# addin: 0 errors, 3 warnings (MSB3277 noise).
- TypeScript MCP server: 0 errors.

## [0.2.0] — Phase 2: Edit, Create, Batch

### Added
- Batch transaction pattern (`revit_batch` / `POST /mcp/batch`).
- Write commands: `create_floor`, `create_level`, `create_grid`,
  `set_parameter`, `delete_elements`, `move_element`.
- Introspection: `list_levels`, `list_wall_types`, `list_floor_types`,
  `list_categories`.
- `GET /commands` endpoint.

### Changed
- Command framework refactor: `IRevitCommand` + `CommandContext` + dispatcher-
  owned transactions.

## [0.1.0] — MVP

- Initial Revit 2026 addin + TypeScript MCP server.
- 5 commands: `ping`, `get_revit_version`, `list_elements`, `get_element_info`,
  `create_wall`.
