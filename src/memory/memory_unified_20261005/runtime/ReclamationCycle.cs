namespace VaM.Memory
{
    // Numeric receipts only: no scene, request, array or resource roots.
    internal sealed class ReclamationCycle
    {
        internal enum Work { None, Sweep, Collect }
        private long requested, gcRequested, collected, sweepRequested, swept, sweepReceipt, janitorRequested;
        private long seenActivity;
        private int readyFrame = -1;
        private bool sweeping, stopped;
        internal bool Pending { get { return !stopped && (gcRequested > collected || sweepRequested > swept || sweeping); } }
        internal long CollectionReceipt { get; private set; }
        internal long SweepReceipt { get { return swept; } }
        internal bool JanitorSweep { get; private set; }
        internal long Completed { get { return collected; } }

        internal bool Request(bool sweep, bool gc)
        {
            if (stopped || (!sweep && !gc)) return false;
            requested++;
            if (gc) gcRequested = requested;
            if (sweep) sweepRequested = requested;
            readyFrame = -1;
            return true;
        }

        internal bool RequestJanitor()
        {
            // Janitor originally requested UUA only; never invent a GC request.
            if (!Request(true, false)) return false;
            janitorRequested = requested;
            return true;
        }

        internal Work Next(bool ready, long activity, int frame, bool operationDone)
        {
            if (stopped) return Work.None;
            if (sweeping)
            {
                if (!operationDone) return Work.None;
                swept = sweepReceipt;
                sweeping = false;
                readyFrame = -1;
            }
            if (!Pending || !ready) { readyFrame = -1; return Work.None; }
            // Distinct main-thread frames, not elapsed standby time. A late
            // callback or a new request invalidates the previous observation.
            if (readyFrame < 0 || seenActivity != activity)
            { seenActivity = activity; readyFrame = frame; return Work.None; }
            if (frame == readyFrame) return Work.None;
            readyFrame = -1;
            if (sweepRequested > swept)
            {
                sweepReceipt = sweepRequested;
                JanitorSweep = janitorRequested > swept;
                sweeping = true;
                return Work.Sweep;
            }
            CollectionReceipt = gcRequested;
            return Work.Collect;
        }

        internal void Collected(long receipt)
        {
            // New requests made by a callback during collection survive.
            if (receipt > collected) collected = receipt;
        }

        internal void Stop() { stopped = true; readyFrame = -1; }
    }
}
