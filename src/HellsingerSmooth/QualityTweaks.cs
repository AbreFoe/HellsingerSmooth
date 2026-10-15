using System;
using System.Collections.Generic;

namespace HellsingerSmooth
{
    /// <summary>
    /// UnityEngine.QualitySettings / Application values. Re-checked every second because the game
    /// re-applies its own quality level when the player changes graphics options; values are only
    /// written when they differ, so steady state costs a few property reads.
    /// </summary>
    internal sealed class QualityTweaks
    {
        private readonly HashSet<string> _reported = new();
        private Type _qs, _app, _screen;
        private int _applies;

        public void Apply()
        {
            _qs ??= Reflect.FindType("UnityEngine.QualitySettings");
            _app ??= Reflect.FindType("UnityEngine.Application");
            if (_qs == null) return;
            var s = Plugin.S;
            // A few seconds in, record what the game itself is running with (helps tuning).
            if (++_applies == 8) Plugin.SafeRun("diagnostics", LogDiagnostics);

            if (s.VSyncCount.Value >= 0) Set(_qs, "vSyncCount", s.VSyncCount.Value);
            if (s.TargetFrameRate.Value > 0 && _app != null) Set(_app, "targetFrameRate", s.TargetFrameRate.Value);
            if (s.MaxQueuedFrames.Value > 0) Set(_qs, "maxQueuedFrames", s.MaxQueuedFrames.Value);

            if (s.AsyncUploadTimeSliceMs.Value > 0) Set(_qs, "asyncUploadTimeSlice", Math.Clamp(s.AsyncUploadTimeSliceMs.Value, 1, 33));
            if (s.AsyncUploadBufferSizeMB.Value > 0) Set(_qs, "asyncUploadBufferSize", Math.Clamp(s.AsyncUploadBufferSizeMB.Value, 2, 2047));
            if (s.AsyncUploadPersistentBuffer.Value) Set(_qs, "asyncUploadPersistentBuffer", true);

            if (s.TextureQualityReduction.Value > 0)
            {
                int limit = Math.Clamp(s.TextureQualityReduction.Value, 0, 3);
                // Renamed in Unity 2022.2; try the new name first.
                if (Reflect.Has(_qs, "globalTextureMipmapLimit")) Set(_qs, "globalTextureMipmapLimit", limit);
                else Set(_qs, "masterTextureLimit", limit);
            }

            if (s.FullscreenMode.Value >= 0)
            {
                _screen ??= Reflect.FindType("UnityEngine.Screen");
                if (_screen != null) Set(_screen, "fullScreenMode", s.FullscreenMode.Value);
            }

            if (s.LodBias.Value > 0f) Set(_qs, "lodBias", Math.Clamp(s.LodBias.Value, 0.1f, 4f));
            if (s.MaximumLODLevel.Value >= 0) Set(_qs, "maximumLODLevel", Math.Clamp(s.MaximumLODLevel.Value, 0, 7));
            if (s.SkinWeights.Value > 0) Set(_qs, "skinWeights", s.SkinWeights.Value switch { 1 => 1, 2 => 2, 4 => 4, _ => 4 });
        }

        private void Set(Type type, string name, object value)
        {
            bool wrote;
            try { wrote = Reflect.SetStaticIfDifferent(type, name, value); }
            catch (Exception e)
            {
                if (_reported.Add("err:" + name))
                    Plugin.L.LogWarning($"{type.Name}.{name}: {e.GetBaseException().Message}");
                return;
            }
            if (!Reflect.Has(type, name))
            {
                if (_reported.Add("missing:" + name)) Plugin.L.LogWarning($"{type.Name}.{name} not available in this game build, skipped.");
                return;
            }
            if (wrote && _reported.Add("set:" + name)) Plugin.L.LogInfo($"{type.Name}.{name} = {value}");
        }

        private void LogDiagnostics()
        {
            var screen = Reflect.FindType("UnityEngine.Screen");
            int level = -1;
            var getLevel = _qs.GetMethod("GetQualityLevel", Type.EmptyTypes);
            if (getLevel != null) level = Convert.ToInt32(getLevel.Invoke(null, null));
            Plugin.L.LogInfo(
                $"Game state: quality level {level}, vSyncCount {Reflect.GetStatic(_qs, "vSyncCount")}, " +
                $"targetFrameRate {Reflect.GetStatic(_app, "targetFrameRate")}, " +
                $"screen {Reflect.GetStatic(screen, "width")}x{Reflect.GetStatic(screen, "height")} " +
                $"{Reflect.GetStatic(screen, "fullScreenMode")}, incremental GC " +
                $"{Reflect.GetStatic(Reflect.FindType("UnityEngine.Scripting.GarbageCollector"), "isIncremental")}");
        }
    }
}
