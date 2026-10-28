using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using HarmonyLib;

public static class CanonicalPathsRuntime {
    public const string Id = "local.vam.startup-canonical-paths";
    public static readonly bool BaselinePaths = false;
    public static long Projected, Fallback;
    private const BindingFlags StaticAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    public static readonly MethodInfo Target = typeof(Path).GetMethod("CanonicalizePath", StaticAll);
    private static readonly MethodInfo[] Omitted = {
        typeof(Path).GetMethod("GetPathRoot", new[] { typeof(string) }),
        typeof(Path).GetMethod("IsPathRooted", new[] { typeof(string) }),
        typeof(Path).GetMethod("SameRoot", StaticAll),
        typeof(Path).GetMethod("IsDsc", StaticAll),
        typeof(string).GetMethod("Split", new[] { typeof(char[]) }),
        typeof(string).GetMethod("Trim", Type.EmptyTypes),
        typeof(string).GetMethod("TrimEnd", new[] { typeof(char[]) })
    };
    // In the installed Harmony build, ToPatchInfo creates each registry value
    // once. Add/Remove publish new arrays on that same public PatchInfo object.
    // Keep the live objects, NOT a cached verdict or a snapshot of the arrays.
    private static PatchInfo[] guardStates;
    private static readonly Func<MethodBase, PatchInfo> RegistryInfo = (Func<MethodBase, PatchInfo>)Delegate.CreateDelegate(
        typeof(Func<MethodBase, PatchInfo>), typeof(Harmony).Assembly.GetType("HarmonyLib.Public.Patching.PatchManager", true)
            .GetMethod("ToPatchInfo", StaticAll));
    public static bool GuardIdentitiesCurrent() {
        for (int i = 0; i < Omitted.Length; i++)
            if (!Object.ReferenceEquals(guardStates[i], RegistryInfo(Omitted[i]))) return false;
        return true;
    }
    public static void Install(Harmony harmony) {
        if (BaselinePaths) return;
        if (Path.DirectorySeparatorChar != '\\' || Path.AltDirectorySeparatorChar != '/' || Path.VolumeSeparatorChar != ':' || Target == null)
            throw new NotSupportedException("Different canonical path implementation");
        foreach (MethodInfo method in Omitted) if (method == null) throw new MissingMethodException("Canonical path dependency");
        guardStates = new PatchInfo[Omitted.Length];
        for (int i = 0; i < Omitted.Length; i++) guardStates[i] = RegistryInfo(Omitted[i]);
        if (!GuardIdentitiesCurrent()) throw new InvalidOperationException("Different Harmony patch registry");
        harmony.Patch(Target, prefix: new HarmonyMethod(typeof(CanonicalPathsRuntime), "Before"));
    }
    private static bool Whitespace(char c) {
        // Reject a superset of this old corlib's Trim characters, including
        // the legacy zero-width entries. The original route handles them.
        return Char.IsWhiteSpace(c) || c == '\u180e' || c == '\u200b' || c == '\ufeff';
    }
    public static bool TryProject(string path, out string result) {
        result = null;
        if (path == null || path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/') ||
            !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))) return false;
        int start = 3;
        bool slash = path[2] == '/';
        for (int i = 3; i <= path.Length; i++) {
            if (i != path.Length && path[i] != '\\' && path[i] != '/') continue;
            int length = i - start;
            // Empty internal components require the original duplicate-
            // separator rule. Exactly one final separator is handled below.
            if (length == 0 && i != path.Length) return false;
            if (length > 0 && (Whitespace(path[i - 1]) ||
                (length == 1 && path[start] == '.') || (length == 2 && path[start] == '.' && path[start + 1] == '.'))) return false;
            if (i < path.Length) { slash |= path[i] == '/'; start = i + 1; }
        }
        // InvalidPathChars is the actual public mutable array, not a copied
        // constant. Exceptions still take the original validation path.
        if (path.IndexOfAny(Path.InvalidPathChars) >= 0) return false;
        int end = path.Length;
        if (end > 3 && (path[end - 1] == '\\' || path[end - 1] == '/')) end--;
        if (!slash) result = end == path.Length ? path : path.Substring(0, end);
        else {
            char[] chars = new char[end];
            for (int i = 0; i < end; i++) chars[i] = path[i] == '/' ? '\\' : path[i];
            result = new string(chars);
        }
        return true;
    }
    public static bool Before(string path, ref string __result) {
        string result;
        if (!TryProject(path, out result)) { Interlocked.Increment(ref Fallback); return true; }
        Thread.MemoryBarrier();
        foreach (PatchInfo patches in guardStates) {
            if (patches.prefixes.Length != 0 || patches.postfixes.Length != 0 || patches.transpilers.Length != 0 || patches.finalizers.Length != 0 || patches.ilmanipulators.Length != 0) {
                Interlocked.Increment(ref Fallback); return true;
            }
        }
        __result = result;
        Interlocked.Increment(ref Projected);
        return false;
    }
}
