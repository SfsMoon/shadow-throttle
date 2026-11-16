# ShadowThrottle

**Up to +60% FPS outside, much smoother frames, and heavy moons stop being heavy.**
Shadows are the most expensive thing Lethal Company renders. This mod keeps them looking the same at the game's low resolution and posterized style, but makes the game redraw them much less often.

## What it does

In vanilla, every shadow is redrawn from scratch **every frame**. That covers every lamp with a shadow, all four cascades of the sun, and every object that casts a shadow, no matter how far away it is. Most of that work produces exactly the same image as the frame before.

| Feature | What changes |
|---|---|
| **Cached lamp shadows** | Spot and point light shadows are cached and only redrawn when needed: close lamps often, far lamps rarely, very far lamps about once a second, off-screen lamps never. Moving lights (held flashlights, cruiser headlights) stay vanilla. |
| **Cached sun cascades** | Each sun cascade gets its own refresh rate. The nearest one (you and things around you) refreshes every frame, the farthest one (trees and buildings far away) about once a second. A cascade also refreshes early once the camera has moved far enough, every cascade refreshes every frame while the ship lands or takes off, and the near cascades refresh every frame while you move. |
| **Distance shadow culling** | Small and medium objects stop casting shadows beyond a distance and get them back as you come closer. Big objects such as trees, buildings and rocks always keep their shadows. |
| **Ship item lights** | Lights of scrap lying in the ship (lamps, the apparatus, glowing items) are switched off while the item is in the ship room. Flashlights are never touched. |
| **Bigger shadow cache** | HDRP's cache for shadows is enlarged at startup so more lamps fit in it (4096², about 64 MB of VRAM). |

Nothing is ever simply switched off where you would notice it. If a shadow can't be cached, the mod leaves that lamp vanilla instead of letting its shadow disappear.

## Measurements

Hardware: Intel Core i3-1115G4 with integrated Intel UHD Graphics, 8 GB RAM. Game v56, vanilla render resolution (860×520), together with LethalPerformance, LethalSponge and CullFactory.

| Moon (outside) | Preset | FPS without | FPS with | Gain | 1% low | Frames > 50 ms |
|---|---|---|---|---|---|---|
| Experimentation | Balanced (A/B test) | 24.9 | **33.4** | **+34%** | 12.7 → **24.0** | — |
| Experimentation | Potato (laptop on battery) | 22.1 | **33.0** | **+49%** | 12.9 → **20.0** | 155 → **12** |
| Artifice | Potato | 20.5 | **33.0** | **+61%** | 12.8 → **22.5** | 198 → **3** |

- *A/B test*: the mod is switched on and off automatically every 10 seconds while standing still, so heat and scene changes affect both sides equally. The other rows are back-to-back 30-second recordings.
- **Heavy moons stop being heavy.** Without the mod, Artifice runs slower than Experimentation. With it, both sit at the same ~33 FPS, because the mod cuts exactly what makes big moons expensive: thousands of distant shadow casters (7500 → ~2500 on Artifice).
- **It also helps CPU-limited PCs.** On battery, the main thread spent 15.4 ms per frame preparing the HDRP render without the mod and 11.1 ms with it. Fewer shadows means less work for the CPU too, not only for the GPU.
- At 860×520 with the game's posterization filter, no visual difference was noticed during testing.

## Presets

Set `Preset` in `BepInEx/config/shadowthrottle.cfg`, or change it in game with LethalConfig.

| | Quality | **Balanced** (default) | Potato |
|---|---|---|---|
| Sun cascades, refresh every N frames (near → far) | 1, 1, 4, 15 | 1, 2, 8, 30 | 1, 2, 8, 45 |
| Small objects lose their shadow beyond | 40 m | 25 m | 15 m |
| Medium objects lose their shadow beyond | 80 m | 50 m | 30 m |
| Lamps: near / mid / far refresh (frames) | 1 / 2 / 4 | 2 / 4 / 8 | 1 / 6 / 20 |
| Mid / far lamp shadow resolution | 256 / 256 | 256 / 128 | 128 / 64 |
| Very far lamps (time based) | > 80 m, 0.5 s | > 60 m, 1 s | > 40 m, 1 s |

Choosing any preset other than **Custom** overwrites the detailed settings every time the game starts. Pick **Custom** to tune everything yourself. Every number above is a separate setting. The shadow cache size only changes after a restart.

## In game

- **F4** toggles the whole mod, so you can compare. You can change the key in the config.
- Each feature can be switched off separately in `[Features]`.

## Advanced: sun cascades

Section `[Sun]`:

| Setting | Meaning |
|---|---|
| `CascadeIntervals2` | Refresh every N frames, per cascade from near to far |
| `CascadeMoveLimits2` | Refresh a cascade early once the camera moved this many metres |
| `BoostCascadesWhileMoving` | How many near cascades refresh every frame while you move |
| `RefreshAllWhileShipMoves` | Refresh every cascade each frame during landing and take-off |
| `CascadeCount`, `MaxShadowDistance`, `CascadeSplits` | Override the game's cascade setup (0 / empty = keep the game's) |
| `ShadowResolution` | Lower the sun's shadow map resolution (it is never raised). LethalSponge may already cap it. |

## Compatibility

- **Client-side only.** Nothing is synced, so other players don't need it, and everyone can use their own preset.
- Works together with **LethalPerformance**, **LethalSponge** and **CullFactory** (all used for the measurements).
- Tested on **v56** and **v80**.
- Lamps that another mod (or the game) already put in a non-"every frame" shadow mode are left alone.
- The sun overrides (`CascadeCount`, `MaxShadowDistance`, `CascadeSplits`) use a global Volume with priority 10000, so they win over other mods that change the same HDRP shadow settings. Leave them at 0 / empty if you want another mod to control them.

## Known limitations

- **Ship item lights:** a light-emitting item (e.g. the fancy lamp) has no light while it is in the ship, even if you are holding it. It lights up again once you carry it outside. Turn off `[Features] ShipItemLightsOff` if you don't want this.
- **Distance culling** uses the object's bounding box. A thin but long object (pipes, fences) can count as "medium" and lose its shadow at the medium distance.
- The shadow cache size (`CachedAtlasSize`) is only applied when the game starts.

## How it works (for modders)

- Lamps: `HDAdditionalLightData.shadowUpdateMode = OnDemand` plus a scheduler calling `RequestShadowMapRendering()`. Placement in the cached atlas is checked with `HDCachedShadowManager.LightHasBeenPlacedInAtlas`; a light that does not fit goes back to `EveryFrame`, and the mod learns how many lights fit. New lights are picked up with a Harmony postfix on `HDAdditionalLightData.OnEnable` instead of scene scans.
- Sun: `OnDemand` plus `RequestSubShadowMapRendering(cascade)` per cascade, with camera-motion, ship-motion and sun-rotation triggers.
- Cached atlas size: `hdShadowInitParams.cachedPunctualLightShadowAtlas` is raised on the HDRP asset before the pipeline is created.
- Casters: `Renderer.shadowCastingMode` is toggled by size and distance with 10% hysteresis, a slice of renderers per frame. If the game changes a renderer's mode itself, the game's value wins.

## Building from source

You only need the [.NET SDK](https://dotnet.microsoft.com/download). Game references come from NuGet.

```
dotnet build -c Release
```

## License

MIT
