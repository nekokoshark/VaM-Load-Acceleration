using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Text.RegularExpressions;
using HarmonyLib;
using MVR.FileManagement;

namespace VaM.Memory {
 // Cache slot NUMBERS, never entries/packages or result lists. The original
 // current-cultural prefix and regex tests build each selection; destination
 // appends remain original and names/paths invalidate each selection. Every set/path mutation invalidates it.
 public static class VarDirectorySelection {
  public const string Id="vam.memory.var-directory-selection";
  private const int CountLimit=32;
  private const long ByteLimit=8L*1024*1024;
  private static readonly object Gate=new object();
  private static readonly LinkedList<Selection> Cache=new LinkedList<Selection>();
  private static readonly FieldInfo SourceField=AccessTools.Field(typeof(FileManager),"allVarFileEntries");
  private static readonly FieldInfo GenerationField=AccessTools.Field(typeof(HashSet<VarFileEntry>),"generation");
  private static readonly FieldInfo SlotsField=AccessTools.Field(typeof(HashSet<VarFileEntry>),"slots");
  private static readonly FieldInfo NextField=AccessTools.Field(typeof(HashSet<VarFileEntry>.Enumerator),"next");
  private static readonly MethodInfo Target=AccessTools.Method(typeof(FileManager),"FindVarFilesRegex",new[]{typeof(string),typeof(string),typeof(List<FileEntry>)});
  private static readonly MethodInfo RewriteMethod=AccessTools.Method(typeof(VarDirectorySelection),"Rewrite");
  private static readonly MethodInfo ChangeMethod=AccessTools.Method(typeof(VarDirectorySelection),"Changing");
  private static readonly MethodInfo ChangeDoneMethod=AccessTools.Method(typeof(VarDirectorySelection),"Changed");
  private static readonly MethodInfo Starts=typeof(string).GetMethod("StartsWith",new[]{typeof(string)});
  private static readonly MethodInfo Match=typeof(Regex).GetMethod("IsMatch",new[]{typeof(string),typeof(string),typeof(RegexOptions)});
  private static readonly MethodInfo PathGetter=AccessTools.PropertyGetter(typeof(VarFileEntry),"InternalSlashPath");
  private static readonly MethodInfo NameGetter=AccessTools.PropertyGetter(typeof(FileEntry),"Name");
  private static readonly MethodInfo PathSetter=AccessTools.PropertySetter(typeof(VarFileEntry),"InternalSlashPath");
  private static readonly MethodInfo NameSetter=AccessTools.PropertySetter(typeof(FileEntry),"Name");
  private static readonly Func<HashSet<VarFileEntry>,int> Generation=FieldReader<int>(GenerationField);
  private static readonly Func<HashSet<VarFileEntry>,VarFileEntry[]> Slots=FieldReader<VarFileEntry[]>(SlotsField);
  private static readonly Func<HashSet<VarFileEntry>.Enumerator,int> Next=NextReader();
  private static MethodBase[] sensitive;
  private static long pathEpoch,checkedEpoch=-1,retainedBytes;
  private static bool installed,compatible;
  public static long Builds,Hits,Scanned,Selected,Declined;
  private sealed class Selection {
   internal WeakReference Source;
   internal string Directory,Pattern;
   internal CompareInfo Culture;
   internal int Generation;
   internal long Epoch;
   internal int[] Indices;
   internal long Bytes;
  }
  private static Func<HashSet<VarFileEntry>,T> FieldReader<T>(FieldInfo field) {
   if(field==null || field.FieldType!=typeof(T))throw new MissingFieldException("Original HashSet selection layout");
   var m=new DynamicMethod("VarSelection"+field.Name,typeof(T),new[]{typeof(HashSet<VarFileEntry>)},typeof(VarDirectorySelection),true);
   var il=m.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,field);il.Emit(OpCodes.Ret);
   return (Func<HashSet<VarFileEntry>,T>)m.CreateDelegate(typeof(Func<HashSet<VarFileEntry>,T>));
  }
  private static Func<HashSet<VarFileEntry>.Enumerator,int> NextReader() {
   if(NextField==null || NextField.FieldType!=typeof(int))throw new MissingFieldException("Original HashSet enumerator cursor");
   var m=new DynamicMethod("VarSelectionNext",typeof(int),new[]{typeof(HashSet<VarFileEntry>.Enumerator)},typeof(VarDirectorySelection),true);
   var il=m.GetILGenerator();il.Emit(OpCodes.Ldarga_S,(byte)0);il.Emit(OpCodes.Ldfld,NextField);il.Emit(OpCodes.Ret);
   return (Func<HashSet<VarFileEntry>.Enumerator,int>)m.CreateDelegate(typeof(Func<HashSet<VarFileEntry>.Enumerator,int>));
  }
  // Hold the same gate from BEFORE the actual setter through its finalizer.
  // A prefix-only invalidation allowed a query to publish old data between
  // invalidation and the field write. Reentrant calls invalidate both ends.
  private static void Changing(FileEntry __instance,ref bool __state) {
   if(!(__instance is VarFileEntry))return;
   Monitor.Enter(Gate);__state=true;Interlocked.Increment(ref pathEpoch);Clear();
  }
  private static Exception Changed(Exception __exception,bool __state) {
   if(__state)try{Interlocked.Increment(ref pathEpoch);Clear();}finally{Monitor.Exit(Gate);}
   return __exception;
  }
  public static void Clear(){lock(Gate){Cache.Clear();retainedBytes=0;}}
  public static long RetainedBytes {get{lock(Gate)return retainedBytes;}}
  public static string Stats(){return "builds="+Builds+" hits="+Hits+" scanned="+Scanned+" selected="+Selected+" declined="+Declined+" bytes="+RetainedBytes+" entryRoots=0";}
  private static bool Compatible() {
   long epoch=MorphSnapshotState.HookEpoch;if(MorphSnapshotState.HooksChanging)return false;
   if(epoch==checkedEpoch)return compatible;
   bool okay=true;
   foreach(var method in sensitive) {
    var info=Harmony.GetPatchInfo(method);if(info==null)continue;
    foreach(var patches in new[]{info.Prefixes,info.Postfixes,info.Transpilers,info.Finalizers,info.ILManipulators})foreach(var patch in patches)
     if(!((method==Target && patch.PatchMethod==RewriteMethod) ||
       ((method==PathSetter || method==NameSetter) &&
        (patch.PatchMethod==ChangeMethod || patch.PatchMethod==ChangeDoneMethod))))okay=false;
   }
   if(epoch!=MorphSnapshotState.HookEpoch || MorphSnapshotState.HooksChanging)return false;
   checkedEpoch=epoch;compatible=okay;if(!okay)Clear();return okay;
  }
  public static bool TryFind(string directory,string regex,List<FileEntry> output) {
   lock(Gate)return FindLocked(directory,regex,output);
  }
  private static bool FindLocked(string directory,string regex,List<FileEntry> output) {
   if(!installed || !Compatible()){Interlocked.Increment(ref Declined);return false;}
   var source=(HashSet<VarFileEntry>)SourceField.GetValue(null);
   if(source==null)return true;
   int generation=Generation(source);long stamp=Interlocked.Read(ref pathEpoch);
   var culture=CultureInfo.CurrentCulture.CompareInfo;Selection found=null;
   lock(Gate) {
    var node=Cache.First;
    while(node!=null) {
     var next=node.Next;var entry=node.Value;var owner=entry.Source.Target;
     if(owner==null || (ReferenceEquals(owner,source) && (entry.Generation!=generation || entry.Epoch!=stamp))) {
      retainedBytes-=entry.Bytes;Cache.Remove(node);
     } else if(ReferenceEquals(owner,source) && ReferenceEquals(entry.Culture,culture) && string.Equals(entry.Directory,directory,StringComparison.Ordinal) && string.Equals(entry.Pattern,regex,StringComparison.Ordinal)) {
      found=entry;Cache.Remove(node);Cache.AddFirst(node);break;
     }
     node=next;
    }
   }
   if(found!=null) {
    var slots=Slots(source);var enumerator=source.GetEnumerator();
    try {
     foreach(int index in found.Indices) {
      // If a real collection mutation occurs, use the ORIGINAL enumerator's
      // version check and exception instead of returning a stale selection.
      if(Generation(source)!=generation)enumerator.MoveNext();
      var file=slots[index];
      output.Add(file);
     }
     if(Generation(source)!=generation)enumerator.MoveNext();
    }finally{enumerator.Dispose();}
    Interlocked.Increment(ref Hits);Interlocked.Add(ref Selected,found.Indices.Length);return true;
   }
   var indices=new List<int>();var original=source.GetEnumerator();var originalSlots=Slots(source);bool layout=true;
   try {
    while(original.MoveNext()) {
     var file=original.Current;Scanned++;
     if(!file.InternalSlashPath.StartsWith(directory))continue;
     if(!Regex.IsMatch(file.Name,regex,RegexOptions.IgnoreCase))continue;
     int index=Next(original)-1;
     if(index<0 || index>=originalSlots.Length || !ReferenceEquals(originalSlots[index],file))layout=false;
     if(indices.Count<ByteLimit/4)indices.Add(index);else layout=false;
     output.Add(file);
    }
   }finally{original.Dispose();}
   long bytes=256+directory.Length*2L+(regex==null?0:regex.Length*2L)+indices.Count*4L;
   if(layout && bytes<=ByteLimit && Generation(source)==generation && Interlocked.Read(ref pathEpoch)==stamp && Compatible()) {
    var added=new Selection {Source=new WeakReference(source),Directory=directory,Pattern=regex,Culture=culture,Generation=generation,Epoch=stamp,Indices=indices.ToArray(),Bytes=bytes};
    lock(Gate) {
     while(Cache.Count!=0 && (Cache.Count>=CountLimit || retainedBytes+bytes>ByteLimit)){retainedBytes-=Cache.Last.Value.Bytes;Cache.RemoveLast();}
     Cache.AddFirst(added);retainedBytes+=bytes;Interlocked.Increment(ref Builds);
    }
   }
   return true;
  }
  public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input,ILGenerator generator) {
   var code=new List<CodeInstruction>(input);var clean=AccessTools.Method(typeof(FileManager),"CleanDirectoryPath",new[]{typeof(string)});
   int site=-1;
   for(int i=0;i<code.Count;i++)if(code[i].opcode==OpCodes.Call && Equals(code[i].operand,clean)){if(site!=-1)throw new InvalidDataException("Multiple VAR directory normalization sites");site=i;}
   if(site<0 || site+2>=code.Count || code[site+1].opcode!=OpCodes.Starg_S)throw new InvalidDataException("VAR directory normalization shape changed");
   int start=site+2;Label fallback=generator.DefineLabel();var entry=new CodeInstruction(OpCodes.Ldarg_0);
   entry.labels.AddRange(code[start].labels);code[start].labels.Clear();code[start].labels.Add(fallback);
   code.InsertRange(start,new[]{entry,new CodeInstruction(OpCodes.Ldarg_1),new CodeInstruction(OpCodes.Ldarg_2),
    new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(VarDirectorySelection),"TryFind")),new CodeInstruction(OpCodes.Brfalse,fallback),new CodeInstruction(OpCodes.Ret)});
   return code;
  }
  public static void Install() {
   if(installed)return;
   if(SourceField==null || SourceField.FieldType!=typeof(HashSet<VarFileEntry>) || Target==null || PathSetter==null || NameSetter==null)throw new MissingMemberException("Original VAR selection contract");
   var methods=new List<MethodBase>{Target,Starts,Match,PathGetter,NameGetter,PathSetter,NameSetter,typeof(List<FileEntry>).GetMethod("Add")};
   methods.AddRange(typeof(HashSet<VarFileEntry>).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly));
   methods.AddRange(typeof(HashSet<VarFileEntry>.Enumerator).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly));
   methods.AddRange(typeof(Regex).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly));
   sensitive=methods.ToArray();foreach(var method in sensitive)if(method==null)throw new MissingMethodException("VAR selection dependency");
   MorphSnapshotState.ObserveHooks();var h=new Harmony(Id);
   try {
    var before=new HarmonyMethod(ChangeMethod){priority=Priority.First};
    var after=new HarmonyMethod(ChangeDoneMethod){priority=Priority.Last};
    h.Patch(PathSetter,prefix:before,finalizer:after);
    h.Patch(NameSetter,prefix:before,finalizer:after);
    h.Patch(Target,transpiler:new HarmonyMethod(RewriteMethod));installed=true;checkedEpoch=-1;
   }catch{h.UnpatchAll(Id);Clear();throw;}
  }
  public static void Stop(){if(installed)new Harmony(Id).UnpatchAll(Id);installed=false;checkedEpoch=-1;Clear();}
 }
 public sealed partial class MemoryPlugin {
  private void InstallVarDirectorySelection() {
   if(Config.Bind("SceneLoading","VarDirectorySelection",true,"Reuse bounded original-cultural directory selections by live HashSet generation and path mutation epoch; slot numbers only, original regex results with live name/path invalidation, no package or entry roots.").Value)
    InstallModule("VarDirectorySelection",delegate {VarDirectorySelection.Install();Logger.LogInfo("[var-directory-selection] prefix=original culture=live source=live-generation entryRoots=0 budgetMiB=8");});
  }
 }
}
