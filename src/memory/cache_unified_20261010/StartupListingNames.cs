using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using HarmonyLib;
using MVR.FileManagement;

public static class ListingNamesRuntime {
    public const string Id = "local.vam.startup-listing-names";
    public static readonly bool BaselineListings = false;
    public static long Files, Dirs, Projected, Fallback;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static readonly MethodInfo GetFiles = typeof(FileManager).GetMethod("GetFiles", new[] { typeof(string), typeof(string), typeof(bool) });
    public static readonly MethodInfo GetDirs = typeof(FileManager).GetMethod("GetDirectories", new[] { typeof(string), typeof(string), typeof(bool) });
    private static readonly MethodBase[] FileBypass = {
        typeof(SystemFileEntry).GetConstructor(new[] { typeof(string) }), typeof(FileEntry).GetConstructor(new[] { typeof(string) }),
        typeof(FileEntry).GetProperty("Name").GetGetMethod(), typeof(SystemDirectoryEntry).GetProperty("Files").GetGetMethod(),
        typeof(DirectoryEntry).GetMethod("GetFiles"), typeof(FileInfo).GetConstructor(new[] { typeof(string) }),
        typeof(File).GetMethod("Exists", new[] { typeof(string) }), typeof(FileInfo).GetProperty("Length").GetGetMethod(),
        typeof(FileInfo).GetProperty("Exists").GetGetMethod(), typeof(FileSystemInfo).GetProperty("LastWriteTime").GetGetMethod(),
        typeof(FileSystemInfo).GetMethod("Refresh", All, null, new[] { typeof(bool) }, null), typeof(FileInfo).GetMethod("InternalRefresh", All),
        typeof(Path).GetMethod("GetFullPath", new[] { typeof(string) }) };
    private static readonly MethodBase[] DirBypass = {
        typeof(SystemDirectoryEntry).GetConstructor(new[] { typeof(string) }), typeof(DirectoryEntry).GetConstructor(new[] { typeof(string) }),
        typeof(DirectoryEntry).GetProperty("Name").GetGetMethod(), typeof(SystemDirectoryEntry).GetProperty("SubDirectories").GetGetMethod(),
        typeof(Directory).GetMethod("GetLastWriteTime", new[] { typeof(string) }), typeof(Path).GetMethod("GetFullPath", new[] { typeof(string) }),
        typeof(DirectoryInfo).GetConstructor(new[] { typeof(string) }), typeof(FileSystemInfo).GetProperty("LastWriteTime").GetGetMethod(),
        typeof(FileSystemInfo).GetMethod("Refresh", All, null, new[] { typeof(bool) }, null), typeof(DirectoryInfo).GetMethod("InternalRefresh", All) };
    private static bool Foreign(MethodBase method) {
        if (method == null) throw new MissingMethodException("Listing projection dependency differs");
        var info = Harmony.GetPatchInfo(method);
        if (info == null) return false;
        foreach (var patches in new[] {info.Prefixes,info.Postfixes,info.Transpilers,info.Finalizers}) foreach(var patch in patches) {
            if(patch.owner==Id) continue;
            // The exact constructor-only snapshot rewrite has no name effects.
            // Keep name-only projection active when both loading modules coexist;
            // any other constructor observer still requires the original route.
            if(patch.owner==VaM.Memory.AssetEntryMetadata.Id &&
               ((method==typeof(SystemFileEntry).GetConstructor(new[]{typeof(string)}) && patch.PatchMethod==AccessTools.Method(typeof(VaM.Memory.AssetEntryMetadata),"Rewrite")) ||
                (method==typeof(FileSystemInfo).GetMethod("Refresh",All,null,new[]{typeof(bool)},null) && patch.PatchMethod==AccessTools.Method(typeof(VaM.Memory.AssetEntryMetadata),"RefreshRewrite"))))continue;
            return true;
        }
        return false;
    }
    private static bool OriginalRoute(MethodInfo target, MethodBase[] bypass) {
        if (BaselineListings || Foreign(target)) return true;
        foreach (MethodBase method in bypass) if (Foreign(method)) return true;
        return false;
    }
    public static void Install(Harmony harmony) {
        harmony.Patch(GetFiles, transpiler: new HarmonyMethod(typeof(ListingNamesRuntime), "FilesRewrite"));
        harmony.Patch(GetDirs, transpiler: new HarmonyMethod(typeof(ListingNamesRuntime), "DirsRewrite"));
    }
    public static IEnumerable<CodeInstruction> FilesRewrite(IEnumerable<CodeInstruction> input, ILGenerator generator) { return Rewrite(input, generator, "TryFiles"); }
    public static IEnumerable<CodeInstruction> DirsRewrite(IEnumerable<CodeInstruction> input, ILGenerator generator) { return Rewrite(input, generator, "TryDirs"); }
    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input, ILGenerator generator, string helper) {
        var codes = new List<CodeInstruction>(input);
        MethodInfo resolver = typeof(FileManager).GetMethod("GetDirectoryEntry", new[] { typeof(string), typeof(bool) });
        int call = -1;
        for (int i = 0; i < codes.Count; i++) if (codes[i].opcode == OpCodes.Call && Object.Equals(codes[i].operand, resolver)) {
            if (call >= 0) throw new InvalidDataException("Multiple listing root resolvers");
            call = i;
        }
        if (call < 0 || codes[call + 1].opcode != OpCodes.Dup || !codes[call + 2].opcode.Name.StartsWith("brtrue")) throw new InvalidDataException("Listing root guard differs");
        var label = (Label)codes[call + 2].operand;
        int start = codes.FindIndex(c => c.labels.Contains(label));
        if (start < call + 3 || codes[start].blocks.Count != 0 || codes[start - 1].opcode != OpCodes.Throw) throw new InvalidDataException("Listing root continuation differs");
        // Original non-null root is still on the evaluation stack. Store it,
        // try the name projection, then restore that same root on fallback.
        // All original security/null checks, root hooks and EH remain intact.
        var root = generator.DeclareLocal(typeof(DirectoryEntry));
        var result = generator.DeclareLocal(typeof(string[]));
        Label fallback = generator.DefineLabel();
        var first = new CodeInstruction(OpCodes.Stloc, root);
        first.labels.AddRange(codes[start].labels); codes[start].labels.Clear();
        var restore = new CodeInstruction(OpCodes.Ldloc, root); restore.labels.Add(fallback);
        codes.InsertRange(start, new[] { first, new CodeInstruction(OpCodes.Ldloc, root), new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldloca, result),
            new CodeInstruction(OpCodes.Call, typeof(ListingNamesRuntime).GetMethod(helper)), new CodeInstruction(OpCodes.Brfalse, fallback),
            new CodeInstruction(OpCodes.Ldloc, result), new CodeInstruction(OpCodes.Ret), restore });
        return codes;
    }
    private static string Name(string path) { return Regex.Replace(path.Replace('\\', '/'), ".*/", string.Empty); }
    private static string Pattern(string pattern) { return pattern == null ? null : "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$"; }
    private static void Add(List<string> output, string dir, string name, string pattern) {
        if (pattern == null || Regex.IsMatch(name, pattern)) output.Add(dir + "\\" + name);
    }
    public static bool TryFiles(DirectoryEntry entry, string __0, string __1, out string[] __result) {
        __result = null;
        if (entry.GetType() != typeof(SystemDirectoryEntry) || OriginalRoute(GetFiles, FileBypass)) { Interlocked.Increment(ref Fallback); return false; }
        var root = (SystemDirectoryEntry)entry;
        string pattern = Pattern(__1);
        var output = new List<string>();
        // Preserve directory-before-file order and registered unpacked VARs.
        foreach (string path in Directory.GetDirectories(root.FixedPath)) if (FileManager.IsPackage(path)) Add(output, __0, Name(path), pattern);
        string[] paths = Directory.GetFiles(root.FixedPath);
        foreach (string path in paths) Add(output, __0, Name(path), pattern);
        Interlocked.Add(ref Projected, paths.Length);
        Interlocked.Increment(ref Files);
        __result = output.ToArray();
        return true;
    }
    public static bool TryDirs(DirectoryEntry entry, string __0, string __1, out string[] __result) {
        __result = null;
        if (entry.GetType() != typeof(SystemDirectoryEntry) || OriginalRoute(GetDirs, DirBypass)) { Interlocked.Increment(ref Fallback); return false; }
        var root = (SystemDirectoryEntry)entry;
        string pattern = Pattern(__1);
        var output = new List<string>();
        string[] paths = Directory.GetDirectories(root.FixedPath);
        foreach (string path in paths) {
            VarPackage package = FileManager.GetPackage(path);
            if (package != null) Add(output, __0, package.RootDirectory.Name, pattern);
            else {
                // Original child constructor rejected directories that disappeared
                // after enumeration. Keep that check, omit unused child mtime.
                if (!Directory.Exists(path)) throw new Exception("Directory " + path.Replace('/', '\\') + " does not exist");
                Add(output, __0, Name(path), pattern);
            }
        }
        foreach (string path in Directory.GetFiles(root.FixedPath)) {
            VarDirectoryEntry packageRoot = FileManager.GetVarRootDirectoryEntryFromPath(path);
            if (packageRoot != null) Add(output, __0, packageRoot.Name, pattern);
        }
        Interlocked.Add(ref Projected, paths.Length);
        Interlocked.Increment(ref Dirs);
        __result = output.ToArray();
        return true;
    }
}
