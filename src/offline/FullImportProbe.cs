using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using ICSharpCode.SharpZipLib.Zip;
using MVR.FileManagement;
using SimpleJSON;
using UnityEngine;

// This helper is loaded by the isolated native Mono host, never by VaM.
public static partial class CollectorProbe {
    static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    sealed class Identity : IEqualityComparer<object> {
        public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object a) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a); }
    }
    static readonly Dictionary<object, string> names = new Dictionary<object, string>(new Identity());
    static readonly Dictionary<object, object> links = new Dictionary<object, object>(new Identity());
    static readonly Dictionary<object, List<DAZMorphSubBank>> children = new Dictionary<object, List<DAZMorphSubBank>>(new Identity());
    static readonly List<VarPackage> packages = new List<VarPackage>();
    static readonly List<ZipFile> archives = new List<ZipFile>();
    static readonly List<string> errors = new List<string>();
    static readonly List<WeakReference> oldOwners = new List<WeakReference>();
    static Assembly memory;
    static Type feature;
    static string folder;
    static int imports, checks, replayScenario;
    static bool coexist;
    [StructLayout(LayoutKind.Sequential)] struct MemoryCounters {
        internal uint Size, PageFaults;
        internal UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile, PrivateUsage;
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("psapi.dll", SetLastError=true)] static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
    static void Require(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Make(Type type) {
        object value = FormatterServices.GetUninitializedObject(type);
        if (typeof(UnityEngine.Object).IsAssignableFrom(type)) Set(value, "m_CachedPtr", new IntPtr(1));
        return value;
    }
    static void Set(object owner, string name, object value) {
        FieldInfo field = AccessTools.Field(owner is Type ? (Type)owner : owner.GetType(), name);
        if (field == null) throw new MissingFieldException(name);
        field.SetValue(owner is Type ? null : owner, value);
    }
    static void Property(object owner, string name, object value) {
        PropertyInfo property = owner.GetType().GetProperty(name, All);
        if (property == null) throw new MissingMemberException(name);
        property.SetValue(owner, value, null);
    }
    static object Call(object owner, string name, params object[] args) {
        try { return AccessTools.Method(owner is Type ? (Type)owner : owner.GetType(), name).Invoke(owner is Type ? null : owner, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static DAZMorphBank NewBank() {
        var bank = (DAZMorphBank)Make(typeof(DAZMorphBank));
        bank.autoImportFolder = folder;
        GameObject go = NewObject("full-bank"); links[bank] = go;
        children[bank] = new List<DAZMorphSubBank>();
        return bank;
    }
    static GameObject NewObject(string name) { var go = (GameObject)Make(typeof(GameObject)); names[go] = name; return go; }
    static GameObject GameObjectOf(Component component) { return (GameObject)links[component]; }
    static Transform TransformOf(UnityEngine.Object owner) {
        object go = owner is Component ? links[owner] : owner;
        object value;
        if (!links.TryGetValue(go, out value)) { value = Make(typeof(Transform)); links[go] = value; links[value] = go; }
        return (Transform)value;
    }
    static void Parent(Transform child, Transform parent) { links[links[child]] = parent; }
    static DAZMorphSubBank AddComponent(GameObject go) {
        var sub = (DAZMorphSubBank)Make(typeof(DAZMorphSubBank));
        sub.alwaysSetDirtyOnEditorChange = true; links[sub] = go;
        Transform parent = (Transform)links[go]; object parentGo = links[parent];
        object bank = null;
        foreach (object key in children.Keys) if (ReferenceEquals(links[key], parentGo)) { bank = key; break; }
        Require(bank != null, "new subbank parent missing"); children[bank].Add(sub);
        return sub;
    }
    static string Name(UnityEngine.Object obj) { return names[obj is Component ? links[obj] : obj]; }
    static DAZMorphSubBank[] Children(Component bank) { return children[bank].ToArray(); }
    static bool Playing() { return true; }
    static float Clock() { return (float)(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency); }
    static bool Different(UnityEngine.Object a, UnityEngine.Object b) { return !ReferenceEquals(a, b); }
    static bool Same(UnityEngine.Object a, UnityEngine.Object b) { return ReferenceEquals(a, b); }
    static void Log(object value) { Console.WriteLine("ORIGINAL_LOG " + value); }
    static void LogError(object value) { errors.Add(Convert.ToString(value)); Console.WriteLine("ORIGINAL_ERROR " + value); }
    static void ImportSeen() {
        imports++;
        if(imports==2 && replayScenario==2) InstallMetaHook();
        if(imports==2 && replayScenario==3) System.Threading.Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
    }
    static void ForeignMeta(DAZMorph __instance) {__instance.displayName+="-foreign";}
    static void InstallMetaHook() {new Harmony("offline.full-import.foreign").Patch(AccessTools.Method(typeof(DAZMorph),"LoadMetaFromJSON"),postfix:new HarmonyMethod(typeof(CollectorProbe),"ForeignMeta"));}
    static IEnumerable<CodeInstruction> Native(IEnumerable<CodeInstruction> input, MethodBase __originalMethod) {
        if(__originalMethod==AccessTools.Method(typeof(DAZMorphBank),"RuntimeImportMorph")) yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(CollectorProbe),"ImportSeen"));
        foreach (CodeInstruction code in input) {
            var ctor = code.operand as ConstructorInfo;
            var method = code.operand as MethodInfo;
            string replacement = null;
            if (ctor != null && ctor.DeclaringType == typeof(GameObject) && ctor.GetParameters().Length == 1) replacement = "NewObject";
            if (method != null) {
                if (method.DeclaringType == typeof(Application) && method.Name == "get_isPlaying") replacement = "Playing";
                else if (method.DeclaringType == typeof(Time) && method.Name == "get_realtimeSinceStartup") replacement = "Clock";
                else if (method.Name == "get_gameObject" && method.DeclaringType == typeof(Component)) replacement = "GameObjectOf";
                else if (method.Name == "get_transform" && (method.DeclaringType == typeof(Component) || method.DeclaringType == typeof(GameObject))) replacement = "TransformOf";
                else if (method.Name == "set_parent" && method.DeclaringType == typeof(Transform)) replacement = "Parent";
                else if (method.Name == "get_name" && method.DeclaringType == typeof(UnityEngine.Object)) replacement = "Name";
                else if (method.Name == "GetComponentsInChildren" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(DAZMorphSubBank)) replacement = "Children";
                else if (method.Name == "AddComponent" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(DAZMorphSubBank)) replacement = "AddComponent";
                else if (method.DeclaringType == typeof(UnityEngine.Object) && method.Name == "op_Inequality") replacement = "Different";
                else if (method.DeclaringType == typeof(UnityEngine.Object) && method.Name == "op_Equality") replacement = "Same";
                else if (method.DeclaringType == typeof(UnityEngine.Debug) && method.Name == "Log") replacement = "Log";
                else if (method.DeclaringType == typeof(UnityEngine.Debug) && method.Name == "LogError") replacement = "LogError";
            }
            if (replacement != null) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(CollectorProbe), replacement); }
            yield return code;
        }
    }
    static void Setup(bool replay = false) {
        memory = Assembly.LoadFrom("VaM.Memory.dll"); feature = memory.GetType("VaM.Memory.MorphSnapshotState", true);
        Call(feature, "Install", new Harmony((string)feature.GetField("Id").GetRawConstantValue()));
        Call(memory.GetType("PackageStreamRuntime", true), "Install", new Harmony("full-import.original-package-stream"));
        var fixture = new Harmony("offline.full-import.native-only");
        foreach (Type type in new[] { typeof(DAZMorphBank), typeof(DAZMorphSubBank) })
            foreach (MethodInfo method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                if (method.GetMethodBody() != null) fixture.Patch(method, transpiler: new HarmonyMethod(typeof(CollectorProbe), "Native"));
        fixture.Patch(AccessTools.Method(typeof(DAZMorph), "Init"), transpiler: new HarmonyMethod(typeof(CollectorProbe), "Native"));
        fixture.Patch(AccessTools.Method(typeof(SuperController), "LogError", new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(CollectorProbe), "ControllerError"));
        string[] settings = File.ReadAllLines("full-import-settings.txt"); folder = settings[0];
        var fileUids = new Dictionary<string, VarFileEntry>(); var filePaths = new Dictionary<string, VarFileEntry>();
        var directoryUids = new Dictionary<string, VarDirectoryEntry>(); var directoryPaths = new Dictionary<string, VarDirectoryEntry>();
        var allDirectories = new HashSet<VarDirectoryEntry>();
        Set(typeof(FileManager), "uidToVarFileEntry", fileUids); Set(typeof(FileManager), "pathToVarFileEntry", filePaths);
        Set(typeof(FileManager), "uidToVarDirectoryEntry", directoryUids); Set(typeof(FileManager), "pathToVarDirectoryEntry", directoryPaths);
        Set(typeof(FileManager), "allVarDirectoryEntries", allDirectories);
        ZipConstants.DefaultCodePage = 0;
        for (int p = 2; p < settings.Length; p++) {
            string[] item = settings[p].Split('|'); var info = new FileInfo(item[0]);
            var package = (VarPackage)Make(typeof(VarPackage)); string uid = Path.GetFileNameWithoutExtension(info.Name);
            foreach (string name in new[] { "Uid", "Path", "FullPath", "SlashPath", "FullSlashPath" }) Property(package, name, name == "Uid" ? uid : info.FullName);
            Property(package, "Size", info.Length); Property(package, "LastWriteTime", info.LastWriteTime);
            Property(package, "Version", int.Parse(uid.Substring(uid.LastIndexOf('.') + 1))); Property(package, "LicenseType", "CC BY"); package.isNewestEnabledVersion = true;
            Property(package, "CachedUidColon", uid + ":"); Property(package, "CachedUidColonSlash", uid + ":/");
            Property(package, "CachedPathColon", info.FullName + ":"); Property(package, "CachedPathColonBackslash", info.FullName + ":\\");
            Property(package, "CachedFullPathColon", info.FullName + ":"); Property(package, "CachedFullPathColonBackslash", info.FullName + ":\\");
            Property(package, "CachedAddonPrefsPfx", "fixture_sources/full-import/prefs/" + uid + "/");
            var group = (VarPackageGroup)Make(typeof(VarPackageGroup)); Set(group, "customOptions", new Dictionary<string, bool> { { "preloadMorphs", item[2] == "True" } }); Property(package, "Group", group);
            Set(package, "_jsonCachePath", item[1]); var zip = new ZipFile(File.OpenRead(info.FullName)); Set(package, "_zipFile", zip); archives.Add(zip); packages.Add(package);
            var dirs = new Dictionary<string, VarDirectoryEntry>();
            var root = new VarDirectoryEntry(package, folder, null); dirs.Add(folder, root);
            foreach (ZipEntry entry in zip) {
                if (!entry.Name.StartsWith(folder + "/", StringComparison.Ordinal) || entry.IsDirectory) continue;
                string relative = entry.Name.Substring(folder.Length + 1); string path = folder;
                string[] segments = relative.Split('/');
                for (int n = 0; n < segments.Length - 1; n++) {
                    string next = path + "/" + segments[n];
                    if (!dirs.ContainsKey(next)) { var child = new VarDirectoryEntry(package, next, dirs[path]); ((HashSet<VarDirectoryEntry>)AccessTools.Field(typeof(VarDirectoryEntry), "varSubDirectories").GetValue(dirs[path])).Add(child); dirs.Add(next, child); }
                    path = next;
                }
                var file = new VarFileEntry(package, entry.Name, entry.DateTime, entry.Size, false);
                ((List<VarFileEntry>)AccessTools.Field(typeof(VarDirectoryEntry), "varFileEntries").GetValue(dirs[path])).Add(file);
                fileUids[FileManager.CleanFilePath(file.Uid)] = file; filePaths[FileManager.CleanFilePath(file.Path)] = file;
            }
            foreach (VarDirectoryEntry dir in dirs.Values) { allDirectories.Add(dir); directoryUids[FileManager.CleanDirectoryPath(dir.Uid)] = dir; directoryPaths[FileManager.CleanDirectoryPath(dir.Path)] = dir; }
        }
        if(coexist) {
            Call(memory.GetType("Quest3TriggerUI.MorphTargetStringReuse",true),"Install");
            Call(memory.GetType("Quest3TriggerUI.PackageJsonWeakRetention",true),"Install");
        }
        DAZMorphBank.DisableDirectoryCache(); DAZMorphBank.EnableDirectoryCache();
        if (replay && feature.GetMethod("ConfigureImportReceipts") != null) {
            Call(feature,"SetImportNativeAdapter",AccessTools.Method(typeof(CollectorProbe),"Native"));
            Call(feature,"ConfigureImportReceipts",settings[1]);
        } else if (feature.GetMethod("ConfigureMetadataDisk") != null) Call(feature, "ConfigureMetadataDisk", settings[1]);
    }
    static bool ControllerError(string __0) { LogError(__0); return false; }
    static List<DAZMorph> List(DAZMorphBank bank, string field) { return (List<DAZMorph>)AccessTools.Field(typeof(DAZMorphBank), field).GetValue(bank); }
    static string Digest(DAZMorphBank bank) {
        using (var bytes = new MemoryStream()) using (var writer = new BinaryWriter(bytes, Encoding.UTF8)) {
            object snapshot = Call(bank, "SaveMorphSnapshots");
            foreach (object row in (IEnumerable)snapshot) {
                var fields = row.GetType().GetFields(All); Array.Sort(fields, delegate(FieldInfo a, FieldInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
                foreach (FieldInfo field in fields) {
                    object value = field.GetValue(row); writer.Write(field.Name);
                    if (field.FieldType == typeof(DAZMorphFormula[])) { var formulas = (DAZMorphFormula[])value; writer.Write(formulas == null ? -1 : formulas.Length); if (formulas != null) foreach (var formula in formulas) { writer.Write((int)formula.targetType); writer.Write(formula.target ?? "<null>"); writer.Write(formula.multiplier); } }
                    else if (value is float) writer.Write((float)value);
                    else writer.Write(value == null ? "<null>" : value.ToString());
                }
            }
            foreach (string field in new[] { "_morphs", "_unactivatedMorphs" }) { writer.Write(field); foreach (DAZMorph m in List(bank, field)) writer.Write(m.uid); }
            foreach (string error in errors) writer.Write(error);
            var badNames=(HashSet<string>)AccessTools.Field(typeof(DAZMorphBank),"badMorphNames").GetValue(bank);
            if(badNames!=null){var bad=new List<string>(badNames);bad.Sort(StringComparer.Ordinal);foreach(string name in bad)writer.Write(name);}
            writer.Flush(); using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "");
        }
    }
    public static string CoexistOffline(){coexist=true;return Offline();}
    public static string CoexistReplay(){coexist=true;return RunMode(true);}
    public static string CoexistExistingTree(){coexist=true;replayScenario=4;return RunMode(true);}
    public static string CoexistMidHook(){coexist=true;replayScenario=2;return RunMode(true);}
    public static string CoexistDynamic(){coexist=true;replayScenario=6;return RunMode(true);}
    public static string Offline() {
        try {
            Setup(true);var entries=(Dictionary<string,VarFileEntry>)AccessTools.Field(typeof(FileManager),"uidToVarFileEntry").GetValue(null);
            var list=new List<FileEntry>();foreach(var entry in entries.Values)list.Add(entry);
            var watch=Stopwatch.StartNew();int count=(int)Call(feature,"BuildImportReceipts",folder,list);watch.Stop();
            Console.WriteLine("OFFLINE_IMPORT_PASS rows="+count+" seconds="+watch.Elapsed.TotalSeconds.ToString("F3",CultureInfo.InvariantCulture)+" "+Call(feature,"ImportReceiptStats"));
            return "OFFLINE_READY";
        }catch(Exception error){Console.WriteLine("TOOL_EXCEPTION: "+error);return "OFFLINE_FAILED";}
    }
    public static string CrossCwdReplay(){replayScenario=5;return RunMode(true);}
    public static string DynamicReplay(){replayScenario=6;return RunMode(true);}
    public static string StreamHookReplay(){replayScenario=7;return RunMode(true);}
    static void StreamForeign() {}
    public static string Run() {return RunMode(false);}
    public static string Replay() {return RunMode(true);}
    public static string HookedReplay(){replayScenario=1;return RunMode(true);}
    public static string MidHookReplay(){replayScenario=2;return RunMode(true);}
    public static string MidCultureReplay(){replayScenario=3;return RunMode(true);}
    public static string ExistingTreeReplay(){replayScenario=4;return RunMode(true);}
    static string RunMode(bool replay) {
        try {
            Setup(replay);
            if(replayScenario==1)InstallMetaHook();
            if(replayScenario==4) {
                var node=(JSONNode)Call(packages[0],"GetJSONCache",folder+"/valid.vmi");node["displayName"]="edited-json-tree";
            }
            if(replayScenario==5){string cwd=Path.GetFullPath("fixture_sources/alternate-cwd");Directory.CreateDirectory(cwd);Directory.SetCurrentDirectory(cwd);}
            if(replayScenario==6)foreach(var package in packages){Set(package.Group,"customOptions",new Dictionary<string,bool>{{"preloadMorphs",true}});Property(package,"LicenseType","changed-license");Property(package,"Version",42);package.isNewestEnabledVersion=false;}
            if(replayScenario==7){var type=memory.GetType("StartupPackageJson.JsonStreamParser",true);new Harmony("offline.stream.foreign").Patch(AccessTools.Method(type,"Parse"),prefix:new HarmonyMethod(typeof(CollectorProbe),"StreamForeign"));}
            var bank = NewBank(); var timer = Stopwatch.StartNew(); bank.Init(); bank.RefreshPackageMorphs(); timer.Stop();
            var counters = new MemoryCounters(); counters.Size = (uint)Marshal.SizeOf(typeof(MemoryCounters));
            Require(GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size), "process memory measurement failed");
            var badNames = (HashSet<string>)AccessTools.Field(typeof(DAZMorphBank), "badMorphNames").GetValue(bank);
            var bad = badNames == null ? new List<string>() : new List<string>(badNames); bad.Sort(StringComparer.Ordinal);
            int formulas = 0;
            foreach (var field in new[] { "_morphs", "_unactivatedMorphs" }) foreach (DAZMorph morph in List(bank, field)) {
                Require(ReferenceEquals(morph.morphBank, bank), "morph references foreign bank"); Require(morph.morphSubBank != null, "original subbank association missing");
                if (morph.formulas != null) formulas += morph.formulas.Length;
                oldOwners.Add(new WeakReference(morph)); if (morph.formulas != null) foreach (var formula in morph.formulas) oldOwners.Add(new WeakReference(formula));
            }
            oldOwners.Add(new WeakReference(bank)); foreach (var sub in children[bank]) oldOwners.Add(new WeakReference(sub));
            Console.WriteLine("FULL_IMPORT_RESULT version=" + memory.GetName().Version + " imports=" + imports + " active=" + List(bank, "_morphs").Count + " unactivated=" + List(bank, "_unactivatedMorphs").Count + " formulas=" + formulas + " regions=" + children[bank].Count + " bad=" + string.Join(",", bad.ToArray()) + " errors=" + errors.Count + " initRefreshMs=" + timer.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " privateBytesBeforeDigest=" + counters.PrivateUsage.ToUInt64() + " peakWorkingSetBeforeDigest=" + counters.PeakWorkingSet.ToUInt64() + " digest=" + Digest(bank));
            if (feature.GetMethod("MetadataDiskStats") != null) Console.WriteLine("FULL_IMPORT_DISK " + Call(feature, "MetadataDiskStats"));
            if (feature.GetMethod("ImportReceiptStats") != null) Console.WriteLine("FULL_IMPORT_REPLAY " + Call(feature,"ImportReceiptStats"));
            Type stream=memory.GetType("PackageStreamRuntime",true);
            Console.WriteLine("FULL_IMPORT_JSON streamed="+stream.GetField("Streamed").GetValue(null)+" fallback="+stream.GetField("Fallback").GetValue(null));
            return "FULL_IMPORT_PASS managedImportOriginal=True filterOriginal=True subbankOriginal=True rebuildOriginal=True";
        } catch (Exception error) { Console.WriteLine("FULL_IMPORT_FAIL " + error); return "FAIL"; }
    }
    public static string Drop() {
        DAZMorphBank.DisableDirectoryCache(); names.Clear(); links.Clear(); children.Clear();
        foreach (var zip in archives) zip.Close(); archives.Clear(); packages.Clear();
        foreach (string name in new[] { "uidToVarFileEntry", "pathToVarFileEntry", "uidToVarDirectoryEntry", "pathToVarDirectoryEntry", "allVarDirectoryEntries" }) Set(typeof(FileManager), name, null);
        return "FULL_IMPORT_DROP";
    }
    public static string Weak() { int alive = 0; foreach (var weak in oldOwners) if (weak.IsAlive) alive++; Require(alive == 0, "old bank/subbank/morph/formula retained: " + alive); return "FULL_IMPORT_WEAK owners=0"; }
    public static string Dump() {
        foreach(MethodInfo method in typeof(DirectoryEntry).GetMethods(All)) Console.WriteLine("DIRECTORY_API "+method);
        foreach(var method in typeof(DAZMorphBank).GetMethods(All)) if(method.Name=="RuntimeImportFromDir")Print(method);
        foreach(Type type in typeof(Harmony).Assembly.GetTypes()) if(type.Name.Contains("SharedState") || type.Name=="GlobalPatchState" || type.Name=="PatchInfo") {
            Console.WriteLine("TYPE " + type);
            foreach(FieldInfo field in type.GetFields(All)) Console.WriteLine("FIELD " + field.FieldType + " " + field.Name);
            foreach(MethodInfo method in type.GetMethods(All|BindingFlags.DeclaredOnly)) Print(method);
        }
        Print(AccessTools.Method(typeof(DAZMorph), "LoadMetaFromJSON"));
        foreach (Type type in new[] { typeof(DAZMorphSubBank), typeof(VarPackage), typeof(VarDirectoryEntry), typeof(SystemDirectoryEntry) }) {
            Console.WriteLine("TYPE " + type);
            foreach (FieldInfo field in type.GetFields(All)) Console.WriteLine("FIELD " + field.FieldType + " " + field.Name);
            foreach (ConstructorInfo method in type.GetConstructors(All)) Print(method);
        }
        foreach (string name in new[] { "AddMorph", "CompleteRuntimeMorphAdd", "CompletePackageMorphAdd", "CompleteTransientMorphAdd", "get_combinedMorphs", "RebuildMorphsByNameAndUid" }) Print(AccessTools.Method(typeof(DAZMorphSubBank), name));
        foreach (string name in new[] { "GetJSONCache", "get_LicenseType", "get_Version", "get_IsSimulated" }) Print(AccessTools.Method(typeof(VarPackage), name));
        foreach (string name in new[] { "GetDirectoryEntry", "GetFileEntry", "FindVarDirectories", "GetVarFileEntry", "GetVarDirectoryEntry", "GetSystemFileEntry", "GetSystemDirectoryEntry", "FileExists" }) Print(AccessTools.Method(typeof(FileManager), name));
        foreach (string name in new[] { "BuildMorphSubBanksByRegion", "BuildMorphsList", "BuildMorphsByNameAndUid", "InitMorphs", "RefreshRuntimeMorphs", "RefreshPackageMorphs", "WarmupDirectoryCache", "GetVmiJsonCached" }) Print(AccessTools.Method(typeof(DAZMorphBank), name));
        return "FULL_IMPORT_DUMP_PASS";
    }
    static void Print(MethodBase method) {
        if (method == null) { Console.WriteLine("METHOD_MISSING"); return; }
        Console.WriteLine("METHOD " + method.DeclaringType + "::" + method);
        if (method.GetMethodBody() == null) return;
        foreach (CodeInstruction code in PatchProcessor.GetOriginalInstructions(method)) Console.WriteLine(code);
    }
}
