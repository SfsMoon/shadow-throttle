using System.Collections.Generic;
using UnityEngine;

namespace ShadowThrottle
{
    /// <summary>
    /// Scrap piled up in the ship can carry its own lights (fancy lamp, apparatus, glowing items...). Every one of them is
    /// another light in HDRP's light loop, often with shadows. Inside the ship they add little, so their lights are
    /// switched off while the item is in the ship room. Flashlights (regular, pro, laser pointer) are never touched.
    /// Items are registered when they start (Harmony postfix on GrabbableObject.Start) and their lights are looked up
    /// once, so the per-second check is a walk over the few items that have lights at all.
    /// </summary>
    internal static class ShipItemLights
    {
        public static bool On;

        private class Item { public GrabbableObject G; public Light[] Lights; }

        private static readonly List<Item> Items = new List<Item>();                  // only items that have lights
        private static readonly HashSet<GrabbableObject> Known = new HashSet<GrabbableObject>();
        private static readonly Dictionary<Light, GrabbableObject> Disabled = new Dictionary<Light, GrabbableObject>();
        private static readonly List<Light> ToRestore = new List<Light>();
        private static float _next;
        private static float _lastScan = float.NegativeInfinity;
        private static bool _scanQueued = true;
        internal static bool NoHook;

        // Safety net for items whose Start override doesn't call base.Start(); without the hook it is the only way.
        // Cheap now: the scan only looks up lights of items it hasn't seen yet.
        private static float FallbackScanSeconds => NoHook ? 5f : 30f;

        /// <summary>Harmony postfix on GrabbableObject.Start.</summary>
        internal static void OnItemStart(GrabbableObject __instance) => Register(__instance);

        /// <summary>Level loaded: look for items once more.</summary>
        public static void Rescan() => _scanQueued = true;

        private static void Register(GrabbableObject g)
        {
            if (g == null || !Known.Add(g) || g is FlashlightItem) return;
            // inactive children too: a light switched on later is caught by the activeInHierarchy check in Tick
            var lights = g.GetComponentsInChildren<Light>(true);
            if (lights.Length > 0) Items.Add(new Item { G = g, Lights = lights });
        }

        private static void Scan()
        {
            Known.RemoveWhere(g => g == null);
            foreach (var g in Object.FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None)) Register(g);
        }

        public static void Tick()
        {
            if (!On) { Off(); return; }
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 1f;

            if (_scanQueued || now >= _lastScan + FallbackScanSeconds)
            {
                _scanQueued = false;
                _lastScan = now;
                Scan();
            }

            // give back lights of items that left the ship (or were destroyed / picked up into a flashlight slot...)
            ToRestore.Clear();
            foreach (var kv in Disabled)
                if (kv.Key == null || kv.Value == null || !kv.Value.isInShipRoom) ToRestore.Add(kv.Key);
            foreach (var l in ToRestore) { if (l != null) l.enabled = true; Disabled.Remove(l); }

            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var item = Items[i];
                if (item.G == null) { Items.RemoveAt(i); continue; }
                if (!item.G.isInShipRoom) continue;
                foreach (var l in item.Lights)
                {
                    if (l == null || !l.enabled || !l.gameObject.activeInHierarchy || Disabled.ContainsKey(l)) continue;
                    l.enabled = false;
                    Disabled[l] = item.G;
                }
            }
        }

        public static int Count => Disabled.Count;

        public static void Off()
        {
            if (Disabled.Count == 0) return;
            foreach (var l in Disabled.Keys) if (l != null) l.enabled = true;
            Disabled.Clear();
        }
    }
}
