# Online services: what to verify live

The online lookups in `src/Sde.Interop/Online` were written using the services' documented formats on a machine that
could not reach census.gov or fema.gov. Tests use hand-written fixtures in `tests/Sde.Interop.Tests/Fixtures/online`.
Before relying on the lookups, check each item below on a machine with internet access.

**Live check of 2026-11-11.** Each item below ends with a result line. The check was run with `curl` from a build
container, with public inputs only (the White House address, public coordinates in Miami Beach, Denver and Houston, and
USGS event 23 in Vermont). It used a handful of requests per service. "Verified" means the request in the item was
made and the answer had the fields and shapes the code reads. Nothing was changed in the code. The things that were not
verified, or that look risky, are named in each result.

## Census geocoder

- URL: `https://geocoding.geo.census.gov/geocoder/locations/onelineaddress?address=<text>&benchmark=Public_AR_Current&format=json`
- Confirm the `Public_AR_Current` benchmark still exists (`CensusGeocoder.Benchmark` changes it).
- Confirm the answer has `result.addressMatches[]`, and that each match has `matchedAddress` and `coordinates.x` (longitude) and `coordinates.y` (latitude).
- Confirm an address with no hit gives an empty `addressMatches` array (SDE then returns no match) rather than an error.
- Result (2026-11-11): verified. `1600 Pennsylvania Ave NW, Washington, DC` gave HTTP 200, one match with
  `matchedAddress` "1600 PENNSYLVANIA AVE NW, WASHINGTON, DC, 20500" and `coordinates` x -77.035189, y 38.898703.
  Benchmark `Public_AR_Current` exists (id 4, "Public Address Ranges - Current Benchmark"). A nonsense address gave
  HTTP 200 with `addressMatches: []`. A bad benchmark name gave HTTP 400 with `{"errors":["Invalid benchmark in request"]}`.

## FEMA National Flood Hazard Layer (NFHL)

- Base URL: `https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer` (`NfhlClient.BaseUrl`).
- Query: `{layer}/query?geometry=<lon>,<lat>&geometryType=esriGeometryPoint&inSR=4326&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&f=json`
- Layer IDs (`NfhlClient.FloodZoneLayer`, `PanelLayer`, `CommunityLayer`). Open the MapServer page and confirm the names match the numbers:
  - 28: Flood Hazard Zones. Fields used: `FLD_ZONE`, `ZONE_SUBTY`, `STATIC_BFE`, `V_DATUM`.
  - 3: FIRM Panels. Fields used: `FIRM_PAN`, `SUFFIX`, `EFF_DATE` (epoch milliseconds).
  - 22: Political Jurisdictions. Fields used: `CID`, `POL_NAME1`.
- Confirm `STATIC_BFE` is -9999 when there is no BFE (SDE maps it to none), and that `ZONE_SUBTY` is `FLOODWAY` for
  floodway polygons (SDE tests for the word, ignoring case).
- Confirm the exact `FLD_ZONE` spellings for zone X, zone A99, and V zones, and what `ZONE_SUBTY` says for shaded X, so `FloodInfoMapper` maps them correctly.
- A point on a boundary can hit several zone polygons. SDE reports the strictest, in this order: floodway, V zone,
  other A zone, shaded X, unshaded X (`ZONE_SUBTY` "AREA OF MINIMAL FLOOD HAZARD" or blank), then anything else.
  Confirm that every shaded X subtype differs from the unshaded one.
- Confirm that a point outside any mapped area returns an empty `features` array, and that an overloaded server returns either an HTTP error or HTTP 200 with an `error` object (both are handled).
- Check that the service accepts the request without a token or referrer, and note its rate limits before using it for large batches (`OnlineBatch` defaults to 4 at a time).
- Check that `EFF_DATE` is the date wanted for SDE's FIRM panel date.
- Result (2026-11-11), layers and fields: verified. The MapServer lists 32 layers. Layer 28 is "Flood Hazard Zones",
  3 is "FIRM Panels" and 22 is "Political Jurisdictions". A point query at -80.13,25.79 (Miami Beach) gave HTTP 200
  and one feature per layer. No token or referrer was needed. Layer 28 had `FLD_ZONE` "AE", `ZONE_SUBTY` null,
  `STATIC_BFE` 8.0 and `V_DATUM` "NGVD29". Layer 3 had `FIRM_PAN` "12086C0317L", `SUFFIX` "L" and `EFF_DATE`
  1252627200000 (epoch milliseconds, 2009-09-11 UTC). Layer 22 had `CID` "120651" and `POL_NAME1` "CITY OF MIAMI BEACH".
- Result (2026-11-11), values: verified. `STATIC_BFE` is -9999.0 where there is no BFE (Denver and Houston points, and
  open water). `ZONE_SUBTY` is JSON null or "AREA OF MINIMAL FLOOD HAZARD" for unshaded X. A point at -40,30 (open
  Atlantic) gave HTTP 200 with an empty `features` array. No `error` object was seen. `EFF_DATE` is present and is
  the panel's effective date. Rate limits were not tested.
- Result (2026-11-11), distinct values on layer 28 (one `returnDistinctValues` query of `FLD_ZONE, ZONE_SUBTY`):
  `FLD_ZONE` spellings are `A`, `A99`, `AE`, `AH`, `AO`, `D`, `OPEN WATER`, `V`, `VE` and `X`. There is no "AREA NOT
  INCLUDED" value. `V` and `VE` both occur. Shaded X has many subtypes, all starting with "0.2 PCT" or "1 PCT" (for
  example "0.2 PCT ANNUAL CHANCE FLOOD HAZARD"), plus "AREA OF SPECIAL CONSIDERATION" and levee wording. Unshaded X
  is "AREA OF MINIMAL FLOOD HAZARD" or no subtype. None of the shaded subtypes contains "MINIMAL".
- Result (2026-11-11), points to watch, not drift: (1) `ZONE_SUBTY` contains "FLOODWAY" in many values, not
  only "FLOODWAY". They include "ADMINISTRATIVE FLOODWAY", "FLOODWAY CONTAINED IN CHANNEL" and "COLORADO RIVER
  FLOODWAY", and also X-zone values such as "1 PCT FUTURE CONDITIONS, FLOODWAY" and "1 PCT CONTAINED IN STRUCTURE,
  FLOODWAY". SDE's substring test would call all of these floodway, including the X-zone ones. Decide whether that is
  wanted. (2) Some A, AE and VE polygons have the literal text `<Null>` as `ZONE_SUBTY`, and some AE and AO polygons
  have a single space. Fixed in 3.5: `OnlineHttp.Text` now reads `<Null>` (any case) and blank text as no value, for
  every field. (3) `STATIC_BFE` and `V_DATUM` are present on every feature checked; `V_DATUM` can be an empty string.

## USGS high-water marks (STN), v2.5

Used by Flood depth from high-water marks (`src/Sde.Interop/Online/StnClient.cs`). The base address can be changed in
Settings, Online services, in case USGS moves the service.

- Base URL: `https://api.waterdata.usgs.gov/stnservices/` (`StnClient.DefaultBaseUrl`).
- Events: `events/` (JSON array). Confirm each event has an id, a name and start and end dates, and that the list is
  not paged.
- Marks: `hwms/filteredhwms/?event=<id>&states=<codes>`, with comma-separated two-letter state codes. Confirm the
  answer is a JSON array and that marks carry `latitude_dd` (or `latitude`), `longitude_dd` (or `longitude`),
  `elev_ft`, `vertical_datum_name`, `hwm_quality_id` (or `hwm_quality_name`) and `hwm_type_name`.
- SDE reads only those fields. Confirm nothing else in a mark is needed, and that marks not yet surveyed have no
  `elev_ft` (SDE skips them and counts them).
- Confirm the datum names used in practice (for example "NAVD88" and "NGVD29") are the ones `VerticalDatums`
  recognises.
- Ask USGS whether an app may call the service directly, and whether it wants a User-Agent or a key. SDE sends
  "SDE-for-Mac/<version>" and retries 502, 503 and 504 twice.
- Check a large event (thousands of marks in one state) for size and time; the timeout is in `StnClient.Timeout`.
- Result (2026-11-11): events and marks are verified, with one drift in event type and status.
  - `events/` gave HTTP 200 and a JSON array of 334 events, not paged. Every event has `id`, `name`, `start_date`
    and `end_date` (`end_date` is null for events still open). The code's fallbacks `name`, `start_date` and
    `end_date` apply. The key `event_name` does not exist.
  - Drift (fixed in 3.5: a purely numeric type or status now reads as unknown, null, and real names are kept): events carry `event_type` and `event_status` as numbers (for example 1 and 2), and no `event_type_name` or
    `event_status_name`. `StnClient.cs` lines 103 and 104 read those keys, so `StnEvent.Type` and `StnEvent.Status`
    become the text "1" or "2", not "Hurricane" or "Completed". The depth sheet does not show them today, so nothing
    visible is wrong yet.
  - `hwms/filteredhwms/?event=23&states=VT` (Irene, Vermont) gave HTTP 200, a JSON array of 846 marks, 1.26 MB, in
    1.8 seconds. Every mark has `latitude_dd`, `longitude_dd`, `elev_ft` (a number), `vertical_datum_name` ("NAVD88"
    for all 846), `hwm_quality_id` (1 to 6; the names run from "Excellent: +/- 0.05 ft" to "VP: > 0.40 ft", plus
    "Unknown/Historical") and `hwm_type_name` ("Debris", "Seed line", "Mud", "Stain line", "Clear water", "Other (Note in
    Description box)"). Marks also have `label` (the code reads `label` first, then `hwm_label`) and `id`. Many other
    fields exist and are not read.
  - Not checked: marks without `elev_ft` (all 846 in this event had one), "NGVD29" naming (only NAVD88 was seen), a
    large multi-state download, and whether USGS wants a key or a User-Agent. No key or special header was needed.

## FEMA flood zones as map tiles (NFHL export), v2.5

The Map page draws FEMA's flood zones over the background map by asking NFHL's MapServer for one picture per
256-pixel tile (`Basemap.FloodZones` in `src/Sde.App/Maps/Basemap.cs`).

- URL: `https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer/export?bbox=<tile>&bboxSR=3857&imageSR=3857&size=256,256&dpi=96&format=png32&transparent=true&f=image&layers=show:28`
- Confirm layer 28 is still Flood Hazard Zones, that the answer is a transparent PNG, and how the server copes with a
  map's worth of tiles at once (TileCache asks a few at a time and keeps them on disk).
- Confirm the attribution FEMA wants shown on the map and on printed maps.
- Result (2026-11-11): partly verified. The `export` URL with `bboxSR=3857`, `layers=show:28` and no token gave HTTP
  200, `image/png`, a 256 by 256 RGBA PNG. Layer 28 is still Flood Hazard Zones (see above). Not checked: a whole
  map's worth of tiles at once (the server reset one connection on a GeoJSON query during this check, see the ArcGIS
  section), and FEMA's required attribution text, which the service metadata leaves empty (`copyrightText` is blank).

## Background maps, v2.5

- USGS The National Map: `https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/tile/{z}/{y}/{x}`
  and `USGSImageryTopo`. Confirm the tile order ({z}/{y}/{x}), the maximum zoom, and the attribution text.
- OpenStreetMap (optional, and in web maps): `https://tile.openstreetmap.org/{z}/{x}/{y}.png`. Its tile usage policy
  asks for a real User-Agent (SDE sends one), caching (TileCache keeps tiles on disk), no bulk downloading, and the
  "© OpenStreetMap contributors" attribution. Check the policy again before each release.
- Web maps load their tiles in the viewer's browser, not through SDE.
- Result (2026-11-11): verified. `USGSTopo/MapServer/tile/5/12/9` and `USGSImageryTopo/MapServer/tile/5/12/9` both
  gave HTTP 200 and a 256 by 256 JPEG. The tile at z5, y12, x9 showed the mid-Atlantic coast with Washington D.C., so
  the order {z}/{y}/{x} is right. Both services have levels 0 to 23 (24 levels, Web Mercator). Level 20 on USGSTopo
  gave HTTP 404 for that tile, so a tile outside the cache is a 404, not an image. Attribution is in the service's
  `copyrightText` (USGSTopo: "USGS The National Map: National Boundaries Dataset, 3DEP Elevation Program, Geographic
  Names Information System, National Hydrography Dataset, National Land Cover Database, National Structures Dataset,
  and National Transportation Dataset; USGS Global Ecosystems; U.S. Census Bureau TIGER/Line data; USFS Road data;
  Natural Earth Data; U.S. Department of State HIU; NOAA National Centers for Environmental Information";
  USGSImageryTopo: "USGS The National Map: Orthoimagery and US Topo"). Check the attribution SDE shows against these.
- Result (2026-11-11), OpenStreetMap: one tile only (`https://tile.openstreetmap.org/5/9/12.png`) with a generic
  descriptive User-Agent. HTTP 200, a 256 by 256 PNG, with `cache-control: max-age=92328`. The order {z}/{x}/{y} is
  right. The tile usage policy text itself was not re-read in this check, so that item is still open.

## ArcGIS REST layers, v2.5

Map, Add layer, From an ArcGIS web address reads any public ArcGIS FeatureServer or MapServer layer
(`src/Sde.Gis/Online/ArcGisRestClient.cs`).

- It asks the layer's JSON for its fields, geometry type, `maxRecordCount` and whether it supports pagination and
  GeoJSON, then queries `query?where=1=1&outFields=*&outSR=4326&f=geojson` limited to the map's extent, paged with
  `resultOffset` until `exceededTransferLimit` is false. Layers that can't be paged are read by object id in batches.
- Try a few real servers (a county parcel FeatureServer, an older MapServer that answers only Esri JSON, and one
  behind a slow connection) and confirm the paging, the fallback, and the messages for a secured or missing layer.

## Photos

`PhotoMetadata` uses the MetadataExtractor library (Apache-2.0). It has been tested only against synthetic JPEGs. Try
real phone and camera photos to confirm that the date and GPS position come through. Include HEIC photos if you expect
them; MetadataExtractor reads that format.
- Result (2026-11-11): partly verified.
  - Layer metadata (`?f=json`) on FEMA NFHL layer 28 gave HTTP 200 with `type` "Feature Layer", `geometryType`
    `esriGeometryPolygon`, `maxRecordCount` 2000, `supportedQueryFormats` "JSON, geoJSON, PBF",
    `advancedQueryCapabilities.supportsPagination` true, a `fields` array with `esriFieldType...` types, and an
    `extent` in wkid 4269. That layer has no `objectIdField` key; SDE then uses the field of type `esriFieldTypeOID`,
    which is present. `copyrightText` is blank.
  - An Esri JSON query on layer 28 with an envelope, `inSR=4326`, `outSR=4326`, `resultOffset=0` and
    `resultRecordCount=2` gave HTTP 200, with `features` and no `error`.
  - A GeoJSON paged query on Esri's public sample server (`sampleserver6.arcgisonline.com`, Census layer 3, three
    records) gave HTTP 200, `type` "FeatureCollection", `exceededTransferLimit` true at the top level and
    `properties.exceededTransferLimit` true as well, and feature `id` values. `returnIdsOnly=true` gave `objectIds`
    (51 ids) and `objectIdFieldName`.
  - Could not be checked: `f=geojson` on NFHL layer 28. Two tries (with and without field filters, a tiny envelope,
    `resultRecordCount=2`) ended with "Connection reset by peer" from the network path, while the same query as Esri
    JSON worked. I stopped after two tries. It may be the proxy or the server; it needs a check from a Mac.
  - Not checked: a county parcel FeatureServer, an older MapServer that answers only Esri JSON, a slow connection, and
    the messages for a secured or missing layer.
