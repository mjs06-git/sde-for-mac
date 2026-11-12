# Phone form screenshots

`docs/screenshots/90-phone-find*.png` and `91-phone-edit*.png` capture the phone form with made-up data. The script
also captures `93-phone-damage*` (damage by element), `94-phone-costs*` (cost adjustments and their totals) and
`95-phone-another*` (starting another assessment of a structure); write them to a temporary folder first and copy over
only the ones the docs use. To regenerate
them on macOS, install Google Chrome and Node 22 or later, then run:

```
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
mkdir -p /tmp/sde-phone
SDE_PHONE_SCREENS=/tmp/sde-phone dotnet test tests/Sde.Field.Tests --filter "FullyQualifiedName~PhoneScreensHost" &
node tools/phone-screens/shoot.mjs /tmp/sde-phone docs/screenshots
```

`PhoneScreensHost` (in `tests/Sde.Field.Tests`) runs the phone form on `127.0.0.1` with four made-up structures assigned
to "Team North". It does nothing unless `SDE_PHONE_SCREENS` is set. `shoot.mjs` starts headless Chrome at phone size
(390 × 844 points, 2× pixels), pairs with the one-time token as the QR code would, shows My assignments, opens the first
structure, and captures each screen in light and dark, then opens the Damage and Cost steps. Last, it starts another
assessment of the open structure (which changes the made-up data, so it runs once, after the other screens). It then
tells the host to stop.

On Linux, point `CHROME` at the browser (for example `CHROME=/usr/bin/google-chrome`). When run as root, as in a
container, the script turns off Chrome's sandbox, which Chrome requires there.
