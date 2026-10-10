using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using HarmonyLib;
using MVR.FileManagement;
using UnityEngine;

public static class StartupMorphIndexPlugin { public static readonly bool Baseline = false; }

namespace StartupMorphIndex {
    public static class Runtime {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo DirectoryCache = typeof(DAZMorphBank).GetField("_dirEntryCacheEnabled", All);
        private static readonly FieldInfo PackageRoot = typeof(VarPackage).GetField("jsonCache", All);
        private static readonly FieldInfo PackageLoaded = typeof(VarPackage).GetField("_jsonCacheLoaded", All);
        private static readonly FieldInfo PackageCachePath = typeof(VarPackage).GetField("_jsonCachePath", All);
        private static readonly Dictionary<MethodBase, FieldInfo[]> Schemas = new Dictionary<MethodBase, FieldInfo[]>();
        private static readonly Dictionary<string, string> FileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Attempted = new HashSet<string>();
        public static Action<string> Log;
        public sealed class Call {
            public string Key, Path;
            public FieldInfo[] Fields;
            public IList List;
            public int Offset;
            public Transform Prefab;
            public MethodBase Method;
            public Stopwatch Watch;
        }
        public static void Register(MethodInfo method) {
            var parameters = method.GetParameters();
            if (method.ReturnType != typeof(void) || parameters.Length != 3 || parameters[0].ParameterType != typeof(Transform) ||
                parameters[1].ParameterType != typeof(DAZCharacterSelector) || !parameters[2].ParameterType.IsGenericType ||
                parameters[2].ParameterType.GetGenericTypeDefinition() != typeof(List<>)) throw new InvalidDataException("Consumer signature changed");
            Type item = parameters[2].ParameterType.GetGenericArguments()[0];
            if (item.FullName != "JayJayWon.MorphCache+MorphNameUIDMap") throw new InvalidDataException("Consumer map type changed");
            Schemas.Add(method, ScalarCache.Schema(item));
        }
        private static string FileHash(string path) {
            string hash;
            if (!FileHashes.TryGetValue(path, out hash)) {
                using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                FileHashes.Add(path, hash);
            }
            return hash;
        }
        private static void Method(BinaryWriter writer, MethodBase method) {
            ScalarCache.String(writer, method.DeclaringType.FullName); ScalarCache.String(writer, method.Name);
            var body = method.GetMethodBody();
            ScalarCache.String(writer, body == null ? "no-body" : ScalarCache.Digest(body.GetILAsByteArray()));
            string location = method.Module.Assembly.Location;
            ScalarCache.String(writer, string.IsNullOrEmpty(location) ? "memory" : FileHash(location));
        }
        private static void Stamp(BinaryWriter writer, string path) {
            ScalarCache.String(writer, path);
            var info = new FileInfo(path);
            writer.Write(info.Exists);
            if (info.Exists) { writer.Write(info.Length); writer.Write(info.LastWriteTimeUtc.Ticks); }
        }
        private static void DirectoryStamp(BinaryWriter writer, string path) {
            ScalarCache.String(writer, path);
            writer.Write(Directory.Exists(path));
            if (!Directory.Exists(path)) return;
            // Fresh local inventory catches additions, removals and metadata
            // edits. Do not rely on the game's possibly retained directory list.
            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files) {
                string extension = Path.GetExtension(file);
                if (extension.Equals(".vmi", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vmb", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".dsf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".fav", StringComparison.OrdinalIgnoreCase)) Stamp(writer, file);
            }
        }
        private static void ScalarFields(BinaryWriter writer, object value) {
            var fields = value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Array.Sort(fields, delegate(FieldInfo a, FieldInfo b) { return StringComparer.Ordinal.Compare(a.Name, b.Name); });
            foreach (var field in fields) {
                Type type = field.FieldType;
                if (!(type.IsPrimitive || type.IsEnum || type == typeof(string))) continue;
                ScalarCache.String(writer, field.Name);
                object scalar = field.GetValue(value);
                if (type == typeof(string)) ScalarCache.String(writer, (string)scalar);
                else if (type == typeof(float)) writer.Write((float)scalar);
                else if (type == typeof(bool)) writer.Write((bool)scalar);
                else ScalarCache.String(writer, Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        private static void PatchIdentity(BinaryWriter writer) {
            var methods = new List<MethodBase>();
            foreach (var method in Harmony.GetAllPatchedMethods())
                if (method.DeclaringType == typeof(DAZMorphBank) || method.DeclaringType == typeof(DAZMorph) ||
                    method.DeclaringType == typeof(DAZMorphSubBank) || method.DeclaringType == typeof(FileManager) ||
                    method.DeclaringType == typeof(VarPackage) || method.DeclaringType.FullName.StartsWith("SimpleJSON.")) methods.Add(method);
            methods.Sort(delegate(MethodBase a, MethodBase b) { return StringComparer.Ordinal.Compare(a.DeclaringType.FullName + a.ToString(), b.DeclaringType.FullName + b.ToString()); });
            foreach (var method in methods) {
                Method(writer, method);
                var patches = Harmony.GetPatchInfo(method);
                foreach (var group in new[] { patches.Prefixes, patches.Postfixes, patches.Transpilers, patches.Finalizers })
                    foreach (var patch in group) { ScalarCache.String(writer, patch.owner); writer.Write(patch.priority); Method(writer, patch.PatchMethod); }
            }
        }
        private static string Key(Transform prefab, MethodBase method) {
            if (DirectoryCache == null || (bool)DirectoryCache.GetValue(null)) return null;
            // Never bypass another consumer patch, nor remove gameplay cache
            // warming when the scene-scoped directory/snapshot cache is active.
            var patches = Harmony.GetPatchInfo(method);
            if (patches != null) foreach (string owner in patches.Owners) if (owner != "local.vam.startup-morph-index") return null;
            var bank = prefab.GetComponent<DAZMorphBank>();
            if (bank == null) return null;
            using (var sha = SHA256.Create()) {
                using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
                using (var writer = new BinaryWriter(crypto)) {
                    ScalarCache.String(writer, "original-scalar-index-v1"); Method(writer, method);
                    // Include every map/iterator/lambda body, not just the outer call.
                    foreach (Type nested in method.DeclaringType.GetNestedTypes(All))
                        foreach (var child in nested.GetMethods(All | BindingFlags.DeclaredOnly)) Method(writer, child);
                    ScalarCache.String(writer, FileHash(typeof(DAZMorphBank).Assembly.Location));
                    PatchIdentity(writer);
                    ScalarCache.String(writer, prefab.name); ScalarCache.String(writer, bank.autoImportFolder);
                    ScalarFields(writer, bank);
                    foreach (var sub in prefab.GetComponentsInChildren<DAZMorphSubBank>()) {
                        ScalarCache.String(writer, sub.name); ScalarFields(writer, sub);
                        var morphs = sub.morphs;
                        writer.Write(morphs == null ? -1 : morphs.Count);
                        if (morphs != null) foreach (var morph in morphs) {
                            ScalarFields(writer, morph);
                            writer.Write(morph.formulas == null ? -1 : morph.formulas.Length);
                            if (morph.formulas != null) foreach (var formula in morph.formulas) ScalarFields(writer, formula);
                        }
                    }
                    if (!string.IsNullOrEmpty(bank.autoImportFolder)) {
                        DirectoryStamp(writer, bank.autoImportFolder);
                        var directories = FileManager.FindVarDirectories(bank.autoImportFolder);
                        writer.Write(directories.Count);
                        foreach (var directory in directories) {
                            var package = directory.Package;
                            if (PackageRoot.GetValue(package) != null) return null;
                            ScalarCache.String(writer, directory.Uid); ScalarCache.String(writer, package.Uid);
                            ScalarCache.String(writer, package.LicenseType); writer.Write(package.Enabled);
                            writer.Write(package.Group.GetCustomOption("preloadMorphs")); writer.Write(package.isNewestEnabledVersion);
                            writer.Write(package.Size); writer.Write(package.LastWriteTime.Ticks);
                            writer.Write((bool)PackageLoaded.GetValue(package));
                            Stamp(writer, package.FullPath);
                            string cache = PackageCachePath.GetValue(package) as string;
                            if (cache != null) Stamp(writer, cache);
                            else ScalarCache.String(writer, null);
                            if (package.IsSimulated) DirectoryStamp(writer, package.FullPath);
                        }
                    }
                    writer.Flush(); crypto.FlushFinalBlock();
                }
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        public static bool Prefix(Transform __0, IList __2, MethodBase __originalMethod, out Call __state) {
            __state = null;
            if (__0 == null || __2 == null) return true;
            string once = __originalMethod.Module.ModuleVersionId.ToString() + ":" + __0.GetInstanceID();
            if (!Attempted.Add(once)) return true;
            var watch = Stopwatch.StartNew();
            try {
                string key = Key(__0, __originalMethod);
                if (key == null) { Emit("original reason=mutable-or-foreign"); return true; }
                FieldInfo[] fields = Schemas[__originalMethod];
                string path = Path.Combine(VaM.Memory.MemoryCachePaths.DirectoryFor("StartupMorphIndex"), key + ".smi");
                ArrayList rows; string digest;
                if (ScalarCache.Read(path, key, fields[0].DeclaringType, fields, out rows, out digest) &&
                    Environment.GetEnvironmentVariable("VAM_STARTUP_MORPH_INDEX") != "record") {
                    ScalarCache.Append(__2, rows);
                    Emit("hit bank=" + __0.name + " rows=" + rows.Count + " sha256=" + digest + " key=" + key + " milliseconds=" + watch.ElapsedMilliseconds);
                    return false;
                }
                __state = new Call { Key = key, Path = path, Fields = fields, List = __2, Offset = __2.Count, Prefab = __0, Method = __originalMethod, Watch = watch };
            } catch (Exception error) { Emit("original reason=" + error.GetType().Name + ":" + error.Message); }
            return true;
        }
        public static Exception Finalizer(Call __state, Exception __exception) {
            if (__state == null || __exception != null) return __exception;
            try {
                string after = Key(__state.Prefab, __state.Method);
                if (after != __state.Key) { Emit("not-recorded inputsChanged=True bank=" + __state.Prefab.name); return __exception; }
                byte[] payload = ScalarCache.Payload(__state.List, __state.Offset, __state.Fields);
                ScalarCache.Write(__state.Path, __state.Key, payload);
                Emit("recorded bank=" + __state.Prefab.name + " rows=" + (__state.List.Count - __state.Offset) + " sha256=" + ScalarCache.Digest(payload) +
                    " key=" + __state.Key + " milliseconds=" + __state.Watch.ElapsedMilliseconds);
            } catch (Exception error) { Emit("not-recorded reason=" + error.GetType().Name + ":" + error.Message); }
            return __exception;
        }
        private static void Emit(string text) { if (Log != null) Log("[startup-morph-index] " + text); }
    }
}
