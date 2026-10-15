using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;

namespace HellsingerSmooth
{
    /// <summary>
    /// Render scale through HDRP's dynamic resolution: the 3D scene is rendered at a lower
    /// resolution and upscaled (FSR 1.0 by default) while the HUD stays at native resolution.
    /// This is the biggest single lever when the GPU is the bottleneck.
    /// </summary>
    internal sealed class ResolutionTweaks
    {
        private const string Hdrp = "UnityEngine.Rendering.HighDefinition.";
        private const int Step = 5;

        private readonly HashSet<string> _reported = new();
        private double _nextCameraScan, _nextReassert, _nextEval;
        private Type _assetType, _camDataType, _cameraType, _graphicsSettings, _app;

        // Dynamic mode state.
        private readonly List<double> _window = new();
        private int _current = -1;
        private double _calmSince = -1;
        private readonly Dictionary<int, double> _blockedUntil = new();
        private int _minSeen = int.MaxValue, _maxSeen;
        private double _scaleSum;
        private int _scaleSamples;

        /// <summary>Called every frame with the measured frame time.</summary>
        public void OnFrame(double now, double dt)
        {
            var mode = Plugin.S.RenderScaleMode.Value.Trim().ToLowerInvariant();
            if (mode == "off") return;
            bool dynamic = mode != "fixed";

            int min = Math.Clamp(Plugin.S.DynamicMinScale.Value, 50, 100);
            int max = Math.Clamp(Plugin.S.DynamicMaxScale.Value, min, 100);
            int fixedScale = Math.Clamp(Plugin.S.RenderScale.Value, 50, 100);

            if (_current < 0) _current = dynamic ? Math.Clamp(80, min, max) : fixedScale;
            if (!dynamic) _current = fixedScale;

            if (dynamic && dt > 0 && dt < 0.25) _window.Add(dt);
            if (dynamic && now >= _nextEval)
            {
                _nextEval = now + 0.5;
                Evaluate(now, min, max);
            }

            _scaleSum += _current;
            _scaleSamples++;
            _minSeen = Math.Min(_minSeen, _current);
            _maxSeen = Math.Max(_maxSeen, _current);

            // Write on change, and re-assert every 2 s in case a settings change swapped the asset.
            if (_current != _applied || now >= _nextReassert)
            {
                _nextReassert = now + 2.0;
                ApplyToAsset(_current);
            }

            if (now >= _nextCameraScan)
            {
                _nextCameraScan = now + 5.0;
                AllowOnCameras();
            }
        }

        /// <summary>
        /// Simple controller: the 90th-percentile frame time of the last half second is compared
        /// with the frame budget. Over budget = step down at once; comfortably under budget for
        /// 3 s = step up. A level that just failed is blocked for 20 s to avoid see-sawing.
        /// </summary>
        private void Evaluate(double now, int min, int max)
        {
            if (_window.Count < 10) { _window.Clear(); return; }
            _window.Sort();
            double p90 = _window[(int)(_window.Count * 0.9)];
            _window.Clear();
            double budget = 1.0 / TargetFps();

            if (p90 > budget * 1.12)
            {
                int down = p90 > budget * 1.4 ? Step * 2 : Step;
                int next = Math.Max(min, _current - down);
                if (next != _current) _blockedUntil[_current] = now + 20.0;
                _current = next;
                _calmSince = -1;
            }
            else if (p90 < budget * 1.04)
            {
                if (_calmSince < 0) _calmSince = now;
                int next = Math.Min(max, _current + Step);
                bool blocked = _blockedUntil.TryGetValue(next, out var until) && now < until;
                if (next != _current && !blocked && now - _calmSince >= 3.0)
                {
                    _current = next;
                    _calmSince = now;
                }
            }
            else
            {
                _calmSince = -1;
            }
        }

        private double TargetFps()
        {
            if (Plugin.S.DynamicTargetFps.Value > 0) return Plugin.S.DynamicTargetFps.Value;
            _app ??= Reflect.FindType("UnityEngine.Application");
            var cap = Reflect.GetStatic(_app, "targetFrameRate");
            int fps = cap != null ? Convert.ToInt32(cap) : -1;
            return fps > 0 ? fps : 60;
        }

        /// <summary>Summary for the periodic stats line; resets the range.</summary>
        public string TakeSummary()
        {
            if (_scaleSamples == 0) return null;
            string text = _minSeen == _maxSeen
                ? $"render {_current}%"
                : $"render {_minSeen}-{_maxSeen}% (avg {_scaleSum / _scaleSamples:F0}%)";
            _minSeen = int.MaxValue; _maxSeen = 0; _scaleSum = 0; _scaleSamples = 0;
            return text;
        }

        private int _applied = -1;

        private object CurrentHdrpAsset()
        {
            _graphicsSettings ??= Reflect.FindType("UnityEngine.Rendering.GraphicsSettings");
            _assetType ??= Reflect.FindType(Hdrp + "HDRenderPipelineAsset");
            if (_graphicsSettings == null || _assetType == null) return null;
            // Includes a per-quality-level override when the game uses one.
            if (Reflect.GetStatic(_graphicsSettings, "currentRenderPipeline") is not Il2CppObjectBase rp) return null;
            return Reflect.Il2CppClassName(rp) == Hdrp + "HDRenderPipelineAsset" ? Reflect.Rewrap(rp, _assetType) : null;
        }

        private void ApplyToAsset(int percent)
        {
            _applied = percent;
            var asset = CurrentHdrpAsset();
            if (asset == null) { Once("noasset", "HDRP asset not found; render scale skipped."); return; }

            // Struct chain: asset.m_RenderPipelineSettings.dynamicResolutionSettings.
            // Each Get returns a copy, so the modified copies are written back up the chain.
            var settings = Reflect.GetMember(asset, "m_RenderPipelineSettings");
            var drs = Reflect.GetMember(settings, "dynamicResolutionSettings");
            if (drs == null) { Once("nodrs", "HDRP dynamic resolution settings not found; render scale skipped."); return; }

            float scale = percent;
            bool changed = false;
            changed |= Reflect.SetMember(drs, "enabled", true);
            changed |= Reflect.SetMember(drs, "forceResolution", true);
            changed |= Reflect.SetMember(drs, "forcedPercentage", scale);
            changed |= Reflect.SetMember(drs, "maxPercentage", 100f);
            var min = Reflect.GetMember(drs, "minPercentage");
            if (min == null || Convert.ToSingle(min) > scale) changed |= Reflect.SetMember(drs, "minPercentage", scale);
            // DX11 has no hardware dynamic resolution; software is what HDRP would fall back to anyway.
            var software = Reflect.EnumByName(Reflect.MemberType(drs, "dynResType"), "Software");
            if (software != null) changed |= Reflect.SetMember(drs, "dynResType", software);
            var filter = PickFilter(Reflect.MemberType(drs, "upsampleFilter"));
            if (filter != null) changed |= Reflect.SetMember(drs, "upsampleFilter", filter);
            // Sharper textures at reduced resolution, where supported.
            Reflect.SetMember(drs, "useMipBias", true);

            if (!changed) return;
            Reflect.ForceSetMember(settings, "dynamicResolutionSettings", drs);
            Reflect.ForceSetMember(asset, "m_RenderPipelineSettings", settings);
            if (_reported.Add("scale-on")) Plugin.L.LogInfo($"Render scale {percent}% with upscaler {filter ?? "default"} ({Plugin.S.RenderScaleMode.Value})");
        }

        private object PickFilter(Type enumType)
        {
            // Config names -> HDRP enum names, most preferred first.
            var wanted = Plugin.S.Upscaler.Value.Trim().ToUpperInvariant() switch
            {
                "CAS" => new[] { "ContrastAdaptiveSharpen", "CatmullRom" },
                "CATMULLROM" => new[] { "CatmullRom" },
                "TAAU" => new[] { "TAAU", "EdgeAdaptiveScalingUpres", "CatmullRom" },
                _ => new[] { "EdgeAdaptiveScalingUpres", "ContrastAdaptiveSharpen", "CatmullRom" },
            };
            foreach (var name in wanted)
            {
                var v = Reflect.EnumByName(enumType, name);
                if (v != null) return v;
            }
            Once("nofilter", "No known upscale filter in this HDRP version; using the game's default.");
            return null;
        }

        /// <summary>HDRP only scales cameras that opt in.</summary>
        private void AllowOnCameras()
        {
            _camDataType ??= Reflect.FindType(Hdrp + "HDAdditionalCameraData");
            _cameraType ??= Reflect.FindType("UnityEngine.Camera");
            int changed = 0;
            foreach (var data in Reflect.FindObjects(_camDataType))
                if (Reflect.SetMember(data, "allowDynamicResolution", true)) changed++;
            foreach (var cam in Reflect.FindObjects(_cameraType))
                Reflect.SetMember(cam, "allowDynamicResolution", true);
            if (changed > 0) Plugin.L.LogInfo($"Dynamic resolution allowed on {changed} camera(s).");
        }

        private void Once(string key, string message)
        {
            if (_reported.Add(key)) Plugin.L.LogWarning(message);
        }
    }
}
