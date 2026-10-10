using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;

namespace Quest3TriggerUI
{
    // FileManager keeps packages alive. A package must not also pin its entire
    // parsed metadata tree forever. Reuse weakly; retain the original disk loader.
    internal static class PackageJsonWeakRetention
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo Root = typeof(VarPackage).GetField("jsonCache", Fields);
        private static readonly FieldInfo Loaded = typeof(VarPackage).GetField("_jsonCacheLoaded", Fields);
        private static readonly FieldInfo ReloadPath = typeof(VarPackage).GetField("_jsonCachePath", Fields);
        // This Unity profile predates ConditionalWeakTable. Identity buckets
        // hold only weak owners/trees; incrementally prune metadata on insertion.
        private static readonly object TableGate = new object();
        private static readonly Dictionary<int, List<Entry>> _entries = new Dictionary<int, List<Entry>>();
        private static readonly Queue<int> Sweep = new Queue<int>();
        private static Harmony _harmony;
        private static volatile bool _enabled;
        private static long _detached, _hotHits, _coldLoads, _unreloadable;
        private static long _batches, _completedBatches;

        internal sealed class Entry
        {
            internal readonly WeakReference Owner;
            internal readonly WeakReference Tree = new WeakReference(null);
            internal int Depth;
            // Only active, synchronous imports pin the parent. No scene-lifetime strong cache.
            internal int Leases;
            internal JSONClass LeasedTree;
            internal Entry(VarPackage package) { Owner = new WeakReference(package); }
        }

        internal struct Call
        {
            internal Entry Entry;
            internal bool Held;
        }

        internal struct BatchCall
        {
            internal Entry Entry;
            internal bool Held;
        }

        private static void BatchPrefix(DirectoryEntry __0, out BatchCall __state)
        {
            __state = new BatchCall();
            var directory = __0 as VarDirectoryEntry;
            if (!_enabled || directory == null || directory.Package == null) return;
            var entry = GetEntry(directory.Package, true);
            lock (entry)
            {
                entry.Leases++;
                __state.Entry = entry;
                __state.Held = true;
                entry.LeasedTree = Root.GetValue(directory.Package) as JSONClass ?? entry.Tree.Target as JSONClass;
                Interlocked.Increment(ref _batches);
            }
        }

        private static Exception BatchFinalizer(ref BatchCall __state, Exception __exception)
        {
            if (!__state.Held) return __exception;
            __state.Held = false;
            lock (__state.Entry)
            {
                if (--__state.Entry.Leases == 0) __state.Entry.LeasedTree = null;
                Interlocked.Increment(ref _completedBatches);
            }
            return __exception;
        }

        // A SyncCustomItems loop borrows only its current package parent.
        // Switching packages releases it before borrowing the next. No cross-frame roots.
        [ThreadStatic] private static ItemScope _itemScope;
        internal sealed class ItemScope
        {
            internal ItemScope Parent;
            internal Entry Entry;
        }
        private static long _itemScopes, _completedItemScopes, _itemPackageSwitches;
        private static void ItemsPrefix(out ItemScope __state)
        {
            __state = null;
            if (!_enabled) return;
            __state = new ItemScope { Parent = _itemScope };
            _itemScope = __state;
            Interlocked.Increment(ref _itemScopes);
        }
        private static void ReleaseItemPackage(ItemScope scope)
        {
            var entry = scope.Entry;
            scope.Entry = null;
            if (entry == null) return;
            lock (entry)
                if (--entry.Leases == 0) entry.LeasedTree = null;
        }
        private static Exception ItemsFinalizer(ItemScope __state, Exception __exception)
        {
            if (__state == null) return __exception;
            try { ReleaseItemPackage(__state); }
            finally
            {
                _itemScope = __state.Parent;
                __state.Parent = null;
                Interlocked.Increment(ref _completedItemScopes);
            }
            return __exception;
        }
        private static JSONNode ItemsJson(VarPackage package, string path)
        {
            var scope = _itemScope;
            if (_enabled && scope != null && package != null)
            {
                var entry = GetEntry(package, true);
                if (!ReferenceEquals(entry, scope.Entry))
                {
                    ReleaseItemPackage(scope);
                    lock (entry)
                    {
                        entry.Leases++;
                        entry.LeasedTree = Root.GetValue(package) as JSONClass ?? entry.LeasedTree ?? entry.Tree.Target as JSONClass;
                        scope.Entry = entry;
                    }
                    Interlocked.Increment(ref _itemPackageSwitches);
                }
            }
            // Original method, including every foreign patch and invalidation, still executes.
            return package.GetJSONCache(path);
        }
        private static IEnumerable<CodeInstruction> ItemsRewrite(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var original = typeof(VarPackage).GetMethod("GetJSONCache", new[] { typeof(string) });
            var replacement = typeof(PackageJsonWeakRetention).GetMethod("ItemsJson", BindingFlags.Static | BindingFlags.NonPublic);
            int sites = 0;
            foreach (var instruction in code)
                if (instruction.opcode == OpCodes.Callvirt && Equals(instruction.operand, original))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    sites++;
                }
            if (sites != 1) throw new InvalidDataException("SyncCustomItems package JSON sites changed");
            return code;
        }

        private static void RequireBatch(MethodInfo method, string expected)
        {
            if (method == null) throw new MissingMethodException("DAZMorphBank package import batch changed");
            var body = method.GetMethodBody();
            var text = new StringBuilder(BitConverter.ToString(body.GetILAsByteArray()));
            text.Append('|').Append(body.InitLocals);
            foreach (var local in body.LocalVariables) text.Append('|').Append(local.LocalType.FullName).Append(':').Append(local.IsPinned);
            foreach (var clause in body.ExceptionHandlingClauses)
            {
                text.Append('|').Append(clause.Flags).Append(':').Append(clause.TryOffset).Append(':').Append(clause.TryLength)
                    .Append(':').Append(clause.HandlerOffset).Append(':').Append(clause.HandlerLength);
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause) text.Append(':').Append(clause.CatchType.FullName);
            }
            using (var hash = SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "") != expected)
                    throw new InvalidDataException("Package import batch fingerprint changed");
        }

        private static Entry GetEntry(VarPackage package, bool create)
        {
            int id = RuntimeHelpers.GetHashCode(package);
            lock (TableGate)
            {
                List<Entry> bucket;
                if (_entries.TryGetValue(id, out bucket))
                    foreach (var entry in bucket)
                        if (object.ReferenceEquals(entry.Owner.Target, package)) return entry;
                if (!create) return null;
                if (bucket == null)
                {
                    bucket = new List<Entry>(1);
                    _entries.Add(id, bucket);
                    Sweep.Enqueue(id);
                }
                var added = new Entry(package);
                bucket.Add(added);
                for (int n = 0; n < 4 && Sweep.Count != 0; n++)
                {
                    int next = Sweep.Dequeue();
                    var scanned = _entries[next];
                    for (int i = scanned.Count - 1; i >= 0; i--)
                        if (!scanned[i].Owner.IsAlive) scanned.RemoveAt(i);
                    if (scanned.Count == 0) _entries.Remove(next);
                    else Sweep.Enqueue(next);
                }
                return added;
            }
        }

        // Read-only witness: do not create an entry, restore a package root, or
        // add any process-lifetime strong reference to the mutable JSON parent.
        internal static JSONClass PeekKnownTree(VarPackage package)
        {
            var root = Root.GetValue(package) as JSONClass;
            if (root != null || !_enabled) return root;
            var entry = GetEntry(package, false);
            if (entry == null) return null;
            lock (entry) return Root.GetValue(package) as JSONClass ?? entry.LeasedTree ?? entry.Tree.Target as JSONClass;
        }

        private static void Restore(VarPackage package, Entry entry)
        {
            if (Root.GetValue(package) != null || (bool)Loaded.GetValue(package)) return;
            // loaded-null is an explicit native/foreign invalidation state, not our detached state.
            var tree = entry.Tree.Target as JSONClass;
            if (tree == null) return;
            Root.SetValue(package, tree);
            Loaded.SetValue(package, true);
            Interlocked.Increment(ref _hotHits);
        }

        private static void Detach(VarPackage package, Entry entry, bool sync)
        {
            var tree = Root.GetValue(package) as JSONClass;
            if (tree == null)
            {
                // Original error/disabled/invalidated states win; never revive
                // an older tree after the native method has explicitly dropped it.
                entry.Tree.Target = null;
                entry.LeasedTree = null;
                return;
            }
            string path = ReloadPath.GetValue(package) as string;
            if (sync || string.IsNullOrEmpty(path))
            {
                // Both native regeneration branches omit _jsonCachePath.
                // Use their exact on-disk target, only after a completed write.
                string dir = CacheManager.GetPackageJSONCacheDir();
                if (!string.IsNullOrEmpty(dir))
                {
                    string generated = dir + "/" + package.Uid + ".vamcachejson";
                    if (File.Exists(generated))
                    {
                        path = generated;
                        ReloadPath.SetValue(package, path);
                    }
                    else if (sync) path = null;
                }
                else if (sync) path = null;
            }
            if (string.IsNullOrEmpty(path))
            {
                // An in-memory tree without a reload source keeps native ownership.
                entry.Tree.Target = null;
                entry.LeasedTree = null;
                Interlocked.Increment(ref _unreloadable);
                return;
            }
            entry.Tree.Target = tree;
            entry.LeasedTree = entry.Leases > 0 ? tree : null;
            // Drop only the package root, not any nodes already handed to users.
            Root.SetValue(package, null);
            Loaded.SetValue(package, false);
            Interlocked.Increment(ref _detached);
        }

        private static void Prefix(VarPackage __instance, MethodBase __originalMethod, out Call __state)
        {
            __state = new Call();
            var entry = GetEntry(__instance, true);
            Monitor.Enter(entry);
            __state.Entry = entry;
            __state.Held = true;
            if (++entry.Depth != 1) return;
            Restore(__instance, entry);
            if (__originalMethod.Name == "GetJSONCache" && Root.GetValue(__instance) == null &&
                !(bool)Loaded.GetValue(__instance) && ReloadPath.GetValue(__instance) != null)
                Interlocked.Increment(ref _coldLoads);
        }

        private static Exception Finalizer(VarPackage __instance, MethodBase __originalMethod,
            ref Call __state, Exception __exception)
        {
            if (!__state.Held) return __exception;
            __state.Held = false;
            var entry = __state.Entry;
            try
            {
                if (entry.Depth == 1 && _enabled)
                    Detach(__instance, entry, __originalMethod.Name == "SyncJSONCache");
            }
            finally
            {
                entry.Depth--;
                Monitor.Exit(entry);
            }
            // In particular, do not swallow an exception from another patch.
            return __exception;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            if (Root == null || Root.FieldType != typeof(JSONClass) || Loaded == null ||
                Loaded.FieldType != typeof(bool) || ReloadPath == null || ReloadPath.FieldType != typeof(string))
                throw new MissingFieldException("VarPackage JSON retention fields changed");
            var get = typeof(VarPackage).GetMethod("GetJSONCache", new[] { typeof(string) });
            var sync = typeof(VarPackage).GetMethod("SyncJSONCache", Type.EmptyTypes);
            var bulk = typeof(VarPackage).GetMethod("SyncJSONCache", new[] { typeof(HashSet<string>), typeof(HashSet<string>) });
            if (get == null || sync == null || bulk == null)
                throw new MissingMethodException("VarPackage JSON retention methods changed");
            var batch = typeof(DAZMorphBank).GetMethod("RuntimeImportFromDir", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(DirectoryEntry), typeof(bool), typeof(bool), typeof(bool) }, null);
            RequireBatch(batch, "A667EB8F4A9C733004286343A63E0FD544F4DEB5275C2CEC3617318CDBF7555B");
            var items = typeof(DAZCharacterSelector).GetMethod("SyncCustomItems", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(DAZDynamicItem.Gender), typeof(DAZDynamicItem[]), typeof(string), typeof(UnityEngine.Transform), typeof(UnityEngine.Transform) }, null);
            if (items == null) throw new MissingMethodException("SyncCustomItems");
            RequireBatch(items, "DCADA8D210D22FB381D22063B95139B3A2FB96DEEF2928A4F556FC02036F6718");
            _harmony = new Harmony("quest3triggerui.packagejson." + typeof(PackageJsonWeakRetention).Namespace);
            try
            {
                var prefix = new HarmonyMethod(typeof(PackageJsonWeakRetention), "Prefix");
                var finalizer = new HarmonyMethod(typeof(PackageJsonWeakRetention), "Finalizer");
                foreach (var method in new[] { get, sync, bulk })
                    _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                _harmony.Patch(batch, prefix: new HarmonyMethod(typeof(PackageJsonWeakRetention), "BatchPrefix"),
                    finalizer: new HarmonyMethod(typeof(PackageJsonWeakRetention), "BatchFinalizer"));
                _harmony.Patch(items, prefix: new HarmonyMethod(typeof(PackageJsonWeakRetention), "ItemsPrefix"),
                    transpiler: new HarmonyMethod(typeof(PackageJsonWeakRetention), "ItemsRewrite"),
                    finalizer: new HarmonyMethod(typeof(PackageJsonWeakRetention), "ItemsFinalizer"));
                _enabled = true;
                int seeded = 0, nodes = 0;
                var packages = FileManager.GetPackages();
                foreach (var package in packages)
                {
                    if (Root.GetValue(package) == null) continue;
                    var entry = GetEntry(package, true);
                    lock (entry)
                    {
                        var tree = Root.GetValue(package) as JSONClass;
                        if (tree == null) continue;
                        seeded++;
                        nodes += tree.Count;
                        Detach(package, entry, false);
                    }
                }
                Log("installed methods=5 currentItemPackageLease=True packageBatchLease=True release=outermost-return packages=" + packages.Count + " seeded=" + seeded +
                    " topEntries=" + nodes + " detached=" + _detached + " unreloadable=" + _unreloadable +
                    " ownership=weak gcPolicy=unchanged");
            }
            catch
            {
                Shutdown();
                throw;
            }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _enabled = false;
            try
            {
                var snapshot = new List<Entry>();
                lock (TableGate)
                    foreach (var bucket in _entries.Values) snapshot.AddRange(bucket);
                foreach (var entry in snapshot)
                {
                    var package = entry.Owner.Target as VarPackage;
                    if (package == null) continue;
                    lock (entry)
                    {
                        Restore(package, entry);
                        entry.LeasedTree = null;
                    }
                }
            }
            finally
            {
                _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                lock (TableGate) { _entries.Clear(); Sweep.Clear(); }
                Log("shutdown detached=" + _detached + " hotHits=" + _hotHits + " coldLoads=" + _coldLoads +
                    " unreloadable=" + _unreloadable + " batches=" + _batches + " completedBatches=" + _completedBatches + " itemScopes=" + _itemScopes + " completedItemScopes=" + _completedItemScopes + " itemPackageSwitches=" + _itemPackageSwitches + " nativeLazyLoad=restored");
            }
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[package-json] " + message);
        }
    }
}
