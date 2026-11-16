using System.Collections.Generic;
using UnityEngine;

namespace ShadowThrottle
{
    /// <summary>
    /// Scrap piled up in the ship can carry its own lights (fancy lamp, apparatus, glowing items...). Every one of them is
    /// another light in HDRP's light loop, often with shadows. Inside the ship they add little, so their lights are
    /// switched off while the item is in the ship room. Flashlights (regular, pro, laser pointer) are never touched.
    /// </summary>
    internal static class ShipItemLights
    {
        public static bool On;

        private static readonly Dictionary<Light, GrabbableObject> Disabled = new Dictionary<Light, GrabbableObject>();
        private static readonly List<Light> Buffer = new List<Light>();
        private static readonly List<Light> ToRestore = new List<Light>();
        private static float _next;

        public static void Tick()
        {
            if (!On) { Off(); return; }
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 1f;

            // give back lights of items that left the ship (or were destroyed / picked up into a flashlight slot...)
            ToRestore.Clear();
            foreach (var kv in Disabled)
                if (kv.Key == null || kv.Value == null || !kv.Value.isInShipRoom) ToRestore.Add(kv.Key);
            foreach (var l in ToRestore) { if (l != null) l.enabled = true; Disabled.Remove(l); }

            foreach (var item in Object.FindObjectsOfType<GrabbableObject>())
            {
                if (!item.isInShipRoom || item is FlashlightItem) continue;
                Buffer.Clear();
                item.GetComponentsInChildren(false, Buffer);
                foreach (var l in Buffer)
                {
                    if (!l.enabled || Disabled.ContainsKey(l)) continue;
                    l.enabled = false;
                    Disabled[l] = item;
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
