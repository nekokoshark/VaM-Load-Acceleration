using System;
using Vam.PersonPrepared.SpecV2;

namespace VaM.PersonPrepared.Runtime
{
    // The very same OwnedPrefab moves from stable acquisition to the actual Atom sidecar.
    internal sealed class AsyncWork : IDrainWork
    {
        private SuperController controller;
        private readonly SuperController.AtomAsset asset;
        internal OwnedPrefab Unit;
        private bool cancelled;
        internal AsyncWork(SuperController sc, SuperController.AtomAsset input)
        {
            controller = sc;
            asset = new SuperController.AtomAsset { assetBundleName = input.assetBundleName, assetName = input.assetName, category = input.category };
        }
        public DrainPhase Begin()
        {
            try { OwnerHost.BeginAsync(controller, asset, out Unit); }
            catch { if (Unit == null || Unit.Protocol.Read().Phase != UnitPhase.NoUnitTerminal) throw; }
            controller = null;
            if (cancelled) Cancel(); return Status();
        }
        public void Cancel()
        {
            cancelled = true;
            if (Unit == null) return;
            Unit.Protocol.RequestCancellation(); Unit.Protocol.ObserveCancellation();
            if (Unit.Protocol.Read().Phase == UnitPhase.Retiring) OwnerHost.Queue(Unit);
        }
        public DrainPhase Poll()
        {
            if (Unit == null) return DrainPhase.NoUnitTerminal;
            if (Unit.Protocol.Read().Phase == UnitPhase.NativeInFlight) OwnerHost.CompleteCold(Unit);
            if (Unit.Protocol.Read().Phase == UnitPhase.Retiring) OriginalBackend.Return(Unit);
            return Status();
        }
        private DrainPhase Status()
        {
            switch (Unit.Protocol.Read().Phase)
            {
                case UnitPhase.Ready: return DrainPhase.Prepared;
                case UnitPhase.NativeInFlight: return DrainPhase.NativeInFlight;
                case UnitPhase.Retiring: return DrainPhase.Retiring;
                case UnitPhase.Returned: return DrainPhase.Returned;
                case UnitPhase.NoUnitTerminal: return DrainPhase.NoUnitTerminal;
                case UnitPhase.Quarantined: return DrainPhase.Quarantined;
                default: throw new InvalidOperationException("Unexpected stable acquisition phase");
            }
        }
        public void Quarantine()
        {
            controller = null;
            if (Unit != null && Unit.Protocol.Read().Phase != UnitPhase.Quarantined)
                OwnerHost.Quarantine(Unit, new InvalidOperationException("Stable backend acquisition fault"));
        }
    }
}
