using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using MVR.FileManagement;
using ICSharpCode.SharpZipLib.Zip;
using VaM.Memory;

// Isolated build-time worker. It does not load UnityPlayer or install a game plugin.
public static class CollectorProbe {
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static void Set(object target,string name,object value){target.GetType().GetProperty(name,All).SetValue(target,value,null);}
    public static string Run() {try{return Build(true);}catch(Exception e){Console.WriteLine("TOOL_EXCEPTION: "+e);return "OFFLINE_FAILED";}}
    public static string Baseline() {return Build(false);}
    public static string Resident(){keepResident=true;try{return Build(true);}finally{keepResident=false;}}
    private static bool keepResident;
    private static string Build(bool disk) {
        string here=Path.GetDirectoryName(typeof(CollectorProbe).Assembly.Location);
        string[] settings=File.ReadAllLines(Path.Combine(here,"offline-settings.txt"));
        string output=settings[0],assemblyPath=settings[1],methodName=settings[2],expected=settings[3];
        int budget=int.Parse(settings[4]),limit=int.Parse(settings[5]),regionCount=int.Parse(settings[6]);
        var regions=new List<HashSet<int>>();
        for(int i=0;i<regionCount;i++){var set=new HashSet<int>();foreach(string v in settings[7+i].Split(','))set.Add(int.Parse(v));if(set.Count>8192)throw new InvalidDataException("region bound");regions.Add(set);}
        var assembly=Assembly.Load(File.ReadAllBytes(assemblyPath));
        Type type=assembly.GetType("NAT.Common.MorphListener",true);MethodInfo method=type.GetMethod(methodName,All);
        Type audit=typeof(MorphClassificationMemo).Assembly.GetType("Quest3TriggerUI.MorphFilterMethodAudit",true);
        string actual=(string)audit.GetMethod("Fingerprint",All).Invoke(null,new object[]{method});
        if(actual!=expected)throw new InvalidDataException("offline classifier fingerprint changed: "+actual);
        ZipConstants.DefaultCodePage=0;ZipEntryLookup.Install();MorphClassificationMemo.Install();
        if(disk){ClassificationDiskReceipts.DisableDiskPruning();ClassificationDiskReceipts.SetDiskLimits(budget,limit);ClassificationDiskReceipts.Configure(output);}
        var entries=new Dictionary<string,VarFileEntry>();typeof(FileManager).GetField("uidToVarFileEntry",All).SetValue(null,entries);
        int packages=0,errors=0,peakTables=0;long calls=0,totalHits=0,totalRecorded=0,positives=0,vertices=0,peakMemory=0;
        try {
            for(int p=7+regionCount;p<settings.Length;p++) {
                string path=settings[p];if(!path.EndsWith(".var",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("archive path");
                long beforeHits=ClassificationDiskReceipts.Hits,beforeRecorded=ClassificationDiskReceipts.Recorded;var watch=Stopwatch.StartNew();
                try {
                    var info=new FileInfo(path);if(!info.Exists)throw new FileNotFoundException("archive vanished",path);
                    using(var zip=new ZipFile(File.OpenRead(path))) {
                        string uid=Path.GetFileNameWithoutExtension(path);
                        var package=(VarPackage)FormatterServices.GetUninitializedObject(typeof(VarPackage));
                        Set(package,"Uid",uid);Set(package,"Path",path.Replace('/','\\'));Set(package,"FullPath",Path.GetFullPath(path));
                        Set(package,"Size",info.Length);Set(package,"LastWriteTime",info.LastWriteTime);
                        Set(package,"CachedUidColonSlash",uid+":/");Set(package,"CachedPathColonBackslash",path.Replace('/','\\')+":\\");
                        Set(package,"CachedFullPathColonBackslash",Path.GetFullPath(path)+":\\");Set(package,"CachedAddonPrefsPfx","AddonPackagesFilePrefs/"+uid+"/");
                        typeof(VarPackage).GetField("_zipFile",All).SetValue(package,zip);
                        var ambiguous=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach(ZipEntry z in zip)if(z.Name.EndsWith(".vmb",StringComparison.OrdinalIgnoreCase) && !names.Add(z.Name))ambiguous.Add(z.Name);
                        foreach(ZipEntry z in zip) {
                            if(ambiguous.Contains(z.Name)){Console.WriteLine("OFFLINE_AMBIGUOUS name="+z.Name);continue;}
                            if(!z.Name.EndsWith(".vmb",StringComparison.OrdinalIgnoreCase)||z.Size<4||z.Size>int.MaxValue||(z.Flags&1)!=0)continue;
                            var entry=new VarFileEntry(package,z.Name,z.DateTime,z.Size,false);
                            if(entry.InternalPath!=z.Name.Replace('/','\\')||entry.InternalSlashPath!=z.Name)throw new InvalidDataException("entry constructor mapping");
                            entries[FileManager.CleanFilePath(entry.Uid)]=entry;
                        }
                        foreach(var region in regions) {
                            object listener=FormatterServices.GetUninitializedObject(type);type.GetField("_vertexIndices",All).SetValue(listener,region);
                            foreach(string key in entries.Keys){
                                long h=ClassificationDiskReceipts.Hits,r=ClassificationDiskReceipts.Recorded,rejected=ClassificationDiskReceipts.Rejected;
                                if((bool)method.Invoke(listener,new object[]{key}))positives++;calls++;
                                if(disk && ClassificationDiskReceipts.Hits==h && ClassificationDiskReceipts.Recorded==r) {
                                    var selected=zip.GetEntry(entries[key].InternalSlashPath);
                                    int matchingNames=0;foreach(ZipEntry item in zip)if(item.Name==entries[key].InternalSlashPath)matchingNames++;
                                    Console.WriteLine("OFFLINE_UNCACHED key="+key+" lookupSame="+Object.ReferenceEquals(FileManager.GetFileEntry(key,true),entries[key])+" rejectedDelta="+(ClassificationDiskReceipts.Rejected-rejected)+" entrySize="+entries[key].Size+" selectedSize="+(selected==null?-1:selected.Size)+" dateMatches="+(selected!=null && selected.DateTime.Ticks==entries[key].LastWriteTime.Ticks)+" decodedNameMatches="+matchingNames);
                                }
                            }
                            if((long)type.GetField("_classificationFiles",All).GetValue(listener)!=entries.Count)throw new InvalidDataException("classifier counter");
                            vertices+=(long)type.GetField("_classificationVertices",All).GetValue(listener);
                        }
                    }
                    ClassificationDiskReceipts.Flush();
                    long hits=ClassificationDiskReceipts.Hits-beforeHits,recorded=ClassificationDiskReceipts.Recorded-beforeRecorded;
                    totalHits+=hits;totalRecorded+=recorded;packages++;
                    Console.WriteLine("OFFLINE_PACKAGE index="+(p-7-regionCount)+" files="+entries.Count+" hits="+hits+" recorded="+recorded+" ms="+watch.ElapsedMilliseconds);
                } catch(Exception error) {errors++;Console.WriteLine("OFFLINE_ERROR index="+(p-7-regionCount)+" error="+error);}
                finally {
                    peakMemory=Math.Max(peakMemory,ClassificationDiskReceipts.ReservedBytes);
                    int tables=((System.Collections.IDictionary)typeof(ClassificationDiskReceipts).GetField("Tables",All).GetValue(null)).Count;
                    peakTables=Math.Max(peakTables,tables);
                    entries.Clear();MorphClassificationMemo.Clear();if(!keepResident)ClassificationDiskReceipts.ResetMemory();
                }
            }
        } finally {entries.Clear();ClassificationDiskReceipts.Disable();MorphClassificationMemo.Stop();ZipEntryLookup.Stop();}
        return "OFFLINE_BUILD_RESULT packages="+packages+" errors="+errors+" calls="+calls+" hits="+totalHits+" recorded="+totalRecorded+" positives="+positives+" vertices="+vertices+" evicted="+ClassificationDiskReceipts.Evicted+" residentPeak="+peakTables+" memoryPeak="+peakMemory+" actualEntryConstructor=True productionDllWrites=0 gameOperations=0";
    }
}
