using System;
using Quest3TriggerUI;
using UnityEngine;
using VaM.PersonPrepared.Runtime;

namespace VaM.Memory
{
    // One owner of automatic character/load-tail cleanup. Existing allocators
    // and native release finalizers retain their ownership; this only schedules
    // the original UUA/GC after their actual completion debts have drained.
    internal static class LoadReclamation
    {
        [Flags]
        internal enum Debt
        {
            None = 0, Scene = 1, Images = 2, Decode = 4, Writes = 8,
            Native = 16, Shared = 32, Roots = 64, Person = 128,
            Wardrobe = 256, Preset = 512, Sweep = 1024
        }
        private static ReclamationCycle cycle;
        private static AsyncOperation operation;
        private static bool installed, wasLoading;
        private static Debt lastDebt;
        private static float nextBlockedLog;
        private static string reason;
        internal static bool Pending { get { return installed && cycle.Pending; } }

        internal static Debt ReadDebt()
        {
            Debt debt = Debt.None;
            var sc = SuperController.singleton;
            if (sc == null || sc.isLoading || SceneLoadAccelerator.SceneLoadActive) debt |= Debt.Scene;
            if (WardrobeJanitor.ImagesBusy() || ImageDrainBarrier.Busy()) debt |= Debt.Images;
            if (TextureDecodeBudget.PendingCount != 0) debt |= Debt.Decode;
            if (TextureCacheWriteBudget.PendingBytes() != 0) debt |= Debt.Writes;
            if (NativeCacheBuffer.LiveSlots != 0) debt |= Debt.Native;
            if (DecodedBufferPool.SharedEntries != 0) debt |= Debt.Shared;
            if (InstanceRootRetirement.PendingCount != 0 || PresetOwnerRetirement.PendingCount != 0) debt |= Debt.Roots;
            if (!RuntimeControl.DrainComplete) debt |= Debt.Person;
            if (WardrobeJanitor.PendingRetirements != 0) debt |= Debt.Wardrobe;
            if (PresetSweepGate.TransactionActive) debt |= Debt.Preset;
            if (!PresetSweepGate.SweepSettled || PresetCleanupCoalescer.OperationPending) debt |= Debt.Sweep;
            return debt;
        }

        private static bool Ready() { return ReadDebt() == Debt.None; }
        private static bool HasPending() { return Pending; }

        private static bool Request(bool sweep, bool gc, string origin)
        {
            if (!installed || !cycle.Request(sweep, gc)) return false;
            reason = origin;
            return true;
        }

        private static bool RequestJanitor()
        {
            if (!installed || !cycle.RequestJanitor()) return false;
            reason = "wardrobe-release";
            return true;
        }

        internal static void Install()
        {
            if (installed) return;
            if (!PresetSweepGate.IsInstalled) throw new InvalidOperationException("Character cleanup adapter not installed");
            ImageDrainBarrier.Install();
            cycle = new ReclamationCycle();
            installed = true;
            PresetSweepGate.ManagedRequest = Request;
            PresetSweepGate.CompletionReady = Ready;
            PresetSweepGate.ManagedPending = HasPending;
            WardrobeJanitor.ManagedCleanup = RequestJanitor;
            Log("installed automatic=load-tail receipts=decode/write/native/shared/roots/person/wardrobe completion=two-frames janitor=event-tail janitor-gc=requested-only manual=preserved idle-policy=preserved");
        }

        internal static void Tick()
        {
            if (!installed) return;
            var sc = SuperController.singleton;
            bool loading = sc != null && (sc.isLoading || SceneLoadAccelerator.SceneLoadActive);
            if (loading) wasLoading = true;
            else if (sc != null && wasLoading)
            {
                wasLoading = false;
                Request(false, true, "scene-completion");
            }
            if (!cycle.Pending) return;
            Debt debt = ReadDebt();
            if (debt != Debt.None && (debt != lastDebt || Time.realtimeSinceStartup >= nextBlockedLog))
            {
                lastDebt = debt;
                nextBlockedLog = Time.realtimeSinceStartup + 30f;
                Log("pending debt=" + debt + " action=wait-consumers timeout-release=False");
            }
            ReclamationCycle.Work work = cycle.Next(debt == Debt.None, PresetSweepGate.ActivityEpoch,
                Time.frameCount, operation == null || operation.isDone);
            if (work == ReclamationCycle.Work.None)
            {
                if (!cycle.Pending)
                {
                    operation = null;
                    lastDebt = Debt.None;
                    Log("completed sweepReceipt=" + cycle.SweepReceipt + " pending=False gc=False physicalRelease=not-guaranteed");
                }
                return;
            }
            try
            {
                // Retire eligible idle staging at this transaction boundary,
                // not after another 10/30 seconds of standby. Live leases and
                // normal Person/resource pools are never touched by this step.
                long nativeIdle = NativeCacheBuffer.TrimIdleNow();
                long managedIdle = DecodedBufferPool.PooledBytes;
                DecodedBufferPool.Clear();
                if (nativeIdle != 0 || managedIdle != 0)
                    Log("staging-retired nativeIdleBytes=" + nativeIdle + " managedIdlePayload=" + managedIdle + " live-consumers=preserved");
                if (work == ReclamationCycle.Work.Sweep)
                {
                    // Keep the existing UUA demotion/backstop decision. A skipped
                    // sweep never becomes a new physical-release claim.
                    operation = cycle.JanitorSweep ? WardrobeJanitor.SubmitManagedCleanup() :
                        PresetSweepGate.SubmitLoadTailSweep(reason);
                    if (operation == null) throw new InvalidOperationException("Cleanup returned no completion operation");
                    return;
                }
                long receipt = cycle.CollectionReceipt;
                PresetSweepGate.CollectLoadTail("load-tail:" + reason);
                cycle.Collected(receipt);
                operation = null;
                lastDebt = Debt.None;
                Log("completed receipt=" + receipt + " pending=" + cycle.Pending + " gc=original physicalRelease=not-guaranteed");
            }
            catch (Exception error)
            {
                // Do not replay a possibly submitted native operation. Restore
                // the previous policy for subsequent original requests.
                Stop();
                Log("fault type=" + error.GetType().Name + " original-policy=restored partial-action=not-retried");
                throw;
            }
        }

        internal static void Stop()
        {
            if (!installed) return;
            installed = false;
            cycle.Stop();
            PresetSweepGate.ManagedRequest = null;
            PresetSweepGate.CompletionReady = null;
            PresetSweepGate.ManagedPending = null;
            WardrobeJanitor.ManagedCleanup = null;
            // The engine owns any submitted operation. No forced native free.
            operation = null;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[load-reclaim] " + message);
        }
    }
}
