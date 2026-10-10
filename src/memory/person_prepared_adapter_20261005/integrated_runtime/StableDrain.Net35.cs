// Stable cold-assembly kernel, C#3/.NET3.5. No scene, Unity, hot delegate or coroutine reference.
using System;
using System.Collections.Generic;
using System.Threading;

namespace VaM.PersonPrepared.Runtime
{
    public enum DrainPhase
    { Reserved, Acquiring, NativeInFlight, Prepared, Retiring, Returned, NoUnitTerminal, Transferred, Quarantined }

    // Implementations belong to the cold backend assembly, never to a retiring scene/hot payload.
    public interface IDrainWork
    {
        DrainPhase Begin();
        DrainPhase Poll();
        void Cancel();
        void Quarantine();
    }

    public sealed class DrainHandle
    {
        internal readonly DrainCell Cell;
        internal DrainHandle(DrainCell cell) { Cell = cell; }
        public long RequestId { get { return Cell.Id; } }
        public long AdmissionGeneration { get { return Cell.Generation; } }
        public DrainPhase Phase { get { Cell.Host.Main(); return Cell.Phase; } }
        public bool CancellationRequested { get { Cell.Host.Main(); return Cell.Cancelled; } }
    }
    internal sealed class DrainCell
    {
        internal readonly StableDrain Host;
        internal readonly long Id, Generation;
        internal IDrainWork Work;
        internal LinkedListNode<DrainCell> Node;
        internal DrainPhase Phase;
        internal bool Busy, Cancelled;
        internal DrainCell(StableDrain host, long id, long generation, IDrainWork work)
        { Host = host; Id = id; Generation = generation; Work = work; Phase = DrainPhase.Reserved; }
    }

    public sealed class StableDrain
    {
        // Load this type only once from the process-lifetime cold assembly, on the main thread.
        public static readonly StableDrain Shared = new StableDrain();
        private readonly int mainThreadId;
        private readonly LinkedList<DrainCell> live = new LinkedList<DrainCell>();
        private long nextId, generation;
        private bool admissionOpen, pumping, quarantineStop;
        private string lastFaultType, lastFaultMessage;
        private long faults;
        private StableDrain() { mainThreadId = Thread.CurrentThread.ManagedThreadId; }
        internal void Main()
        { if (Thread.CurrentThread.ManagedThreadId != mainThreadId) throw new InvalidOperationException("Main thread required"); }
        public int Pending { get { Main(); return live.Count; } }
        public long FaultCount { get { Main(); return faults; } }
        internal bool OwnershipAdmissionStopped { get { Main(); return quarantineStop; } }
        internal void StopForOwnershipFault(Exception error)
        {
            Main(); quarantineStop = true; admissionOpen = false; faults++;
            lastFaultType = error.GetType().FullName;
            string message = error.Message ?? "";
            lastFaultMessage = message.Length <= 256 ? message : message.Substring(0, 256);
        }
        public string LastFaultType { get { Main(); return lastFaultType; } }
        public string LastFaultMessage { get { Main(); return lastFaultMessage; } }
        public long OpenAdmission()
        { Main(); generation = checked(generation + 1); admissionOpen = true; return generation; }
        public void CloseAdmission(long token)
        { Main(); if (token == generation) admissionOpen = false; }

        // Allocate/link handle and work BEFORE any backend acquisition or native side effect.
        public DrainHandle Reserve(long token, IDrainWork work)
        {
            Main(); if (!admissionOpen || quarantineStop || token != generation) throw new InvalidOperationException("Admission closed, stale or quarantined");
            if (work == null) throw new ArgumentNullException("work");
            long id = checked(nextId + 1);
            var cell = new DrainCell(this, id, token, work);
            var handle = new DrainHandle(cell);
            var node = new LinkedListNode<DrainCell>(cell);
            cell.Node = node; live.AddLast(node); nextId = id; return handle;
        }
        private DrainCell Require(DrainHandle handle)
        {
            Main(); if (handle == null || !ReferenceEquals(handle.Cell.Host, this)) throw new InvalidOperationException("Foreign drain handle");
            return handle.Cell;
        }
        public void Start(DrainHandle handle)
        {
            DrainCell cell = Require(handle);
            if (cell.Phase != DrainPhase.Reserved || cell.Busy) throw new InvalidOperationException("Work already started");
            if (cell.Cancelled) { cell.Phase = DrainPhase.NoUnitTerminal; Drop(cell); return; }
            cell.Busy = true; cell.Phase = DrainPhase.Acquiring;
            try { Accept(cell, cell.Work.Begin()); }
            catch (Exception error) { Fault(cell, error); }
            finally { cell.Busy = false; }
            if (cell.Cancelled && cell.Work != null && cell.Phase != DrainPhase.Quarantined) Advance(cell);
            Finish(cell);
        }
        public void Cancel(DrainHandle handle)
        {
            CancelCell(Require(handle));
        }
        private void CancelCell(DrainCell cell)
        {
            if (cell.Work == null || cell.Phase == DrainPhase.Quarantined) return; // Already transferred/terminal has no return authority.
            cell.Cancelled = true;
            if (cell.Busy) return; // Reentrant cancellation must not modify an in-progress return/registration.
            if (cell.Phase == DrainPhase.Reserved) { cell.Phase = DrainPhase.NoUnitTerminal; Drop(cell); return; }
            Advance(cell); Finish(cell);
        }
        public void CancelGeneration(long token)
        {
            Main(); var snapshot = new DrainCell[live.Count]; int count = 0;
            // Capture/allocate before returns: a native callback can remove the next live-list node.
            foreach (DrainCell cell in live) if (cell.Generation == token) snapshot[count++] = cell;
            for (int i = 0; i < count; i++) CancelCell(snapshot[i]);
        }
        // A transfer consumes the only service ticket; old handles cannot release the receiver's unit.
        public IDrainWork TakePrepared(DrainHandle handle)
        {
            DrainCell cell = Require(handle);
            if (cell.Phase != DrainPhase.Prepared || cell.Cancelled || cell.Busy || cell.Work == null)
                throw new InvalidOperationException("Prepared work not publishable");
            IDrainWork work = cell.Work; cell.Phase = DrainPhase.Transferred; Drop(cell); return work;
        }
        public void Pump()
        {
            Main(); if (pumping) return;
            pumping = true;
            try
            {
                var node = live.First; int remaining = live.Count;
                while (node != null && remaining-- > 0)
                {
                    var next = node.Next; DrainCell cell = node.Value;
                    if (!cell.Busy && cell.Work != null && cell.Phase != DrainPhase.Reserved && cell.Phase != DrainPhase.Quarantined)
                    { Advance(cell); Finish(cell); }
                    node = next;
                }
            }
            finally { pumping = false; }
        }
        private void Advance(DrainCell cell)
        {
            cell.Busy = true;
            try
            {
                if (cell.Cancelled) cell.Work.Cancel();
                Accept(cell, cell.Work.Poll());
            }
            catch (Exception error) { Fault(cell, error); }
            finally { cell.Busy = false; }
        }
        private void Finish(DrainCell cell)
        { if (cell.Phase == DrainPhase.Returned || cell.Phase == DrainPhase.NoUnitTerminal) Drop(cell); }
        private void Accept(DrainCell cell, DrainPhase phase)
        {
            // A backend can quarantine a partially executed return without propagating its exception.
            // That outcome must close admission just like a thrown acquisition/poll fault.
            if (phase == DrainPhase.Quarantined)
                Fault(cell, new InvalidOperationException("Backend work entered quarantine"));
            else cell.Phase = phase;
        }
        private void Drop(DrainCell cell)
        {
            if (cell.Node != null) { live.Remove(cell.Node); cell.Node = null; }
            cell.Work = null; // Completed external handles retain only numeric state and the thin service.
        }
        private void Fault(DrainCell cell, Exception error)
        {
            cell.Phase = DrainPhase.Quarantined;
            StopForOwnershipFault(error);
            try { cell.Work.Quarantine(); }
            catch { } // The first ambiguous obligation stays live; never retry a backend side effect.
        }
    }
}
