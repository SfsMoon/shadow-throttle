using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ShadowThrottle
{
    /// <summary>Reads/changes the HDRP shadow settings that limit how many cached shadows fit.</summary>
    internal static class AtlasSetup
    {
        public static int MaxPunctualRes = 0;           // 0 = unknown
        public static int MaxDirectionalRes = 0;
        private static int[] _dirLevels = new int[0];
        private static int[] _levels = new int[0];

        private static HDRenderPipelineAsset Asset =>
            (QualitySettingsPipeline() ?? GraphicsSettings.defaultRenderPipeline) as HDRenderPipelineAsset;

        private static RenderPipelineAsset QualitySettingsPipeline()
        {
            try { return UnityEngine.QualitySettings.renderPipeline; } catch { return null; }
        }

        public static void Apply(int wantedCachedAtlas)
        {
            var asset = Asset;
            if (asset == null) { Plugin.Log.LogWarning("HDRP asset not found, atlas setup skipped"); return; }

            var field = AccessTools.Field(typeof(HDRenderPipelineAsset), "m_RenderPipelineSettings");
            if (field == null) { Plugin.Log.LogWarning("m_RenderPipelineSettings not found"); return; }
            var s = (RenderPipelineSettings)field.GetValue(asset);
            var sh = s.hdShadowInitParams;

            MaxPunctualRes = sh.maxPunctualShadowMapResolution;
            MaxDirectionalRes = sh.maxDirectionalShadowMapResolution;
            _dirLevels = new int[4];
            for (int i = 0; i < 4; i++) { try { _dirLevels[i] = sh.shadowResolutionDirectional[i]; } catch { _dirLevels[i] = 0; } }
            Plugin.Log.LogInfo($"HDRP sun shadows: max directional res {MaxDirectionalRes}, directional levels [{string.Join(",", _dirLevels)}]");
            _levels = new int[4];
            for (int i = 0; i < 4; i++) { try { _levels[i] = sh.shadowResolutionPunctual[i]; } catch { _levels[i] = 0; } }

            Plugin.Log.LogInfo($"HDRP shadows: atlas {sh.punctualLightShadowAtlas.shadowAtlasResolution}, cached atlas {sh.cachedPunctualLightShadowAtlas}, " +
                               $"max punctual res {MaxPunctualRes}, punctual levels [{string.Join(",", _levels)}]");

            if (wantedCachedAtlas <= 0 || sh.cachedPunctualLightShadowAtlas >= wantedCachedAtlas) return;
            int maxTex = UnityEngine.SystemInfo.maxTextureSize;
            if (maxTex > 0 && wantedCachedAtlas > maxTex)
            {
                Plugin.Log.LogWarning($"CachedAtlasSize {wantedCachedAtlas} is more than this GPU supports, using {maxTex}");
                wantedCachedAtlas = maxTex;
                if (sh.cachedPunctualLightShadowAtlas >= wantedCachedAtlas) return;
            }
            int bits = (int)sh.punctualLightShadowAtlas.shadowAtlasDepthBits;
            Plugin.Log.LogInfo($"Cached shadow atlas {sh.cachedPunctualLightShadowAtlas} -> {wantedCachedAtlas} " +
                               $"(~{(long)wantedCachedAtlas * wantedCachedAtlas * (bits > 0 ? bits : 32) / 8 / (1024 * 1024)} MB)");

            sh.cachedPunctualLightShadowAtlas = wantedCachedAtlas;
            s.hdShadowInitParams = sh;
            field.SetValue(asset, s);

            // The shadow manager is built when the pipeline is created, so rebuild it once (next frame recreates it).
            if (RenderPipelineManager.currentPipeline != null)
            {
                var cleanup = typeof(RenderPipelineManager).GetMethod("CleanupRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (cleanup != null)
                {
                    try { cleanup.Invoke(null, null); Plugin.Log.LogInfo($"Cached shadow atlas -> {wantedCachedAtlas}, HDRP pipeline recreated"); }
                    catch (Exception e) { Plugin.Log.LogError($"Pipeline rebuild failed: {e}"); }
                }
                else Plugin.Log.LogWarning("CleanupRenderPipeline not found: new atlas size applies after the pipeline is recreated");
            }
            else Plugin.Log.LogInfo($"Cached shadow atlas -> {wantedCachedAtlas} (pipeline not created yet)");
        }

        public static int EffectiveDirectionalResolution(HDAdditionalLightData hd)
        {
            var r = hd.shadowResolution;
            int res = r.useOverride ? r.@override : (r.level >= 0 && r.level < _dirLevels.Length ? _dirLevels[r.level] : 0);
            if (res <= 0) res = 2048;
            if (MaxDirectionalRes > 0) res = Math.Min(res, MaxDirectionalRes);
            return res;
        }

        /// <summary>Resolution the light actually renders at.</summary>
        public static int EffectiveResolution(HDAdditionalLightData hd)
        {
            var r = hd.shadowResolution;
            int res = r.useOverride ? r.@override : (r.level >= 0 && r.level < _levels.Length ? _levels[r.level] : 0);
            if (res <= 0) res = 512;
            if (MaxPunctualRes > 0) res = Math.Min(res, MaxPunctualRes);
            return res;
        }
    }
}
