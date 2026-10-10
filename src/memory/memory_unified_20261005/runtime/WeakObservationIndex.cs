using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Quest3TriggerUI
{
    // Per-observation identity book. A key is never a resource ownership edge.
    // Values used here are bools or path/count metadata, not sampled resources.
    internal sealed class WeakObservationIndex<T>
    {
        private sealed class Entry
        {
            internal WeakReference Key;
            internal T Value;
            internal Entry Next;
        }

        private readonly Dictionary<int, Entry> buckets = new Dictionary<int, Entry>();
        private int count;

        // Includes dead keys: historical rows and admission caps stay stable
        // during a non-atomic scan. We do not reclaim budget to hide truncation.
        public int Count { get { return count; } }

        private Entry Find(object key)
        {
            if (ReferenceEquals(key, null)) throw new ArgumentNullException("key");
            Entry entry;
            if (buckets.TryGetValue(RuntimeHelpers.GetHashCode(key), out entry))
                for (; entry != null; entry = entry.Next)
                    if (ReferenceEquals(entry.Key.Target, key)) return entry;
            return null;
        }

        public bool ContainsKey(object key) { return Find(key) != null; }

        public bool TryGetValue(object key, out T value)
        {
            Entry entry = Find(key);
            value = entry == null ? default(T) : entry.Value;
            return entry != null;
        }

        public void Add(object key, T value)
        {
            if (Find(key) != null) throw new ArgumentException("Duplicate observation key", "key");
            int hash = RuntimeHelpers.GetHashCode(key);
            Entry head;
            buckets.TryGetValue(hash, out head);
            buckets[hash] = new Entry { Key = new WeakReference(key), Value = value, Next = head };
            count++;
        }

        public T this[object key]
        {
            set
            {
                Entry entry = Find(key);
                if (entry == null) Add(key, value);
                else entry.Value = value;
            }
        }

        // Emit recorded metadata even when a weak key dies before report time.
        // Never yield Key.Target or preserve it in an enumerator state machine.
        public IEnumerable<T> Values
        {
            get
            {
                foreach (Entry head in buckets.Values)
                    for (Entry entry = head; entry != null; entry = entry.Next)
                        yield return entry.Value;
            }
        }

        public void Clear() { buckets.Clear(); count = 0; }
    }

    internal sealed class WeakObservationSet
    {
        private readonly WeakObservationIndex<bool> index = new WeakObservationIndex<bool>();
        public int Count { get { return index.Count; } }
        public bool Contains(object key) { return index.ContainsKey(key); }
        public bool Add(object key)
        {
            if (index.ContainsKey(key)) return false;
            index.Add(key, true);
            return true;
        }
        public void Clear() { index.Clear(); }
    }
}
