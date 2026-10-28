using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;
using Quest3TriggerUI;

namespace VaM.Memory {
 public static partial class MorphSnapshotState {
  private const int ImportMagic=0x32494D56;
  private static string importRoot;
  private static long importHits,importMisses,importWrites,importRejected,importAmbiguous;
  private static MethodInfo importNativeAdapter;
  private static readonly JSONNode ImportMarker=new JSONClass();
  private static readonly MethodInfo ImportMethod=AccessTools.Method(typeof(DAZMorphBank),"RuntimeImportMorph");
  private static readonly MethodInfo JsonMethod=AccessTools.Method(typeof(DAZMorphBank),"GetVmiJsonCached");
  private static readonly MethodInfo LoadMethod=AccessTools.Method(typeof(DAZMorph),"LoadMetaFromJSON");
  private static readonly Func<FileEntry,JSONNode> OriginalImportJson=(Func<FileEntry,JSONNode>)Delegate.CreateDelegate(typeof(Func<FileEntry,JSONNode>),JsonMethod);
  private static readonly FieldInfo PackageJsonCache=AccessTools.Field(typeof(VarPackage),"jsonCache");
  private static readonly FieldInfo PackageJsonLoaded=AccessTools.Field(typeof(VarPackage),"_jsonCacheLoaded");
  private static readonly FieldInfo VmiJsonCache=AccessTools.Field(typeof(DAZMorphBank),"_vmiJsonCache");
  private static readonly FieldInfo JsonCachePath=AccessTools.Field(typeof(VarPackage),"_jsonCachePath");
  private static FieldInfo[] ImportSnapshotFields,ImportMorphFields;
  private static FieldInfo ImportRowPath,ImportRowStamp;
  private static readonly MethodInfo ReplayMethod=AccessTools.Method(typeof(MorphSnapshotState),"ReplayImport");
  private static readonly MethodInfo StreamMethod=AccessTools.Method(typeof(PackageStreamRuntime),"Rewrite");
  private static readonly MethodInfo TargetReuseMethod=AccessTools.Method(typeof(MorphTargetStringReuse),"Rewrite");
  private static readonly MethodInfo WeakJsonPrefix=AccessTools.Method(typeof(PackageJsonWeakRetention),"Prefix");
  private static readonly MethodInfo WeakJsonFinalizer=AccessTools.Method(typeof(PackageJsonWeakRetention),"Finalizer");
  private static long importPatchEpoch,importCheckedEpoch=-1;
  private static int importPatchBusy;
  private static bool importObserverInstalled,importCheckedCompatible;
  private static string importBlockedMethod;
  private static MethodBase[] importSensitiveMethods;

  // Pre-filter receipts never restore accepted banks or runtime object links.
  public static void ConfigureImportReceipts(string directory) {
   lock(Gate) {
    ConfigureMetadataDisk(directory);importRoot=diskRoot;diskRoot=null;ClearMetadata();
    if(importRoot==null)return;
    string[] fields={"morphName","displayName","overrideName","group","region","overrideRegion","min","max","numDeltas","isPoseControl","formulas"};
    ImportSnapshotFields=new FieldInfo[fields.Length];ImportMorphFields=new FieldInfo[fields.Length];
    for(int i=0;i<fields.Length;i++) {
     ImportSnapshotFields[i]=SnapshotType.GetField(fields[i],BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
     ImportMorphFields[i]=AccessTools.Field(typeof(DAZMorph),fields[i]);
     if(ImportSnapshotFields[i]==null || ImportMorphFields[i]==null || ImportSnapshotFields[i].FieldType!=ImportMorphFields[i].FieldType)throw new MissingFieldException("import metadata "+fields[i]);
    }
    ImportRowPath=SnapshotType.GetField("metaLoadPath");ImportRowStamp=SnapshotType.GetField("uid");
    PrepareDirectImport();
    var sensitive=new List<MethodBase>();
    sensitive.Add(AccessTools.Constructor(typeof(DAZMorphFormula)));
    foreach(Type type in typeof(MorphSnapshotState).Assembly.GetTypes())
     if(type==typeof(PackageStreamRuntime) || type==typeof(PackageTreeDisk) || type.Namespace=="StartupPackageJson" || type==typeof(MorphTargetStringReuse) || type==typeof(PackageJsonWeakRetention))
     sensitive.AddRange(type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly));
    sensitive.Add(AccessTools.Method(typeof(FileManager),"ReadAllText",new[]{typeof(string),typeof(bool)}));
    sensitive.Add(AccessTools.Method(typeof(FileManager),"ReadAllText",new[]{typeof(FileEntry)}));
    sensitive.Add(AccessTools.Method(typeof(JSON),"Parse",new[]{typeof(string)}));
    sensitive.Add(AccessTools.Method(typeof(JSONNode),"Parse",new[]{typeof(string)}));
    sensitive.Add(AccessTools.Method(typeof(Enum),"Parse",new[]{typeof(Type),typeof(string)}));
    foreach(Type type in new[]{typeof(JSONNode),typeof(JSONClass),typeof(JSONArray),typeof(JSONData),typeof(JSONNode).Assembly.GetType("SimpleJSON.JSONLazyCreator",true)})
     sensitive.AddRange(type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly));
    foreach(Type type in new[]{typeof(JSONNode),typeof(JSONClass),typeof(JSONArray),typeof(JSONData),typeof(JsonCapacityRuntime)}) {
     sensitive.AddRange(type.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static));
     if(type==typeof(JsonCapacityRuntime))sensitive.AddRange(type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.DeclaredOnly));
    }
    importSensitiveMethods=sensitive.ToArray();InstallImportPatchObserver();importCheckedEpoch=-1;
   }
  }
  // The isolated host replaces Unity internal calls; this witness is never configured by Awake.
  public static void SetImportNativeAdapter(MethodInfo method) {importNativeAdapter=method;importCheckedEpoch=-1;}
  internal static void ObserveHooks(){InstallImportPatchObserver();}
  internal static long HookEpoch {get{return Interlocked.Read(ref importPatchEpoch);}}
  internal static bool HooksChanging {get{return importPatchBusy!=0;}}
  private static void PatchChanging(){Interlocked.Increment(ref importPatchBusy);Interlocked.Increment(ref importPatchEpoch);}
  private static void PatchChanged(){Interlocked.Increment(ref importPatchEpoch);Interlocked.Decrement(ref importPatchBusy);}
  private static void InstallImportPatchObserver() {
   lock(Gate) {
    if(importObserverInstalled)return;
    // HarmonyX 2.9 publishes five patch arrays through these exact mutators.
    // Obsolete singular Add wrappers delegate here and must not be observed twice.
    Type type=typeof(Harmony).Assembly.GetType("HarmonyLib.PatchInfo",true);
    var methods=new List<MethodInfo>();
    var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    string[] adds={"AddPrefixes","AddPostfixes","AddTranspilers","AddFinalizers","AddILManipulators"};
    string[] removes={"RemovePrefix","RemovePostfix","RemoveTranspiler","RemoveFinalizer","RemoveILManipulator"};
    foreach(string name in adds) {
     var method=type.GetMethod(name,flags,null,new[]{typeof(string),typeof(HarmonyMethod[])},null);
     if(method==null || method.ReturnType!=typeof(void))throw new MissingMethodException(type.FullName,name);
     methods.Add(method);
    }
    foreach(string name in removes) {
     var method=type.GetMethod(name,flags,null,new[]{typeof(string)},null);
     if(method==null || method.ReturnType!=typeof(void))throw new MissingMethodException(type.FullName,name);
     methods.Add(method);
    }
    var remove=type.GetMethod("RemovePatch",flags,null,new[]{typeof(MethodInfo)},null);
    if(remove==null || remove.ReturnType!=typeof(void))throw new MissingMethodException(type.FullName,"RemovePatch(MethodInfo)");
    methods.Add(remove);
    var harmony=new Harmony(Id+".import-hook-epoch");
    try {
     foreach(var method in methods)harmony.Patch(method,prefix:new HarmonyMethod(typeof(MorphSnapshotState),"PatchChanging"),finalizer:new HarmonyMethod(typeof(MorphSnapshotState),"PatchChanged"));
     importObserverInstalled=true;
    } catch {
     harmony.UnpatchSelf();
     throw;
    }
   }
  }
  public static string ImportReceiptStats() {
   return "enabled="+(importRoot!=null)+" hits="+importHits+" misses="+importMisses+" writes="+importWrites+" rejected="+importRejected+" ambiguousPaths="+importAmbiguous+" runtimeRoots="+(active==null?0:1)+" blocked="+(importBlockedMethod??"none");
  }
  private static bool AllowedPatches(MethodBase method,params MethodInfo[] allowed) {
   var info=Harmony.GetPatchInfo(method);if(info==null)return true;
   foreach(var patches in new[]{info.Prefixes,info.Postfixes,info.Transpilers,info.Finalizers,info.ILManipulators})foreach(var patch in patches) {
    bool found=false;foreach(var valid in allowed)if(valid!=null && patch.PatchMethod==valid){found=true;break;}
    if(!found){importBlockedMethod=method.DeclaringType.FullName+"."+method.Name;return false;}
   }
   return true;
  }
  private static bool ImportCompatible() {
   long epoch=Interlocked.Read(ref importPatchEpoch);
   if(importPatchBusy!=0)return false;
   if(importObserverInstalled && epoch==importCheckedEpoch)return importCheckedCompatible;
   bool result=ScanImportCompatible();
   if(importPatchBusy!=0 || epoch!=Interlocked.Read(ref importPatchEpoch))return false;
   importCheckedCompatible=result;importCheckedEpoch=epoch;return result;
  }
  private static bool ScanImportCompatible() {
   importBlockedMethod=null;
   // Unknown callbacks may mutate the JSON tree; do not infer that they are observers.
   if(!AllowedPatches(ImportMethod,ReplayMethod,AccessTools.Method(typeof(MorphSnapshotState),"ImportEnded"),importNativeAdapter))return false;
   if(!AllowedPatches(JsonMethod,importNativeAdapter) || !AllowedPatches(LoadMethod,TargetReuseMethod) || !AllowedPatches(AccessTools.Constructor(typeof(DAZMorph))))return false;
   if(!AllowedPatches(AccessTools.Method(typeof(VarPackage),"GetJSONCache"),StreamMethod,WeakJsonPrefix,WeakJsonFinalizer))return false;
   foreach(var method in importSensitiveMethods)
    if(!AllowedPatches(method,method==JsonCapacityRuntime.Target?AccessTools.Method(typeof(JsonCapacityRuntime),"Rewrite"):null))return false;
   return true;
  }
  private static string ScopeSourceSignature(string folder) {
   if(importRoot==null)return SourceSignature(folder);
   string original=SourceSignature(folder);if(original==null)return null;
   try {
    using(var buffer=new MemoryStream())using(var writer=new BinaryWriter(buffer,Encoding.UTF8)) {
     WriteText(writer,original);WriteText(writer,CultureInfo.CurrentCulture.Name);
     NumberFormatInfo number=CultureInfo.CurrentCulture.NumberFormat;
     foreach(string value in new[]{number.NumberDecimalSeparator,number.NumberGroupSeparator,number.NegativeSign,number.PositiveSign,number.NaNSymbol,number.NegativeInfinitySymbol,number.PositiveInfinitySymbol})WriteText(writer,value);
     foreach(var directory in FileManager.FindVarDirectories(folder)) {
      string path=(string)JsonCachePath.GetValue(directory.Package);WriteText(writer,path);
      if(path==null)continue;
      var file=new FileInfo(path);writer.Write(file.Exists);
      if(file.Exists){writer.Write(file.Length);writer.Write(file.LastWriteTimeUtc.Ticks);}
     }
     writer.Flush();using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(buffer.ToArray())).Replace("-","");
    }
   }catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(NotSupportedException){return null;}
  }
  private static string ImportPath(string key) {
   using(var hash=SHA256.Create())return Path.Combine(importRoot,"imp-"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key+"\0"+CultureInfo.CurrentCulture.Name))).Replace("-","")+".mri");
  }
  private static string PackageSourceStamp(VarPackage package) {
   if(package==null || package.invalid || package.forceRefresh || package.IsSimulated || string.IsNullOrEmpty(package.FullPath))return null;
   try {
    var file=new FileInfo(package.FullPath);
    if(!file.Exists || file.Length!=package.Size || file.LastWriteTime.Ticks!=package.LastWriteTime.Ticks)return null;
    string cache=(string)JsonCachePath.GetValue(package);if(string.IsNullOrEmpty(cache))return null;
    var json=new FileInfo(cache);if(!json.Exists)return null;
    return file.FullName+"\0"+file.Length+"\0"+file.LastWriteTimeUtc.Ticks+"\0"+json.FullName+"\0"+json.Length+"\0"+json.LastWriteTimeUtc.Ticks;
   }catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(NotSupportedException){return null;}
  }
  private static string ImportEntryStamp(FileEntry entry,Scope scope) {
   var file=entry as VarFileEntry;if(file==null)return null;
   string stamp;
   if(scope.ImportPackageStamps==null)scope.ImportPackageStamps=new Dictionary<VarPackage,string>();
   if(!scope.ImportPackageStamps.TryGetValue(file.Package,out stamp)) {
    stamp=PackageSourceStamp(file.Package);scope.ImportPackageStamps.Add(file.Package,stamp);
   }
   return stamp==null?null:stamp+"\0"+file.InternalSlashPath+"\0"+entry.Size+"\0"+entry.LastWriteTime.Ticks;
  }
  private static string[] ImportCultureValues() {
   NumberFormatInfo n=CultureInfo.CurrentCulture.NumberFormat;
   return new[]{CultureInfo.CurrentCulture.Name,n.NumberDecimalSeparator,n.NumberGroupSeparator,n.NegativeSign,n.PositiveSign,n.NaNSymbol,n.NegativeInfinitySymbol,n.PositiveInfinitySymbol};
  }
  private static bool ImportCultureMatches(Scope scope) {
   string[] c=scope.ImportCulture;NumberFormatInfo n=CultureInfo.CurrentCulture.NumberFormat;
   return c!=null && c[0]==CultureInfo.CurrentCulture.Name && c[1]==n.NumberDecimalSeparator && c[2]==n.NumberGroupSeparator && c[3]==n.NegativeSign && c[4]==n.PositiveSign && c[5]==n.NaNSymbol && c[6]==n.NegativeInfinitySymbol && c[7]==n.PositiveInfinitySymbol;
  }
  private static void OpenImportScope(Scope scope,string key) {
   scope.ImportKey=scope.Bank.autoImportFolder;
   if(!ImportCompatible()){importRejected++;return;}
   scope.ImportRows=new Dictionary<string,object>(StringComparer.Ordinal);
   scope.ImportCulture=ImportCultureValues();scope.ImportProtectedPackages=new HashSet<VarPackage>();
   foreach(var directory in FileManager.FindVarDirectories(scope.Bank.autoImportFolder))
    if(PackageJsonWeakRetention.PeekKnownTree(directory.Package)!=null || (bool)PackageJsonLoaded.GetValue(directory.Package))scope.ImportProtectedPackages.Add(directory.Package);
   scope.ImportProtectedPaths=new HashSet<string>(((Dictionary<string,JSONNode>)VmiJsonCache.GetValue(null)).Keys,StringComparer.Ordinal);
   // Original _vmiJsonCache is keyed by Path, not entry stamp. Duplicate archive
   // paths must keep its first-reader semantics even when the directory order varies.
   var paths=new HashSet<string>(StringComparer.Ordinal);var stack=new Stack<DirectoryEntry>();
   foreach(var directory in FileManager.FindVarDirectories(scope.Bank.autoImportFolder))stack.Push(directory);
   while(stack.Count!=0) {
    var directory=stack.Pop();foreach(var child in directory.SubDirectories)stack.Push(child);
    foreach(var entry in directory.Files)if(string.Equals(Path.GetExtension(entry.Path),".vmi",StringComparison.OrdinalIgnoreCase) && !paths.Add(entry.Path))
     if(scope.ImportProtectedPaths.Add(entry.Path))importAmbiguous++;
   }
   try {
    string path=ImportPath(scope.ImportKey);var file=new FileInfo(path);if(!file.Exists || file.Length>MetadataBudget)return;
    byte[] payload;
    using(var input=File.OpenRead(path))using(var reader=new BinaryReader(input,Encoding.UTF8)) {
     if(reader.ReadInt32()!=ImportMagic || ReadText(reader)!=diskSchema || ReadText(reader)!=scope.ImportKey || ReadText(reader)!=("physical-source-v1\0"+string.Join("\0",ImportCultureValues())))return;
     int count=reader.ReadInt32();if(count<32 || count>MetadataBudget || input.Length-input.Position!=count)return;
     payload=reader.ReadBytes(count);
    }
    foreach(object row in (IEnumerable)DecodeMetadata(payload)) {
     string rowPath=(string)ImportRowPath.GetValue(row);
     if(string.IsNullOrEmpty(rowPath) || scope.ImportRows.ContainsKey(rowPath))throw new InvalidDataException("import row key");
     scope.ImportRows.Add(rowPath,row);
    }
   }catch(InvalidDataException){scope.ImportRows.Clear();}catch(IOException){scope.ImportRows.Clear();}
   catch(UnauthorizedAccessException){scope.ImportRows.Clear();}catch(ICSharpCode.SharpZipLib.SharpZipBaseException){scope.ImportRows.Clear();}
  }
  private static DAZMorphFormula[] CopyImportFormulas(DAZMorphFormula[] input,bool replay=false) {
   if(input==null)return null;var output=new DAZMorphFormula[input.Length];
   for(int i=0;i<input.Length;i++)output[i]=input[i]==null?null:new DAZMorphFormula {targetType=input[i].targetType,target=replay?MorphTargetStringReuse.Canonical(input[i].target):input[i].target,multiplier=input[i].multiplier};
   return output;
  }
  private static JSONNode ImportJson(FileEntry entry) {
   Scope scope=active;object row;
   if(importRoot==null || scope==null || scope.ImportRows==null)return OriginalImportJson(entry);
   bool existing=((Dictionary<string,JSONNode>)VmiJsonCache.GetValue(null)).ContainsKey(entry.Path);
   if(scope!=null)scope.ImportCapture=!existing;
   if(importRoot!=null && scope!=null && scope.ImportRows!=null && scope.Generation==generation && ImportCultureMatches(scope) &&
    !existing && scope.ImportRows.TryGetValue(entry.Uid,out row) &&
    !scope.ImportProtectedPaths.Contains(entry.Path) &&
    ImportEntryStamp(entry,scope)!=null && (string)ImportRowStamp.GetValue(row)==ImportEntryStamp(entry,scope) && ImportCompatible() &&
    (!(entry is VarFileEntry) || (!scope.ImportProtectedPackages.Contains(((VarFileEntry)entry).Package) &&
      !((VarFileEntry)entry).Package.forceRefresh && !((VarFileEntry)entry).Package.invalid && !((VarFileEntry)entry).Package.IsSimulated &&
      PackageJsonWeakRetention.PeekKnownTree(((VarFileEntry)entry).Package)==null && !(bool)PackageJsonLoaded.GetValue(((VarFileEntry)entry).Package)))) {
    scope.ImportSelected=row;scope.ImportSelectedEntry=entry;importHits++;return ImportMarker;
   }
   if(importRoot!=null)importMisses++;
   return OriginalImportJson(entry);
  }
  private static void ImportMeta(DAZMorph morph,JSONNode json,FileEntry entry) {
   Scope scope=active;
   if(ReferenceEquals(json,ImportMarker)) {
    if(scope==null || scope.ImportSelected==null || !ReferenceEquals(scope.ImportSelectedEntry,entry))throw new InvalidOperationException("import receipt context");
    object row=scope.ImportSelected;scope.ImportSelected=null;scope.ImportSelectedEntry=null;
    if(scope.Generation==generation && ImportCultureMatches(scope) && ImportCompatible()) {
     if(importCopy!=null){importCopy(row,morph);DirectImports++;return;}
     for(int i=0;i<ImportMorphFields.Length;i++) {
      object value=ImportSnapshotFields[i].GetValue(row);
      if(ImportMorphFields[i].FieldType==typeof(DAZMorphFormula[]))value=CopyImportFormulas((DAZMorphFormula[])value,true);
      ImportMorphFields[i].SetValue(morph,value);
     }
     return;
    }
    json=OriginalImportJson(entry);
   }
   morph.LoadMetaFromJSON(json);
   if(importRoot==null || scope==null || !scope.ImportCapture || scope.ImportRows==null || scope.Generation!=generation || !ImportCultureMatches(scope) || !ImportCompatible() ||
    ImportEntryStamp(entry,scope)==null || scope.ImportProtectedPaths.Contains(entry.Path) || (entry is VarFileEntry && scope.ImportProtectedPackages.Contains(((VarFileEntry)entry).Package)))return;
   if(scope.ImportRows.Count>=100000 && !scope.ImportRows.ContainsKey(entry.Uid)){scope.ImportRows=null;importRejected++;return;}
   // GetJSONCache catches parse exceptions and returns null before the original
   // per-entry ZIP fallback. Such rows must not suppress its next-session error.
   if(entry is VarFileEntry && PackageJsonWeakRetention.PeekKnownTree(((VarFileEntry)entry).Package)==null)return;
   object unchanged;
   if(scope.ImportRows.TryGetValue(entry.Uid,out unchanged) && (string)ImportRowStamp.GetValue(unchanged)==ImportEntryStamp(entry,scope))return;
   if(morph.formulas!=null && morph.formulas.Length>65536)return;
   object captured=Activator.CreateInstance(SnapshotType,true);
   for(int i=0;i<ImportMorphFields.Length;i++) {
    object value=ImportMorphFields[i].GetValue(morph);
    if(ImportMorphFields[i].FieldType==typeof(DAZMorphFormula[]))value=CopyImportFormulas((DAZMorphFormula[])value);
    ImportSnapshotFields[i].SetValue(captured,value);
   }
   ImportRowPath.SetValue(captured,entry.Uid);ImportRowStamp.SetValue(captured,ImportEntryStamp(entry,scope));
   scope.ImportRows[entry.Uid]=captured;scope.ImportDirty=true;
  }
  private static void CloseImportScope(Scope scope,Exception error) {
   if(importRoot==null || error!=null || scope.Generation!=generation || !scope.ImportDirty || scope.ImportRows==null || !ImportCompatible())return;
   if(scope.ImportPackageStamps!=null)foreach(var pair in scope.ImportPackageStamps)if(pair.Value!=PackageSourceStamp(pair.Key))return;
   WriteImportRows(scope);
  }
  private static void WriteImportRows(Scope scope) {
   var list=(IList)Activator.CreateInstance(ListType);foreach(object row in scope.ImportRows.Values)list.Add(row);
   long bytes;object encoded=PureCopy(list,out bytes);if(encoded==null)return;
   byte[] payload=(byte[])encoded;string path=ImportPath(scope.ImportKey),temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try {
    Directory.CreateDirectory(importRoot);
    using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new BinaryWriter(output,Encoding.UTF8)) {
     writer.Write(ImportMagic);WriteText(writer,diskSchema);WriteText(writer,scope.ImportKey);WriteText(writer,"physical-source-v1\0"+string.Join("\0",ImportCultureValues()));writer.Write(payload.Length);writer.Write(payload);
     if(output.Length>MetadataBudget)throw new InvalidDataException("import budget");
    }
    if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);importWrites++;
    // Disk retention is independent of the bounded per-file/memory budget; no eviction.

   }catch(InvalidDataException){}catch(IOException){}catch(UnauthorizedAccessException){}
   finally{if(File.Exists(temporary)){try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}}
  }
  public static int BuildImportReceipts(string folder,IEnumerable<FileEntry> entries) {
   if(importRoot==null || !ImportCompatible())throw new InvalidOperationException("receipt builder not configured");
   var scope=new Scope {ImportKey=folder,ImportRows=new Dictionary<string,object>(StringComparer.Ordinal)};
   var groups=new Dictionary<VarPackage,List<FileEntry>>();var paths=new HashSet<string>(StringComparer.Ordinal);var ambiguous=new HashSet<string>(StringComparer.Ordinal);
   foreach(var entry in entries) {
    var file=entry as VarFileEntry;if(file==null || !string.Equals(Path.GetExtension(file.InternalSlashPath),".vmi",StringComparison.OrdinalIgnoreCase))continue;
    if(!paths.Add(entry.Path))ambiguous.Add(entry.Path);
    List<FileEntry> list;if(!groups.TryGetValue(file.Package,out list)){list=new List<FileEntry>();groups.Add(file.Package,list);}list.Add(entry);
   }
   foreach(var group in groups) {
    var pending=new Dictionary<string,object>(StringComparer.Ordinal);
    if(PackageSourceStamp(group.Key)==null)continue;
    try {
     foreach(var entry in group.Value) {
      if(ambiguous.Contains(entry.Path))continue;
      string stamp=ImportEntryStamp(entry,scope);if(stamp==null)continue;
      JSONNode json=OriginalImportJson(entry);if(ReferenceEquals(json,null))continue;
      var morph=new DAZMorph();morph.LoadMetaFromJSON(json);
      if(morph.formulas!=null && morph.formulas.Length>65536)continue;
      object row=Activator.CreateInstance(SnapshotType,true);
      for(int i=0;i<ImportMorphFields.Length;i++) {
       object value=ImportMorphFields[i].GetValue(morph);
       if(ImportMorphFields[i].FieldType==typeof(DAZMorphFormula[]))value=CopyImportFormulas((DAZMorphFormula[])value);
       ImportSnapshotFields[i].SetValue(row,value);
      }
      ImportRowPath.SetValue(row,entry.Uid);ImportRowStamp.SetValue(row,stamp);pending[entry.Uid]=row;
     }
     if(PackageJsonWeakRetention.PeekKnownTree(group.Key)!=null && scope.ImportPackageStamps[group.Key]==PackageSourceStamp(group.Key))foreach(var pair in pending)scope.ImportRows[pair.Key]=pair.Value;
    }catch(Exception error){Console.WriteLine("OFFLINE_PACKAGE_REJECTED "+group.Key.Uid+" "+error.GetType().FullName);}
    finally {
     ((Dictionary<string,JSONNode>)VmiJsonCache.GetValue(null)).Clear();
     PackageJsonCache.SetValue(group.Key,null);PackageJsonLoaded.SetValue(group.Key,false);
    }
    if(scope.ImportRows.Count>100000)throw new InvalidDataException("offline row budget");
   }
   WriteImportRows(scope);return scope.ImportRows.Count;
  }
  private static bool OwnedImportFile(string path) {
   string name=Path.GetFileName(path);if(name.Length!=72 || !name.StartsWith("imp-",StringComparison.Ordinal) || !name.EndsWith(".mri",StringComparison.Ordinal))return false;
   for(int i=4;i<68;i++)if(!((name[i]>='0' && name[i]<='9') || (name[i]>='A' && name[i]<='F')))return false;
   return Path.GetDirectoryName(Path.GetFullPath(path))==importRoot;
  }
  public static IEnumerable<CodeInstruction> ReplayImport(IEnumerable<CodeInstruction> input) {
   var code=new List<CodeInstruction>(input);int json=0,meta=0;
   for(int i=0;i<code.Count;i++) {
    var method=code[i].operand as MethodInfo;
    if(method==JsonMethod){code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(MorphSnapshotState),"ImportJson");json++;}
    else if(method==LoadMethod) {
     var load=new CodeInstruction(OpCodes.Ldarg_1);load.labels.AddRange(code[i].labels);load.blocks.AddRange(code[i].blocks);code[i].labels.Clear();code[i].blocks.Clear();
     code.Insert(i,load);i++;code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(MorphSnapshotState),"ImportMeta");meta++;
    }
   }
   if(json!=1 || meta!=1)throw new InvalidOperationException("original import call sites changed");return code;
  }
 }
}
