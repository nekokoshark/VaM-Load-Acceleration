using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using HarmonyLib;
using MonoMod.Cil;

public static class CollectorProbe {
 const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static Type state;
 static Assembly memory;
 static object Call(Type t,string n,params object[] a){return t.GetMethod(n,All).Invoke(null,a);}
 static void Require(bool b,string msg){if(!b)throw new Exception(msg);}
 static long Epoch(){return (long)state.GetProperty("HookEpoch",All).GetValue(null,null);}
 static bool Busy(){return (bool)state.GetProperty("HooksChanging",All).GetValue(null,null);}
 [MethodImpl(MethodImplOptions.NoInlining)] public static int Target(){return 7;}
 public static void Prefix(){}
 public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input){return input;}
 public static Exception Finalizer(Exception __exception){return __exception;}
 public static void Manipulator(ILContext il){}
 public static void Throwing(){throw new InvalidOperationException("fixture mutation failure");}
 public static string Run(){try{return Check();}catch(Exception e){Console.WriteLine(e);throw;}}
 static string Check(){
  string root=Path.GetDirectoryName(typeof(CollectorProbe).Assembly.Location);
  memory=Assembly.LoadFrom(Path.Combine(root,"VaM.Memory.dll"));
  state=memory.GetType("VaM.Memory.MorphSnapshotState",true);
  string[] types={"PackageTreeDisk","MorphSnapshotState","ClassificationDiskReceipts","AssetAnatomyPass","PathCharacterSearch","AssetEntryMetadata","PackageNegativeLookup","VarDirectorySelection"};
  string[] methods={"Configure","ConfigureImportReceipts","Configure","Install","Install","Install","Install","Install"};
  int failed=0;
  for(int i=0;i<types.Length;i++) {
   try {
    var t=memory.GetType("VaM.Memory."+types[i],true);
    if(i==1)Call(t,"Install",new Harmony("vam.memory.morph-snapshot-state"));
    Call(t,methods[i],i<3?new object[]{Path.Combine(root,"cache-"+types[i])}:new object[0]);
    Console.WriteLine("MODULE_PASS "+types[i]);
   }catch(Exception e){failed++;Console.WriteLine("MODULE_FAIL "+types[i]+" "+e);}
  }
  if(failed!=0)return "MUTATION_BASELINE failures="+failed;
  long initial=Epoch();Call(state,"ObserveHooks");Require(Epoch()==initial,"Duplicate observer install");
  var type=typeof(PatchInfo);var info=new PatchInfo();
  string[] adds={"AddPrefixes","AddPostfixes","AddTranspilers","AddFinalizers","AddILManipulators"};
  string[] removes={"RemovePrefix","RemovePostfix","RemoveTranspiler","RemoveFinalizer","RemoveILManipulator"};
  var patch=typeof(CollectorProbe).GetMethod("Prefix");
  foreach(string name in adds){long e=Epoch();type.GetMethod(name,All).Invoke(info,new object[]{"fixture",new[]{new HarmonyMethod(patch)}});Require(Epoch()==e+2 && !Busy(),"Missed "+name);}
  foreach(string name in removes){long e=Epoch();type.GetMethod(name,All).Invoke(info,new object[]{"fixture"});Require(Epoch()==e+2 && !Busy(),"Missed "+name);}
  long stamp=Epoch();type.GetMethod("RemovePatch",All).Invoke(info,new object[]{patch});Require(Epoch()==stamp+2 && !Busy(),"RemovePatch epoch");
  Console.WriteLine("MUTATORS_PASS direct=11 balanced=True duplicateInstall=False");
  var target=typeof(CollectorProbe).GetMethod("Target");
  var h=new Harmony("mutation.probe.foreign");
  var targets=new[]{"Prefix","Prefix","Transpiler","Finalizer","Manipulator"};
  for(int k=0;k<5;k++) {
   long e=Epoch();var hm=new HarmonyMethod(typeof(CollectorProbe).GetMethod(targets[k]));
   if(k<4)h.Patch(target,k==0?hm:null,k==1?hm:null,k==2?hm:null,k==3?hm:null);
   else h.CreateProcessor(target).AddILManipulator(hm).Patch();
   Require(Epoch()>e && !Busy(),"Live add "+k);
   Require(!(bool)Call(state,"AllowedPatches",target,new MethodInfo[0]),"Foreign family accepted "+k);
   Require(Target()==7,"Target changed");
   e=Epoch();h.Unpatch(target,typeof(CollectorProbe).GetMethod(targets[k]));Require(Epoch()>e && !Busy(),"Live remove "+k);
   Require((bool)Call(state,"AllowedPatches",target,new MethodInfo[0]),"Guard failed recovery");
  }
  Console.WriteLine("LIVE_FAMILIES_PASS families=5 foreignRejected=True removedRestored=True");
  h.CreateProcessor(target).AddILManipulator(new HarmonyMethod(typeof(CollectorProbe).GetMethod("Manipulator"))).Patch();
  foreach(string n in new[]{"AssetAnatomyPass","ClassificationDiskReceipts"})
   Require(!(bool)Call(memory.GetType("VaM.Memory."+n),"Allowed",target),"ILManipulator guard "+n);
  Require((bool)Call(memory.GetType("ListingNamesRuntime"),"Foreign",target),"Listing guard");
  Require(!(bool)Call(memory.GetType("VaM.Memory.ClassificationStreamCompatibility"),"Unpatched",typeof(CollectorProbe)),"Stream guard");
  foreach(string n in new[]{"PathCharacterSearch","AssetEntryMetadata","PackageTreeDisk","PackageNegativeLookup","VarDirectorySelection"})
   Call(memory.GetType("VaM.Memory."+n),"Compatible");
  using(var bytes=new MemoryStream())using(var writer=new BinaryWriter(bytes))Call(memory.GetType("StartupMorphIndex.Runtime"),"PatchIdentity",writer);
  h.UnpatchSelf();
  Console.WriteLine("GUARD_BODIES_PASS executed=11 foreignILRejected=True");
  // Exercise the observed mutator's exception path, not just normal completion.
  var method=type.GetMethod("AddPrefixes",All);
  var trouble=new Harmony("mutation.probe.throw");
  trouble.Patch(method,prefix:new HarmonyMethod(typeof(CollectorProbe).GetMethod("Throwing")){priority=Priority.Last});
  bool threw=false;try{method.Invoke(info,new object[]{"fixture",new[]{new HarmonyMethod(patch)}});}catch(TargetInvocationException e){threw=e.InnerException is InvalidOperationException;}
  Require(threw && !Busy(),"Unbalanced exception");trouble.UnpatchSelf();
  Console.WriteLine("EXCEPTION_PASS propagated=True busy=0");
  for(int i=3;i<types.Length;i++)Call(memory.GetType("VaM.Memory."+types[i]),"Stop");
  Call(memory.GetType("VaM.Memory.ClassificationDiskReceipts"),"Disable");
  Require(!Busy(),"Cleanup busy");
  return "MUTATION_MODIFIED_PASS modules=8 mutators=11 families=5 exception=True busy=0";
 }
}
