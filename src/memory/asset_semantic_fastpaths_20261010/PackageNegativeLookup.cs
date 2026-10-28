using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using MVR.FileManagement;

namespace VaM.Memory {
 // Preserve both live package dictionaries and the original read-path check.
 // Only the final filesystem probe changes: an ordinary Windows name cannot
 // contain a non-drive colon followed by a directory separator. This is a syntax fact, not a negative
 // filesystem cache. Local prefs, valid drive paths, ADS and devices stay live.
 public static class PackageNegativeLookup {
  public const string Id="vam.memory.package-negative-lookup";
  private static readonly MethodInfo Target=AccessTools.Method(typeof(FileManager),"FileExists",new[]{typeof(string),typeof(bool),typeof(bool)});
  private static readonly MethodInfo RewriteMethod=AccessTools.Method(typeof(PackageNegativeLookup),"Rewrite");
  private static readonly bool Windows=Environment.OSVersion.Platform==PlatformID.Win32NT;
  private static MethodBase[] sensitive;
  private static long checkedEpoch=-1;
  private static bool compatible,installed;
  public static long ImpossibleProbesSkipped;

  internal static bool Impossible(string path) {
   if(!Windows || string.IsNullOrEmpty(path) || path[0]=='\\' || path[0]=='/')return false;
   for(int i=2;i<path.Length-1;i++) {
    if(path[i]!=':' || (path[i+1]!='\\' && path[i+1]!='/'))continue;
    return true;
   }
   return false;
  }
  private static bool Compatible() {
   long epoch=MorphSnapshotState.HookEpoch;if(MorphSnapshotState.HooksChanging)return false;
   if(epoch==checkedEpoch)return compatible;
   bool okay=true;
   foreach(var method in sensitive) {
    var info=Harmony.GetPatchInfo(method);if(info==null)continue;
    foreach(var patches in new[]{info.Prefixes,info.Postfixes,info.Transpilers,info.Finalizers,info.ILManipulators})
     foreach(var patch in patches)if(method!=Target || patch.PatchMethod!=RewriteMethod)okay=false;
   }
   if(epoch!=MorphSnapshotState.HookEpoch || MorphSnapshotState.HooksChanging)return false;
   checkedEpoch=epoch;compatible=okay;return okay;
  }
  public static bool FilesystemFallback(string path) {
   if(Compatible() && Impossible(path)) {
    Interlocked.Increment(ref ImpossibleProbesSkipped);return false;
   }
   return File.Exists(path);
  }
  public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input) {
   var code=new List<CodeInstruction>(input);int count=0;
   var original=typeof(File).GetMethod("Exists",new[]{typeof(string)});
   foreach(var c in code)if(c.opcode==OpCodes.Call && Equals(c.operand,original)) {
    c.operand=AccessTools.Method(typeof(PackageNegativeLookup),"FilesystemFallback");count++;
   }
   if(count!=1)throw new InvalidOperationException("FileManager filesystem fallback changed");return code;
  }
  public static void Install() {
   if(installed)return;
   var mono=typeof(File).Assembly.GetType("System.IO.MonoIO",true);
   sensitive=new MethodBase[]{Target,typeof(File).GetMethod("Exists",new[]{typeof(string)}),
    AccessTools.Method(mono,"ExistsFile"),AccessTools.Method(mono,"GetFileAttributes")};
   foreach(var method in sensitive)if(method==null)throw new MissingMethodException("Package negative lookup dependency");
   MorphSnapshotState.ObserveHooks();
   var h=new Harmony(Id);
   try{h.Patch(Target,transpiler:new HarmonyMethod(RewriteMethod));installed=true;checkedEpoch=-1;}
   catch{h.UnpatchAll(Id);throw;}
  }
  public static void Stop(){if(installed)new Harmony(Id).UnpatchAll(Id);installed=false;checkedEpoch=-1;}
 }
 public sealed partial class MemoryPlugin {
  private void InstallPackageNegativeLookup() {
   if(Config.Bind("SceneLoading","PackageNegativeFilesystemProbe",true,"Preserve original live package lookup and checks; skip only impossible ordinary Windows non-drive-colon directory filesystem probes, never cache local absence.").Value)
    InstallModule("PackageNegativeFilesystemProbe",delegate{PackageNegativeLookup.Install();Logger.LogInfo("[package-negative-lookup] packageDictionaries=original localFiles=live negativeFileCache=False");});
  }
 }
}
