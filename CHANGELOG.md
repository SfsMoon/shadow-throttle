# Changelog

## 0.6.0 — first public test release

Everything below ships in this version. Earlier versions were only private builds, and their history is summarised under "Development history".

### Lamps (spot / point lights)
- Shadows are cached (`OnDemand`) and refreshed by distance: near lamps often, mid and far lamps rarely, very far lamps about once a second, off-screen lamps never.
- Moving lights (held flashlights, cruiser headlights) stay vanilla.
- Mid and far cached lamps get a lower shadow resolution, so more of them fit in the cache.
- The shadow cache is enlarged to 4096² at startup, before the render pipeline is created.
- Safety net: if a lamp does not fit in the cache, it goes back to vanilla instead of losing its shadow. The mod learns how many lamps fit and retries later.
- New lamps are picked up through a hook on `HDAdditionalLightData.OnEnable` instead of scanning the scene every few seconds (removes a ~6 ms hitch every 2 s).

### Sun
- Each shadow cascade gets its own refresh rate. The near cascade refreshes every frame, the far one about once a second.
- A cascade also refreshes early once the camera has moved far enough, the two nearest cascades refresh every frame while you move, every cascade refreshes every frame while the ship lands or takes off, and everything refreshes when the sun has rotated.
- Optional overrides: cascade count, shadow distance, cascade splits and sun shadow resolution.

### Distance shadow culling
- Small and medium objects stop casting shadows beyond a distance (10% hysteresis, a slice of objects checked per frame). Big objects always keep their shadows.
- If the game changes an object's shadow mode itself, the game's value wins.

### Ship
- Lights of scrap lying in the ship are switched off. Flashlights are never touched.

### General
- Presets: Quality / Balanced (default) / Potato / Custom.
- F4 toggles the whole mod. Every feature can be turned off separately.
- The F4 tip no longer crashes the mod if the game's HUD API changes.
- Builds from NuGet, no game install needed.

### Development history (private builds)
- 0.1: first lamp scheduler (cache every lamp). Shadows could disappear when the cache was full.
- 0.2: cap on cached lamps, only visible and non-moving lamps are cached, and a lamp that does not fit falls back to vanilla.
- 0.3: bigger cache, lower resolution for far lamps.
- 0.4: OnEnable hook instead of periodic scans.
- 0.5: sun cascades, distance culling, ship item lights, very far lamps.
- 0.6: presets, sun resolution, motion and ship refresh for the sun, configurable cascades.
