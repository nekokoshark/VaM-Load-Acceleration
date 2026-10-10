using System;
using System.IO;
using System.Security.Cryptography;
using HarmonyLib;

namespace VaM.PersonPrepared.Runtime
{
    public static class RuntimeControl
    {
        private static bool installed;
        public static void InstallForIntegration()
        {
            StableDrain.Shared.Main(); if (installed) return;
            using (var stream = File.OpenRead(typeof(SuperController).Assembly.Location))
            using (SHA256 hash = SHA256.Create())
            {
                string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (actual != "2440172dcd4fc5bf71b677fc464cc26d7ea0e8d5289209c37217f7870df6875c")
                    throw new InvalidOperationException("Main assembly does not match the tested input");
            }
            try { LifecycleHooks.Install(); PlanHooks.Install(); SceneHooks.Install(); installed = true; }
            catch
            {
                new Harmony("vam.personprepared.scene.cold").UnpatchAll("vam.personprepared.scene.cold");
                new Harmony("vam.personprepared.plan.cold").UnpatchAll("vam.personprepared.plan.cold");
                LifecycleHooks.RemoveUnusedHooks(); throw;
            }
            // Installation alone remains inert at all acquisition entry points.
        }
        public static void OpenForIntegration()
        { StableDrain.Shared.Main(); if (!installed || StableDrain.Shared.OwnershipAdmissionStopped) throw new InvalidOperationException("Integration admission not ready"); PlanRegistry.Open(); }
        public static void CloseEntry()
        { StableDrain.Shared.Main(); PlanRegistry.Close(); SceneHooks.CancelAll(); }
        public static void Pump() { if (installed) OwnerHost.Pump(); }
        public static bool DrainComplete
        {
            get
            {
                if (!installed) return true;
                StableDrain.Shared.Main();
                return OwnerHost.Creators == 0 && OwnerHost.RetirementCount == 0 && StableDrain.Shared.Pending == 0
                    && SceneHooks.Count == 0 && PlanRegistry.LoadCount == 0 && PlanRegistry.TagCount == 0;
            }
        }
        public static string Snapshot()
        {
            StableDrain.Shared.Main();
            return "units=" + OwnerHost.Units.Count + " atoms=" + OwnerHost.InstanceCount + " retire=" + OwnerHost.RetirementCount
                + " native=" + StableDrain.Shared.Pending + " bundles=" + BundleReceipts.Count + " scenes=" + SceneHooks.Count
                + " plans=" + PlanRegistry.LoadCount + " tags=" + PlanRegistry.TagCount + " faults=" + StableDrain.Shared.FaultCount;
        }
    }
}
