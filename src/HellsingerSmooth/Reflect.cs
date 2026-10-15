using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace HellsingerSmooth
{
    /// <summary>
    /// Small reflection layer over the interop assemblies BepInEx generates at first launch.
    /// Every lookup is cached and every failure is reported as null/false instead of throwing,
    /// so a missing Unity/HDRP member only disables the one tweak that needs it.
    /// </summary>
    internal static class Reflect
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static readonly Dictionary<string, Type> TypeCache = new();
        private static readonly Dictionary<(Type, string), PropertyInfo> PropCache = new();

        public static Type FindType(string fullName)
        {
            if (TypeCache.TryGetValue(fullName, out var cached)) return cached;
            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { found = asm.GetType(fullName, false); } catch { }
                if (found != null) break;
            }
            // Interop assemblies load lazily: HDRP's is usually not in the AppDomain yet when
            // the plugin starts, so load the one that should contain the type and look again.
            if (found == null)
            {
                foreach (var asmName in CandidateAssemblies(fullName))
                {
                    var asm = LoadInterop(asmName);
                    try { found = asm?.GetType(fullName, false); } catch { }
                    if (found != null) break;
                }
            }
            // Only cache hits: interop assemblies may load after the first lookup.
            if (found != null) TypeCache[fullName] = found;
            return found;
        }

        private static IEnumerable<string> CandidateAssemblies(string fullName)
        {
            if (fullName.StartsWith("UnityEngine.Rendering.HighDefinition.", StringComparison.Ordinal))
                yield return "Unity.RenderPipelines.HighDefinition.Runtime";
            if (fullName.StartsWith("UnityEngine.Rendering.", StringComparison.Ordinal))
                yield return "Unity.RenderPipelines.Core.Runtime";
            if (fullName.StartsWith("Il2CppSystem.", StringComparison.Ordinal))
                yield return "Il2Cppmscorlib";
            if (fullName.StartsWith("UnityEngine.", StringComparison.Ordinal))
                yield return "UnityEngine.CoreModule";
        }

        private static readonly Dictionary<string, Assembly> Loaded = new();

        private static Assembly LoadInterop(string name)
        {
            if (Loaded.TryGetValue(name, out var cached)) return cached;
            Assembly asm = null;
            try { asm = Assembly.Load(new AssemblyName(name)); }
            catch
            {
                try
                {
                    var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "interop", name + ".dll");
                    if (System.IO.File.Exists(path)) asm = Assembly.LoadFrom(path);
                }
                catch { }
            }
            Loaded[name] = asm;
            return asm;
        }

        /// <summary>Most-derived property with that name (interop types re-declare overridden properties).</summary>
        public static PropertyInfo Prop(Type type, string name)
        {
            if (type == null) return null;
            if (PropCache.TryGetValue((type, name), out var cached)) return cached;
            PropertyInfo found = null;
            for (var t = type; t != null && found == null; t = t.BaseType)
                found = t.GetProperty(name, Any | BindingFlags.DeclaredOnly);
            PropCache[(type, name)] = found;
            return found;
        }

        private static readonly Dictionary<(Type, string), FieldInfo> FieldCache = new();

        /// <summary>Field lookup, for blittable interop structs which keep real fields.</summary>
        public static FieldInfo Field(Type type, string name)
        {
            if (type == null) return null;
            if (FieldCache.TryGetValue((type, name), out var cached)) return cached;
            FieldInfo found = null;
            for (var t = type; t != null && found == null; t = t.BaseType)
                found = t.GetField(name, Any | BindingFlags.DeclaredOnly);
            FieldCache[(type, name)] = found;
            return found;
        }

        /// <summary>Reads a property or field (instance; obj may be a boxed struct).</summary>
        public static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var p = Prop(obj.GetType(), name);
            if (p != null) return p.GetValue(obj);
            return Field(obj.GetType(), name)?.GetValue(obj);
        }

        public static Type MemberType(object obj, string name)
        {
            if (obj == null) return null;
            return Prop(obj.GetType(), name)?.PropertyType ?? Field(obj.GetType(), name)?.FieldType;
        }

        /// <summary>
        /// Writes a property or field if it differs. Works on boxed structs (the box is mutated),
        /// so callers must write a struct copy back to its owner afterwards. Returns true on write.
        /// </summary>
        public static bool SetMember(object obj, string name, object value)
        {
            if (obj == null) return false;
            var t = obj.GetType();
            var p = Prop(t, name);
            if (p != null && p.CanWrite)
            {
                var target = Coerce(value, p.PropertyType);
                if (Equals(p.GetValue(obj), target)) return false;
                p.SetValue(obj, target);
                return true;
            }
            var f = Field(t, name);
            if (f != null)
            {
                var target = Coerce(value, f.FieldType);
                if (Equals(f.GetValue(obj), target)) return false;
                f.SetValue(obj, target);
                return true;
            }
            return false;
        }

        /// <summary>Always writes (used to put a modified struct copy back).</summary>
        public static void ForceSetMember(object obj, string name, object value)
        {
            var p = Prop(obj.GetType(), name);
            if (p != null && p.CanWrite) { p.SetValue(obj, value); return; }
            Field(obj.GetType(), name)?.SetValue(obj, value);
        }

        private static MethodInfo _findObjects;
        private static bool _findObjectsHasFlag;

        /// <summary>UnityEngine.Object.FindObjectsOfType(type), re-wrapped as that interop type.</summary>
        public static List<Il2CppObjectBase> FindObjects(Type interopType)
        {
            var result = new List<Il2CppObjectBase>();
            if (interopType == null) return result;
            if (_findObjects == null)
            {
                var objectType = FindType("UnityEngine.Object");
                var il2cppTypeType = FindType("Il2CppSystem.Type");
                if (objectType == null || il2cppTypeType == null) return result;
                _findObjects = objectType.GetMethod("FindObjectsOfType", new[] { il2cppTypeType })
                    ?? objectType.GetMethod("FindObjectsOfType", new[] { il2cppTypeType, typeof(bool) });
                _findObjectsHasFlag = _findObjects != null && _findObjects.GetParameters().Length == 2;
                if (_findObjects == null) return result;
            }
            var il2cppType = Il2CppTypeOf(interopType);
            var found = _findObjects.Invoke(null, _findObjectsHasFlag ? new[] { il2cppType, false } : new[] { il2cppType });
            foreach (var o in Iterate(found))
                if (o is Il2CppObjectBase b && Rewrap(b, interopType) is Il2CppObjectBase w) result.Add(w);
            return result;
        }

        /// <summary>Enum value of an interop enum type by member name, or null if absent.</summary>
        public static object EnumByName(Type enumType, string name)
        {
            if (enumType == null || !enumType.IsEnum) return null;
            foreach (var n in Enum.GetNames(enumType))
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(enumType, n);
            return null;
        }

        public static object GetStatic(Type type, string name) => Prop(type, name)?.GetValue(null);

        /// <summary>Sets a static property only when it differs; returns true if a write happened.</summary>
        public static bool SetStaticIfDifferent(Type type, string name, object value)
        {
            var p = Prop(type, name);
            if (p == null || !p.CanWrite) return false;
            var target = Coerce(value, p.PropertyType);
            if (Equals(p.GetValue(null), target)) return false;
            p.SetValue(null, target);
            return true;
        }

        public static object Get(object obj, string name) => obj == null ? null : Prop(obj.GetType(), name)?.GetValue(obj);

        public static bool Has(Type type, string name) => Prop(type, name) != null;

        public static object Coerce(object value, Type target)
        {
            if (value == null) return null;
            if (target.IsInstanceOfType(value)) return value;
            if (target.IsEnum) return Enum.ToObject(target, Convert.ToInt32(value));
            return Convert.ChangeType(value, target);
        }

        /// <summary>Il2Cpp class "Namespace.Name" of a live interop object.</summary>
        public static string Il2CppClassName(Il2CppObjectBase obj)
        {
            var ptr = obj?.Pointer ?? IntPtr.Zero;
            if (ptr == IntPtr.Zero) return null;
            var klass = IL2CPP.il2cpp_object_get_class(ptr);
            if (klass == IntPtr.Zero) return null;
            var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        /// <summary>Re-wraps an interop object as a more derived interop type (same native pointer).</summary>
        public static object Rewrap(Il2CppObjectBase obj, Type type)
        {
            if (obj == null || type == null) return null;
            return Activator.CreateInstance(type, obj.Pointer);
        }

        /// <summary>Iterates an Il2Cpp List&lt;T&gt; or array through its Count/get_Item members.</summary>
        public static IEnumerable<object> Iterate(object il2cppCollection)
        {
            if (il2cppCollection == null) yield break;
            if (il2cppCollection is IEnumerable e)
            {
                foreach (var x in e) yield return x;
                yield break;
            }
            var t = il2cppCollection.GetType();
            var count = Prop(t, "Count") ?? Prop(t, "Length");
            var item = t.GetMethod("get_Item", new[] { typeof(int) });
            if (count == null || item == null) yield break;
            int n = Convert.ToInt32(count.GetValue(il2cppCollection));
            for (int i = 0; i < n; i++) yield return item.Invoke(il2cppCollection, new object[] { i });
        }

        /// <summary>Il2CppSystem.Type for an interop type, returned as object (no Il2Cppmscorlib reference at build time).</summary>
        public static object Il2CppTypeOf(Type managed)
        {
            var from = typeof(Il2CppType).GetMethod("From", new[] { typeof(Type) });
            return from?.Invoke(null, new object[] { managed });
        }
    }
}
