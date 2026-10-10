using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace VaM.Memory {
 public static class AnatomyInactiveGuard {
  public const string Id="vam.memory.anatomy-inactive-guard";
  public static int LastRewriteCount {get;private set;}
  private static bool installed;
  private static FieldInfo ActiveField;
  private static readonly MethodInfo Target=AccessTools.Method(typeof(DAZCharacterSelector),"SyncAnatomy",Type.EmptyTypes);
  private static readonly MethodInfo NullOperator=AccessTools.Method(typeof(UnityEngine.Object),"op_Inequality",new[]{typeof(UnityEngine.Object),typeof(UnityEngine.Object)});
  private static readonly MethodInfo ActiveGetter=AccessTools.PropertyGetter(typeof(DAZDynamicItem),"active");

  // The original active getter is a pure Boolean field read. An active item still
  // takes Unity's destroyed-object check and the original following getter.
  public static bool ActiveAndAlive(DAZDynamicItem item,UnityEngine.Object other) {
   return !Object.ReferenceEquals(item,null) && item.active && ((UnityEngine.Object)item)!=other;
  }
  private static void VerifyGetter() {
   var body=ActiveGetter.GetMethodBody().GetILAsByteArray();
   if(body.Length!=7 || body[0]!=0x02 || body[1]!=0x7b || body[6]!=0x2a)
    throw new InvalidOperationException("active getter is no longer a pure field read");
   var field=ActiveGetter.Module.ResolveField(BitConverter.ToInt32(body,2));
   if(field.DeclaringType!=typeof(DAZDynamicItem) || field.Name!="_active" || field.FieldType!=typeof(bool))
    throw new InvalidOperationException("active field identity changed");
   ActiveField=field;
  }
  public static void Install() {
   if(installed)return;
   VerifyGetter();LastRewriteCount=0;
   var h=new Harmony(Id);
   try {
    h.Patch(Target,transpiler:new HarmonyMethod(typeof(AnatomyInactiveGuard),"Rewrite"));
    if(LastRewriteCount!=4)throw new InvalidOperationException("expected four original item predicates");
    installed=true;
   } catch(Exception original) {
    try {h.UnpatchAll(Id);}catch(Exception cleanup){original.Data["unpatchError"]=cleanup.ToString();}
    throw;
   }
  }
  public static void Stop() {if(installed)new Harmony(Id).UnpatchAll(Id);installed=false;}
  private static int Local(CodeInstruction ins) {
   if(ins.opcode==OpCodes.Ldloc_0)return 0;
   if(ins.opcode==OpCodes.Ldloc_1)return 1;
   if(ins.opcode==OpCodes.Ldloc_2)return 2;
   if(ins.opcode==OpCodes.Ldloc_3)return 3;
   if(ins.opcode!=OpCodes.Ldloc && ins.opcode!=OpCodes.Ldloc_S)return -1;
   var builder=ins.operand as LocalBuilder;
   if(builder!=null)return builder.LocalIndex;
   var info=ins.operand as LocalVariableInfo;
   if(info!=null)return info.LocalIndex;
   return Convert.ToInt32(ins.operand);
  }
  private static Type LocalType(CodeInstruction ins,IList<LocalVariableInfo> original) {
   var info=ins.operand as LocalVariableInfo;
   if(info!=null)return info.LocalType;
   int index=Local(ins);return index>=0 && index<original.Count?original[index].LocalType:null;
  }
  private static bool FalseBranch(CodeInstruction ins) {
   return ins.opcode==OpCodes.Brfalse || ins.opcode==OpCodes.Brfalse_S;
  }
  private static int BranchTarget(object target,List<CodeInstruction> code) {
   if(!(target is Label))return -1;
   for(int i=0;i<code.Count;i++)if(code[i].labels.Contains((Label)target)) {
    while(i<code.Count && code[i].opcode==OpCodes.Nop)i++;
    return i;
   }
   return -1;
  }
  private static bool SameTarget(object a,object b,List<CodeInstruction> code) {
   int first=BranchTarget(a,code);return first>=0 && first==BranchTarget(b,code);
  }
  public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
   VerifyGetter();
   var code=new List<CodeInstruction>(instructions);var matches=new List<int>();
   var locals=Target.GetMethodBody().LocalVariables;
   for(int i=2;i+4<code.Count;i++) {
    if(code[i].opcode!=OpCodes.Call || !Equals(code[i].operand,NullOperator) || code[i-1].opcode!=OpCodes.Ldnull)continue;
    int local=Local(code[i-2]);
    if(local<0)continue;
    var type=LocalType(code[i-2],locals);
    if(type!=typeof(DAZClothingItem) && type!=typeof(DAZHairGroup))continue;
    if(!FalseBranch(code[i+1]) || (Local(code[i+2])!=local || LocalType(code[i+2],locals)!=type) || code[i+3].opcode!=OpCodes.Callvirt ||
       !Equals(code[i+3].operand,ActiveGetter) || !FalseBranch(code[i+4]) || !SameTarget(code[i+1].operand,code[i+4].operand,code))
     throw new InvalidOperationException("original active item predicate changed");
    matches.Add(i);
   }
   if(matches.Count!=4)throw new InvalidOperationException("original item predicate count changed: "+matches.Count);
   // Insert pure guards before the original predicates; leave the complete
   // original instruction sequence, Unity check, and active getter intact.
   // Reject unexpected incoming edges or exception boundaries before any edit.
   foreach(int i in matches)for(int n=i-2;n<=i+4;n++)
    if((n!=i-2 && code[n].labels.Count!=0) || code[n].blocks.Count!=0)
     throw new InvalidOperationException("item predicate has an unexpected interior boundary");
   var starts=new HashSet<int>();foreach(int i in matches)starts.Add(i-2);
   var output=new List<CodeInstruction>(code.Count+20);
   for(int i=0;i<code.Count;i++) {
    if(starts.Contains(i)) {
     var load=new CodeInstruction(code[i]);load.blocks.Clear();
     output.Add(load);
     output.Add(new CodeInstruction(OpCodes.Brfalse,code[i+3].operand));
     var second=new CodeInstruction(code[i]);second.labels.Clear();second.blocks.Clear();
     output.Add(second);
     output.Add(new CodeInstruction(OpCodes.Ldfld,ActiveField));
     output.Add(new CodeInstruction(OpCodes.Brfalse,code[i+3].operand));
     var original=new CodeInstruction(code[i]);original.labels.Clear();
     output.Add(original);
    } else output.Add(code[i]);
   }
   LastRewriteCount=matches.Count;return output;
  }
 }
 public sealed partial class MemoryPlugin {
  private void InstallColdAnatomy() {
   if(Config.Bind("SceneLoading","AnatomyInactiveGuard",true,"Skip pure inactive item predicates before the original Unity destroyed-object check; no item cache or delayed anatomy sync.").Value)
    InstallModule("AnatomyInactiveGuard",delegate {
     AnatomyInactiveGuard.Install();
     Logger.LogInfo("[anatomy-inactive] active predicates="+AnatomyInactiveGuard.LastRewriteCount+" predicateMode=inline-scalar unityNull=preserved itemCache=0 deferredSync=0");
    });
  }
 }
}
