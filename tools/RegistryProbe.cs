using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

public static class CollectorProbe {
 const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static int callbacks;
 [MethodImpl(MethodImplOptions.NoInlining)] public static int Target(){return 1;}
 public static void Foreign(){callbacks++;}
 static object Call(Type type,string name,params object[] args){return type.GetMethod(name,All).Invoke(null,args);}
 static void Require(bool value,string message){if(!value)throw new Exception(message);}
 public static string Run() {
  try {return Check();} catch(Exception e){Console.WriteLine("PROBE_EXCEPTION "+e);throw;}
 }
 static string Check() {
  string root=Path.GetDirectoryName(typeof(CollectorProbe).Assembly.Location);
  var memory=Assembly.LoadFrom(Path.Combine(root,"VaM.Memory.dll"));
  string[] types={"CanonicalPathsRuntime","JsonCapacityRuntime"};
  int failures=0;
  foreach(string name in types) {
   try {RuntimeHelpers.RunClassConstructor(memory.GetType(name,true).TypeHandle);Console.WriteLine("INIT_PASS "+name);}
   catch(Exception e){failures++;Console.WriteLine("INIT_FAIL "+name+" "+e);}
  }
  if(failures!=0)return "REGISTRY_BASELINE failures="+failures;
  var manager=typeof(Harmony).Assembly.GetType("HarmonyLib.Public.Patching.PatchManager",true);
  var get=manager.GetMethod("ToPatchInfo",All);
  var target=typeof(CollectorProbe).GetMethod("Target");
  var info=(PatchInfo)get.Invoke(null,new object[]{target});
  var foreign=new Harmony("registry.probe.foreign");
  for(int round=0;round<20;round++) {
   foreign.Patch(target,prefix:new HarmonyMethod(typeof(CollectorProbe).GetMethod("Foreign")));
   Require(Object.ReferenceEquals(info,get.Invoke(null,new object[]{target})) && info.prefixes.Length==1,"Registry identity/add failed");
   Target();
   foreign.UnpatchSelf();
   Require(Object.ReferenceEquals(info,get.Invoke(null,new object[]{target})) && info.prefixes.Length==0,"Registry identity/remove failed");
  }
  Require(callbacks==20,"Callback count");
  Console.WriteLine("LIVE_REGISTRY_PASS cycles=20 stableIdentity=True callbacks=20 removed=True");
  var paths=memory.GetType(types[0]);var json=memory.GetType(types[1]);
  var hp=new Harmony("registry.probe.paths");var hj=new Harmony("registry.probe.json");
  try {
   Call(paths,"Install",hp);Call(json,"Install",hj);
   Require((bool)Call(paths,"GuardIdentitiesCurrent"),"Path registry identity");
   object[] argv={"C:/test/人物/file.png",null};
   Require(!(bool)paths.GetMethod("Before",All).Invoke(null,argv),"Unpatched path should accelerate");
   Require((string)argv[1]=="C:\\test\\人物\\file.png","Canonical output");
   var omitted=(MethodInfo[])paths.GetField("Omitted",All).GetValue(null);
   foreign.Patch(omitted[0],prefix:new HarmonyMethod(typeof(CollectorProbe).GetMethod("Foreign")));
   argv=new object[]{"C:/test/人物/file.png",null};
   Require((bool)paths.GetMethod("Before",All).Invoke(null,argv),"Late path patch not respected");
   foreign.UnpatchSelf();
   Require(!(bool)paths.GetMethod("Before",All).Invoke(null,argv),"Unpatched path did not recover");
   Console.WriteLine("PATH_GUARD_PASS accelerated=True latePatchFallback=True unpatchRestored=True");
   var deps=(MethodBase[])json.GetField("Dependencies",All).GetValue(null);
   int before=callbacks;
   foreign.Patch(deps[0],prefix:new HarmonyMethod(typeof(CollectorProbe).GetMethod("Foreign")));
   var dictionary=Call(json,"CreateDictionary");
   Require(callbacks>before,"Late JSON default constructor callback bypassed");
   foreign.UnpatchSelf();
   before=callbacks;
   dictionary=Call(json,"CreateDictionary");
   Require(callbacks==before,"JSON callback survived unpatch");
   var game=Assembly.Load("Assembly-CSharp");
   var parser=game.GetType("SimpleJSON.JSON",true).GetMethod("Parse",All);
   var parsed=parser.Invoke(null,new object[]{"{\"name\":\"人物\",\"values\":[1,2,3]}"});
   Require(parsed.ToString().Contains("人物"),"Native JSON parse corrupted");
   Console.WriteLine("JSON_GUARD_PASS latePatchCallback=True unpatchRestored=True nativeParse=True");
  } finally {foreign.UnpatchSelf();hj.UnpatchSelf();hp.UnpatchSelf();}
  return "REGISTRY_MODIFIED_PASS initializers=2 liveCycles=20 path=True json=True cleanup=True";
 }
}
