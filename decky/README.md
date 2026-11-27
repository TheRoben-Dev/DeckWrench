# DeckWrench for Decky Loader

A Quick Access menu panel for [DeckWrench](../README.md): what needs attention (Home's checkup), free
space per drive, and Steam changes waiting for a restart with an "Apply now" button. "Fix in
DeckWrench" opens the app. The panel runs the `deckwrench` helper the app installs, so install the
app first.

Install: download `DeckWrench-decky.zip` from the latest release, then in Game Mode open Decky's
settings → Developer → Install plugin from ZIP (Developer mode on).

Build: `pnpm install && pnpm run build` (writes `dist/index.js`), then
`tools/release/package-decky.sh decky out/` packages it.
