using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security;
using System.Threading;
using HarmonyLib;
using MVR.FileManagement;

namespace VaM.Memory {
 // A descriptor is a single live filesystem snapshot, not a path cache.
 // Keep the original base constructor, field setters, full-path validation,
 // and metadata getters. Only merge repeated queries within this constructor.
 public static class AssetEntryMetadata {
  public const string Id="vam.memory.asset-entry-metadata";
  private static readonly ConstructorInfo Target=typeof(SystemFileEntry).GetConstructor(new[]{typeof(string)});
  private static readonly MethodInfo Internal=AccessTools.Method(typeof(FileInfo),"InternalRefresh");
  private static readonly FieldInfo Stat=AccessTools.Field(typeof(FileSystemInfo),"stat");
  private static readonly FieldInfo Attributes=Stat==null?null:AccessTools.Field(Stat.FieldType,"Attributes");
  private static readonly FieldInfo Exists=AccessTools.Field(typeof(FileInfo),"exists");
  private static readonly Func<FileInfo,FileAttributes> ReadAttributes=MakeReader();
  private static readonly Action<FileInfo,bool> WriteExists=MakeWriter();
  private static readonly Func<FileInfo,bool> ReadStat=MakeStatReader();
  private static readonly MethodInfo RewriteMethod=AccessTools.Method(typeof(AssetEntryMetadata),"Rewrite");
  private static readonly MethodInfo RefreshTarget=AccessTools.Method(typeof(FileSystemInfo),"Refresh",new[]{typeof(bool)});
  private static readonly MethodInfo RefreshRewriteMethod=AccessTools.Method(typeof(AssetEntryMetadata),"RefreshRewrite");
  private static readonly Action<FileSystemInfo> OriginalInternal=MakeInternal();
  private static MethodBase[] sensitive;
  private static bool installed,compatible;
  private static long checkedEpoch=-1;
  public static long Snapshots,Reused,SingleStats,Declined,RefreshStats;
  public static string Stats(){return "snapshots="+Snapshots+" reused="+Reused+" singleStats="+SingleStats+" refreshStats="+RefreshStats+" declined="+Declined+" retainedEntries=0";}
  public sealed class Receipt {internal string Path;internal FileInfo Info;}
  private static Func<FileInfo,FileAttributes> MakeReader() {
   if(Stat==null || Attributes==null || Attributes.FieldType!=typeof(FileAttributes))throw new MissingFieldException("FileSystemInfo.stat.Attributes");
   var m=new DynamicMethod("EntryStatAttributes",typeof(FileAttributes),new[]{typeof(FileInfo)},typeof(FileInfo),true);
   var il=m.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldflda,Stat);il.Emit(OpCodes.Ldfld,Attributes);il.Emit(OpCodes.Ret);
   return (Func<FileInfo,FileAttributes>)m.CreateDelegate(typeof(Func<FileInfo,FileAttributes>));
  }
  private static Action<FileInfo,bool> MakeWriter() {
   if(Exists==null || Exists.FieldType!=typeof(bool))throw new MissingFieldException("FileInfo.exists");
   var m=new DynamicMethod("EntryStatExists",typeof(void),new[]{typeof(FileInfo),typeof(bool)},typeof(FileInfo),true);
   var il=m.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Stfld,Exists);il.Emit(OpCodes.Ret);
   return (Action<FileInfo,bool>)m.CreateDelegate(typeof(Action<FileInfo,bool>));
  }
  private static Func<FileInfo,bool> MakeStatReader() {
   var mono=typeof(File).Assembly.GetType("System.IO.MonoIO",true);
   var error=typeof(File).Assembly.GetType("System.IO.MonoIOError",true);
   var native=AccessTools.Method(mono,"GetFileStat",new[]{typeof(string),Stat.FieldType.MakeByRefType(),error.MakeByRefType()});
   var valid=AccessTools.Field(typeof(FileSystemInfo),"valid");
   if(native==null || native.ReturnType!=typeof(bool) || valid==null || valid.FieldType!=typeof(bool))throw new MissingMemberException("MonoIO.GetFileStat/FileSystemInfo.valid");
   var m=new DynamicMethod("EntrySingleNativeStat",typeof(bool),new[]{typeof(FileInfo)},typeof(FileInfo),true);
   var il=m.GetILGenerator();var code=il.DeclareLocal(error);var success=il.DeclareLocal(typeof(bool));
   il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Callvirt,typeof(FileSystemInfo).GetProperty("FullName").GetGetMethod());
   il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldflda,Stat);il.Emit(OpCodes.Ldloca,code);il.Emit(OpCodes.Call,native);il.Emit(OpCodes.Stloc,success);
   il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldloc,success);il.Emit(OpCodes.Stfld,valid);il.Emit(OpCodes.Ldloc,success);il.Emit(OpCodes.Ret);
   return (Func<FileInfo,bool>)m.CreateDelegate(typeof(Func<FileInfo,bool>));
  }
  private static Action<FileSystemInfo> MakeInternal() {
   var m=new DynamicMethod("EntryOriginalInternalRefresh",typeof(void),new[]{typeof(FileSystemInfo)},typeof(FileSystemInfo),true);
   var il=m.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Callvirt,AccessTools.Method(typeof(FileSystemInfo),"InternalRefresh"));il.Emit(OpCodes.Ret);
   return (Action<FileSystemInfo>)m.CreateDelegate(typeof(Action<FileSystemInfo>));
  }
  private static bool Compatible() {
   long epoch=MorphSnapshotState.HookEpoch;if(MorphSnapshotState.HooksChanging)return false;
   if(epoch==checkedEpoch)return compatible;
   bool okay=true;
   foreach(var method in sensitive) {
    var info=Harmony.GetPatchInfo(method);if(info==null)continue;
    foreach(var patches in new[]{info.Prefixes,info.Postfixes,info.Transpilers,info.Finalizers,info.ILManipulators})foreach(var patch in patches) {
     if(patch.PatchMethod!=RewriteMethod && patch.PatchMethod!=RefreshRewriteMethod &&
        patch.PatchMethod!=AccessTools.Method(typeof(CanonicalPathsRuntime),"Before"))okay=false;
    }
   }
   if(epoch!=MorphSnapshotState.HookEpoch || MorphSnapshotState.HooksChanging)return false;
   checkedEpoch=epoch;compatible=okay;return okay;
  }
  private static bool Ordinary(string path) {
   if(string.IsNullOrEmpty(path) || path.Length>=248 || path[0]=='\\' || path[0]=='/' || path.IndexOfAny(Path.InvalidPathChars)>=0)return false;
   int start=0;
   if(path.Length>=2 && path[1]==':') {
    if(path.Length<4 || !((path[0]>='A' && path[0]<='Z') || (path[0]>='a' && path[0]<='z')) || path[2]!='\\')return false;
    start=3;
   }
   for(int i=start;i<=path.Length;i++) {
    if(i<path.Length) {
     char c=path[i];if(c==':' || c=='*' || c=='?' || c=='/' || c<32)return false;
     if(c!='\\')continue;
    }
    int n=i-start;
    if(n==0 || path[i-1]=='.' || Char.IsWhiteSpace(path[i-1]) || (n==1 && path[start]=='.') ||
       (n==2 && path[start]=='.' && path[start+1]=='.'))return false;
    start=i+1;
   }
   return true;
  }
  public static bool Probe(string path,out Receipt receipt) {
   receipt=null;
   if(!Compatible() || !Ordinary(path)){Interlocked.Increment(ref Declined);return File.Exists(path);}
   FileInfo info;
   try {info=new FileInfo(path);}
   catch(ArgumentException){return File.Exists(path);}
   catch(NotSupportedException){return File.Exists(path);}
   catch(PathTooLongException){return File.Exists(path);}
   catch(SecurityException){return File.Exists(path);}
   // GetFileStat may return false while leaving zero Attributes in this old
   // Mono. Its BOOLEAN result is mandatory; attributes alone are not evidence
   // of existence. Failed stats retain the original File.Exists route.
   bool success=ReadStat(info);FileAttributes attrs=ReadAttributes(info);
   if(!success || (int)attrs==-1 || (attrs&FileAttributes.ReparsePoint)!=0 || !Compatible())return File.Exists(path);
   bool exists=(attrs&FileAttributes.Directory)==0;WriteExists(info,exists);
   Interlocked.Increment(ref SingleStats);
   if(exists){receipt=new Receipt {Path=path,Info=info};Interlocked.Increment(ref Snapshots);}
   return exists;
  }
  public static FileInfo Consume(string path,Receipt receipt) {
   if(receipt!=null && string.Equals(path,receipt.Path,StringComparison.Ordinal) && Compatible()) {
    var info=receipt.Info;receipt.Info=null;receipt.Path=null;Interlocked.Increment(ref Reused);return info;
   }
   return new FileInfo(path);
  }
  public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input,ILGenerator generator) {
   var code=new List<CodeInstruction>(input);var receipt=generator.DeclareLocal(typeof(Receipt));
   var exists=typeof(File).GetMethod("Exists",new[]{typeof(string)});var ctor=typeof(FileInfo).GetConstructor(new[]{typeof(string)});
   var output=new List<CodeInstruction>();int probes=0,consumers=0;
   foreach(var c in code) {
    if(Equals(c.operand,exists)) {
     var first=new CodeInstruction(OpCodes.Ldloca,receipt);first.labels.AddRange(c.labels);first.blocks.AddRange(c.blocks);c.labels.Clear();c.blocks.Clear();output.Add(first);
     c.opcode=OpCodes.Call;c.operand=AccessTools.Method(typeof(AssetEntryMetadata),"Probe");probes++;
    }else if(c.opcode==OpCodes.Newobj && Equals(c.operand,ctor)) {
     var first=new CodeInstruction(OpCodes.Ldloc,receipt);first.labels.AddRange(c.labels);first.blocks.AddRange(c.blocks);c.labels.Clear();c.blocks.Clear();output.Add(first);
     c.opcode=OpCodes.Call;c.operand=AccessTools.Method(typeof(AssetEntryMetadata),"Consume");consumers++;
    }
    output.Add(c);
   }
   if(probes!=1 || consumers!=1)throw new InvalidOperationException("SystemFileEntry metadata sites changed");return output;
  }
  private static void CompleteRefresh(FileSystemInfo owner,bool success) {
   var file=owner as FileInfo;
   if(success && file!=null && Compatible()) {
    var attrs=ReadAttributes(file);
    if((int)attrs!=-1 && (attrs&FileAttributes.ReparsePoint)==0) {
     WriteExists(file,(attrs&FileAttributes.Directory)==0);Interlocked.Increment(ref RefreshStats);return;
    }
   }
   OriginalInternal(owner);
  }
  public static IEnumerable<CodeInstruction> RefreshRewrite(IEnumerable<CodeInstruction> input,ILGenerator generator) {
   var code=new List<CodeInstruction>(input);var success=generator.DeclareLocal(typeof(bool));
   var native=AccessTools.Method(typeof(File).Assembly.GetType("System.IO.MonoIO",true),"GetFileStat");
   var internalMethod=AccessTools.Method(typeof(FileSystemInfo),"InternalRefresh");
   var output=new List<CodeInstruction>();int captures=0,finishes=0;
   for(int i=0;i<code.Count;i++) {
    var c=code[i];
    if(Equals(c.operand,native)) {
     if(i+1>=code.Count || code[i+1].opcode!=OpCodes.Pop)throw new InvalidOperationException("Refresh stat result differs");
     code[i+1].opcode=OpCodes.Stloc;code[i+1].operand=success;captures++;
    }else if(Equals(c.operand,internalMethod)) {
     var first=new CodeInstruction(OpCodes.Ldloc,success);first.labels.AddRange(c.labels);first.blocks.AddRange(c.blocks);c.labels.Clear();c.blocks.Clear();output.Add(first);
     c.opcode=OpCodes.Call;c.operand=AccessTools.Method(typeof(AssetEntryMetadata),"CompleteRefresh");finishes++;
    }
    output.Add(c);
   }
   if(captures!=1 || finishes!=1)throw new InvalidOperationException("Refresh metadata sites changed");return output;
  }
  public static void Install() {
   if(installed)return;
   sensitive=new MethodBase[]{Target,Internal,RefreshTarget,typeof(FileEntry).GetConstructor(new[]{typeof(string)}),
    typeof(File).GetMethod("Exists",new[]{typeof(string)}),typeof(FileInfo).GetConstructor(new[]{typeof(string)}),
    typeof(FileInfo).GetProperty("Exists").GetGetMethod(),typeof(FileInfo).GetProperty("Length").GetGetMethod(),
    typeof(FileSystemInfo).GetProperty("LastWriteTime").GetGetMethod(),typeof(FileSystemInfo).GetProperty("FullName").GetGetMethod(),
    AccessTools.Method(typeof(FileSystemInfo),"Refresh",new[]{typeof(bool)}),
    AccessTools.Method(typeof(File).Assembly.GetType("System.IO.MonoIO",true),"GetFileStat"),
    AccessTools.Method(typeof(File).Assembly.GetType("System.IO.MonoIO",true),"GetFileAttributes"),
    AccessTools.Method(typeof(File).Assembly.GetType("System.IO.MonoIO",true),"ExistsFile"),typeof(Path).GetMethod("GetFullPath",new[]{typeof(string)})};
   var methods=new List<MethodBase>(sensitive);
   foreach(var c in PatchProcessor.GetOriginalInstructions(Target)) {var method=c.operand as MethodBase;if(method!=null && !methods.Contains(method))methods.Add(method);}
   sensitive=methods.ToArray();foreach(var method in sensitive)if(method==null)throw new MissingMethodException("Entry metadata dependency");
   MorphSnapshotState.ObserveHooks();var h=new Harmony(Id);
   try {h.Patch(RefreshTarget,transpiler:new HarmonyMethod(RefreshRewriteMethod));h.Patch(Target,transpiler:new HarmonyMethod(RewriteMethod));installed=true;checkedEpoch=-1;}
   catch {h.UnpatchAll(Id);throw;}
  }
  public static void Stop(){if(installed)new Harmony(Id).UnpatchAll(Id);installed=false;checkedEpoch=-1;}
 }
 public sealed partial class MemoryPlugin {
  private void InstallAssetEntryMetadata() {
   if(Config.Bind("SceneLoading","AssetEntryMetadata",true,"Merge repeated file queries into one live metadata snapshot within each ordinary SystemFileEntry constructor; no cross-call cache, retain original getters/validation and foreign hooks.").Value)
    InstallModule("AssetEntryMetadata",delegate {AssetEntryMetadata.Install();Logger.LogInfo("[asset-entry-metadata] snapshot=perConstructor statQueries=merged filesystemCache=False retainedEntries=0");});
  }
 }
}
