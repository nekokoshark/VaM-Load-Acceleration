using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using HarmonyLib;
using ICSharpCode.SharpZipLib.Zip;
using MVR.FileManagement;
using SimpleJSON;

// Invoked by an isolated Mono process, not the Unity game process.
public static partial class CollectorProbe {
    public static string InitializePortable() {
        memory = Assembly.LoadFrom("VaM.Memory.dll");
        string game = File.ReadAllLines("portable-settings.txt")[0];
        memory.GetType("VaM.Memory.MemoryCachePaths",true).GetMethod("Initialize").Invoke(null,new object[]{game});
        memory.GetType("VaM.Memory.DelegateGraphLifetime",true).GetMethod("Install").Invoke(null,null);
        return "PORTABLE_INIT_PASS";
    }

    public static string SourceCaches() {
        string[] lines=File.ReadAllLines("source-settings.txt");
        string output=lines[0]; Directory.CreateDirectory(output); int built=0, rejected=0;
        ZipConstants.DefaultCodePage=0;
        for(int i=1;i<lines.Length;i++) {
            try {
                string path=lines[i]; var info=new FileInfo(path);
                var package=(VarPackage)FormatterServices.GetUninitializedObject(typeof(VarPackage));
                string uid=Path.GetFileNameWithoutExtension(path);
                foreach(string name in new[]{"Uid","Path","FullPath","SlashPath","FullSlashPath"})
                    Property(package,name,name=="Uid"?uid:info.FullName);
                Property(package,"Size",info.Length); Property(package,"LastWriteTime",info.LastWriteTime);
                Property(package,"CachedUidColonSlash",uid+":/");
                Property(package,"CachedUidColon",uid+":");
                Property(package,"CachedPathColon",info.FullName+":");
                Property(package,"CachedFullPathColon",info.FullName+":");
                Property(package,"CachedPathColonBackslash",info.FullName+":\\");
                Property(package,"CachedFullPathColonBackslash",info.FullName+":\\");
                Property(package,"CachedAddonPrefsPfx","AddonPackagesFilePrefs/"+uid+"/");
                using(var zip=new ZipFile(File.OpenRead(path))) {
                    Set(package,"_zipFile",zip);
                    var dirs=new Dictionary<string,VarDirectoryEntry>();
                    var root=new VarDirectoryEntry(package,"",null); dirs.Add("",root);
                    var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int vmb=0;
                    foreach(ZipEntry z in zip) {
                        if(!z.IsDirectory && z.Name.EndsWith(".vmb",StringComparison.OrdinalIgnoreCase))vmb++;
                        if(z.IsDirectory || !(z.Name.EndsWith(".vmi",StringComparison.OrdinalIgnoreCase)||z.Name.EndsWith(".vam",StringComparison.OrdinalIgnoreCase))) continue;
                        if(!seen.Add(z.Name))throw new InvalidDataException("ambiguous JSON entry");
                        if(z.Name.IndexOf('\r')>=0||z.Name.IndexOf('\n')>=0||z.Name.IndexOf('|')>=0)throw new InvalidDataException("entry separator");
                        string[] segments=z.Name.Split('/'); string parent="";
                        for(int n=0;n<segments.Length-1;n++) {
                            string next=parent.Length==0?segments[n]:parent+"/"+segments[n];
                            if(!dirs.ContainsKey(next)) {
                                var child=new VarDirectoryEntry(package,next,dirs[parent]);
                                ((HashSet<VarDirectoryEntry>)AccessTools.Field(typeof(VarDirectoryEntry),"varSubDirectories").GetValue(dirs[parent])).Add(child);
                                dirs.Add(next,child);
                            }
                            parent=next;
                        }
                        var entry=new VarFileEntry(package,z.Name,z.DateTime,z.Size,false);
                        ((List<VarFileEntry>)AccessTools.Field(typeof(VarDirectoryEntry),"varFileEntries").GetValue(dirs[parent])).Add(entry);
                    }
                    var cache=new JSONClass();
                    // Exactly the two patterns used by this game's original constructor.
                    Call(package,"AddDirToCache",root,"*.vmi",cache);
                    Call(package,"AddDirToCache",root,"*.vam",cache);
                    string target=Path.Combine(output,uid+".vamcachejson");
                    File.WriteAllText(target,cache.ToString(""),new UTF8Encoding(false));
                    var meta=new JSONClass();meta["type"]="json";meta["size"]=info.Length.ToString();meta["date"]=info.LastWriteTime.ToString();
                    File.WriteAllText(Path.Combine(output,uid+".vamcachemeta"),meta.ToString(""),new UTF8Encoding(false));
                    Console.WriteLine("SOURCE_CACHE_BUILT "+uid);built++;
                    if(vmb>0)Console.WriteLine("CLASSIFICATION_PACKAGE "+uid);
                }
            } catch(Exception e) { rejected++;Console.WriteLine("SOURCE_CACHE_REJECTED "+lines[i]+"|"+e); }
        }
        return "SOURCE_CACHE_PASS built="+built+" rejected="+rejected;
    }

    public static string PortableTrees() {
        string[] lines=File.ReadAllLines("tree-settings.txt");
        Type disk=memory.GetType("VaM.Memory.PackageTreeDisk",true);
        Call(disk,"Configure",lines[0]);int built=0,rejected=0;
        for(int i=1;i<lines.Length;i++) {
            try { Console.WriteLine("TREE_BUILT "+(string)Call(disk,"Build",lines[i]));built++; }
            catch(Exception e) {Console.WriteLine("TREE_REJECTED "+lines[i]+"|"+e.GetType().FullName);rejected++;}
        }
        return "TREE_BUILD_PASS built="+built+" rejected="+rejected;
    }

    public static string VerifyTrees() {
        string[] lines=File.ReadAllLines("tree-settings.txt");
        Type disk=memory.GetType("VaM.Memory.PackageTreeDisk",true);Call(disk,"Configure",lines[0]);int count=0;
        for(int i=1;i<lines.Length;i++) {
            using(var owner=new SystemFileEntryStreamReader(new SystemFileEntry(lines[i]))) {
                object[] args=new object[]{lines[i],owner,null};
                bool hit=(bool)disk.GetMethod("TryRead").Invoke(null,args);
                if(!hit || args[2]==null)throw new InvalidDataException("generated tree did not replay");
                var expected=JSON.Parse(File.ReadAllText(lines[i]));
                if(((JSONNode)args[2]).ToString("")!=expected.ToString(""))throw new InvalidDataException("tree result changed");
                count++;
            }
        }
        return "TREE_REPLAY_PASS count="+count+" "+Call(disk,"Stats");
    }
}
