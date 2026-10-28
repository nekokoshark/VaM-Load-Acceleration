using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace VaM.Memory {
 // The old corlib scans the entire needle set for every path character.
 // Two local bitmaps replace that nested loop for ASCII needle sets only.
 // Read the supplied mutable array on EVERY call; never cache its identity.
 public static class PathCharacterSearch {
  public const string Id="vam.memory.path-character-search";
  private static readonly MethodInfo Target=typeof(string).GetMethod("IndexOfAnyUnchecked",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(char[]),typeof(int),typeof(int)},null);
  private static readonly MethodInfo Prefix=AccessTools.Method(typeof(PathCharacterSearch),"Before");
  private static bool installed,compatible;
  private static long checkedEpoch=-1;
  public static long Accelerated;
  private static bool Compatible() {
   long epoch=MorphSnapshotState.HookEpoch;
   if(MorphSnapshotState.HooksChanging)return false;
   if(epoch==checkedEpoch)return compatible;
   var info=Harmony.GetPatchInfo(Target);
   bool result=info!=null && info.Prefixes.Count==1 && info.Prefixes[0].PatchMethod==Prefix &&
       info.Postfixes.Count==0 && info.Transpilers.Count==0 && info.Finalizers.Count==0 && info.ILManipulators.Count==0;
   if(epoch!=MorphSnapshotState.HookEpoch || MorphSnapshotState.HooksChanging)return false;
   checkedEpoch=epoch;compatible=result;return result;
  }
  public static bool TrySearch(string text,char[] any,int start,int count,out int result) {
   result=-1;
   // Public corlib overloads retain their own argument checks, empty/single
   // needle optimizations, and exceptions. Unchecked invalid inputs fall back.
   if(text==null || any==null || any.Length<5 || count<32 || start<0 || start>text.Length || count>text.Length-start)return false;
   ulong low=0,high=0;
   for(int j=0;j<any.Length;j++) {
    int c=any[j];if(c>=128)return false;
    if(c<64)low|=1UL<<c;else high|=1UL<<(c-64);
   }
   int end=start+count;
   for(int i=start;i<end;i++) {
    int c=text[i];
    if(c<64 ? (low&(1UL<<c))!=0 : c<128 && (high&(1UL<<(c-64)))!=0) {result=i;break;}
   }
   return true;
  }
  private static bool Before(string __instance,char[] __0,int __1,int __2,ref int __result) {
   if(!Compatible())return true;
   int found;if(!TrySearch(__instance,__0,__1,__2,out found))return true;
   __result=found;Interlocked.Increment(ref Accelerated);return false;
  }
  public static void Install() {
   if(installed)return;
   if(Target==null || Target.ReturnType!=typeof(int))throw new MissingMethodException("corlib IndexOfAnyUnchecked");
   MorphSnapshotState.ObserveHooks();
   var h=new Harmony(Id);
   try {h.Patch(Target,prefix:new HarmonyMethod(Prefix));installed=true;checkedEpoch=-1;}
   catch {h.UnpatchAll(Id);throw;}
  }
  public static void Stop(){if(installed)new Harmony(Id).UnpatchAll(Id);installed=false;checkedEpoch=-1;}
 }
 public sealed partial class MemoryPlugin {
  private void InstallPathCharacterSearch() {
   if(Config.Bind("SceneLoading","PathCharacterSearch",true,"Accelerate exact ASCII character-set searches used by path validation; original filesystem queries, argument checks and mutable needle arrays remain live.").Value)
    InstallModule("PathCharacterSearch",delegate {PathCharacterSearch.Install();Logger.LogInfo("[path-character-search] localBitmap=True mutableNeedles=readPerCall fileQueries=original persistentRoots=0");});
  }
 }
}
