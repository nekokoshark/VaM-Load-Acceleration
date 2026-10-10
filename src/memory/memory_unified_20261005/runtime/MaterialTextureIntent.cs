// Integrate into the existing single VaM.Memory assembly. C# 3/.NET 3.5 APIs.
// NOT a standalone auto-install plugin. Observe-only by default.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class MaterialTextureIntent
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;
        private const string PatchId = "vam.memory.material-texture-intent";
        private const int MaxRequests = 1024, MaxOwners = 128, EventCapacity = 1024;
        private static readonly List<Owner> Owners = new List<Owner>();
        private static readonly List<Request> Requests = new List<Request>();
        private static readonly Event[] Events = new Event[EventCapacity];
        private static readonly MethodInfo[] Receivers = new MethodInfo[6];
        private static readonly FieldInfo[] Urls = new FieldInfo[6];
        private static readonly FieldInfo[] CurrentTextures = new FieldInfo[6];
        private static FieldInfo StringValue, RawImage, WebRequest;
        private static Harmony harmony;
        private static int mainThread, eventCursor, finishDepth;
        private static long serial, observed, approved, rejected, unknown, overflow, eventLoss;
        private static long tracked, completed, candidateBytes, largestCandidateBytes, reportAt;
        private static string lastReport;
        private static bool accepting, enforce, stopping;
        [ThreadStatic] private static ImageLoaderThreaded.QueuedImage nativeCompletion;

        private sealed class Owner
        {
            internal WeakReference Target;
            internal long Id;
            internal readonly long[] Latest = new long[6];
            internal readonly string[] Paths = new string[6];
            internal readonly bool[] Protected = new bool[6];
            internal readonly long[] Published = new long[6];
            internal readonly WeakReference[] PublishedTextures = new WeakReference[6];
            internal int RequestCount;
        }
        private sealed class Request
        {
            internal WeakReference Image, Callback;
            internal Owner Owner;
            internal string Path;
            internal long Id;
            internal int Slot, Flags;
            internal float BumpStrength;
            internal bool Rejected, Observed;
        }
        // Action: 1=newer intent only (never cancelled),
        // 2=newer publication witnessed (observe only), 3=rejected before CreateTexture.
        internal struct Event
        {
            internal long Sequence, Qpc, RequestId, OwnerId, LatestId;
            // PayloadBytes is a layout estimate for ONE texture, not CPU+GPU+commit.
            internal long PayloadBytes;
            internal int Slot, Width, Height, Format, Action;
        }
        internal static string Snapshot()
        {
            return "mode=" + (enforce ? "enforce" : "observe") + " tracked=" + tracked + " completed=" + completed +
                " newerPending=" + (observed - approved) + " eligible=" + approved +
                " eligiblePayloadBytes=" + candidateBytes + " largestPayloadBytes=" + largestCandidateBytes +
                " retired=" + rejected + " unknown=" + unknown + " bypass=" + overflow +
                " pending=" + Requests.Count + " owners=" + Owners.Count + " eventLoss=" + eventLoss;
        }
        internal static void Tick()
        {
            if (harmony == null || !OnMainThread()) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < reportAt) return;
            reportAt = now + TimeSpan.TicksPerSecond * 5;
            Prune();
            string report = Snapshot();
            if (report == lastReport) return;
            lastReport = report;
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[texture-intent] " + report);
        }
        internal static long ObservedCandidates { get { return observed; } }
        internal static long ApprovedCandidates { get { return approved; } }
        internal static long RejectedBeforeCreate { get { return rejected; } }
        internal static long UnknownOrChanged { get { return unknown; } }
        internal static long CapacityBypasses { get { return overflow; } }
        internal static long LostEvents { get { return eventLoss; } }
        internal static int PendingRecords { get { return Requests.Count; } }
        private static bool OnMainThread() { return Thread.CurrentThread.ManagedThreadId == mainThread; }
        private static HarmonyMethod Hook(string name)
        { return new HarmonyMethod(typeof(MaterialTextureIntent).GetMethod(name, All)); }
        private static Request Find(ImageLoaderThreaded.QueuedImage q)
        {
            for (int i = 0; i < Requests.Count; i++)
                if (ReferenceEquals(Requests[i].Image.Target, q)) return Requests[i];
            return null;
        }
        private static Owner FindOwner(object obj)
        {
            for (int i = 0; i < Owners.Count; i++)
                if (ReferenceEquals(Owners[i].Target.Target, obj)) return Owners[i];
            return null;
        }
        private static int SlotOf(ImageLoaderThreaded.ImageLoaderCallback cb, object owner)
        {
            if (cb == null) return -1;
            Delegate[] list = cb.GetInvocationList();
            if (list.Length != 1 || !ReferenceEquals(list[0].Target, owner)) return -1;
            for (int i = 0; i < 6; i++) if (Equals(list[0].Method, Receivers[i])) return i;
            return -1;
        }
        private static int Flags(ImageLoaderThreaded.QueuedImage q)
        {
            return (q.compress ? 1 : 0) | (q.linear ? 2 : 0) |
                (q.createMipMaps ? 4 : 0) | (q.isNormalMap ? 8 : 0) |
                (q.createAlphaFromGrayscale ? 16 : 0) | (q.createNormalFromBump ? 32 : 0) |
                (q.invert ? 64 : 0) | (q.setSize ? 128 : 0) | (q.fillBackground ? 256 : 0);
        }
        private static bool Ordinary(ImageLoaderThreaded.QueuedImage q)
        {
            return !q.forceReload && !q.skipCache && !q.cancel && !q.hadError &&
                !q.isThumbnail && !q.isPreload && !q.useWebCache &&
                RawImage.GetValue(q) == null && WebRequest.GetValue(q) == null &&
                (q.imgPath == null || (q.imgPath.Length <= 1024 &&
                 q.imgPath.IndexOf("://", StringComparison.Ordinal) < 0 &&
                 q.imgPath.IndexOf(".latest:", StringComparison.Ordinal) < 0));
        }
        // Original QueueImage runs exactly once before registration. Unknown
        // QueueImage patches must be excluded by the local effective-patch audit.
        private static void QueueOwned(ImageLoaderThreaded loader,
            ImageLoaderThreaded.QueuedImage q, MaterialOptions owner)
        {
            loader.QueueImage(q);
            if (!OnMainThread() || !accepting || q == null || ReferenceEquals(owner, null)) return;
            try
            {
                Prune();
                int slot = SlotOf(q.callback, owner);
                if (slot < 0)
                {
                    // A custom/multicast callback cannot establish a slot witness.
                    // Fence any existing owner so an earlier publication cannot
                    // authorize retirement across this unclassified request.
                    Owner ambiguous = FindOwner(owner);
                    if (ambiguous != null)
                        for (int i = 0; i < 6; i++) ambiguous.Protected[i] = true;
                    unknown++;
                    return;
                }
                Owner o = FindOwner(owner);
                if (o == null)
                {
                    if (Owners.Count >= MaxOwners) { overflow++; return; }
                    o = new Owner { Target = new WeakReference(owner), Id = checked(++serial) };
                    Owners.Add(o);
                }
                // Even a non-admitted request fences the old slot. Do not apply
                // last-wins policy across forceReload/multicast/foreign behavior.
                o.Latest[slot] = checked(++serial);
                o.Paths[slot] = q.imgPath == null || q.imgPath.Length <= 1024 ? q.imgPath : null;
                o.Protected[slot] = true;
                if (owner.GetType().Assembly != typeof(MaterialOptions).Assembly ||
                    !Ordinary(q) || !ReferenceEquals(q.tex, null) || q.processed || q.finished ||
                    Requests.Count >= MaxRequests)
                { overflow++; return; }
                Request r = new Request { Image = new WeakReference(q),
                    Callback = new WeakReference(q.callback), Owner = o, Slot = slot,
                    Id = o.Latest[slot], Path = q.imgPath, Flags = Flags(q), BumpStrength = q.bumpStrength };
                Requests.Add(r); o.RequestCount++; tracked++;
                o.Protected[slot] = false;
            }
            catch (Exception)
            {
                // Only bookkeeping follows QueueImage; never retry the queue call.
                Owner o = FindOwner(owner);
                if (o != null) for (int i = 0; i < 6; i++) o.Protected[i] = true;
                unknown++;
            }
        }
        // Called ONLY at the original PostProcessCompletedImages -> Finish site.
        // A worker has returned from Process and handed q through _processedLock.
        private static void FinishFromNativeQueue(ImageLoaderThreaded.QueuedImage q)
        {
            ImageLoaderThreaded.QueuedImage saved = nativeCompletion;
            nativeCompletion = q; finishDepth++;
            try { q.Finish(); }
            finally { finishDepth--; nativeCompletion = saved; }
        }
        // Last-priority Prefix on Finish, after both DecodeBudget's snapshot
        // and deployed TextureUploadReuse. A cache reuse is NOT our saving.
        private static void AfterReuse(ImageLoaderThreaded.QueuedImage __instance)
        {
            ImageLoaderThreaded.QueuedImage q = __instance;
            if (!OnMainThread() || !ReferenceEquals(nativeCompletion, q) || q == null) return;
            Request r = Find(q);
            if (r == null || r.Rejected || stopping) return;
            try
            {
                MaterialOptions owner = r.Owner.Target.Target as MaterialOptions;
                if (owner == null) return; // Already handled by deployed stale-receiver guard.
                if (r.Owner.Protected[r.Slot] || r.Id >= r.Owner.Latest[r.Slot] ||
                    String.Equals(r.Path, r.Owner.Paths[r.Slot], StringComparison.Ordinal)) return;
                if (!ReferenceEquals(q.callback, r.Callback.Target) || Flags(q) != r.Flags || q.bumpStrength != r.BumpStrength ||
                    !String.Equals(q.imgPath, r.Path, StringComparison.Ordinal) ||
                    !Ordinary(q) || !q.processed || !q.preprocessed || q.finished ||
                    !ReferenceEquals(q.tex, null)) { unknown++; return; }
                // Raw backing field only: no JSON/Unity getter initialization.
                object url = Urls[r.Slot].GetValue(owner);
                if (url == null) { unknown++; return; }
                string actual = (string)StringValue.GetValue(url);
                string latest = r.Owner.Paths[r.Slot];
                if (!(String.Equals(actual, latest, StringComparison.Ordinal) ||
                    (latest == null && actual == String.Empty))) { unknown++; return; }
                int format = (int)q.textureFormat;
                if (q.compress && format != 10 && format != 12) { unknown++; return; }
                long bytes = PayloadBytes(q.width, q.height, format, q.createMipMaps);
                long input = q.raw != null ? q.raw.LongLength : NativeCacheBuffer.StagedLength(q);
                // A truncated/error-prone input is not silently converted to success.
                if (bytes <= 0 || input != bytes) { unknown++; return; }
                // Pending newer intent alone is diagnostic, not permission.
                // A newer successful publication and the same actual slot value
                // are required. A failed/pending replacement retains original fallback.
                WeakReference published = r.Owner.PublishedTextures[r.Slot];
                Texture2D winner = published == null ? null : published.Target as Texture2D;
                bool winnerPublished = r.Owner.Published[r.Slot] == r.Owner.Latest[r.Slot] &&
                    winner != null &&
                    ReferenceEquals(CurrentTextures[r.Slot].GetValue(owner), winner);
                if (!r.Observed)
                {
                    r.Observed = true; observed++;
                    if (winnerPublished) approved++;
                    Record(r, q, bytes, winnerPublished ? (enforce ? 3 : 2) : 1);
                }
                if (!enforce || !winnerPublished) return;
                // No Texture2D exists; no material, public texture state or count is changed.
                // Do not set cancel: native progress, Finish and DoCallback must all drain.
                r.Rejected = true; rejected++;
                q.raw = null; q.finished = true; q.skipCache = true;
            }
            catch (Exception) { unknown++; } // Classification failure retains native path.
        }
        // An explicitly rejected request has no result. Keep DoCallback itself,
        // but do not let its known receiver assign null to a currently used slot.
        private static bool ReceiverPrefix(MaterialOptions __instance,
            ImageLoaderThreaded.QueuedImage __0)
        {
            if (!OnMainThread() || __0 == null) return true;
            Request r = Find(__0);
            if (r == null || !r.Rejected) return true;
            if (!ReferenceEquals(__instance, r.Owner.Target.Target) ||
                !ReferenceEquals(__0.tex, null) || !__0.finished || !__0.skipCache ||
                !ReferenceEquals(__0.callback, r.Callback.Target))
            { unknown++; return true; } // Post-decision external mutation: no destructive guess.
            return false;
        }
        // Run as finalizer: original receiver or another patch can throw/reenter.
        // Successful publication is a witness, not a second texture acquisition.
        private static Exception ReceiverDone(MaterialOptions __instance,
            ImageLoaderThreaded.QueuedImage __0, Exception __exception)
        {
            if (!OnMainThread() || __0 == null) return __exception;
            try
            {
                Request r = Find(__0);
                if (r == null || r.Rejected) return __exception;
                Owner o = r.Owner; int slot = r.Slot;
                if (__exception != null || __0.hadError ||
                    !ReferenceEquals(__instance, o.Target.Target) ||
                    !ReferenceEquals(__0.callback, r.Callback.Target))
                { o.Protected[slot] = true; return __exception; }
                if (r.Id != o.Latest[slot] || o.Protected[slot]) return __exception;
                if (ReferenceEquals(__0.tex, null) ||
                    !ReferenceEquals(CurrentTextures[slot].GetValue(__instance), __0.tex))
                { o.Protected[slot] = true; return __exception; }
                o.PublishedTextures[slot] = new WeakReference(__0.tex);
                o.Published[slot] = r.Id;
            }
            catch (Exception) { unknown++; }
            return __exception;
        }
        private static void RemoveAt(int index)
        {
            Requests[index].Owner.RequestCount--;
            Requests.RemoveAt(index);
        }
        private static Exception CallbackDone(ImageLoaderThreaded.QueuedImage __instance,
            Exception __exception)
        {
            if (OnMainThread() && Requests.Count != 0) try
            {
                Request r = Find(__instance);
                if (r != null)
                {
                    completed++;
                    if (__exception != null) r.Owner.Protected[r.Slot] = true;
                    int i = Requests.IndexOf(r); if (i >= 0) RemoveAt(i);
                }
                Prune();
            }
            catch (Exception) { unknown++; }
            return __exception;
        }
        private static void Prune()
        {
            for (int i = Requests.Count - 1; i >= 0; i--)
                if (!Requests[i].Image.IsAlive) RemoveAt(i);
            for (int i = Owners.Count - 1; i >= 0; i--)
                if (Owners[i].RequestCount == 0) Owners.RemoveAt(i);
        }
        private static void Record(Request r, ImageLoaderThreaded.QueuedImage q, long bytes, int action)
        {
            if (action == 2 || action == 3)
            { candidateBytes += bytes; largestCandidateBytes = Math.Max(largestCandidateBytes, bytes); }
            long seq = checked(observed);
            if (Events[eventCursor].Sequence != 0) eventLoss++;
            Events[eventCursor] = new Event { Sequence = seq,
                Qpc = System.Diagnostics.Stopwatch.GetTimestamp(), RequestId = r.Id,
                OwnerId = r.Owner.Id, LatestId = r.Owner.Latest[r.Slot], Slot = r.Slot + 1,
                Width = q.width, Height = q.height, Format = (int)q.textureFormat,
                PayloadBytes = bytes, Action = action };
            eventCursor = (eventCursor + 1) % EventCapacity;
        }
        internal static Event[] ReadAndClearEvents()
        {
            if (!OnMainThread()) throw new InvalidOperationException("Main thread only");
            var copy = new List<Event>();
            for (int n = 0; n < EventCapacity; n++)
            { int i = (eventCursor + n) % EventCapacity;
              if (Events[i].Sequence != 0) copy.Add(Events[i]); Events[i] = new Event(); }
            return copy.ToArray();
        }
        internal static long PayloadBytes(int w, int h, int format, bool mips)
        {
            if (w < 1 || h < 1 || w > 32768 || h > 32768) return -1;
            int b = format == 1 ? 1 : format == 3 ? 3 :
                (format == 4 || format == 5) ? 4 : 0;
            if (b == 0 && format != 10 && format != 12) return -1;
            long sum = 0;
            checked
            {
                do
                {
                    sum += b != 0 ? (long)w * h * b :
                        ((long)w + 3) / 4 * (((long)h + 3) / 4) * (format == 10 ? 8 : 16);
                    if (!mips || (w == 1 && h == 1)) return sum;
                    w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                } while (true);
            }
        }
        private static IEnumerable<CodeInstruction> QueueTranspiler(IEnumerable<CodeInstruction> code)
        {
            var list = new List<CodeInstruction>(code); int hits = 0;
            MethodInfo call = typeof(ImageLoaderThreaded).GetMethod("QueueImage", All,
                null, new Type[] { typeof(ImageLoaderThreaded.QueuedImage) }, null);
            for (int i = 0; i < list.Count; i++)
                if (Equals(list[i].operand, call))
                {
                    if (list[i].blocks.Count != 0) throw new InvalidOperationException("Queue EH anchor");
                    CodeInstruction old = list[i];
                    var load = new CodeInstruction(OpCodes.Ldarg_0);
                    load.labels.AddRange(old.labels); old.labels.Clear();
                    list.Insert(i++, load);
                    old.opcode = OpCodes.Call; old.operand = typeof(MaterialTextureIntent).GetMethod("QueueOwned", All);
                    hits++;
                }
            if (hits != 1) throw new InvalidOperationException("Expected one MaterialOptions QueueImage call");
            return list;
        }
        private static IEnumerable<CodeInstruction> PostTranspiler(IEnumerable<CodeInstruction> code)
        {
            var list = new List<CodeInstruction>(code); int hits = 0;
            MethodInfo finish = typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Finish", All);
            foreach (CodeInstruction c in list) if (Equals(c.operand, finish))
            {
                c.opcode = OpCodes.Call;
                c.operand = typeof(MaterialTextureIntent).GetMethod("FinishFromNativeQueue", All); hits++;
            }
            if (hits != 1) throw new InvalidOperationException("Expected one native completion Finish call");
            return list;
        }
        internal static void InstallObserveOnly()
        {
            if (harmony != null) return;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            for (int i = 0; i < 6; i++)
            {
                Receivers[i] = typeof(MaterialOptions).GetMethod("OnTexture" + (i + 1) + "Loaded", All,
                    null, new Type[] { typeof(ImageLoaderThreaded.QueuedImage) }, null);
                Urls[i] = typeof(MaterialOptions).GetField("customTexture" + (i + 1) + "UrlJSON", All);
                CurrentTextures[i] = typeof(MaterialOptions).GetField("customTexture" + (i + 1), All);
                if (Receivers[i] == null || Receivers[i].ReturnType != typeof(void) ||
                    Urls[i] == null || Urls[i].FieldType != typeof(JSONStorableUrl) ||
                    CurrentTextures[i] == null || CurrentTextures[i].FieldType != typeof(Texture2D))
                    throw new MissingMemberException("MaterialOptions texture slot");
            }
            StringValue = typeof(JSONStorableString).GetField("_val", All);
            RawImage = typeof(ImageLoaderThreaded.QueuedImage).GetField("rawImageToLoad", All);
            WebRequest = typeof(ImageLoaderThreaded.QueuedImage).GetField("webRequest", All);
            if (StringValue == null || StringValue.FieldType != typeof(string) ||
                RawImage == null || WebRequest == null) throw new MissingFieldException("Request/value layout");
            harmony = new Harmony(PatchId);
            try
            {
                harmony.Patch(typeof(MaterialOptions).GetMethod("QueueCustomTexture", All),
                    transpiler: Hook("QueueTranspiler"));
                // Existing TextureCompletionBudget must run before this transpiler;
                // it locates the ORIGINAL Finish call for its own Charge insertion.
                HarmonyMethod post = Hook("PostTranspiler");
                post.after = new string[] { "Quest3TriggerUI.texture-completion-budget" };
                harmony.Patch(typeof(ImageLoaderThreaded).GetMethod("PostProcessCompletedImages", All),
                    transpiler: post);
                HarmonyMethod finishGuard = Hook("AfterReuse");
                finishGuard.priority = Priority.Last;
                finishGuard.after = new string[] {
                    "Quest3TriggerUI.texture-decode-budget",
                    "Quest3TriggerUI.texture-upload-reuse",
                    "Quest3TriggerUI.stale-texture-request" };
                harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Finish", All),
                    prefix: finishGuard);
                for (int i = 0; i < 6; i++) harmony.Patch(Receivers[i],
                    prefix: Hook("ReceiverPrefix"), finalizer: Hook("ReceiverDone"));
                harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("DoCallback", All),
                    finalizer: Hook("CallbackDone"));
                stopping = false; accepting = true; enforce = false;
            }
            catch { harmony.UnpatchAll(PatchId); harmony = null; throw; }
        }
        // Deliberately NO automatic enable. The implementation is a compiled-in
        // adapter; call only after the local real-method and patch-composition
        // verification described in INTEGRATION.md. This is not an attestation.
        internal static void SetEnforcementAfterLocalVerification(bool enabled)
        {
            if (!OnMainThread() || harmony == null || stopping) throw new InvalidOperationException("Inactive adapter");
            enforce = enabled;
        }
        internal static void StopAccepting()
        {
            if (harmony == null) return;
            if (!OnMainThread()) throw new InvalidOperationException("Main thread only");
            accepting = false; enforce = false; stopping = true;
        }
        internal static bool TryDetachAfterDrain()
        {
            if (!OnMainThread()) throw new InvalidOperationException("Main thread only");
            if (!stopping || finishDepth != 0) return false;
            Prune();
            foreach (Request r in Requests) if (r.Rejected && r.Image.IsAlive) return false;
            if (harmony != null) harmony.UnpatchAll(PatchId);
            harmony = null; Requests.Clear(); Owners.Clear(); return true;
        }
    }
}
