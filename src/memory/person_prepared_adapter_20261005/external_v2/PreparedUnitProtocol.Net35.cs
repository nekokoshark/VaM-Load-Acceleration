// Reference event reducer, C# 3 / .NET 3.5 API surface.
// NOT a Harmony patch or a verified Unity/backend adapter. Not compiled in this review.
// All Witness* calls require affirmative per-acquisition evidence from the adapter.
// No object/Host/delegate/Unity reference is stored in this reducer or its observer value.
using System;
using System.Threading;

namespace Vam.PersonPrepared.SpecV2
{
    public enum ReceiptStage { None, BundleOnly, RegisteredPrefab, BulkDetached }
    public enum UnitPhase
    {
        Reserved, Acquiring, NativeInFlight, NeedsRegistration, Ready,
        Creating, Bound, Retiring, Returning, Returned, NoUnitTerminal, Quarantined
    }
    public struct Observer
    {
        public readonly long UnitId, Version, InstanceId;
        public readonly UnitPhase Phase;
        public readonly ReceiptStage Stage;
        public Observer(long unit, long version, long instance, UnitPhase phase, ReceiptStage stage)
        { UnitId = unit; Version = version; InstanceId = instance; Phase = phase; Stage = stage; }
    }
    public sealed class PreparedUnitProtocol
    {
        private readonly int mainThreadId;
        private readonly long unitId;
        private long version = 1;
        private long creatorId;
        private long instanceId;
        private int cancelled;
        private bool producerActive;
        private bool nativeComplete;
        private bool requiresDestruction;
        private bool destructionComplete;
        private bool consumersComplete;
        private bool readyNotificationClaimed;
        private bool completionNotificationClaimed;
        private UnitPhase phase = UnitPhase.Reserved;
        private ReceiptStage stage;

        public PreparedUnitProtocol(long id)
        {
            if (id <= 0) throw new ArgumentOutOfRangeException("id");
            unitId = id; mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }
        private void Main()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                throw new InvalidOperationException("Main-thread transition required");
        }
        private void Require(UnitPhase expected)
        { Main(); if (phase != expected) throw new InvalidOperationException("Unexpected phase: " + phase); }
        public Observer Read()
        { Main(); return new Observer(unitId, version, instanceId, phase, stage); }
        public void RequestCancellation() { Interlocked.Exchange(ref cancelled, 1); }
        public bool CancellationRequested
        { get { return Interlocked.CompareExchange(ref cancelled, 0, 0) != 0; } }
        public void BeginAcquisition()
        {
            Require(UnitPhase.Reserved);
            phase = CancellationRequested ? UnitPhase.NoUnitTerminal : UnitPhase.Acquiring;
        }
        public void WitnessNoUnitAcquired()
        { Require(UnitPhase.Acquiring); phase = UnitPhase.NoUnitTerminal; }
        public void WitnessCacheAcquisitionCommitted()
        {
            Require(UnitPhase.Acquiring);
            stage = ReceiptStage.RegisteredPrefab; nativeComplete = true;
            phase = CancellationRequested ? UnitPhase.Retiring : UnitPhase.Ready;
        }
        public void WitnessColdAcquisitionCommitted()
        {
            Require(UnitPhase.Acquiring);
            stage = ReceiptStage.BundleOnly; nativeComplete = false;
            phase = UnitPhase.NativeInFlight;
        }
        // operation queue removal/Update()==false is NOT sufficient evidence for this event.
        public void WitnessNativeTerminal(bool usableResult)
        {
            Require(UnitPhase.NativeInFlight); nativeComplete = true;
            phase = (!usableResult || CancellationRequested)
                ? UnitPhase.Retiring : UnitPhase.NeedsRegistration;
        }
        public void WitnessRegistrationCommitted()
        {
            Require(UnitPhase.NeedsRegistration);
            // The same acquisition is promoted; do not acquire another native unit.
            stage = ReceiptStage.RegisteredPrefab;
            phase = CancellationRequested ? UnitPhase.Retiring : UnitPhase.Ready;
        }
        public void ObserveCancellation()
        {
            Main(); if (!CancellationRequested) return;
            if (phase == UnitPhase.Reserved) phase = UnitPhase.NoUnitTerminal;
            else if (phase == UnitPhase.Ready || phase == UnitPhase.NeedsRegistration)
                phase = UnitPhase.Retiring;
            // Acquiring: receipt witness/exception processing must settle what really happened.
            // NativeInFlight: drain to native terminal; no early return.
            // Creating: producer must abandon a known clone or complete a previously valid bind.
            // Bound: cancellation of the OLD REQUEST does not retire the transferred Atom.
        }
        public long BeginCreation(long newCreatorId)
        {
            Require(UnitPhase.Ready);
            if (CancellationRequested) throw new InvalidOperationException("Stale creation request");
            if (newCreatorId <= 0) throw new ArgumentOutOfRangeException("newCreatorId");
            version = checked(version + 1);
            creatorId = newCreatorId; producerActive = true; phase = UnitPhase.Creating;
            return version;
        }
        public long BindInstance(long creator, long actualInstanceId)
        {
            Require(UnitPhase.Creating);
            if (!producerActive || creator != creatorId || actualInstanceId <= 0)
                throw new InvalidOperationException("Wrong creation scope");
            if (CancellationRequested) throw new InvalidOperationException("Publication forbidden");
            long next = checked(version + 1);
            instanceId = actualInstanceId; version = next; phase = UnitPhase.Bound;
            return version; // creator's old ticket cannot release this Atom's unit
        }
        public void AbandonCreation(long creator, long knownCloneId, bool exactNoCloneProof)
        {
            Require(UnitPhase.Creating);
            if (!producerActive || creator != creatorId) throw new InvalidOperationException("Wrong creator");
            if (knownCloneId <= 0 && !exactNoCloneProof)
            { phase = UnitPhase.Quarantined; return; }
            instanceId = knownCloneId; requiresDestruction = knownCloneId > 0;
            phase = UnitPhase.Retiring;
        }
        public void EndProducer(long creator)
        {
            Main();
            if (!producerActive || creator != creatorId) throw new InvalidOperationException("Producer not active");
            producerActive = false; creatorId = 0;
            if (phase == UnitPhase.Creating)
                phase = UnitPhase.Quarantined; // neither a known bind nor a proved rollback
        }
        public bool RetireUnused(long requestVersion)
        {
            Main();
            if (requestVersion != version || phase != UnitPhase.Ready) return false;
            phase = UnitPhase.Retiring; return true;
        }
        public long TransferBoundOwner(long ownerVersion)
        {
            Require(UnitPhase.Bound);
            if (version != ownerVersion) throw new InvalidOperationException("Stale owner");
            version = checked(version + 1); return version; // no engine count operation
        }
        public bool RequestAtomRetirement(long ownerVersion)
        {
            Main();
            if (phase != UnitPhase.Bound || ownerVersion != version) return false;
            requiresDestruction = true; phase = UnitPhase.Retiring; return true;
        }
        // Called only after the exact HardReset table-detach/loop step was witnessed.
        // Native bundle return was intercepted, not executed. Never call UnregisterPrefab again.
        public void WitnessBulkDetach()
        {
            Main();
            if (stage != ReceiptStage.RegisteredPrefab ||
                (phase != UnitPhase.Ready && phase != UnitPhase.Bound && phase != UnitPhase.Retiring))
                throw new InvalidOperationException("Unit not eligible for proved bulk detachment");
            long next = checked(version + 1);
            stage = ReceiptStage.BulkDetached; requiresDestruction = instanceId > 0;
            phase = UnitPhase.Retiring; version = next;
        }
        public void WitnessDestructionAndConsumersComplete()
        {
            Main();
            if (phase != UnitPhase.Retiring || !requiresDestruction)
                throw new InvalidOperationException("No instance retirement pending");
            destructionComplete = consumersComplete = true;
        }
        public bool TryClaimReturn(out ReceiptStage command)
        {
            Main(); command = ReceiptStage.None;
            if (phase != UnitPhase.Retiring || !nativeComplete || producerActive) return false;
            if (requiresDestruction && (!destructionComplete || !consumersComplete)) return false;
            if (stage == ReceiptStage.None) throw new InvalidOperationException("No acquired unit");
            phase = UnitPhase.Returning; command = stage; return true;
        }
        public void WitnessReturnConfirmed()
        { Require(UnitPhase.Returning); phase = UnitPhase.Returned; }
        public void Quarantine()
        {
            Main();
            if (phase == UnitPhase.Returned || phase == UnitPhase.NoUnitTerminal)
                throw new InvalidOperationException("Terminal unit");
            phase = UnitPhase.Quarantined;
        }
        public bool ClaimReadyNotification()
        {
            Main(); if (readyNotificationClaimed) return false;
            if (phase != UnitPhase.Ready && phase != UnitPhase.Retiring && phase != UnitPhase.NoUnitTerminal)
                throw new InvalidOperationException("Preload has no terminal readiness outcome");
            readyNotificationClaimed = true; return true;
        }
        public bool ClaimPlanCompletionNotification()
        {
            Main(); if (completionNotificationClaimed) return false;
            // The scene adapter additionally verifies the request's child/creation outcome.
            completionNotificationClaimed = true; return true;
        }
    }
}
