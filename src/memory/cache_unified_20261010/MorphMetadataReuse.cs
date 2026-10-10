using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using MVR.FileManagement;

namespace VaM.Memory {
 public static partial class MorphSnapshotState {
  private const long MetadataBudget=64L*1024*1024;
  private const int MetadataSlots=8;
  private static readonly FieldInfo InitCache=AccessTools.Field(typeof(DAZMorphBank),"_morphInitCache");
  private static readonly Dictionary<string,MetadataEntry> Metadata=new Dictionary<string,MetadataEntry>(StringComparer.Ordinal);
  private static Type SnapshotType,ListType;
  private static FieldInfo[] SnapshotFields;
  private static long metadataBytes,metadataClock,metadataHits,metadataMisses;
  private sealed class MetadataEntry {
   internal string Key,Source;
   internal State State;
   internal object Snapshot;
   internal long Bytes,Used;
  }
  private static void VerifyPureSchema() {
   if(InitCache==null)throw new MissingFieldException("_morphInitCache");
   ListType=InitCache.FieldType.GetGenericArguments()[1];SnapshotType=ListType.GetGenericArguments()[0];
   var schema=new Dictionary<string,Type>();
   foreach(string n in new[]{"morphName","displayName","overrideName","group","region","overrideRegion","metaLoadPath","deltasLoadPath","uid","packageUid","packageLicense","version"})schema.Add(n,typeof(string));
   foreach(string n in new[]{"isPoseControl","isInPackage","isLatestVersion","isDemandLoaded"})schema.Add(n,typeof(bool));
   schema.Add("min",typeof(float));schema.Add("max",typeof(float));schema.Add("numDeltas",typeof(int));schema.Add("formulas",typeof(DAZMorphFormula[]));
   SnapshotFields=SnapshotType.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
   if(SnapshotFields.Length!=schema.Count)throw new InvalidOperationException("morph snapshot schema changed");
   foreach(var f in SnapshotFields)if(!schema.ContainsKey(f.Name) || schema[f.Name]!=f.FieldType)throw new InvalidOperationException("snapshot field changed: "+f.Name);
   PrepareDirectRows();
  }
  private static long TextBytes(string value) {return value==null?0:32L+value.Length*2L;}
  private static void ClearMetadata() {Metadata.Clear();metadataBytes=0;}
  public static string MetadataStats() {
   lock(Gate)return "hits="+metadataHits+" misses="+metadataMisses+" tables="+Metadata.Count+" estimatedBytes="+metadataBytes+" budget="+MetadataBudget+" slots="+MetadataSlots+" payload=compressed-pure-scalars ownerRoots=0";
  }
  private static void PublishMetadata(Scope scope) {
   var bad=(HashSet<string>)BadNames.GetValue(scope.Bank);
   if(scope.ImportFailed || (bad!=null && bad.Count!=0) || scope.PureSaved==null || scope.SourceBefore==null || scope.SourceBefore!=scope.SourceAfter || !Matches(scope.Bank,scope.SavedState))return;
   var cache=(IDictionary)InitCache.GetValue(null);string key=null;
   foreach(DictionaryEntry item in cache)if(ReferenceEquals(item.Value,scope.SavedSnapshot)){key=(string)item.Key;break;}
   if(key==null)return;
   long bytes=scope.PureBytes+TextBytes(key)+TextBytes(scope.SourceAfter)+256+scope.SavedState.Directories.Length*64L;
   foreach(string uid in scope.SavedState.Loaded)bytes+=TextBytes(uid)+64;
   foreach(string uid in scope.SavedState.Preload)bytes+=TextBytes(uid)+64;
   if(bytes>MetadataBudget)return;
   MetadataEntry old;if(Metadata.TryGetValue(key,out old)){metadataBytes-=old.Bytes;Metadata.Remove(key);}
   while(Metadata.Count>=MetadataSlots || metadataBytes+bytes>MetadataBudget) {
    MetadataEntry victim=null;foreach(var item in Metadata.Values)if(victim==null || item.Used<victim.Used)victim=item;
    if(victim==null)break;Metadata.Remove(victim.Key);metadataBytes-=victim.Bytes;
   }
   scope.SavedState.MetadataSource=scope.SourceAfter;
   Metadata[key]=new MetadataEntry {Key=key,Source=scope.SourceAfter,State=scope.SavedState,Snapshot=scope.PureSaved,Bytes=bytes,Used=++metadataClock};metadataBytes+=bytes;
   WriteDiskMetadata(Metadata[key]);
  }
  private static object LookupMetadata(IDictionary original,string key) {
   // The original per-load cache always wins, including its comparer and exception behavior.
   if(original.Contains(key))return original[key];
   Scope scope=active;if(scope==null)return null;
   if(importRoot!=null){OpenImportScope(scope,key);return null;}
   lock(Gate) {
    MetadataEntry entry;
    bool fromDisk=false;
    if(!Metadata.TryGetValue(key,out entry)) {
     entry=ReadDiskMetadata(scope,key);
     if(entry==null){metadataMisses++;return null;}
     fromDisk=true;
    }
    if(!Matches(scope.Bank,entry.State) || scope.SourceBefore==null || entry.Source!=scope.SourceBefore || SourceSignature(scope.Bank.autoImportFolder)!=entry.Source) {
     if(!fromDisk){Metadata.Remove(key);metadataBytes-=entry.Bytes;}metadataMisses++;return null;
    }
    object copy;
    try {copy=DecodeMetadata(entry.Snapshot);}
    catch(InvalidDataException){if(!fromDisk){Metadata.Remove(key);metadataBytes-=entry.Bytes;}metadataMisses++;return null;}
    catch(IOException){if(!fromDisk){Metadata.Remove(key);metadataBytes-=entry.Bytes;}metadataMisses++;return null;}
    catch(ICSharpCode.SharpZipLib.SharpZipBaseException){if(!fromDisk){Metadata.Remove(key);metadataBytes-=entry.Bytes;}metadataMisses++;return null;}
    if(fromDisk) {
     while(Metadata.Count>=MetadataSlots || metadataBytes+entry.Bytes>MetadataBudget) {
      MetadataEntry victim=null;foreach(var item in Metadata.Values)if(victim==null || item.Used<victim.Used)victim=item;
      if(victim==null)break;Metadata.Remove(victim.Key);metadataBytes-=victim.Bytes;
     }
     Metadata.Add(key,entry);metadataBytes+=entry.Bytes;DiskReadAccepted();
    }
    if(States.Count>=128)States.Clear();States[copy]=entry.State;
    entry.Used=++metadataClock;metadataHits++;
    if(Log!=null)Log("[morph-metadata] "+MetadataStats()+" reuse=validated-pure clone=original firstRefresh=rechecked");
    return copy;
   }
  }
  public static IEnumerable<CodeInstruction> ReuseLookup(IEnumerable<CodeInstruction> instructions) {
   var code=new List<CodeInstruction>(instructions);int found=-1;
   for(int i=3;i<code.Count;i++) {
    var m=code[i].operand as MethodInfo;
    if(m==null || m.Name!="TryGetValue" || m.DeclaringType!=InitCache.FieldType)continue;
    if(found>=0 || code[i-3].opcode!=OpCodes.Ldsfld || !Equals(code[i-3].operand,InitCache) ||
       (code[i-2].opcode!=OpCodes.Ldloc_0) || (code[i-1].opcode!=OpCodes.Ldloca_S && code[i-1].opcode!=OpCodes.Ldloca))
     throw new InvalidOperationException("original snapshot lookup changed");
    for(int n=i-2;n<=i;n++)if(code[n].labels.Count!=0 || code[n].blocks.Count!=0)throw new InvalidOperationException("snapshot lookup interior boundary changed");
    found=i;
   }
   if(found<0)throw new InvalidOperationException("original snapshot lookup missing");
   var local=code[found-1].operand;
   code[found-1]=new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(MorphSnapshotState),"LookupMetadata"));
   code[found]=new CodeInstruction(OpCodes.Castclass,ListType);
   code.InsertRange(found+1,new[]{new CodeInstruction(OpCodes.Dup),new CodeInstruction(OpCodes.Stloc,local),new CodeInstruction(OpCodes.Ldnull),new CodeInstruction(OpCodes.Cgt_Un)});
   return code;
  }
  private static string SourceSignature(string folder) {
   if(string.IsNullOrEmpty(folder))return null;
   try {
    using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,Encoding.UTF8)) {
     writer.Write(folder);
     // Favorites are intentionally not cached; original InitMorphs rereads them.
     string path=Path.GetFullPath(folder);writer.Write(path);writer.Write(Directory.Exists(path));
     if(Directory.Exists(path)) {
      var files=Directory.GetFiles(path,"*",SearchOption.AllDirectories);if(files.Length>100000)return null;Array.Sort(files,StringComparer.Ordinal);
      foreach(string file in files) {
       string ext=Path.GetExtension(file);
       if(!string.Equals(ext,".vmi",StringComparison.OrdinalIgnoreCase) && !string.Equals(ext,".vmb",StringComparison.OrdinalIgnoreCase) && !string.Equals(ext,".dsf",StringComparison.OrdinalIgnoreCase))continue;
       var info=new FileInfo(file);if(!info.Exists)return null;
       writer.Write(file);writer.Write(info.Length);writer.Write(info.LastWriteTimeUtc.Ticks);
       if(stream.Length>8L*1024*1024)return null;
      }
     }
     var dirs=FileManager.FindVarDirectories(folder);writer.Write(dirs.Count);
     foreach(var dir in dirs) {
      VarPackage package=dir.Package;
      if(package==null || package.forceRefresh || package.invalid || package.IsSimulated || string.IsNullOrEmpty(package.FullPath))return null;
      var info=new FileInfo(package.FullPath);
      if(!info.Exists || info.Length!=package.Size || info.LastWriteTime.Ticks!=package.LastWriteTime.Ticks)return null;
      writer.Write(dir.Path);writer.Write(dir.LastWriteTime.Ticks);writer.Write(package.Uid);
      writer.Write(info.FullName);writer.Write(info.Length);writer.Write(info.LastWriteTimeUtc.Ticks);
      writer.Write(package.Group.GetCustomOption("preloadMorphs"));writer.Write(package.isNewestEnabledVersion);writer.Write(package.Version);
      writer.Write(package.LicenseType??"");
     }
     writer.Flush();stream.Position=0;
     using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");
    }
   }catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(NotSupportedException){return null;}
  }
 }
}
