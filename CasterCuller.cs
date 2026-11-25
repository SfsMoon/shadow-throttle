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
    /// </summary>
    internal static class CasterCuller
    {
        public static bool On;   // A/B switch (LagScope target: ShadowThrottle.CasterCuller:On)

        private class Caster { public Renderer R; public ShadowCastingMode Orig; public bool Culled; }

        private static readonly List<Caster> Casters = new List<Caster>();
        private static readonly HashSet<Renderer> Known = new HashSet<Renderer>();
        private static int _cursor;
        private static float _nextScan;
        private static Vector3 _lastCamPos;
        private static bool _hasCamPos;
        public static int CulledCount;

        // camera moved farther than this in one frame (facility entrance, teleport, spectating someone else):
        // re-check everything at once, otherwise nearby objects would stay shadowless until the slice reaches them
        private const float CameraJump = 10f;

        public static void Rescan() => _nextScan = 0;

        public static void Tick(Camera cam)
        {
            if (!On) { Off(); return; }
            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + Plugin.CasterRescanSeconds.Value;
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
            foreach (var r in Object.FindObjectsOfType<Renderer>())
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
