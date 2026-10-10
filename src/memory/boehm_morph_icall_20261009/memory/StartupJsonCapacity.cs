using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx;
using HarmonyLib;
using SimpleJSON;

public static class JsonCapacityRuntime {
    public const string Id = "local.vam.startup-json-capacity";
    public static readonly bool BaselineObjects = false;
    public static readonly ConstructorInfo Target = typeof(JSONClass).GetConstructor(Type.EmptyTypes);
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type DictionaryType = typeof(Dictionary<string, JSONNode>);
    private static readonly ConstructorInfo DefaultConstructor = DictionaryType.GetConstructor(Type.EmptyTypes);
    private static readonly ConstructorInfo SizedConstructor = DictionaryType.GetConstructor(new[] { typeof(int) });
    private static readonly MethodBase[] Dependencies = {
        DefaultConstructor, SizedConstructor, DictionaryType.GetMethod("Init", All), DictionaryType.GetMethod("InitArrays", All)
    };
    private static readonly Func<MethodBase, PatchInfo> RegistryInfo = (Func<MethodBase, PatchInfo>)Delegate.CreateDelegate(
        typeof(Func<MethodBase, PatchInfo>), typeof(Harmony).Assembly.GetType("HarmonyLib.Internal.GlobalPatchState", true)
            .GetMethod("ToPatchInfo", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
    private static PatchInfo[] guards;

    public static void Install(Harmony harmony) {
        if (BaselineObjects) return;
        FieldInfo field = typeof(JSONClass).GetField("m_Dict", All);
        if (Target == null || field == null || field.FieldType != DictionaryType) throw new MissingFieldException("Actual JSONClass backing dictionary differs");
        guards = new PatchInfo[Dependencies.Length];
        for (int i = 0; i < Dependencies.Length; i++) {
            if (Dependencies[i] == null) throw new MissingMethodException("Actual corlib dictionary dependency differs");
            guards[i] = RegistryInfo(Dependencies[i]);
        }
        harmony.Patch(Target, transpiler: new HarmonyMethod(typeof(JsonCapacityRuntime), "Rewrite"));
    }

    public static Dictionary<string, JSONNode> CreateDictionary() {
        // The live registry publishes patch-array replacements on add/remove.
        // Preserve callbacks on dependencies; no cached verdict or per-node
        // reflection/collection allocation occurs in this hot constructor.
        Thread.MemoryBarrier();
        for (int i = 0; i < guards.Length; i++) {
            PatchInfo info = guards[i];
            if (info.prefixes.Length != 0 || info.postfixes.Length != 0 || info.transpilers.Length != 0 || info.finalizers.Length != 0)
                return new Dictionary<string, JSONNode>();
        }
        return new Dictionary<string, JSONNode>(3);
    }

    public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input) {
        var codes = new List<CodeInstruction>(input);
        int matches = 0;
        foreach (var code in codes) {
            if (code.opcode == OpCodes.Newobj && Object.Equals(code.operand, DefaultConstructor)) {
                code.opcode = OpCodes.Call;
                code.operand = typeof(JsonCapacityRuntime).GetMethod("CreateDictionary");
                matches++;
            }
        }
        if (matches != 1) throw new InvalidOperationException("Actual JSONClass dictionary constructor anchor count=" + matches);
        return codes;
    }
}
