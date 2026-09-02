# WS Pro Open Data Import Centre — Field Mapping Reference

Manual mappings worked out for importing `wn_*` CSV exports (from `upload_to_acc.rb` /
ACC downloads) via WS Pro's Open Data Import Centre. Use **Auto-Map** first in the
dialog — headers already match WS Pro's internal field names — then check against
this list for anything it missed.

**Import order matters**: Node → Fixed Head (Reservoir) → Tank (Reservoir table
in WS Pro's naming) → Pump → Pipe. Pipes reference nodes by ID; import nodes first
or the link will fail to find its endpoints (e.g. `RIVER`/`LAKE` live in
`wn_fixed_head.csv`, not `wn_node.csv`).

**Source Type**: set to `Delimited Text File` (not `MapInfo TAB File`, the default).

**Skip on every table**: any `*_flag` column (handled by the dialog's "Flag
Behaviour" panel, not per-field), and any column showing `#<WSStructure:...>` in
the data (structured/nested fields our export can't flatten — safe to ignore).

---

## Node (`wn_node.csv`)

| Object Field | Import Field |
|---|---|
| Node ID | `node_id` |
| Notes | `notes` |
| Area Code | `area` |
| Isolation Area | `isolation_area` |
| Asset ID | `asset_id` |
| X | `x` |
| Y | `y` |
| Elevation | `z` |
| Ground Level | `ground_level` |
| Highest Property Level | `highest_property` |
| Leakage Loss | `leakage_loss` |
| Nominal Pressure | `nominal_average_pressure` |
| Total Conns | `total_connections` |
| System Type | `system_type` |
| Fire Zone | `fire_zone` |
| Asset UID | `asset_uid` |
| Asset Network UID | `asset_network_uid` |
| User Number 1–15 | `user_number_1` … `user_number_15` |
| User Text 1–15 | `user_text_1` … `user_text_15` |
| Hotlinks | `hotlinks` |

Skip (structured, no scalar value): `alt_customer_points`, `alt_demand_by_category`,
`alt_land_use`, `customer_points`, `demand_by_category`, `land_use`, `landuse_areas`.

## Reservoir table in WS Pro ← `wn_fixed_head.csv` (EPANET reservoirs: RIVER, LAKE)

Same core fields as Node: Node ID ← `node_id`, X ← `x`, Y ← `y`. This table has no
elevation/demand fields — reservoirs are fixed-head sources, not junctions.

## Tank table in WS Pro ← `wn_reservoir.csv` (EPANET tanks — WS Pro naming differs!)

Same core fields as Node: Node ID ← `node_id`, X ← `x`, Y ← `y`. Note the naming
flip: EPANET's "Reservoir" concept = WS Pro's `wn_fixed_head` table; EPANET's
"Tank" concept = WS Pro's `wn_reservoir` table. See specs/13-metadata-schema.md
for the full mapping rationale.

## Pipe (`wn_pipe.csv`)

| Object Field | Import Field |
|---|---|
| US Node ID | `us_node_id` |
| DS Node ID | `ds_node_id` |
| Link Suffix | `link_suffix` |
| Notes | `notes` |
| Area Code | `area` |
| Isolation Area | `isolation_area` |
| Asset ID | `asset_id` |
| Local Loss | `local_loss` |
| Length | `length` |
| Diameter | `diameter` |
| Roughness Type | `roughness_type` |
| K (roughness) | `k` |
| Darcy Weisbach | `darcy_weissbach` |
| Hazen Williams | `hazen_williams` |
| Modified Hazen Williams | `modified_hazen_williams` |
| Material | `material` |
| Construction Date | `construction_date` |
| Year | `year` |
| Bulk Coefficient | `bulk_coeff` |
| Wall Coefficient | `wall_coeff` |
| System Type | `system_type` |
| Criticality | `criticality` |
| Wave Celerity | `wave_celerity` |
| Pressure Rating | `pressure_rating` |
| Asset UID | `asset_uid` |
| Asset Network UID | `asset_network_uid` |
| User Number 1–15 | `user_number_1` … `user_number_15` |
| User Text 1–15 | `user_text_1` … `user_text_15` |
| Hotlinks | `hotlinks` |

Skip (structured, no scalar value): `alt_customer_points`, `bends`,
`customer_points`, `spatial_data`. Skip also: `iwlive_can_be_closed` (internal
live-simulation state, not meaningful on import).

## Pump (`wn_pump.csv`) — not yet mapped in detail

Core join field: `id` (or `us_node_id`/`ds_node_id` depending on WS Pro's pump
table shape — verify against the file's actual header row before mapping).

---

## Native Import Centre configs

Once a mapping is confirmed working in WS Pro, use the dialog's own
**Save Config...** button (top of the Field Mapping Configuration panel) and save
into this folder, e.g. `node_mapping.ic`, `pipe_mapping.ic` — then **Load Config...**
next time instead of re-mapping by hand. This file only records mappings in
human-readable form as a backup/reference; it is not a substitute for WS Pro's
own saved config format.
