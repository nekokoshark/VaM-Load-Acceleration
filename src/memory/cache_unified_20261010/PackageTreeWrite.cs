using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MVR.FileManagement;
using SimpleJSON;

namespace VaM.Memory {
 public static partial class PackageTreeDisk {
  public sealed class WriteReceipt {
   internal byte[] SourceHash;
   internal string Directory;
   internal long Epoch;
  }
  public static long Writes,WriteRejected,WriteTicks;
  // The receipt and parsed trees exist only inside the original load call.
  // No background queue, tree root, whole-library scan, or disk eviction.
  public static void Commit(WriteReceipt receipt,FileEntryStreamReader owner,JSONNode tree) {
   if(receipt==null || owner==null || owner.StreamReader==null || !Compatible() ||
      receipt.Epoch!=MorphSnapshotState.HookEpoch || receipt.Directory!=root || !StartupPackageJson.JsonStreamParser.CanStream)return;
   var reader=owner.StreamReader;var source=reader.BaseStream as FileStream;
   if(source==null || !source.CanSeek)return;
   long begin=Stopwatch.GetTimestamp(),position=source.Position;
   try {
    byte[] data=Encode(tree);
    // Reparse the same already-authorized open stream, without foreign callbacks.
    // Reject nondeterminism instead of freezing a one-off parser result.
    reader.DiscardBufferedData();source.Position=0;
    JSONNode second;
    try {second=StartupPackageJson.JsonStreamParser.Parse(reader);}
    catch(Exception e) {
     // This optional second parse may see an edited source after the successful
     // original load. Preserve that original result, but never swallow fatal errors.
     if(e.GetType()!=typeof(Exception) && !(e is FormatException) && !(e is IndexOutOfRangeException))throw;
     Interlocked.Increment(ref WriteRejected);return;
    }
    if(!Same(data,Encode(second))) {Interlocked.Increment(ref WriteRejected);return;}
    reader.DiscardBufferedData();source.Position=0;
    if(!Same(receipt.SourceHash,CacheSha256.Hash(source)) || receipt.Epoch!=MorphSnapshotState.HookEpoch ||
       receipt.Directory!=root || !Compatible()) {Interlocked.Increment(ref WriteRejected);return;}
    Store(receipt.SourceHash,data,receipt.Directory);
    Interlocked.Increment(ref Writes);Interlocked.Add(ref WriteTicks,Stopwatch.GetTimestamp()-begin);
   }catch(InvalidDataException){Interlocked.Increment(ref WriteRejected);}
    catch(IOException){Interlocked.Increment(ref WriteRejected);}
    catch(UnauthorizedAccessException){Interlocked.Increment(ref WriteRejected);}
    catch(ArgumentException){Interlocked.Increment(ref WriteRejected);}
    catch(InvalidOperationException){Interlocked.Increment(ref WriteRejected);}
   finally {reader.DiscardBufferedData();source.Position=position;}
  }
  private static void Store(byte[] hash,byte[] data,string directory) {
   string target=Path.Combine(directory,BitConverter.ToString(hash).Replace("-","")+".vjt");
   System.IO.Directory.CreateDirectory(directory);string staged=target+"."+Guid.NewGuid().ToString("N")+".tmp";
   try {
    using(var output=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new BinaryWriter(output)) {
     writer.Write(Magic);writer.Write(Schema);writer.Write(hash);writer.Write(CacheSha256.Hash(data,0,data.Length));writer.Write(data.Length);writer.Write(data);
    }
    if(File.Exists(target))File.Replace(staged,target,null);else File.Move(staged,target);
   }finally {if(File.Exists(staged))File.Delete(staged);}
  }
 }
}
