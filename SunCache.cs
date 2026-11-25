using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ShadowThrottle
{
    /// <summary>
    /// The sun (directional light) re-renders all its shadow cascades every frame. Cascade 0 covers the few metres
    /// around the player, the last cascade covers everything up to the shadow distance (trees, buildings far away).
    /// Here the sun becomes OnDemand and every cascade gets its own refresh rate: near ones often, far ones about once
    /// a second. A cascade is also refreshed as soon as the camera has moved far enough for it to drift.
    /// </summary>
    internal static class SunCache
    {
        public static bool On;   // A/B switch (LagScope target: ShadowThrottle.SunCache:On)

        private class Sun
        {
            public HDAdditionalLightData Hd;
            public Light L;
            public ShadowUpdateMode Orig;
            public bool Cached;
            public float[] LastTime = new float[4];
            public Vector3[] LastPos = new Vector3[4];
            public Quaternion LastRot;
            public bool OrigUseOverride; public int OrigOverride; public bool ResChanged;
        }

        private static readonly List<Sun> Suns = new List<Sun>();
        private static Vector3 _lastCamPos;
        private static Vector3 _lastShipPos;
        private static Quaternion _lastShipRot;
        private static float _shipBoostUntil;
        private static Transform _ship;
        private static GameObject _volGo;
        private static string _volKey;
        private static float[] _volSplits;
        private static float _nextScan;
        private static int[] _intervals = { 2, 4, 12, 40 };
        private static float[] _moveLimit = { 1f, 3f, 8f, 20f };
        private static string _parsed;

        public static void Rescan() => _nextScan = 0;

        public static void Tick(Camera cam, int frame)
        {
            if (!On) { Off(); return; }
            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + 5f;
                Suns.RemoveAll(s => s.Hd == null || s.L == null);
                foreach (var l in Object.FindObjectsOfType<Light>())
                {
                    if (l.type != LightType.Directional || Suns.Any(s => s.L == l)) continue;
                    var hd = l.GetComponent<HDAdditionalLightData>();
                    if (hd != null) Suns.Add(new Sun { Hd = hd, L = l, Orig = hd.shadowUpdateMode });
                }
            }
            ParseConfig();
            ApplyVolume(cam);

            // motion boosts: near cascades every frame while the camera moves (walking, jumping),
            // everything every frame while the ship moves (landing / take-off) and a bit after
            Vector3 cp = cam.transform.position;
            bool camMoving = (cp - _lastCamPos).sqrMagnitude > 0.0004f; // > 2 cm this frame
            _lastCamPos = cp;
            if (_ship == null)
            {
                var sor = StartOfRound.Instance;
                if (sor != null && sor.elevatorTransform != null) _ship = sor.elevatorTransform;
            }
            if (_ship != null && Plugin.SunShipRefresh.Value)
            {
                if ((_ship.position - _lastShipPos).sqrMagnitude > 1e-6f || Quaternion.Angle(_ship.rotation, _lastShipRot) > 0.01f)
                    _shipBoostUntil = now + 0.5f;
                _lastShipPos = _ship.position; _lastShipRot = _ship.rotation;
            }
            bool shipBoost = now < _shipBoostUntil;
            int boostCascades = camMoving ? Plugin.SunBoostCascades.Value : 0;

            int cascades = 4;
            try
            {
                var settings = VolumeManager.instance.stack.GetComponent<HDShadowSettings>();
                if (settings != null) cascades = Mathf.Clamp(settings.cascadeShadowSplitCount.value, 1, 4);
            }
            catch { }

            Vector3 camPos = cam.transform.position;
            foreach (var s in Suns)
            {
                bool active = s.L.isActiveAndEnabled && s.L.shadows != LightShadows.None && s.Orig == ShadowUpdateMode.EveryFrame;
                if (!active) { Restore(s); continue; }

                ApplyResolution(s);
                if (!s.Cached)
                {
                    s.Hd.shadowUpdateMode = ShadowUpdateMode.OnDemand;
                    s.Cached = true;
                    s.Hd.RequestShadowMapRendering();
                    for (int i = 0; i < 4; i++) { s.LastTime[i] = now; s.LastPos[i] = camPos; }
                    s.LastRot = s.L.transform.rotation;
                    continue;
                }

                // the sun turned (time of day, eclipse...) -> everything is stale
                if (Quaternion.Angle(s.LastRot, s.L.transform.rotation) > Plugin.SunAngleRefresh.Value)
                {
                    s.Hd.RequestShadowMapRendering();
                    s.LastRot = s.L.transform.rotation;
                    for (int i = 0; i < 4; i++) { s.LastTime[i] = now; s.LastPos[i] = camPos; }
                    continue;
                }

                for (int i = 0; i < cascades; i++)
                {
                    int interval = _intervals[Mathf.Min(i, _intervals.Length - 1)];
                    float limit = _moveLimit[Mathf.Min(i, _moveLimit.Length - 1)];
                    bool due = shipBoost || i < boostCascades || (frame + i) % interval == 0;
                    bool moved = (camPos - s.LastPos[i]).sqrMagnitude > limit * limit;
                    if (!due && !moved) continue;
                    s.Hd.RequestSubShadowMapRendering(i);
                    s.LastTime[i] = now;
                    s.LastPos[i] = camPos;
                }
            }
        }

        public static void Off()
        {
            foreach (var s in Suns) Restore(s);
            RemoveVolume();
        }

        // ---- optional overrides of the game's cascade setup (count / distance / splits) through a top-priority Volume
        private static void ApplyVolume(Camera cam)
        {
            int count = Plugin.SunCascadeCount.Value;
            float dist = Plugin.SunMaxShadowDistance.Value;
            string splitsText = (Plugin.SunCascadeSplits.Value ?? "").Trim();
            string key = $"{count}|{dist}|{splitsText}";
            if (key != _volKey)
            {
                // settings changed: parse once (a typo is reported once and ignored, never kills the sun feature)
                _volKey = key;
                _volSplits = null;
                if (splitsText.Length > 0 && !TryParseList(splitsText, float.TryParse, out _volSplits))
                    Plugin.Log.LogWarning($"[Sun] CascadeSplits '{splitsText}' is not a list of numbers like '0.05,0.15,0.4', ignored");
                RemoveVolume();
            }
            if (count <= 0 && dist <= 0 && _volSplits == null) { RemoveVolume(); return; }
            if (_volGo != null) return;

            _volGo = new GameObject("ShadowThrottle_ShadowSettings");
            Object.DontDestroyOnLoad(_volGo);
            var cd = cam.GetComponent<HDAdditionalCameraData>();
            int mask = cd != null ? (int)cd.volumeLayerMask : 1;
            for (int b = 0; b < 32; b++) if ((mask & (1 << b)) != 0) { _volGo.layer = b; break; }
            var vol = _volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10000f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var sh = profile.Add<HDShadowSettings>(true);
            if (count > 0) sh.cascadeShadowSplitCount.Override(Mathf.Clamp(count, 1, 4));
            if (dist > 0) sh.maxShadowDistance.Override(dist);
            if (_volSplits != null)
            {
                var v = _volSplits;
                if (v.Length > 0) sh.cascadeShadowSplit0.Override(v[0]);
                if (v.Length > 1) sh.cascadeShadowSplit1.Override(v[1]);
                if (v.Length > 2) sh.cascadeShadowSplit2.Override(v[2]);
            }
            vol.sharedProfile = profile;
            Plugin.Log.LogInfo($"Sun cascades override: count={(count > 0 ? count.ToString() : "game")}, distance={(dist > 0 ? dist.ToString() : "game")}, splits={(_volSplits != null ? splitsText : "game")}");
            foreach (var s in Suns) if (s.Cached && s.Hd != null) s.Hd.RequestShadowMapRendering();
        }

        private static void RemoveVolume()
        {
            if (_volGo != null) Object.Destroy(_volGo);
            _volGo = null;
        }

        private static void ApplyResolution(Sun s)
        {
            int want = Plugin.SunResolution.Value;
            if (want <= 0) { RestoreResolution(s); return; }
            if (!s.ResChanged)
            {
                var r = s.Hd.shadowResolution;
                s.OrigUseOverride = r.useOverride; s.OrigOverride = r.@override;
                int cur = AtlasSetup.EffectiveDirectionalResolution(s.Hd);
                if (want >= cur) return;                       // only ever lower it
                Plugin.Log.LogInfo($"Sun shadow resolution {cur} -> {want}");
                s.ResChanged = true;
            }
            if (s.Hd.shadowResolution.@override != want || !s.Hd.shadowResolution.useOverride)
            {
                s.Hd.SetShadowResolutionOverride(true);
                s.Hd.SetShadowResolution(want);
            }
        }

        private static void RestoreResolution(Sun s)
        {
            if (!s.ResChanged || s.Hd == null) return;
            s.Hd.SetShadowResolutionOverride(s.OrigUseOverride);
            s.Hd.SetShadowResolution(s.OrigOverride);
            s.ResChanged = false;
        }

        private static void Restore(Sun s)
        {
            RestoreResolution(s);
            if (!s.Cached || s.Hd == null) return;
            s.Hd.shadowUpdateMode = s.Orig;
            s.Cached = false;
        }

        private static void ParseConfig()
        {
            string a = Plugin.SunCascadeIntervals.Value + "|" + Plugin.SunCascadeMoveLimits.Value;
            if (a == _parsed) return;
            _parsed = a;
            if (TryParseList<int>(Plugin.SunCascadeIntervals.Value, int.TryParse, out var iv))
                _intervals = iv.Select(x => Mathf.Max(1, x)).ToArray();
            else Plugin.Log.LogWarning($"[Sun] CascadeIntervals2 '{Plugin.SunCascadeIntervals.Value}' is not a list of whole numbers like '1,2,8,30', keeping {string.Join(",", _intervals)}");
            if (TryParseList<float>(Plugin.SunCascadeMoveLimits.Value, float.TryParse, out var ml))
                _moveLimit = ml;
            else Plugin.Log.LogWarning($"[Sun] CascadeMoveLimits2 '{Plugin.SunCascadeMoveLimits.Value}' is not a list of numbers like '0.5,1.5,6,15', keeping {string.Join(",", _moveLimit)}");
        }

        private delegate bool Parser<T>(string s, NumberStyles style, System.IFormatProvider provider, out T result);

        /// <summary>Parses "a, b, c" with the invariant culture. False if empty or any item is not a number.</summary>
        private static bool TryParseList<T>(string text, Parser<T> parse, out T[] result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(',');
            var values = new T[parts.Length];
            var style = typeof(T) == typeof(int) ? NumberStyles.Integer : NumberStyles.Float;
            for (int i = 0; i < parts.Length; i++)
                if (!parse(parts[i].Trim(), style, CultureInfo.InvariantCulture, out values[i])) return false;
            result = values;
            return true;
        }
    }
}
