# Forward terrain data and limitations

FlightGuard 0.6 samples a corridor along the aircraft's current GPS ground track using Mapzen / AWS Terrain Tiles. The default horizon is 60 seconds; projected clearance depends on current ground speed and vertical speed. Cautions escalate to a recorded terrain-ahead / pull-up warning for nearer conflicts. This is an independent simulator estimate, not certified aircraft TAWS software.

Public dataset: https://registry.opendata.aws/terrain-tiles/
Format: https://github.com/tilezen/joerd/blob/master/docs/formats.md
Required provider attribution and licence details are preserved in attribution.md in this folder.

When enabled, missing map tiles are downloaded from the public AWS S3 elevation-tiles-prod bucket and cached under Data/TerrainCache. Requests identify geographic tile coordinates to the provider. Flight recordings and the API key are not uploaded. No terrain-provider API key is needed. Disable the forward-terrain checkbox to prevent further map downloads; local GPWS remains available.

The app preloads nearby terrain at the gate and keeps downloads outside the telemetry/audio loop. It uses a narrow sampled corridor, current track and vertical speed, not the flight plan or future pilot inputs. Buildings, towers, runway databases, obstacles and traffic are not included. High latitudes beyond 85 degrees are unsupported.

Real-world terrain can differ from Infinite Flight scenery. A mismatch above 500 ft at the current position disables the forward estimate. Smaller offsets are aligned to the observed simulator ground elevation. Track changes, stale results and major speed/vertical-speed changes invalidate predictions. Configured normal landings over flat ground are inhibited; rising terrain and unsafe descent remain monitored.

If data/position/track is missing or downloads time out, the interface reports unavailable/loading and basic GPWS continues. Forward warnings cannot be guaranteed before initial data has loaded. Existing recordings without GPS/forward assessments replay local GPWS only; replay never downloads maps.

Terrain Tiles accessed 21 October 2026. Managed by Mapzen, a Linux Foundation project; hosted on the AWS Registry of Open Data. See attribution.md for USGS, NOAA, ArcticDEM, national data providers, copyright and licence statements.
