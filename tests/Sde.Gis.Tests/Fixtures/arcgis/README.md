These hand-written ArcGIS REST response fixtures for `ArcGisRestClientTests` follow the shapes sent by ArcGIS Server
10.2 to 11.3. They are not recorded responses: the parcels, flood zones and levees in Cedar Ford are all synthetic.

- `parcels-*`: a paginated FeatureServer layer (`maxRecordCount` 2). Page 1 puts `exceededTransferLimit` at the top
  level; page 2 puts it under `properties`, as some servers do. Page 3 is the last.
- `zones-*`: a MapServer layer without pagination. The first response is truncated, so the client requests object IDs
  and fetches them in batches.
- `old-*`: a 10.2 layer that cannot return GeoJSON. The client reads Esri JSON instead (a polygon with a hole and two parts).
- `error-*`, `raster-layer.json`: error responses and a layer that is not a feature layer.
