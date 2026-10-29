# Security policy

SDE for Mac holds owners' names, addresses and photos of their homes. While the phone form is running, it listens on
the office network.

## Supported versions
Only the latest release gets security fixes. Please update before reporting.

## Reporting a vulnerability
**Please don't report security problems in a public issue.** Report them privately instead:

1. Open this repository's **[Security](../../security)** tab.
2. Click **Report a vulnerability** and fill in the form.

Only the maintainers can see the report. Please include:

- the SDE for Mac version, your Mac (Apple silicon or Intel) and your macOS version;
- what the problem is and what someone could do with it;
- the steps to reproduce it, with a made-up example rather than real owner data.

You should hear back within about a week. This is a volunteer project, so a fix may take longer; you'll be kept up to
date, and credited in the release notes if you like.

## What counts
In scope:
- **The phone form** (`src/Sde.Field`): anything that lets someone without the QR code or PIN reach data, change
  assessments, upload files, or keep a session after it should have ended, or that makes it reachable from outside
  the local network.
- **Reading files**: an SDE export folder, `.mdb` database, spreadsheet, shapefile, letter template or photo that
  makes SDE run code, read or write files it shouldn't, or crash in a way that loses data.
- **Releases**: the packaging, signing and GitHub Actions workflows in this repository.

Out of scope:
- The phone form's traffic being readable on the network. It uses plain HTTP by design, and this is documented in
  [the phone form guide](docs/PHONE-FORM.md#security); use it only on a network you trust.
- Problems that need someone to already have access to an unlocked Mac and its user account.
- FEMA's, the Census Bureau's and other online services that SDE uses.

## What SDE sends over the internet
SDE has no accounts, analytics or telemetry. Your database stays on your Mac, but some features contact online
services. Requests include:

| When | To | What is sent |
|---|---|---|
| You use **Look up online** | U.S. Census Bureau geocoder | The structure's address |
| You use **Look up online** | FEMA National Flood Hazard Layer | The structure's latitude and longitude |
| You use an online basemap | USGS National Map or OpenStreetMap, depending on your selection | Requests for map tiles, which identify the area in view |
| You use the FEMA flood-zone overlay | FEMA National Flood Hazard Layer | Requests for map tiles, including their geographic bounds |
| You add a layer from an ArcGIS web address | The selected ArcGIS service | Layer and feature requests, including the map extent |
| You request USGS high-water marks | USGS Short-Term Network service (or the configured service) | The selected event ID and state codes |
| You click a FEMA link in Help | FEMA's website | An ordinary web page request |

The phone form serves the local network only. It is off until someone starts it, and it never connects to the
internet. Exported web maps load basemap tiles in the viewer's browser. If you connect an AI assistant, the assistant
may send information it reads to its provider; check that provider's privacy policy before connecting it to sensitive
data.
