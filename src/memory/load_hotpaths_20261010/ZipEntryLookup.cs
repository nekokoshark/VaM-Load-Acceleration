using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using ICSharpCode.SharpZipLib.Zip;
using Quest3TriggerUI;

namespace VaM.Memory {
    public static class ZipEntryLookup {
        public const string Id = "vam.memory.zip-entry-lookup";
        private const long MaxBytes = 32L * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly WeakObservationIndex<Index> States = new WeakObservationIndex<Index>();
        private static readonly FieldInfo Entries = AccessTools.Field(typeof(ZipFile), "entries_");
        private static readonly FieldInfo Disposed = AccessTools.Field(typeof(ZipFile), "isDisposed_");
        private static long reserved;
        public static long Built, Hits, ExactHits, CulturalHits, TablesBuilt, CapacityClears, LinearProbes;
        public static Action<string> Log;
        private sealed class Index {
            internal WeakReference Entries;
            internal NameTable Sensitive, Insensitive;
            internal long Bytes;
            internal int Queries;
            internal bool Ready;
        }
        private sealed class NameTable {
            internal Dictionary<string,int> Cultural, Exact;
        }
        private static NameTable BuildNames(ZipEntry[] entries, bool ignoreCase) {
            var table = new NameTable {
                Cultural = new Dictionary<string,int>(entries.Length, ignoreCase ? StringComparer.InvariantCultureIgnoreCase : StringComparer.InvariantCulture),
                Exact = new Dictionary<string,int>(entries.Length, StringComparer.Ordinal)
            };
            for (int i=0;i<entries.Length;i++) {
                string key=entries[i].Name; if(key==null)continue;
                int first;
                if(!table.Cultural.TryGetValue(key,out first)) {first=i;table.Cultural.Add(key,first);}
                // Exact spelling still points at the FIRST CULTURAL match, not itself.
                // This preserves canonical equivalents, ignorable characters and duplicates.
                if(!table.Exact.ContainsKey(key))table.Exact.Add(key,first);
            }
            TablesBuilt++;return table;
        }
        public static void Install() {
            if (Entries == null || Entries.FieldType != typeof(ZipEntry[]) || Disposed == null || Disposed.FieldType != typeof(bool))
                throw new MissingFieldException("ZIP lookup schema");
            using (var hash=SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(AccessTools.Method(typeof(ZipFile),"FindEntry").GetMethodBody().GetILAsByteArray())).Replace("-","") !=
                    "DCEDC89B8198737ADA5A9F16183EFC6AA6D185B9A90B8FB217F0E981E84E8F6F")
                    throw new InvalidOperationException("ZIP FindEntry implementation changed");
            var harmony = new Harmony(Id);
            harmony.Patch(AccessTools.Method(typeof(ZipFile), "FindEntry"), prefix: new HarmonyMethod(typeof(ZipEntryLookup), "Before"));
            foreach (string name in new[] { "ReadEntries", "DisposeInternal" })
                harmony.Patch(AccessTools.Method(typeof(ZipFile), name), prefix: new HarmonyMethod(typeof(ZipEntryLookup), "Invalidate"));
        }
        private static void Invalidate(ZipFile __instance) {
            lock (Gate) {
                Index state;
                if (States.TryGetValue(__instance, out state) && state != null) {
                    reserved -= state.Bytes; States[__instance] = null;
                }
            }
        }
        public static void Clear() { lock (Gate) { States.Clear(); reserved = 0; } }
        public static void Stop() { new Harmony(Id).UnpatchAll(Id); Clear(); }
        public static long ReservedBytes { get { lock(Gate) return reserved; } }
        private static bool Before(ZipFile __instance, string name, bool ignoreCase, ref int __result) {
            // Original handles disposal and null lookup (including its exact exception/result).
            if (__instance == null || name == null || (bool)Disposed.GetValue(__instance)) return true;
            var entries = (ZipEntry[])Entries.GetValue(__instance);
            if (entries == null || entries.Length < 32) return true;
            lock (Gate) {
                Index state;
                if (!States.TryGetValue(__instance, out state) || state == null || !ReferenceEquals(state.Entries.Target, entries)) {
                    if (state != null) { reserved -= state.Bytes; States[__instance] = null; }
                    if(States.Count>=4096 || reserved+128>MaxBytes){Clear();CapacityClears++;}
                    state=new Index {Entries=new WeakReference(entries),Bytes=128};
                    States[__instance]=state;reserved+=128;
                }
                // First two lookups use original FindEntry. Package-library scans
                // often touch an archive only once; do not build full tables for them.
                if(state.Queries<2){state.Queries++;LinearProbes++;return true;}
                if(!state.Ready) {
                    long bytes = 128;
                    foreach (ZipEntry entry in entries) {
                        if (entry == null || entry.GetType() != typeof(ZipEntry)) return true;
                        // Reserve both lazy cultural+exact tables, even if only one is used.
                        if (entry.Name != null) bytes += 256L + entry.Name.Length * 2L;
                        if (bytes > MaxBytes) return true;
                    }
                    if(reserved+bytes-state.Bytes>MaxBytes) {
                        Clear();CapacityClears++;States[__instance]=state;reserved=state.Bytes;
                    }
                    reserved+=bytes-state.Bytes;state.Bytes=bytes;state.Ready=true;Built++;
                }
                NameTable table=ignoreCase ? state.Insensitive : state.Sensitive;
                if(table==null) {
                    table=BuildNames(entries,ignoreCase);
                    if(ignoreCase)state.Insensitive=table;else state.Sensitive=table;
                }
                int result;
                if(table.Exact.TryGetValue(name,out result)) {__result=result;ExactHits++;}
                else {__result=table.Cultural.TryGetValue(name,out result) ? result : -1;CulturalHits++;}
                Hits++;
                if (Log != null && (Hits == 1 || Hits % 8192 == 0))
                    Log("[zip-entry-lookup] hits=" + Hits + " built=" + Built + " exact="+ExactHits+" cultural="+CulturalHits+" tables="+TablesBuilt+" linearProbes="+LinearProbes+" capacityClears="+CapacityClears+" estimatedIndexBytes=" + reserved + " archiveKeys=weak");
                return false;
            }
        }
    }
}
