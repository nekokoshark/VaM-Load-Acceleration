using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;

public static class PackageStreamRuntime {
    public static readonly bool BaselineInput = false;
    public static long Streamed, Fallback;
    private static readonly MethodInfo ReadPath = typeof(FileManager).GetMethod("ReadAllText", new[] { typeof(string), typeof(bool) });
    private static readonly MethodInfo ReadEntry = typeof(FileManager).GetMethod("ReadAllText", new[] { typeof(FileEntry) });
    private static readonly MethodInfo Parse = typeof(JSON).GetMethod("Parse", new[] { typeof(string) });
    private static bool Patched(MethodInfo method) {
        var patches = Harmony.GetPatchInfo(method);
        return patches != null && patches.Owners.Count > 0;
    }
    public static JSONNode ParseCachePath(string path, bool restrictPath) {
        // Preserve foreign readers/parsers and their null/exception behavior.
        if (!StartupPackageJson.JsonStreamParser.CanStream || Patched(ReadPath) || Patched(ReadEntry)) {
            Interlocked.Increment(ref Fallback);
            string original = FileManager.ReadAllText(path, restrictPath);
            return original == null ? null : JSON.Parse(original);
        }
        string fallbackText = null;
        JSONNode result = null;
        bool usedStreaming = false;
        using (FileEntryStreamReader reader = FileManager.OpenStreamReader(path, restrictPath)) {
            if (StartupPackageJson.JsonStreamParser.CanStreamEntry(reader)) {
                // Original null reader maps to a null input, not empty JSON.
                if (reader.StreamReader != null) {
                    VaM.Memory.PackageTreeDisk.WriteReceipt receipt;
                    if (!VaM.Memory.PackageTreeDisk.TryReadForLoad(path, reader, out result, out receipt)) {
                        result = StartupPackageJson.JsonStreamParser.Parse(reader.StreamReader);
                        VaM.Memory.PackageTreeDisk.Commit(receipt, reader, result);
                    }
                }
                usedStreaming = true;
            } else fallbackText = reader.ReadToEnd();
        }
        // For virtual overrides keep Dispose-before-Parse, as ReadAllText did.
        if (usedStreaming) { Interlocked.Increment(ref Streamed); return result; }
        Interlocked.Increment(ref Fallback);
        return fallbackText == null ? null : JSON.Parse(fallbackText);
    }
    public static void Install(Harmony harmony) {
        var method = typeof(VarPackage).GetMethod("GetJSONCache", new[] { typeof(string) });
        harmony.Patch(method, transpiler: new HarmonyMethod(typeof(PackageStreamRuntime), "Rewrite"));
    }
    private static bool Call(CodeInstruction code, MethodInfo method) {
        return (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && Object.Equals(code.operand, method);
    }
    private static int Local(CodeInstruction code) {
        if (code.opcode == OpCodes.Stloc_0 || code.opcode == OpCodes.Ldloc_0) return 0;
        if (code.opcode == OpCodes.Stloc_1 || code.opcode == OpCodes.Ldloc_1) return 1;
        if (code.opcode == OpCodes.Stloc_2 || code.opcode == OpCodes.Ldloc_2) return 2;
        if (code.opcode == OpCodes.Stloc_3 || code.opcode == OpCodes.Ldloc_3) return 3;
        if (code.operand is LocalBuilder) return ((LocalBuilder)code.operand).LocalIndex;
        if (code.operand is LocalVariableInfo) return ((LocalVariableInfo)code.operand).LocalIndex;
        if (code.operand is int) return (int)code.operand;
        if (code.operand is byte) return (byte)code.operand;
        if (code.operand is short) return (short)code.operand;
        return -1;
    }
    public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input, ILGenerator generator) {
        var codes = new List<CodeInstruction>(input);
        int read = -1, parse = -1;
        for (int i = 0; i < codes.Count; i++) {
            if (Call(codes[i], ReadPath)) { if (read >= 0) throw new InvalidDataException("Multiple cache readers"); read = i; }
            if (Call(codes[i], Parse)) { if (parse >= 0) throw new InvalidDataException("Multiple cache parsers"); parse = i; }
        }
        // Exact original cold-load pattern. Labels, EH, loaded/root fields and
        // other patches' prefix/finalizer ownership remain on the original method.
        if (read < 0 || parse != read + 6 || !codes[read + 1].opcode.Name.StartsWith("stloc") ||
            !codes[read + 2].opcode.Name.StartsWith("ldloc") || !codes[read + 3].opcode.Name.StartsWith("brfalse") ||
            codes[read + 4].opcode != OpCodes.Ldarg_0 || !codes[read + 5].opcode.Name.StartsWith("ldloc"))
            throw new InvalidDataException("Unrecognized GetJSONCache cold-load pattern");
        int local = Local(codes[read + 1]);
        if (local < 0 || Local(codes[read + 2]) != local || Local(codes[read + 5]) != local)
            throw new InvalidDataException("Cold-load locals disagree");
        var node = generator.DeclareLocal(typeof(JSONNode));
        codes[read].operand = typeof(PackageStreamRuntime).GetMethod("ParseCachePath");
        foreach (int i in new[] { read + 1, read + 2, read + 5 }) {
            codes[i].opcode = i == read + 1 ? OpCodes.Stloc : OpCodes.Ldloc;
            codes[i].operand = node;
        }
        // JSONNode is already parsed; leave the original AsObject/root store.
        codes[parse].opcode = OpCodes.Nop;
        codes[parse].operand = null;
        return codes;
    }
}
