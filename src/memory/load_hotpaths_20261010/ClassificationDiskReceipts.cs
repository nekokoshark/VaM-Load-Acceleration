using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MVR.FileManagement;
using ICSharpCode.SharpZipLib.Zip;

namespace VaM.Memory {
    public static class ClassificationDiskReceipts {
        private const long MaxMemory=16L*1024*1024;
        private const int MaxTables=256;
        private static long diskLimitBytes=512L*1024*1024;
        private static int diskFileLimit=8192;
        private static bool diskPruning=true;
        public static void DisableDiskPruning() {lock(Gate)diskPruning=false;}
        private static readonly object Gate=new object();
        private static readonly Dictionary<string,Table> Tables=new Dictionary<string,Table>(StringComparer.Ordinal);
        private static string root;
        private static long memoryBytes,diskBytes,useSequence;
        private static int diskFiles;
        public static long Hits,Recorded,Rejected,Corrupt,Evicted,Bypassed;
        private static long nextFlushCheck,quietUntil,observedRecords,flushedRecords;
        public static void TickIdle(long now,bool loading) {
            if(root==null || now<nextFlushCheck)return;
            nextFlushCheck=now+TimeSpan.TicksPerSecond;
            lock(Gate) {
                if(loading || Recorded!=observedRecords) {
                    observedRecords=Recorded;quietUntil=now+5*TimeSpan.TicksPerSecond;return;
                }
                if(now<quietUntil || Recorded==flushedRecords)return;
                Flush();flushedRecords=Recorded;
            }
        }
        public static Action<string> Log;
        public static long ReservedBytes {get {lock(Gate)return memoryBytes;}}
        private sealed class Table {
            internal Dictionary<string,ClassificationReceiptFormat.Row> Rows=new Dictionary<string,ClassificationReceiptFormat.Row>(StringComparer.Ordinal);
            internal bool Dirty,Writable=true;
            internal long Bytes=256,FileBytes,LastUsed;
        }
        private static readonly MethodBase[] Inputs=new MethodBase[] {
            AccessTools.Method(typeof(MVR.FileManagementSecure.FileManagerSecure),"ReadAllBytes"),
            AccessTools.Method(typeof(FileManager),"GetFileEntry",new[]{typeof(string),typeof(bool)}),
            AccessTools.Method(typeof(FileManager),"GetVarFileEntry",new[]{typeof(string)}),
            AccessTools.Method(typeof(FileManager),"ReadAllBytes",new[]{typeof(string),typeof(bool)}),
            AccessTools.Method(typeof(FileManager),"ReadAllBytes",new[]{typeof(FileEntry)}),
            AccessTools.Method(typeof(FileManager),"OpenStream",new[]{typeof(FileEntry)}),
            AccessTools.PropertyGetter(typeof(VarPackage),"ZipFile"),
            AccessTools.Constructor(typeof(VarFileEntryStream),new[]{typeof(VarFileEntry)}),
            AccessTools.PropertyGetter(typeof(FileEntryStream),"Stream"),
            AccessTools.PropertySetter(typeof(FileEntryStream),"Stream"),
            AccessTools.Method(typeof(ZipFile),"GetInputStream",new[]{typeof(ZipEntry)}),
            AccessTools.Method(typeof(ZipFile),"GetInputStream",new[]{typeof(long)}),
            AccessTools.Method(typeof(ZipFile),"LocateEntry"),
            AccessTools.Method(typeof(ZipFile),"GetEntry"),
            AccessTools.Method(typeof(ZipFile),"FindEntry")
        };
        // Unknown input/result rewrites are not portable receipts. Known patches are identity-checked.
        private static bool Allowed(MethodBase method) {
            var patches=Harmony.GetPatchInfo(method);if(patches==null)return true;
            foreach(var patch in patches.Prefixes)
                if(patch.PatchMethod!=AccessTools.Method(typeof(MorphClassificationMemo),"Before") &&
                    patch.PatchMethod!=AccessTools.Method(typeof(ZipEntryLookup),"Before"))return false;
            foreach(var patch in patches.Postfixes)
                if(patch.PatchMethod!=AccessTools.Method(typeof(MorphClassificationMemo),"Read") && !ClassificationStreamCompatibility.Allows(method,patch))return false;
            if(patches.Transpilers.Count!=0 || patches.ILManipulators.Count!=0)return false;
            foreach(var patch in patches.Finalizers)
                if(patch.PatchMethod!=AccessTools.Method(typeof(MorphClassificationMemo),"After"))return false;
            return true;
        }
        private static readonly Dictionary<MethodBase,bool> Compatibility=new Dictionary<MethodBase,bool>();
        private static long compatibilityEpoch=-1;
        public static long CompatibilityChecks,CompatibilityHits;
        private static bool Compatible(MethodBase classifier) {
            long epoch=MorphSnapshotState.HookEpoch;
            if(MorphSnapshotState.HooksChanging)return false;
            if(epoch!=compatibilityEpoch){Compatibility.Clear();compatibilityEpoch=epoch;}
            bool result;
            if(Compatibility.TryGetValue(classifier,out result)) {
                CompatibilityHits++;return epoch==MorphSnapshotState.HookEpoch && !MorphSnapshotState.HooksChanging && result;
            }
            result=CheckCompatible(classifier);CompatibilityChecks++;
            if(epoch!=MorphSnapshotState.HookEpoch || MorphSnapshotState.HooksChanging)return false;
            if(Compatibility.Count>=128)Compatibility.Clear();
            Compatibility[classifier]=result;return result;
        }
        private static bool CheckCompatible(MethodBase classifier) {
            if(!Allowed(classifier))return false;
            foreach(var input in Inputs)if(input==null || !Allowed(input))return false;
            return true;
        }
        public static void SetDiskLimits(int megabytes,int files) {
            if(megabytes<16 || megabytes>16384 || files<128 || files>65536)throw new ArgumentOutOfRangeException("classification disk limits");
            lock(Gate) {diskLimitBytes=(long)megabytes*1024*1024;diskFileLimit=files;}
        }
        public static void Configure(string directory) {
            MorphSnapshotState.ObserveHooks();
            lock(Gate) {
                ResetMemory();root=Path.GetFullPath(directory);diskBytes=0;diskFiles=0;
                nextFlushCheck=quietUntil=0;observedRecords=flushedRecords=Recorded;
                if(diskPruning && Directory.Exists(root))foreach(string path in Directory.GetFiles(root,"*.mcr")) {
                    diskFiles++;diskBytes+=new FileInfo(path).Length;

                }
            }
        }
        public static void ResetMemory() {lock(Gate){Tables.Clear();memoryBytes=0;}}
        public static void Disable() {lock(Gate){ResetMemory();Compatibility.Clear();compatibilityEpoch=-1;root=null;WindowsReceiptDigest.Shutdown();ClassificationStreamCompatibility.Clear();}}
        private static long RowBytes(string key) {return 256L+key.Length*2L;}
        private static byte[] ReadBounded(string path) {
            using(var file=File.OpenRead(path)) {
                if(file.Length>ClassificationReceiptFormat.MaxFileBytes)throw new InvalidDataException("receipt file bound");
                byte[] bytes=new byte[(int)file.Length];int read=0;
                while(read<bytes.Length){int n=file.Read(bytes,read,bytes.Length-read);if(n==0)throw new EndOfStreamException();read+=n;}
                return bytes;
            }
        }
        private static Table For(string key) {
            Table table;if(Tables.TryGetValue(key,out table)){table.LastUsed=++useSequence;return table;}
            table=new Table();string path=Path.Combine(root,key+".mcr");
            if(File.Exists(path)) {
                try {
                    byte[] bytes=ReadBounded(path);
                    table.Rows=ClassificationReceiptFormat.Decode(bytes,key);table.FileBytes=bytes.Length;
                    foreach(string name in table.Rows.Keys)table.Bytes+=RowBytes(name);
                } catch(InvalidDataException) {table.Rows.Clear();table.Writable=false;Corrupt++;}
                  catch(IOException) {table.Rows.Clear();table.Writable=false;Corrupt++;}
                  catch(ArgumentException) {table.Rows.Clear();table.Writable=false;Corrupt++;}
                  catch(UnauthorizedAccessException) {table.Rows.Clear();table.Writable=false;Corrupt++;}
            }
            if(table.Bytes>MaxMemory)return null;
            while(Tables.Count>=MaxTables || memoryBytes+table.Bytes>MaxMemory) {
                string oldest=null;long age=long.MaxValue;
                foreach(var pair in Tables)if(!pair.Value.Dirty && pair.Value.LastUsed<age){oldest=pair.Key;age=pair.Value.LastUsed;}
                if(oldest==null)return null; // Pending receipts remain owned until normal quit.
                memoryBytes-=Tables[oldest].Bytes;Tables.Remove(oldest);
            }
            table.LastUsed=++useSequence;Tables.Add(key,table);memoryBytes+=table.Bytes;return table;
        }
        internal static bool TryGet(VarFileEntry entry,string algorithm,string region,MethodBase method,
            out MorphClassificationMemo.Result result,out ClassificationSourceStamp stamp) {
            result=new MorphClassificationMemo.Result();stamp=null;
            lock(Gate) {
                if(root==null)return false;
                if(!Compatible(method)) {
                    Bypassed++;
                    if(Log!=null && (Bypassed==1 || Bypassed%4096==0))Log("[classification-disk] bypass=input-patches count="+Bypassed);
                    return false;
                }
                try {
                    stamp=ClassificationSourceStamp.Capture(entry,algorithm,region);
                    if(stamp==null){Rejected++;return false;}
                    Table table=For(stamp.NamespaceKey);ClassificationReceiptFormat.Row row;
                    if(table!=null && table.Rows.TryGetValue(stamp.RowKey,out row) && row.Signature==stamp.Signature) {
                        result=row.Result;Hits++;
                        if(Log!=null && (Hits==1 || Hits%4096==0))Log("[classification-disk] hits="+Hits+" recorded="+Recorded+" estimatedBytes="+memoryBytes+" payloadCache=none input=compressed-sha256");
                        return true;
                    }
                    return false;
                } catch(Exception) {stamp=null;Rejected++;return false;}
            }
        }
        internal static void Record(VarFileEntry entry,string algorithm,string region,MethodBase method,
            ClassificationSourceStamp before,MorphClassificationMemo.Result result) {
            lock(Gate) {
                if(root==null || before==null || !Compatible(method))return;
                try {
                    var after=ClassificationSourceStamp.Capture(entry,algorithm,region);
                    if(!before.Equal(after)){Rejected++;return;}
                    Table table=For(before.NamespaceKey);if(table==null || !table.Writable)return;
                    bool present=table.Rows.ContainsKey(before.RowKey);
                    long bytes=present?0:RowBytes(before.RowKey);
                    if((!present && table.Rows.Count>=ClassificationReceiptFormat.MaxRows) ||
                        table.Bytes+bytes>ClassificationReceiptFormat.MaxFileBytes || memoryBytes+bytes>MaxMemory)return;
                    table.Rows[before.RowKey]=new ClassificationReceiptFormat.Row{Signature=before.Signature,Result=result};
                    memoryBytes+=bytes;table.Bytes+=bytes;table.Dirty=true;Recorded++;
                } catch(Exception) {Rejected++;}
            }
        }
        private static bool Room(string target,long old,int bytes,bool exists) {
            if(!diskPruning)return true;
            if((exists || diskFiles<diskFileLimit) && diskBytes-old+bytes<=diskLimitBytes)return true;
            if(!Directory.Exists(root))return false;
            var candidates=new List<FileInfo>();
            foreach(string path in Directory.GetFiles(root,"*.mcr")) {
                string full=Path.GetFullPath(path),key=Path.GetFileNameWithoutExtension(full);
                if(!string.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase) || full==target ||
                    !ClassificationReceiptFormat.IsHash(key) || Tables.ContainsKey(key))continue;
                candidates.Add(new FileInfo(full));
            }
            candidates.Sort(delegate(FileInfo a,FileInfo b){return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);});
            foreach(FileInfo file in candidates) {
                try {
                    string key=Path.GetFileNameWithoutExtension(file.FullName);
                    if(file.Length>ClassificationReceiptFormat.MaxFileBytes)continue;
                    // Ownership requires this format, matching namespace and a valid complete checksum.
                    ClassificationReceiptFormat.Decode(ReadBounded(file.FullName),key);
                    long length=file.Length;File.Delete(file.FullName);diskBytes-=length;diskFiles--;Evicted++;
                    if((exists || diskFiles<diskFileLimit) && diskBytes-old+bytes<=diskLimitBytes)return true;
                } catch(InvalidDataException) { }
                  catch(IOException) { }
                  catch(ArgumentException) { }
                  catch(UnauthorizedAccessException) { }
            }
            return false;
        }
        // Coalesced after loading becomes quiet, and again at normal application quit.
        public static void Flush() {
            lock(Gate) {
                if(root==null)return;
                foreach(var pair in Tables) {
                    Table table=pair.Value;if(!table.Dirty || !table.Writable)continue;
                    string target=Path.Combine(root,pair.Key+".mcr"),temp=null;
                    try {
                        byte[] bytes=ClassificationReceiptFormat.Encode(pair.Key,table.Rows);
                        bool exists=File.Exists(target);
                        long old=exists?new FileInfo(target).Length:0;
                        // A concurrently replaced file is not ours to overwrite.
                        if(exists && (old>ClassificationReceiptFormat.MaxFileBytes ||
                            ClassificationReceiptFormat.Decode(ReadBounded(target),pair.Key)==null))continue;
                        if(!Room(target,old,bytes.Length,exists))continue;
                        Directory.CreateDirectory(root);temp=Path.Combine(root,pair.Key+"."+Guid.NewGuid().ToString("N")+".tmp");
                        using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {file.Write(bytes,0,bytes.Length);file.Flush();}
                        if(exists)File.Replace(temp,target,null);else File.Move(temp,target);
                        temp=null;diskBytes+=bytes.Length-old;if(!exists)diskFiles++;table.FileBytes=bytes.Length;table.Dirty=false;
                    } catch(InvalidDataException) {Rejected++;}
                      catch(IOException) {Rejected++;}
                      catch(ArgumentException) {Rejected++;}
                      catch(UnauthorizedAccessException) {Rejected++;}
                    finally {if(temp!=null)try{File.Delete(temp);}catch(IOException){Rejected++;}catch(UnauthorizedAccessException){Rejected++;}}
                }
            }
        }
    }
}
