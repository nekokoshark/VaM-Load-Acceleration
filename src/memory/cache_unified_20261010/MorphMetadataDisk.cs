using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MVR.FileManagement;

namespace VaM.Memory {
 public static partial class MorphSnapshotState {
  private const int DiskMagic=0x31444D50;
  private static string diskRoot,diskSchema;
  private static long diskHits,diskMisses,diskWrites;

  // Research prototype: no production startup caller. An explicit isolated caller is required.
  public static void ConfigureMetadataDisk(string directory) {
   lock(Gate) {
    diskRoot=directory==null?null:Path.GetFullPath(directory);
    diskSchema=null;
    if(diskRoot==null)return;
    using(var content=new MemoryStream())using(var writer=new BinaryWriter(content,Encoding.UTF8)) {
     WriteText(writer,"morph-metadata-disk-prototype-v2-complete-imports-only");
     foreach(var field in SnapshotFields){WriteText(writer,field.Name);WriteText(writer,field.FieldType.FullName);}
     writer.Write(CodecMagic);
     using(var input=File.OpenRead(typeof(DAZMorphBank).Assembly.Location))using(var hash=SHA256.Create())writer.Write(hash.ComputeHash(input));
     // This Mono may expose no Location for a LoadFrom/byte-loaded plugin. Use its loaded module identity.
     WriteText(writer,typeof(MorphSnapshotState).Assembly.FullName);
     writer.Write(typeof(MorphSnapshotState).Module.ModuleVersionId.ToByteArray());
     writer.Flush();using(var hash=SHA256.Create())diskSchema=BitConverter.ToString(hash.ComputeHash(content.ToArray())).Replace("-","");
    }
   }
  }
  public static string MetadataDiskStats() {
   lock(Gate)return "enabled="+(diskRoot!=null)+" hits="+diskHits+" misses="+diskMisses+" writes="+diskWrites+" slots="+MetadataSlots+" budget="+MetadataBudget;
  }
  private static string DiskPath(string key) {
   using(var buffer=new MemoryStream())using(var writer=new BinaryWriter(buffer,Encoding.UTF8)) {
    WriteText(writer,key);writer.Flush();
    using(var hash=SHA256.Create())return Path.Combine(diskRoot,"meta-"+BitConverter.ToString(hash.ComputeHash(buffer.ToArray())).Replace("-","")+".mmr");
   }
  }
  private static State CurrentSourceState(DAZMorphBank bank,string source) {
   var dirs=FileManager.FindVarDirectories(bank.autoImportFolder);
   var loaded=new HashSet<string>();var preload=new HashSet<string>();var stamps=new Stamp[dirs.Count];
   for(int i=0;i<dirs.Count;i++) {
    var entry=dirs[i];var package=entry.Package;
    if(package==null || package.forceRefresh || package.invalid || package.IsSimulated)return null;
    loaded.Add(package.Uid);if(package.Group.GetCustomOption("preloadMorphs"))preload.Add(package.Uid);
    stamps[i]=new Stamp {Entry=new WeakReference(entry),EntryTicks=entry.LastWriteTime.Ticks,PackageTicks=package.LastWriteTime.Ticks,PackageSize=package.Size};
   }
   return new State {Folder=bank.autoImportFolder,MetadataSource=source,Generation=libraryGeneration,Loaded=loaded,Preload=preload,Directories=stamps};
  }
  private static MetadataEntry ReadDiskMetadata(Scope scope,string key) {
   if(diskRoot==null || scope.SourceBefore==null)return null;
   try {
    string path=DiskPath(key);var info=new FileInfo(path);
    if(!info.Exists || info.Length>MetadataBudget){diskMisses++;return null;}
    byte[] payload;
    using(var input=File.OpenRead(path))using(var reader=new BinaryReader(input,Encoding.UTF8)) {
     if(reader.ReadInt32()!=DiskMagic || ReadText(reader)!=diskSchema || ReadText(reader)!=key || ReadText(reader)!=scope.SourceBefore){diskMisses++;return null;}
     int size=reader.ReadInt32();
     if(size<32 || size>MetadataBudget || input.Length-input.Position!=size){diskMisses++;return null;}
     payload=reader.ReadBytes(size);if(payload.Length!=size)throw new EndOfStreamException();
    }
    State state=CurrentSourceState(scope.Bank,scope.SourceBefore);
    if(state==null || !Matches(scope.Bank,state)){diskMisses++;return null;}
    long bytes=32L+payload.Length+TextBytes(key)+TextBytes(scope.SourceBefore)+256+state.Directories.Length*64L;
    foreach(string uid in state.Loaded)bytes+=TextBytes(uid)+64;
    foreach(string uid in state.Preload)bytes+=TextBytes(uid)+64;
    if(bytes>MetadataBudget){diskMisses++;return null;}
    return new MetadataEntry {Key=key,Source=scope.SourceBefore,State=state,Snapshot=payload,Bytes=bytes};
   }catch(InvalidDataException){diskMisses++;return null;}catch(IOException){diskMisses++;return null;}catch(UnauthorizedAccessException){diskMisses++;return null;}
  }
  private static void DiskReadAccepted(){diskHits++;}
  private static bool OwnedDiskFile(string path) {
   string name=Path.GetFileName(path);
   if(name.Length!=73 || !name.StartsWith("meta-",StringComparison.Ordinal) || !name.EndsWith(".mmr",StringComparison.Ordinal))return false;
   for(int i=5;i<69;i++)if(!((name[i]>='0' && name[i]<='9') || (name[i]>='A' && name[i]<='F')))return false;
   return Path.GetDirectoryName(Path.GetFullPath(path))==diskRoot;
  }
  private static void TrimDisk(string current) {
   var files=new List<FileInfo>();long total=0;
   foreach(string path in Directory.GetFiles(diskRoot,"meta-*.mmr"))if(OwnedDiskFile(path)){var file=new FileInfo(path);files.Add(file);total+=file.Length;}
   files.Sort(delegate(FileInfo a,FileInfo b){return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);});
   int remaining=files.Count;
   foreach(var file in files) {
    if(remaining<=MetadataSlots && total<=MetadataBudget)break;
    if(file.FullName==current)continue;
    if(!OwnedDiskFile(file.FullName))throw new InvalidDataException("metadata cache path changed");
    long size=file.Length;File.Delete(file.FullName);remaining--;total-=size;
   }
  }
  private static void WriteDiskMetadata(MetadataEntry entry) {
   if(diskRoot==null)return;
   string temporary=null;
   try {
    byte[] payload=(byte[])entry.Snapshot;
    Directory.CreateDirectory(diskRoot);string path=DiskPath(entry.Key);
    temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
    using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new BinaryWriter(output,Encoding.UTF8)) {
     writer.Write(DiskMagic);WriteText(writer,diskSchema);WriteText(writer,entry.Key);WriteText(writer,entry.Source);
     writer.Write(payload.Length);writer.Write(payload);
     if(output.Length>MetadataBudget)throw new InvalidDataException("metadata disk budget");
    }
    if(!OwnedDiskFile(path) || Path.GetDirectoryName(Path.GetFullPath(temporary))!=diskRoot)throw new InvalidDataException("metadata destination changed");
    if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
    temporary=null;TrimDisk(path);diskWrites++;
   }catch(InvalidDataException){}catch(IOException){}catch(UnauthorizedAccessException){}
   finally {
    if(temporary!=null && Path.GetDirectoryName(Path.GetFullPath(temporary))==diskRoot) {
     try {File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
   }
  }
 }
}
