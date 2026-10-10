using System;
using System.Collections.Generic;
using System.Reflection;
using AssetBundles;
using UnityEngine;
using Vam.PersonPrepared.SpecV2;

namespace VaM.PersonPrepared.Runtime
{
    internal static class OriginalBackend
    {
        internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        internal static readonly FieldInfo CountField = typeof(SuperController).GetField("assetBundleAssetNameRefCounts", All);
        internal static readonly FieldInfo CacheField = typeof(SuperController).GetField("assetBundleAssetNameToPrefab", All);
        internal static readonly FieldInfo TrackedField = typeof(AssetBundleManager).GetField("m_TrackedAssetBundles", All);
        internal static readonly FieldInfo AssetsField = typeof(SuperController).GetField("atomAssetByType", All);
        internal static readonly MethodInfo Remap = typeof(AssetBundleManager).GetMethod("RemapVariantName", All);
        internal static Dictionary<string, int> Counts(SuperController sc) { return (Dictionary<string, int>)CountField.GetValue(sc); }
        internal static Dictionary<string, GameObject> Cache(SuperController sc) { return (Dictionary<string, GameObject>)CacheField.GetValue(sc); }
        internal static Dictionary<string, LoadedAssetBundle> Tracked() { return (Dictionary<string, LoadedAssetBundle>)TrackedField.GetValue(null); }
        internal static void RequireCanonical(string bundle)
        { if ((string)Remap.Invoke(null, new object[] { bundle }) != bundle) throw new InvalidOperationException("Variant bridge not admitted"); }
        internal static void Return(OwnedPrefab unit)
        {
            if (!BundleReceipts.Ready(unit)) return;
            ReceiptStage stage;
            if (!unit.Protocol.TryClaimReturn(out stage)) return;
            try
            {
                LoadedAssetBundle actual;
                if (!Tracked().TryGetValue(unit.Bundle, out actual) || !ReferenceEquals(actual, unit.Wrapper))
                    throw new InvalidOperationException("Return resource incarnation changed");
                if (stage == ReceiptStage.RegisteredPrefab)
                {
                    GameObject prefab;
                    if (!Cache(unit.Controller).TryGetValue(unit.Key, out prefab) || !ReferenceEquals(prefab, unit.CacheIdentity))
                        throw new InvalidOperationException("Prefab cache incarnation changed");
                }
                using (new ReturnWitness.Scope(unit, stage == ReceiptStage.RegisteredPrefab ? "UnregisterPrefab" : "UnloadAssetBundleInternal"))
                {
                    if (stage == ReceiptStage.RegisteredPrefab) unit.Controller.UnregisterPrefab(unit.Bundle, unit.Asset);
                    else BundleReceipts.ReturnExact(unit.Bundle, unit.Wrapper, null);
                }
                if (unit.ReturnWrites != 1) throw new InvalidOperationException("Return write unproved");
                unit.Protocol.WitnessReturnConfirmed(); OwnerHost.Drop(unit);
            }
            catch (Exception error) { OwnerHost.Quarantine(unit, error); }
        }
        internal static void Validate()
        {
            Check(CountField, typeof(Dictionary<string, int>)); Check(CacheField, typeof(Dictionary<string, GameObject>));
            Check(TrackedField, typeof(Dictionary<string, LoadedAssetBundle>)); Check(AssetsField, typeof(Dictionary<string, SuperController.AtomAsset>));
            if (Remap == null || Remap.ReturnType != typeof(string)) throw new MissingMethodException("RemapVariantName");
        }
        private static void Check(FieldInfo field, Type type)
        { if (field == null || field.FieldType != type) throw new MissingFieldException("Ownership field mapping changed"); }
    }
}
