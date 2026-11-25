using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;

namespace ShadowThrottle
{
    /// <summary>
    /// Spot/point light shadow maps are normally re-rendered every frame. HDRP can instead cache them
    /// (ShadowUpdateMode.OnDemand) in a separate atlas, but that atlas is small. So we only cache a limited
    /// number of VISIBLE, non-moving lights and refresh them on a schedule (near often, far rarely). Every other
    /// light stays vanilla (EveryFrame) - off-screen ones cost nothing anyway since HDRP culls them.
    /// If a cached light does not fit in the atlas it is put back to vanilla, so shadows never disappear.
    /// </summary>
    [BepInPlugin("shadowthrottle", "ShadowThrottle", "0.6.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<Key> ToggleKey;
        internal static ConfigEntry<int> MaxCachedLights;
        internal static ConfigEntry<float> NearDistance, MidDistance;
        internal static ConfigEntry<int> NearInterval, MidInterval, FarInterval;
        internal static ConfigEntry<bool> MovingEveryFrame;
        internal static ConfigEntry<float> RescanSeconds;
        internal static ConfigEntry<bool> DebugLog;
        internal static ConfigEntry<int> CachedAtlasSize, MidResolution, FarResolution;

        internal static ConfigEntry<bool> LampsEnabled, SunEnabled, CastersEnabled;
        internal static ConfigEntry<float> VeryFarDistance, VeryFarSeconds;
        internal static ConfigEntry<string> SunCascadeIntervals, SunCascadeMoveLimits;
        internal static ConfigEntry<float> SunAngleRefresh, SunMaxShadowDistance;
        internal static ConfigEntry<int> SunBoostCascades, SunCascadeCount;
        internal static ConfigEntry<bool> SunShipRefresh;
        internal static ConfigEntry<string> SunCascadeSplits;
        internal static ConfigEntry<int> SunResolution;

        public enum Preset { Quality, Balanced, Potato, Custom }
        internal static ConfigEntry<Preset> PresetEntry;
        internal static ConfigEntry<float> SmallSize, MediumSize, SmallDistance, MediumDistance, CasterRescanSeconds;
        internal static ConfigEntry<int> CastersPerFrame;

        internal static bool RuntimeOn;
        public static bool LampsOn;   // A/B switch (LagScope target: ShadowThrottle.Plugin:LampsOn)
        /// <summary>A/B switch for everything added in 0.5 at once (LagScope target: ShadowThrottle.Plugin:New05).</summary>
        public static bool New05 = true;
        internal static ConfigEntry<bool> ShipLightsEnabled;

        internal static void ApplyPreset()
        {
            var p = PresetEntry.Value;
            if (p == Preset.Custom) return;
            //                         Quality              Balanced             Potato
            T Pick<T>(T q, T b, T po) => p == Preset.Quality ? q : p == Preset.Potato ? po : b;

            // 64 MB of VRAM for a 4096 cache is nothing for any PC that runs this game
            CachedAtlasSize.Value      = Pick(4096, 4096, 4096);
            MaxCachedLights.Value      = Pick(128, 128, 128);

            NearDistance.Value         = Pick(12f, 12f, 6f);
            NearInterval.Value         = Pick(1, 2, 1);
            MidDistance.Value          = Pick(30f, 30f, 15f);
            MidInterval.Value          = Pick(2, 4, 6);
            FarInterval.Value          = Pick(4, 8, 20);
            MidResolution.Value        = Pick(256, 256, 128);
            FarResolution.Value        = Pick(256, 128, 64);
            VeryFarDistance.Value      = Pick(80f, 60f, 40f);
            VeryFarSeconds.Value       = Pick(0.5f, 1f, 1f);

            // cascade count / distance stay the game's in every preset: fewer, bigger cascades measured no faster
            SunCascadeIntervals.Value  = Pick("1,1,4,15", "1,2,8,30", "1,2,8,45");
            SunCascadeMoveLimits.Value = Pick("0.3,1,4,10", "0.5,1.5,6,15", "0.5,1.5,6,15");
            SunBoostCascades.Value     = Pick(2, 2, 2);
            SunCascadeCount.Value      = Pick(0, 0, 0);
            SunMaxShadowDistance.Value = Pick(0f, 0f, 0f);
            SunCascadeSplits.Value     = Pick("", "", "");
            SunResolution.Value        = Pick(0, 0, 512);   // no-op when LethalSponge already caps it lower

            SmallSize.Value            = Pick(2f, 2f, 2f);
            SmallDistance.Value        = Pick(40f, 25f, 15f);
            MediumSize.Value           = Pick(8f, 8f, 8f);
            MediumDistance.Value       = Pick(80f, 50f, 30f);

            Log.LogInfo($"Preset {p} applied");
        }

        internal static void ApplySwitches()
        {
            LampsOn = RuntimeOn && LampsEnabled.Value;
            SunCache.On = RuntimeOn && SunEnabled.Value;
            CasterCuller.On = RuntimeOn && CastersEnabled.Value;
            ShipItemLights.On = RuntimeOn && ShipLightsEnabled.Value;
        }

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Master switch (the in-game toggle key does not change this).");
            ToggleKey = Config.Bind("General", "ToggleKey", Key.F4, "Turn the mod on/off in game for A/B comparison. None = no key.");
            MaxCachedLights = Config.Bind("General", "MaxCachedLights", 32,
                "At most this many lights use cached shadows at once. The mod also learns the real atlas capacity and stays below it.");

            NearDistance = Config.Bind("Schedule", "NearDistance", 12f, "Lights closer than this (m) to the camera are 'near'.");
            NearInterval = Config.Bind("Schedule", "NearInterval", 2, "Near lights refresh their shadow every N frames.");
            MidDistance = Config.Bind("Schedule", "MidDistance", 30f, "Lights closer than this (m) are 'mid', farther ones are 'far'.");
            MidInterval = Config.Bind("Schedule", "MidInterval", 4, "Mid lights refresh every N frames.");
            FarInterval = Config.Bind("Schedule", "FarInterval", 8, "Far lights refresh every N frames.");
            MovingEveryFrame = Config.Bind("Schedule", "MovingLightsVanilla", true,
                "Lights that move (held flashlights, vehicle headlights...) are left vanilla (every frame).");
            RescanSeconds = Config.Bind("Advanced", "FullRescanSeconds", 30f,
                "Safety full scan for lights (FindObjectsOfType costs a few ms). New lights are normally picked up instantly via OnEnable.");
            CachedAtlasSize = Config.Bind("Atlas", "CachedAtlasSize", 2048,
                "Size of HDRP's cached shadow atlas (more space = more lights can be cached). 2048 = 16 MB of VRAM. 0 = don't touch.");
            MidResolution = Config.Bind("Atlas", "MidResolution", 256,
                "Max shadow resolution for cached 'mid' lights (smaller = more lights fit). 0 = keep the light's own.");
            FarResolution = Config.Bind("Atlas", "FarResolution", 128,
                "Max shadow resolution for cached 'far' lights. 0 = keep the light's own.");
            DebugLog = Config.Bind("Advanced", "DebugLog", false, "Log every promote/demote.");

            LampsEnabled = Config.Bind("Features", "Lamps", true, "Cache and throttle spot/point light shadows.");
            SunEnabled = Config.Bind("Features", "Sun", true, "Cache the sun's shadow cascades and refresh far cascades rarely.");
            CastersEnabled = Config.Bind("Features", "DistanceCasterCulling", true, "Small/medium objects far away stop casting shadows.");
            ShipLightsEnabled = Config.Bind("Features", "ShipItemLightsOff", true, "Lights of scrap lying in the ship are switched off (flashlights excluded).");

            VeryFarDistance = Config.Bind("Schedule", "VeryFarDistance", 60f, "Lamps farther than this (m) are refreshed by time instead of by frames.");
            VeryFarSeconds = Config.Bind("Schedule", "VeryFarSeconds", 1f, "Very far lamps refresh their shadow every N seconds.");

            SunCascadeIntervals = Config.Bind("Sun", "CascadeIntervals2", "1,2,8,30",
                "Refresh every N frames, per cascade from near to far (comma separated).");
            SunCascadeMoveLimits = Config.Bind("Sun", "CascadeMoveLimits2", "0.5,1.5,6,15",
                "A cascade is refreshed early once the camera moved this many metres since its last refresh.");
            SunBoostCascades = Config.Bind("Sun", "BoostCascadesWhileMoving", 2,
                "While the camera moves (walking, jumping, driving) this many nearest cascades refresh every frame.");
            SunShipRefresh = Config.Bind("Sun", "RefreshAllWhileShipMoves", true, "All cascades refresh every frame while the ship lands / takes off.");
            SunCascadeCount = Config.Bind("Sun", "CascadeCount", 0, "Override the number of sun cascades (1-4). 0 = keep the game's.");
            SunMaxShadowDistance = Config.Bind("Sun", "MaxShadowDistance", 0f, "Override how far sun shadows reach (m). 0 = keep the game's.");
            SunCascadeSplits = Config.Bind("Sun", "CascadeSplits", "",
                "Override where cascades end, as fractions of MaxShadowDistance, e.g. '0.05,0.15,0.4'. Empty = keep the game's.");
            SunResolution = Config.Bind("Sun", "ShadowResolution", 0,
                "Max resolution of the sun's shadow map (e.g. 1024, 512). Only ever lowers it. 0 = keep the game's.");
            SunAngleRefresh = Config.Bind("Sun", "SunAngleRefresh", 0.5f, "Refresh all cascades when the sun rotated more than this many degrees.");

            SmallSize = Config.Bind("Casters", "SmallSize", 2f, "Objects smaller than this (bounding box diagonal, m) are 'small'.");
            SmallDistance = Config.Bind("Casters", "SmallDistance", 25f, "Small objects stop casting shadows beyond this distance (m).");
            MediumSize = Config.Bind("Casters", "MediumSize", 8f, "Objects smaller than this are 'medium'. Bigger ones always cast shadows.");
            MediumDistance = Config.Bind("Casters", "MediumDistance", 50f, "Medium objects stop casting shadows beyond this distance (m).");
            CastersPerFrame = Config.Bind("Casters", "CheckedPerFrame", 300, "How many objects are re-checked per frame (spreads the cost).");
            CasterRescanSeconds = Config.Bind("Casters", "RescanSeconds", 15f, "How often new objects are picked up (also on every level load).");

            PresetEntry = Config.Bind("General", "Preset", Preset.Balanced,
                "Quality = gentle, Balanced = default, Potato = everything turned down for very weak PCs. " +
                "Any preset except Custom OVERWRITES the detailed settings below every time the game starts; " +
                "pick Custom to tune them yourself.");
            PresetEntry.SettingChanged += (s, e) => ApplyPreset();
            ApplyPreset();

            RuntimeOn = Enabled.Value;
            ApplySwitches();
            if (Enabled.Value)
            {
                try { AtlasSetup.Apply(CachedAtlasSize.Value); }
                catch (System.Exception e) { Log.LogError($"Atlas setup failed: {e}"); }
            }
            // pick up lights the moment they are enabled instead of scanning the whole scene every few seconds
            var onEnable = AccessTools.Method(typeof(HDAdditionalLightData), "OnEnable");
            if (onEnable != null)
            {
                try { new Harmony("shadowthrottle").Patch(onEnable, postfix: new HarmonyMethod(typeof(Scheduler), nameof(Scheduler.OnLightEnabled))); }
                catch (System.Exception e) { Log.LogWarning($"OnEnable hook failed, falling back to periodic scans: {e.Message}"); Scheduler.NoHook = true; }
            }
            else Scheduler.NoHook = true;

            var go = new GameObject("ShadowThrottle");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Scheduler>();
            Log.LogInfo($"ShadowThrottle loaded ({(RuntimeOn ? "enabled" : "disabled")}, toggle: {ToggleKey.Value})");
        }
    }

    internal class Scheduler : MonoBehaviour
    {
        private class Entry
        {
            public HDAdditionalLightData Hd;
            public Light Light;
            public Transform T;
            public int Phase;
            public float LastMoved = -999f;
            public float LastVisible = -999f;
            public float BlockedUntil;
            public float LastRequest;
            public bool Visible, WasVisible, Moving, Active;
            public float Distance;
            public bool Cached, Placed;
            public int UnplacedFrames;
            public bool OrigUseOverride; public int OrigOverride; public int OrigRes; public int CurRes;
        }

        internal static readonly List<HDAdditionalLightData> Pending = new List<HDAdditionalLightData>();
        internal static bool NoHook;
        private bool _fullScanQueued = true;

        // while the mod is off nothing drains Pending, so don't queue; a full scan runs when it is turned back on
        internal static void OnLightEnabled(HDAdditionalLightData __instance) { if (Plugin.RuntimeOn) Pending.Add(__instance); }

        private void Awake()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) =>
            {
                _fullScanQueued = true;
                SunCache.Rescan();
                CasterCuller.Rescan();
            };
        }

        private readonly Dictionary<HDAdditionalLightData, Entry> _entries = new Dictionary<HDAdditionalLightData, Entry>();
        private readonly List<Entry> _list = new List<Entry>();
        private readonly List<Entry> _candidates = new List<Entry>();
        private readonly Plane[] _planes = new Plane[6];
        private float _nextScan;
        private int _frame;
        private bool _lastRuntimeOn = true;
        private int _cachedCount;
        private int _capacity = int.MaxValue;   // learned from atlas failures
        private float _capacityRetryAt;

        private void LateUpdate()
        {
            var kb = Keyboard.current;
            var key = Plugin.ToggleKey.Value;
            if (kb != null && key != Key.None && kb[key].wasPressedThisFrame)
            {
                Plugin.RuntimeOn = !Plugin.RuntimeOn;
                Plugin.ApplySwitches();
                Plugin.Log.LogInfo($"ShadowThrottle {(Plugin.RuntimeOn ? "ON" : "OFF")}");
                try { ShowTip(Plugin.RuntimeOn ? "ON" : "OFF"); } catch { /* game API changed: the tip is optional */ }
            }

            float now = Time.unscaledTime;
            _frame++;
            if (Plugin.RuntimeOn != _lastRuntimeOn) // also when flipped by LagScope A/B
            {
                _lastRuntimeOn = Plugin.RuntimeOn;
                Plugin.ApplySwitches();
                if (Plugin.RuntimeOn) _fullScanQueued = true; // pick up lights enabled while the mod was off
                else Pending.Clear();
            }

            if (!Plugin.RuntimeOn)
            {
                if (_cachedCount > 0) foreach (var e in _list) if (e.Cached) Demote(e, "mod off");
                SunCache.Off();
                CasterCuller.Off();
                ShipItemLights.Off();
                return;
            }

            if (Pending.Count > 0)
            {
                foreach (var hd in Pending) if (hd != null) Register(hd);
                Pending.Clear();
            }
            if (_fullScanQueued || now >= _nextScan)
            {
                _fullScanQueued = false;
                _nextScan = now + (NoHook ? 3f : Plugin.RescanSeconds.Value);
                Rescan();
            }
            if (_capacity != int.MaxValue && now >= _capacityRetryAt) _capacity = int.MaxValue; // try again later

            var cam = GetCamera();
            if (cam == null) return;
            if (Plugin.New05) { try { SunCache.Tick(cam, _frame); } catch (System.Exception ex) { Plugin.Log.LogError($"SunCache: {ex}"); SunCache.On = false; } }
            else SunCache.Off();
            bool n05 = Plugin.New05;
            if (n05) { try { CasterCuller.Tick(cam); } catch (System.Exception ex) { Plugin.Log.LogError($"CasterCuller: {ex}"); CasterCuller.On = false; } }
            else CasterCuller.Off();
            if (n05) { try { ShipItemLights.Tick(); } catch (System.Exception ex) { Plugin.Log.LogError($"ShipItemLights: {ex}"); ShipItemLights.On = false; } }
            else ShipItemLights.Off();
            if (!Plugin.LampsOn)
            {
                if (_cachedCount > 0) foreach (var e in _list) if (e.Cached) Demote(e, "lamps off");
                return;
            }
            GeometryUtility.CalculateFrustumPlanes(cam, _planes);
            Vector3 camPos = cam.transform.position;

            float near = Plugin.NearDistance.Value, mid = Plugin.MidDistance.Value;
            int nearI = Mathf.Max(1, Plugin.NearInterval.Value), midI = Mathf.Max(1, Plugin.MidInterval.Value), farI = Mathf.Max(1, Plugin.FarInterval.Value);
            bool movingVanilla = Plugin.MovingEveryFrame.Value;

            // ---- update state + schedule cached lights
            for (int i = 0; i < _list.Count; i++)
            {
                var e = _list[i];
                if (e.Hd == null || e.Light == null) { e.Active = false; continue; }

                e.Active = e.Light.isActiveAndEnabled && e.Light.shadows != LightShadows.None;
                if (!e.Active)
                {
                    if (e.Cached) Demote(e, "disabled");
                    e.WasVisible = false;
                    continue;
                }

                if (e.T.hasChanged) { e.T.hasChanged = false; e.LastMoved = now; }
                e.Moving = movingVanilla && now - e.LastMoved < 1f;

                Vector3 p = e.T.position;
                float range = e.Light.range;
                e.Visible = GeometryUtility.TestPlanesAABB(_planes, new Bounds(p, new Vector3(range, range, range) * 2f));
                e.Distance = Mathf.Max(0f, Vector3.Distance(camPos, p) - range * 0.5f);
                if (e.Visible) e.LastVisible = now;

                if (!e.Cached) { e.WasVisible = e.Visible; continue; }

                if (e.Moving) { Demote(e, "moving"); continue; }
                if (now - e.LastVisible > 2f) { Demote(e, "out of view"); continue; }

                if (!e.Placed)
                {
                    if (HDCachedShadowManager.instance.LightHasBeenPlacedInAtlas(e.Hd)) e.Placed = true;
                    else if (++e.UnplacedFrames > 20)
                    {
                        // atlas is full: back to vanilla, remember how many fit
                        int placed = 0;
                        foreach (var x in _list) if (x.Cached && x.Placed) placed++;
                        _capacity = Mathf.Max(1, placed);
                        _capacityRetryAt = now + 30f;
                        e.BlockedUntil = now + 10f;
                        Demote(e, "atlas full");
                        Plugin.Log.LogInfo($"Cached shadow atlas full: capacity now {_capacity} lights");
                        continue;
                    }
                }

                if (!e.Visible) { e.WasVisible = false; continue; }
                bool force = !e.WasVisible || !e.Placed;
                e.WasVisible = true;

                int interval = e.Distance < near ? nearI : e.Distance < mid ? midI : farI;

                int want = WantedRes(e, near, mid);
                if (want > e.CurRes) { ApplyRes(e, want); force = true; e.Placed = false; e.UnplacedFrames = 0; } // got closer: sharpen (lowering waits for re-promotion)
                bool due;
                if (Plugin.New05 && e.Distance >= Plugin.VeryFarDistance.Value) due = now - e.LastRequest >= Plugin.VeryFarSeconds.Value;
                else due = interval == 1 || (_frame + e.Phase) % interval == 0;
                if (force || due)
                {
                    e.Hd.RequestShadowMapRendering();
                    e.LastRequest = now;
                }
            }

            // ---- promote new candidates a few at a time
            if (_frame % 10 == 0)
            {
                int limit = Mathf.Min(Plugin.MaxCachedLights.Value, _capacity);
                int slots = limit - _cachedCount;
                if (slots > 0)
                {
                    _candidates.Clear();
                    foreach (var e in _list)
                        if (e.Active && !e.Cached && e.Visible && !e.Moving && now >= e.BlockedUntil
                            && e.Hd.shadowUpdateMode == ShadowUpdateMode.EveryFrame)
                            _candidates.Add(e);
                    // far lights first: they gain the most (refreshed rarely) and stale shadows are least visible
                    _candidates.Sort((a, b) => b.Distance.CompareTo(a.Distance));
                    int n = Mathf.Min(slots, _candidates.Count);
                    for (int i = 0; i < n; i++) Promote(_candidates[i]);
                }
            }
        }

        private int WantedRes(Entry e, float near, float mid)
        {
            int cap = e.Distance < near ? 0 : e.Distance < mid ? Plugin.MidResolution.Value : Plugin.FarResolution.Value;
            return cap > 0 ? System.Math.Min(e.OrigRes, cap) : e.OrigRes;
        }

        private static void ApplyRes(Entry e, int res)
        {
            e.CurRes = res;
            if (res == e.OrigRes)
            {
                e.Hd.SetShadowResolutionOverride(e.OrigUseOverride);
                e.Hd.SetShadowResolution(e.OrigOverride);
            }
            else
            {
                e.Hd.SetShadowResolutionOverride(true);
                e.Hd.SetShadowResolution(res);
            }
        }

        private void Promote(Entry e)
        {
            var r = e.Hd.shadowResolution;
            e.OrigUseOverride = r.useOverride; e.OrigOverride = r.@override;
            e.OrigRes = AtlasSetup.EffectiveResolution(e.Hd);
            e.CurRes = e.OrigRes;
            int want = WantedRes(e, Plugin.NearDistance.Value, Plugin.MidDistance.Value);
            if (want != e.OrigRes) ApplyRes(e, want); // set while still EveryFrame: no atlas churn
            e.Hd.shadowUpdateMode = ShadowUpdateMode.OnDemand;
            e.Cached = true; e.Placed = false; e.UnplacedFrames = 0; e.WasVisible = false;
            _cachedCount++;
            e.Hd.RequestShadowMapRendering();
            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"cache + {e.Light.name} ({e.Distance:F0} m, res {e.CurRes}/{e.OrigRes}), cached={_cachedCount}");
        }

        private void Demote(Entry e, string why)
        {
            if (e.Hd != null)
            {
                e.Hd.shadowUpdateMode = ShadowUpdateMode.EveryFrame;
                if (e.CurRes != e.OrigRes) ApplyRes(e, e.OrigRes);
            }
            e.Cached = false; e.Placed = false;
            _cachedCount--;
            if (Plugin.DebugLog.Value && e.Light != null) Plugin.Log.LogInfo($"cache - {e.Light.name} ({why}), cached={_cachedCount}");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ShowTip(string body)
        {
            if (HUDManager.Instance != null) HUDManager.Instance.DisplayTip("ShadowThrottle", body);
        }

        private static Camera GetCamera()
        {
            var sor = StartOfRound.Instance;
            if (sor != null && sor.activeCamera != null && sor.activeCamera.isActiveAndEnabled) return sor.activeCamera;
            return Camera.main;
        }

        private void Rescan()
        {
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                var e = _list[i];
                if (e.Hd != null) continue;
                if (e.Cached) { e.Cached = false; _cachedCount--; }
                _list.RemoveAt(i);
            }
            if (_entries.Count != _list.Count)
            {
                _entries.Clear();
                foreach (var e in _list) _entries[e.Hd] = e;
            }

            foreach (var hd in FindObjectsOfType<HDAdditionalLightData>()) Register(hd);
        }

        private void Register(HDAdditionalLightData hd)
        {
            {
                if (_entries.ContainsKey(hd)) return;
                var l = hd.GetComponent<Light>();
                if (l == null || (l.type != LightType.Spot && l.type != LightType.Point)) return;
                // lights the game itself already caches are not ours: never touch them
                if (hd.shadowUpdateMode != ShadowUpdateMode.EveryFrame) return;
                var e = new Entry { Hd = hd, Light = l, T = hd.transform, Phase = _list.Count * 7 };
                hd.transform.hasChanged = false;
                _entries[hd] = e;
                _list.Add(e);
            }
        }
    }
}
