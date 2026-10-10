using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MVR.FileManagement;
namespace Quest3TriggerUI {
// Cold-start candidate. Install before FileManager builds its catalogue.
internal static class VarEntryLazyFlags {
 const BindingFlags B = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
 static readonly string[] Names = { "flagBasePath", "favPath", "<hidePath>k__BackingField", "packagedHidePath", "packagedFlagPath" };
 static readonly MethodInfo ResolveMethod = typeof(VarEntryLazyFlags).GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic);
 static readonly MethodInfo NativeMethod = typeof(VarEntryLazyFlags).GetMethod("Native", BindingFlags.Static | BindingFlags.NonPublic);
 static readonly MethodInfo HideSetter = typeof(FileEntry).GetProperty("hidePath", B).GetSetMethod(true);
 static Harmony harmony;
 static bool Native(FileEntry entry) { return entry.GetType().Equals(typeof(VarFileEntry)); }
 static int Slot(FieldInfo field) {
  if (field == null) return -1;
  for (int i = 0; i < Names.Length; i++)
   if (field.Name == Names[i] && field.DeclaringType == (i < 3 ? typeof(FileEntry) : typeof(VarFileEntry))) return i;
  return -1;
 }
 static string Resolve(FileEntry entry, string stored, int slot) {
  if (stored != null || !Native(entry)) return stored;
  var file = (VarFileEntry)entry;
  switch (slot) {
   case 0: return file.Package.CachedAddonPrefsPfx + file.InternalSlashPath + ".";
   case 1: return file.Package.CachedAddonPrefsPfx + file.InternalSlashPath + ".fav";
   case 2: return file.Package.CachedAddonPrefsPfx + file.InternalSlashPath + ".hide";
   case 3: return file.Path + ".hide";
   case 4: return file.Path + ".";
   default: throw new ArgumentOutOfRangeException("slot");
  }
 }
 static int Pop(CodeInstruction i) {
  if (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) {
   var m = (MethodBase)i.operand; return m.GetParameters().Length + (m.IsStatic ? 0 : 1);
  }
  switch (i.opcode.StackBehaviourPop) {
   case StackBehaviour.Pop0: return 0;
   case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
   case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi:
   case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
   default: throw new InvalidOperationException("Unexpected assignment pop: " + i);
  }
 }
 static int Push(CodeInstruction i) {
  if (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) return ((MethodInfo)i.operand).ReturnType == typeof(void) ? 0 : 1;
  switch (i.opcode.StackBehaviourPush) {
   case StackBehaviour.Push0: return 0;
   case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushref: return 1;
   default: throw new InvalidOperationException("Unexpected assignment push: " + i);
  }
 }
 static IEnumerable<CodeInstruction> Constructor(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
  var code = new List<CodeInstruction>(instructions);
  var starts = new Dictionary<int, int>(); int count = 0;
  for (int end = 0; end < code.Count; end++) {
   var ins = code[end];
   bool field = ins.opcode == OpCodes.Stfld && Slot(ins.operand as FieldInfo) >= 0;
   bool hide = (ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && HideSetter.Equals(ins.operand);
   if (!field && !hide) continue;
   int need = 2, start = end - 1;
   for (; start >= 0; start--) {
    var item = code[start];
    if (item.opcode.FlowControl == FlowControl.Branch || item.opcode.FlowControl == FlowControl.Cond_Branch || item.blocks.Count != 0)
     throw new InvalidOperationException("Branch/EH inside flag assignment");
    need += Pop(item) - Push(item); if (need == 0) break;
    if (need < 0) throw new InvalidOperationException("Invalid assignment stack");
   }
   if (start < 0 || end + 1 >= code.Count) throw new InvalidOperationException("Missing flag assignment producer/continuation");
   for (int k = start + 1; k <= end; k++) if (code[k].labels.Count != 0 || code[k].blocks.Count != 0)
    throw new InvalidOperationException("Interior flag assignment label/EH");
   starts.Add(start, end); count++;
  }
  if (count != 5) throw new InvalidOperationException("Expected five flag assignments, got " + count);
  for (int i = 0; i < code.Count; i++) {
   int end;
   if (starts.TryGetValue(i, out end)) {
    var next = generator.DefineLabel(); code[end + 1].labels.Add(next);
    var load = new CodeInstruction(OpCodes.Ldarg_0);
    load.labels.AddRange(code[i].labels); code[i].labels.Clear();
    load.blocks.AddRange(code[i].blocks); code[i].blocks.Clear();
    yield return load; yield return new CodeInstruction(OpCodes.Call, NativeMethod);
    yield return new CodeInstruction(OpCodes.Brtrue, next);
   }
   yield return code[i];
  }
 }
 static int Expected(MethodBase m) {
  if (m.DeclaringType == typeof(FileEntry)) {
   switch (m.Name) { case "get_hidePath": return 1; case "HasFlagFile": case "SetFlagFile": case "IsFavorite": return 2; case "SetFavorite": return 5; }
  } else if (m.DeclaringType == typeof(VarFileEntry)) {
   switch (m.Name) { case "HasFlagFile": return 4; case "IsFlagFileModifiable": case "IsHidden": case "IsHiddenModifiable": return 2; }
  }
  throw new InvalidOperationException("Unexpected flag consumer " + m);
 }
 static IEnumerable<CodeInstruction> Reads(IEnumerable<CodeInstruction> instructions, MethodBase original) {
  int count = 0;
  foreach (var ins in instructions) {
   int slot = ins.opcode == OpCodes.Ldfld ? Slot(ins.operand as FieldInfo) : -1;
   if (slot < 0) { yield return ins; continue; }
   var dup = new CodeInstruction(OpCodes.Dup); dup.labels.AddRange(ins.labels); ins.labels.Clear();
   dup.blocks.AddRange(ins.blocks); ins.blocks.Clear();
   yield return dup; yield return ins;
   yield return new CodeInstruction(OpCodes.Ldc_I4, slot); yield return new CodeInstruction(OpCodes.Call, ResolveMethod); count++;
  }
  if (count != Expected(original)) throw new InvalidOperationException("Flag read anchor mismatch " + original + " count=" + count);
 }
 internal static void Install() {
  if (harmony != null) return;
  var h = new Harmony("local.vam.var-entry-lazy-flags");
  try {
   foreach (var name in new[] {"get_hidePath", "HasFlagFile", "SetFlagFile", "IsFavorite", "SetFavorite"})
    h.Patch(typeof(FileEntry).GetMethod(name, B), transpiler: new HarmonyMethod(typeof(VarEntryLazyFlags).GetMethod("Reads", BindingFlags.Static | BindingFlags.NonPublic)));
   foreach (var name in new[] {"HasFlagFile", "IsFlagFileModifiable", "IsHidden", "IsHiddenModifiable"})
    h.Patch(typeof(VarFileEntry).GetMethod(name, B), transpiler: new HarmonyMethod(typeof(VarEntryLazyFlags).GetMethod("Reads", BindingFlags.Static | BindingFlags.NonPublic)));
   h.Patch(typeof(VarFileEntry).GetConstructor(new[] {typeof(VarPackage), typeof(string), typeof(DateTime), typeof(long), typeof(bool)}),
    transpiler: new HarmonyMethod(typeof(VarEntryLazyFlags).GetMethod("Constructor", BindingFlags.Static | BindingFlags.NonPublic)));
   harmony = h;
  } catch { h.UnpatchAll(h.Id); throw; }
 }
 // No live unpatch: existing native entries have absent fields. Lifecycle integration must preserve readers or rehydrate first.
}
}
