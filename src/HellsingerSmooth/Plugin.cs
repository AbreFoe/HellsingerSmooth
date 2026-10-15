using System;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace HellsingerSmooth
{
    /// <summary>
    /// Performance / frame-pacing tweaks for Metal: Hellsinger.
    ///
    /// Scope on purpose: rendering cost (HDRP volume effects, LOD, shadows, textures) and asset
    /// upload pacing only. Nothing here touches Time, physics, audio, input or any game
    /// (Assembly-CSharp) code, so beat detection, hit windows and music sync are untouched.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "henry.hellsinger.smooth";
        public const string Name = "Hellsinger Smooth";
        public const string Version = "1.3.0";

        internal static ManualLogSource L;
        internal static Settings S;

        private static QualityTweaks _quality;
        private static VolumeTweaks _volumes;
        private static ResolutionTweaks _resolution;
        private static FrameStats _stats;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _nextQuality, _nextVolumes;
        private static Func<int> _frameCount;
        private static int _lastFrame = -1;
        private static double _lastTick;

        public override void Load()
        {
            L = Log;
            S = new Settings(Config);
            if (!S.Enabled.Value)
            {
                L.LogInfo("Disabled in config, nothing applied.");
                return;
            }

            _quality = new QualityTweaks();
            _volumes = new VolumeTweaks();
            _resolution = new ResolutionTweaks();
            _stats = new FrameStats();

            if (!HookMonoBehaviour() && !HookVolumeManager())
                L.LogError("Could not hook a per-frame callback. Only startup quality settings were applied.");

            // Apply what we can right away; the per-frame hook re-applies after the game changes settings.
            SafeRun("startup quality", () => _quality.Apply());
            L.LogInfo($"{Name} {Version} loaded.");
        }

        /// <summary>Preferred: an injected MonoBehaviour on BepInEx's manager object (Update every frame).</summary>
        private bool HookMonoBehaviour()
        {
            try
            {
                if (!ClassInjector.IsTypeRegisteredInIl2Cpp<SmoothTicker>())
                    ClassInjector.RegisterTypeInIl2Cpp<SmoothTicker>();
                AddComponent<SmoothTicker>();
                L.LogInfo("Per-frame hook: injected MonoBehaviour");
                return true;
            }
            catch (Exception e)
            {
                L.LogWarning("MonoBehaviour hook failed, trying fallback: " + e.GetBaseException().Message);
                return false;
            }
        }

        /// <summary>Fallback: postfix on HDRP's VolumeManager.Update (runs every frame per camera).</summary>
        private static bool HookVolumeManager()
        {
            try
            {
                var vm = Reflect.FindType("UnityEngine.Rendering.VolumeManager");
                if (vm == null)
                {
                    L.LogWarning("VolumeManager type not found.");
                    return false;
                }
                var harmony = new Harmony(Guid);
                var postfix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(TickPostfix), BindingFlags.NonPublic | BindingFlags.Static));
                int patched = 0;
                foreach (var m in vm.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "Update") continue;
                    harmony.Patch(m, postfix: postfix);
                    patched++;
                }
                if (patched == 0) return false;
                L.LogInfo($"Per-frame hook: VolumeManager.Update ({patched} overloads)");
                return true;
            }
            catch (Exception e)
            {
                L.LogWarning("VolumeManager hook failed: " + e.GetBaseException().Message);
                return false;
            }
        }

        private static void TickPostfix() => Tick();

        internal static void TickFromBehaviour() => Tick();

        internal static string ScaleSummary() => _resolution?.TakeSummary();

        private static int FrameCount()
        {
            if (_frameCount == null)
            {
                var getter = Reflect.FindType("UnityEngine.Time")?.GetProperty("frameCount")?.GetGetMethod();
                _frameCount = getter != null ? (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), getter) : () => -1;
            }
            return _frameCount();
        }

        private static void Tick()
        {
            try
            {
                // The fallback hook fires once per camera; only do work once per frame.
                int frame = FrameCount();
                if (frame >= 0)
                {
                    if (frame == _lastFrame) return;
                    _lastFrame = frame;
                }

                double now = Clock.Elapsed.TotalSeconds;
                double dt = _lastTick > 0 ? now - _lastTick : 0;
                _lastTick = now;
                _stats.OnFrame(now);
                SafeRun("resolution", () => _resolution.OnFrame(now, dt));

                if (now >= _nextQuality)
                {
                    _nextQuality = now + 1.0;
                    SafeRun("quality", () => _quality.Apply());
                }
                if (now >= _nextVolumes)
                {
                    _nextVolumes = now + 2.0;
                    SafeRun("volumes", () => _volumes.Apply(now));
                }
            }
            catch (Exception e)
            {
                L.LogError("Tick: " + e);
            }
        }

        private static int _errors;
        internal static void SafeRun(string what, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                // Never let a tweak break the frame; cap log spam.
                if (_errors++ < 20) L.LogWarning($"{what}: {e.GetBaseException().GetType().Name}: {e.GetBaseException().Message}");
            }
        }
    }

    internal sealed class Settings
    {
        public readonly ConfigEntry<bool> Enabled;

        // Frame pacing
        public readonly ConfigEntry<int> TargetFrameRate;
        public readonly ConfigEntry<int> VSyncCount;
        public readonly ConfigEntry<int> MaxQueuedFrames;

        // Streaming / hitching
        public readonly ConfigEntry<int> AsyncUploadTimeSliceMs;
        public readonly ConfigEntry<int> AsyncUploadBufferSizeMB;
        public readonly ConfigEntry<bool> AsyncUploadPersistentBuffer;
        public readonly ConfigEntry<int> TextureQualityReduction;

        // Geometry
        public readonly ConfigEntry<float> LodBias;
        public readonly ConfigEntry<int> MaximumLODLevel;
        public readonly ConfigEntry<int> SkinWeights;

        // Resolution
        public readonly ConfigEntry<string> RenderScaleMode;
        public readonly ConfigEntry<int> RenderScale;
        public readonly ConfigEntry<int> DynamicMinScale;
        public readonly ConfigEntry<int> DynamicMaxScale;
        public readonly ConfigEntry<int> DynamicTargetFps;
        public readonly ConfigEntry<string> Upscaler;
        public readonly ConfigEntry<int> FullscreenMode;

        // HDRP effects
        public readonly ConfigEntry<bool> DisableMotionBlur;
        public readonly ConfigEntry<bool> DisableDepthOfField;
        public readonly ConfigEntry<bool> DisableChromaticAberration;
        public readonly ConfigEntry<bool> DisableFilmGrain;
        public readonly ConfigEntry<bool> DisableLensDistortion;
        public readonly ConfigEntry<bool> DisableBloom;
        public readonly ConfigEntry<bool> DisableScreenSpaceReflections;
        public readonly ConfigEntry<bool> DisableScreenSpaceGI;
        public readonly ConfigEntry<bool> DisableContactShadows;
        public readonly ConfigEntry<bool> DisableVolumetricClouds;
        public readonly ConfigEntry<bool> DisableVolumetricFog;
        public readonly ConfigEntry<bool> DisableAmbientOcclusion;
        public readonly ConfigEntry<float> MaxShadowDistance;
        public readonly ConfigEntry<int> ShadowCascades;

        // Diagnostics
        public readonly ConfigEntry<int> StatsIntervalSeconds;

        public Settings(ConfigFile c)
        {
            Enabled = c.Bind("General", "Enabled", true, "Master switch. false = the mod does nothing (handy for A/B comparison).");

            TargetFrameRate = c.Bind("FramePacing", "TargetFrameRate", 0,
                "FPS cap. 0 = leave the game's setting (the game caps at 60). Set 75/120/144 for a high-refresh monitor; rhythm timing follows the music, not frames. A cap a bit below your usual FPS (e.g. 60) gives even frame pacing and fewer microstutters. Ignored by Unity while VSync is on.");
            VSyncCount = c.Bind("FramePacing", "VSyncCount", -1,
                "-1 = leave the game's setting, 0 = VSync off, 1 = VSync on.");
            MaxQueuedFrames = c.Bind("FramePacing", "MaxQueuedFrames", -1,
                "-1 = leave default. 1 = lower input latency (can lower FPS slightly), 2 = Unity default, 3 = smoother when CPU-bound but more latency.");

            AsyncUploadTimeSliceMs = c.Bind("Streaming", "AsyncUploadTimeSliceMs", 4,
                "Milliseconds per frame the render thread may spend uploading textures/meshes. Unity default 2. Higher = streamed assets arrive in fewer, smaller hitches. 0 = leave default.");
            AsyncUploadBufferSizeMB = c.Bind("Streaming", "AsyncUploadBufferSizeMB", 32,
                "Ring buffer for async uploads. Unity default 16. Too small forces costly resizes (hitches) when big textures load. 0 = leave default.");
            AsyncUploadPersistentBuffer = c.Bind("Streaming", "AsyncUploadPersistentBuffer", true,
                "Keep the upload buffer allocated instead of freeing/reallocating it (avoids allocation hitches).");
            TextureQualityReduction = c.Bind("Streaming", "TextureQualityReduction", 0,
                "0 = full textures, 1 = half resolution, 2 = quarter. Use 1 if your GPU (4 GB like GTX 1050 Ti) runs out of video memory and stutters.");

            LodBias = c.Bind("Geometry", "LodBias", 0f,
                "0 = leave the game's value. Lower (e.g. 0.7) switches to simpler models closer to the camera = less GPU/CPU work.");
            MaximumLODLevel = c.Bind("Geometry", "MaximumLODLevel", -1,
                "-1 = leave. 1 = never use the most detailed LOD of each model.");
            SkinWeights = c.Bind("Geometry", "SkinWeights", 0,
                "Bones per vertex for animated models. 0 = leave, 2 = cheaper skinning, 4 = full.");

            RenderScaleMode = c.Bind("Resolution", "RenderScaleMode", "Dynamic",
                "Dynamic = resolution follows load: drops in heavy fights to hold FPS, rises again in calm moments (between DynamicMinScale and DynamicMaxScale). Fixed = always RenderScale. Off = native resolution.");
            RenderScale = c.Bind("Resolution", "RenderScale", 80,
                "Fixed mode: percent of screen resolution the 3D scene is rendered at, then upscaled; the HUD stays sharp. 80 = good balance on GTX 1050 Ti at 1080p, 67 = maximum FPS. Range 50-99.");
            DynamicMinScale = c.Bind("Resolution", "DynamicMinScale", 70,
                "Dynamic mode: lowest render scale percent (used in the heaviest moments).");
            DynamicMaxScale = c.Bind("Resolution", "DynamicMaxScale", 90,
                "Dynamic mode: highest render scale percent (used when there is spare GPU time). 100 = allow native.");
            DynamicTargetFps = c.Bind("Resolution", "DynamicTargetFps", 0,
                "Dynamic mode: FPS to defend. 0 = the game's frame cap (60 by default, or TargetFrameRate below).");
            Upscaler = c.Bind("Resolution", "Upscaler", "FSR",
                "How the lower-resolution image is upscaled: FSR (AMD FidelityFX Super Resolution 1.0, sharpest), CAS, CatmullRom, TAAU.");

            FullscreenMode = c.Bind("Resolution", "FullscreenMode", -1,
                "-1 = leave. 0 = exclusive fullscreen (often smoother frame pacing and lower latency on DX11), 1 = borderless window, 3 = windowed.");

            DisableMotionBlur = c.Bind("Effects", "DisableMotionBlur", true, "Turn off motion blur (cheaper, clearer image in fast fights).");
            DisableDepthOfField = c.Bind("Effects", "DisableDepthOfField", true, "Turn off depth of field.");
            DisableChromaticAberration = c.Bind("Effects", "DisableChromaticAberration", true, "Turn off chromatic aberration.");
            DisableFilmGrain = c.Bind("Effects", "DisableFilmGrain", true, "Turn off film grain.");
            DisableLensDistortion = c.Bind("Effects", "DisableLensDistortion", true, "Turn off lens distortion.");
            DisableBloom = c.Bind("Effects", "DisableBloom", false, "Turn off bloom (glow). Changes the look a lot; off by default.");
            DisableScreenSpaceReflections = c.Bind("Effects", "DisableScreenSpaceReflections", true, "Turn off screen-space reflections (expensive on GTX 10xx).");
            DisableScreenSpaceGI = c.Bind("Effects", "DisableScreenSpaceGI", true, "Turn off screen-space global illumination if a level uses it.");
            DisableContactShadows = c.Bind("Effects", "DisableContactShadows", true, "Turn off contact shadows (small detail shadows).");
            DisableVolumetricClouds = c.Bind("Effects", "DisableVolumetricClouds", true, "Turn off volumetric clouds if a level uses them.");
            DisableVolumetricFog = c.Bind("Effects", "DisableVolumetricFog", false,
                "Turn off volumetric fog/light shafts (regular fog stays). Big GPU saving, but changes the atmosphere; try true if FPS is still low.");
            DisableAmbientOcclusion = c.Bind("Effects", "DisableAmbientOcclusion", false,
                "Force SSAO off regardless of the in-game setting. The in-game SSAO option usually does the same.");
            MaxShadowDistance = c.Bind("Effects", "MaxShadowDistance", 0f,
                "0 = leave. Otherwise caps how far (meters) shadows are drawn, e.g. 60. Fewer shadow casters = more FPS.");

            ShadowCascades = c.Bind("Effects", "ShadowCascades", 0,
                "0 = leave. Number of sun shadow cascades (1-4). 2 renders the sun's shadows twice instead of four times per frame.");

            StatsIntervalSeconds = c.Bind("Diagnostics", "StatsIntervalSeconds", 30,
                "Every N seconds write average FPS, 1% low and hitch count to BepInEx/LogOutput.log. 0 = off.");
        }
    }

    /// <summary>Lives on BepInEx's DontDestroyOnLoad manager object; drives the per-frame work.</summary>
    public class SmoothTicker : UnityEngine.MonoBehaviour
    {
        public SmoothTicker(IntPtr pointer) : base(pointer) { }

        private void Update() => Plugin.TickFromBehaviour();
    }
}
