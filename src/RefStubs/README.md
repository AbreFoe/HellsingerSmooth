Reference-only stubs. They mirror the names and IntPtr constructors of the interop types
BepInEx generates on the player's machine (Il2CppSystem.Object -> UnityEngine.MonoBehaviour),
so the plugin can derive from MonoBehaviour without shipping game files. They are never
copied next to the plugin; at runtime the real BepInEx/interop assemblies are loaded instead.
