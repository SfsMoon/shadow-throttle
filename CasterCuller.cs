using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShadowThrottle
{
    /// <summary>
    /// Every shadow-casting object is drawn again into every shadow map that can see it (sun cascades, lamps).
    /// Small and medium objects far from the camera cast shadows nobody can see at 860x520, so this switches their
    /// shadow casting off beyond a distance and back on when you come closer. Big objects (trees, buildings, terrain)
    /// keep their shadows; the sun's far cascades refresh them rarely anyway (see SunCache).
    /// The list is walked a slice per frame, so the cost stays flat.
    /// Finding new objects needs a scene scan (thousands of renderers), so it runs when objects actually appear:
    /// level load, interior generated, ship landed, plus a rare fallback.
    /// </summary>
    internal static class CasterCuller
    {
        public static bool On;   // A/B switch (LagScope target: ShadowThrottle.CasterCuller:On)

        private class Caster { public Renderer R; public ShadowCastingMode Orig; public bool Culled; }

        private static readonly List<Caster> Casters = new List<Caster>();
        private static readonly HashSet<Renderer> Known = new HashSet<Renderer>();
        private static int _cursor;
        private static readonly List<float> ScanAt = new List<float> { 0f };   // pending scan times (first one: startup)
        private static float _lastScan = float.NegativeInfinity;
        private static bool _wasOff, _dungeonDone, _landed;
        private static Vector3 _lastCamPos;
        private static bool _hasCamPos;
        public static int CulledCount;

        // camera moved farther than this in one frame (facility entrance, teleport, spectating someone else):
        // re-check everything at once, otherwise nearby objects would stay shadowless until the slice reaches them
        private const float CameraJump = 10f;

        /// <summary>Level loaded: scan now and again a bit later, objects keep spawning for a while.</summary>
        public static void Rescan() => QueueScans(Time.unscaledTime, 0f, 5f, 15f);

        private static void QueueScans(float now, params float[] delays)
        {
            foreach (var d in delays) ScanAt.Add(now + d);
        }

        /// <summary>The interior and its scrap / hazards appear after the level loads: scan when that happens.</summary>
        private static void WatchRound(float now)
        {
            var rm = RoundManager.Instance;
            bool dungeon = rm != null && rm.dungeonCompletedGenerating;
            if (dungeon && !_dungeonDone) QueueScans(now, 0f, 3f, 10f);
            _dungeonDone = dungeon;
            var sor = StartOfRound.Instance;
            bool landed = sor != null && sor.shipHasLanded;
            if (landed && !_landed) QueueScans(now, 0f);
            _landed = landed;
        }

        public static void Tick(Camera cam)
        {
            if (!On) { Off(); _wasOff = true; return; }
            float now = Time.unscaledTime;
            if (_wasOff) { _wasOff = false; QueueScans(now, 0f); }   // objects that appeared while switched off
            WatchRound(now);
            bool due = false;
            for (int i = ScanAt.Count - 1; i >= 0; i--)
                if (ScanAt[i] <= now) { ScanAt.RemoveAt(i); due = true; }
            float every = Plugin.CasterRescanSeconds.Value;
            if (due || (every > 0f && now >= _lastScan + every))
            {
                _lastScan = now;
                Scan();
            }
            if (Casters.Count == 0) return;

            Vector3 camPos = cam.transform.position;
            bool jumped = _hasCamPos && (camPos - _lastCamPos).sqrMagnitude > CameraJump * CameraJump;
            _lastCamPos = camPos; _hasCamPos = true;
            float smallSize = Plugin.SmallSize.Value, mediumSize = Plugin.MediumSize.Value;
            float smallD = Plugin.SmallDistance.Value, mediumD = Plugin.MediumDistance.Value;
            int budget = jumped ? Casters.Count : Mathf.Min(Plugin.CastersPerFrame.Value, Casters.Count);

            for (int n = 0; n < budget; n++)
            {
                if (_cursor >= Casters.Count) _cursor = 0;
                var c = Casters[_cursor++];
                if (c.R == null) continue;
                var b = c.R.bounds;
                float size = b.size.magnitude;
                float limit = size < smallSize ? smallD : size < mediumSize ? mediumD : -1f;
                if (limit < 0) { if (c.Culled) Uncull(c); continue; }   // big: always casts
                float d2 = b.SqrDistance(camPos);
                // 10% hysteresis so things on the border don't flicker
                if (!c.Culled && d2 > limit * limit * 1.21f)
                {
                    var mode = c.R.shadowCastingMode;
                    if (mode == ShadowCastingMode.Off) continue;          // the game switched it off itself
                    c.Orig = mode;                                        // remember the game's latest choice
                    c.R.shadowCastingMode = ShadowCastingMode.Off; c.Culled = true; CulledCount++;
                }
                else if (c.Culled && d2 < limit * limit) Uncull(c);
            }
        }

        private static void Uncull(Caster c)
        {
            // only undo our own change: if the game changed the mode meanwhile, keep the game's value
            if (c.R.shadowCastingMode == ShadowCastingMode.Off) c.R.shadowCastingMode = c.Orig;
            c.Culled = false;
            CulledCount--;
        }

        private static void Scan()
        {
            Casters.RemoveAll(c =>
            {
                if (c.R != null) return false;
                if (c.Culled) CulledCount--;
                return true;
            });
            Known.RemoveWhere(r => r == null);
            // FindObjectsByType without sorting is the cheap variant of FindObjectsOfType (which sorts by instance id)
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (Known.Contains(r)) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (r.shadowCastingMode == ShadowCastingMode.Off) continue;
                Known.Add(r);
                Casters.Add(new Caster { R = r, Orig = r.shadowCastingMode });
            }
        }

        public static void Off()
        {
            if (CulledCount == 0) return;
            foreach (var c in Casters)
            {
                if (!c.Culled) continue;
                if (c.R != null) Uncull(c);
                // destroyed renderer: nothing to restore, but drop the flag so Scan() doesn't count it a second time
                else c.Culled = false;
            }
            CulledCount = 0;
        }
    }
}
