using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;

namespace HellsingerSmooth
{
    /// <summary>
    /// Turns expensive HDRP volume effects off by overriding their parameters on every active
    /// Volume's profile. Purely visual: the overrides only exist in memory for this session.
    /// </summary>
    internal sealed class VolumeTweaks
    {
        private const string Hdrp = "UnityEngine.Rendering.HighDefinition.";

        private readonly List<Il2CppObjectBase> _volumes = new();
        private readonly List<object> _defaultProfiles = new();
        private bool _defaultsSearched;
        private readonly HashSet<string> _reported = new();
        private double _nextScan;
        private int _lastSceneHandle = int.MinValue;
        private Type _volumeType;
        private System.Reflection.MethodInfo _activeScene;

        public void Apply(double now)
        {
            _volumeType ??= Reflect.FindType("UnityEngine.Rendering.Volume");
            if (_volumeType == null) return;

            // Rescan right after a scene change and otherwise every 10 s (FindObjectsOfType is not free).
            int scene = ActiveSceneHandle();
            if (scene != _lastSceneHandle)
            {
                _lastSceneHandle = scene;
                _nextScan = 0;
            }
            if (now >= _nextScan)
            {
                _nextScan = now + 10.0;
                Scan();
            }

            if (!_defaultsSearched)
            {
                _defaultsSearched = true;
                FindDefaultProfiles();
            }
            foreach (var profile in _defaultProfiles)
            {
                try { ApplyToProfile(profile); } catch (Exception) { }
            }

            for (int i = _volumes.Count - 1; i >= 0; i--)
            {
                try { ApplyToVolume(_volumes[i]); }
                catch (Exception)
                {
                    // Destroyed with its scene; the next scan picks up the new ones.
                    _volumes.RemoveAt(i);
                }
            }
        }

        private int ActiveSceneHandle()
        {
            try
            {
                _activeScene ??= Reflect.FindType("UnityEngine.SceneManagement.SceneManager")?.GetMethod("GetActiveScene", Type.EmptyTypes);
                var scene = _activeScene?.Invoke(null, null);
                var handle = Reflect.Get(scene, "handle");
                return handle != null ? Convert.ToInt32(handle) : 0;
            }
            catch { return 0; }
        }

        private void Scan()
        {
            _volumes.Clear();
            _volumes.AddRange(Reflect.FindObjects(_volumeType));
            if (_reported.Add("scan:" + _lastSceneHandle))
                Plugin.L.LogInfo($"Found {_volumes.Count} post-processing volumes in scene.");
        }

        private void ApplyToVolume(Il2CppObjectBase volume)
        {
            // profileRef = runtime instance if one was created, else the shared asset.
            ApplyToProfile(Reflect.Get(volume, "profileRef") ?? Reflect.Get(volume, "sharedProfile"));
        }

        /// <summary>
        /// HDRP's project-wide default profile (used where no scene volume overrides a value).
        /// Where it lives differs between HDRP versions, so several known places are tried.
        /// </summary>
        private void FindDefaultProfiles()
        {
            _defaultProfiles.Clear();
            var sources = new (string type, string instance, string[] props)[]
            {
                (Hdrp + "HDRenderPipelineGlobalSettings", "instance", new[] { "volumeProfile", "m_DefaultVolumeProfile" }),
                (Hdrp + "HDRenderPipelineAsset", null, new[] { "defaultVolumeProfile", "m_DefaultVolumeProfile" }),
            };
            foreach (var (typeName, instanceProp, props) in sources)
            {
                try
                {
                    var t = Reflect.FindType(typeName);
                    if (t == null) continue;
                    object owner;
                    if (instanceProp != null)
                    {
                        owner = Reflect.GetStatic(t, instanceProp);
                    }
                    else
                    {
                        // currentRenderPipeline is typed RenderPipelineAsset; rewrap as the HDRP asset.
                        var rp = Reflect.GetStatic(Reflect.FindType("UnityEngine.Rendering.GraphicsSettings"), "currentRenderPipeline") as Il2CppObjectBase;
                        owner = rp != null && Reflect.Il2CppClassName(rp) == typeName ? Reflect.Rewrap(rp, t) : null;
                    }
                    if (owner == null) continue;
                    foreach (var prop in props)
                    {
                        var profile = Reflect.Get(owner, prop);
                        if (profile == null) continue;
                        _defaultProfiles.Add(profile);
                        Plugin.L.LogInfo($"Default HDRP volume profile found via {t.Name}.{prop}");
                        break;
                    }
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"Default profile lookup on {typeName}: {e.GetBaseException().Message}");
                }
            }
        }

        private void ApplyToProfile(object profile)
        {
            if (profile == null) return;

            // Collected for a one-time diagnostic per distinct effect list (the game spawns
            // many short-lived, mostly empty profiles, so per-profile logging would spam).
            var names = new List<string>();
            foreach (var item in Reflect.Iterate(Reflect.Get(profile, "components")))
            {
                if (item is not Il2CppObjectBase comp) continue;
                var cls = Reflect.Il2CppClassName(comp);
                if (cls != null) names.Add(cls.Substring(cls.LastIndexOf('.') + 1));
                if (cls == null || !cls.StartsWith(Hdrp, StringComparison.Ordinal)) continue;
                var type = Reflect.FindType(cls);
                if (type == null) continue;
                ApplyToComponent(cls.Substring(Hdrp.Length), Reflect.Rewrap(comp, type));
            }
            if (names.Count > 0)
            {
                var list = string.Join(", ", names);
                if (_reported.Add("list:" + list)) Plugin.L.LogInfo("Profile effects: " + list);
            }
        }

        private void ApplyToComponent(string name, object c)
        {
            var s = Plugin.S;
            switch (name)
            {
                // Overrides are written (not the component deactivated) so a lower-priority or
                // default profile cannot bring the effect back.
                case "MotionBlur" when s.DisableMotionBlur.Value:
                case "ChromaticAberration" when s.DisableChromaticAberration.Value:
                case "FilmGrain" when s.DisableFilmGrain.Value:
                case "LensDistortion" when s.DisableLensDistortion.Value:
                case "Bloom" when s.DisableBloom.Value:
                    SetParam(name, c, "intensity", 0f);
                    break;
                case "DepthOfField" when s.DisableDepthOfField.Value:
                    SetParam(name, c, "focusMode", 0); // DepthOfFieldMode.Off
                    break;
                case "ScreenSpaceReflection" when s.DisableScreenSpaceReflections.Value:
                    SetParam(name, c, "enabled", false);
                    SetParam(name, c, "enabledTransparent", false);
                    break;
                case "GlobalIllumination" when s.DisableScreenSpaceGI.Value:
                    SetParam(name, c, "enable", false);
                    break;
                case "ContactShadows" when s.DisableContactShadows.Value:
                    SetParam(name, c, "enable", false);
                    break;
                case "VolumetricClouds" when s.DisableVolumetricClouds.Value:
                    SetParam(name, c, "enable", false);
                    break;
                case "Fog" when s.DisableVolumetricFog.Value:
                    SetParam(name, c, "enableVolumetricFog", false);
                    break;
                case "AmbientOcclusion" when s.DisableAmbientOcclusion.Value:
                    SetParam(name, c, "intensity", 0f);
                    break;
                case "HDShadowSettings":
                    if (s.MaxShadowDistance.Value > 0f)
                    {
                        var current = Reflect.Get(Reflect.Get(c, "maxShadowDistance"), "value");
                        if (current != null && Convert.ToSingle(current) > s.MaxShadowDistance.Value)
                            SetParam(name, c, "maxShadowDistance", s.MaxShadowDistance.Value);
                    }
                    if (s.ShadowCascades.Value > 0)
                    {
                        var current = Reflect.Get(Reflect.Get(c, "cascadeShadowSplitCount"), "value");
                        int wanted = Math.Clamp(s.ShadowCascades.Value, 1, 4);
                        if (current != null && Convert.ToInt32(current) > wanted)
                            SetParam(name, c, "cascadeShadowSplitCount", wanted);
                    }
                    break;
            }
        }

        /// <summary>Sets VolumeParameter.value and overrideState = true on a component field.</summary>
        private void SetParam(string name, object comp, string field, object value)
        {
            var param = Reflect.Get(comp, field);
            if (param == null) { Missing(name + "." + field); return; }
            var valueProp = Reflect.Prop(param.GetType(), "value");
            var overrideProp = Reflect.Prop(param.GetType(), "overrideState");
            if (valueProp == null || overrideProp == null) { Missing(name + "." + field + ".value"); return; }

            var target = Reflect.Coerce(value, valueProp.PropertyType);
            if (Equals(overrideProp.GetValue(param), true) && Equals(valueProp.GetValue(param), target)) return;
            overrideProp.SetValue(param, true);
            valueProp.SetValue(param, target);
            Report(name + "." + field, target);
        }

        private void Report(string what, object value)
        {
            if (_reported.Add("set:" + what)) Plugin.L.LogInfo($"HDRP {what} -> {value}");
        }

        private void Missing(string what)
        {
            if (_reported.Add("missing:" + what)) Plugin.L.LogWarning($"HDRP {what} not found in this game build, skipped.");
        }
    }
}
