using System;

namespace StartupPackageJson {
    // Parse-local storage. Every published string is a fresh copy, so neither
    // later appends nor resets can mutate an existing JSON key or value.
    internal sealed class ReusableTokens {
        private char[] buffer = new char[16];
        private int length;
        internal int Length {
            get { return length; }
            set {
                if (value != 0) throw new ArgumentOutOfRangeException("value");
                length = 0;
            }
        }
        private void Reserve(int needed) {
            if (needed <= buffer.Length) return;
            int capacity = buffer.Length <= int.MaxValue / 2 ? buffer.Length * 2 : int.MaxValue;
            if (capacity < needed) capacity = needed;
            Array.Resize(ref buffer, capacity);
        }
        internal void Append(char value) {
            if (length == int.MaxValue) throw new OutOfMemoryException();
            Reserve(length + 1);
            buffer[length++] = value;
        }
        internal void Append(ReusableTokens other) {
            int needed = checked(length + other.length);
            Reserve(needed);
            Array.Copy(other.buffer, 0, buffer, length, other.length);
            length = needed;
        }
        public override string ToString() {
            return new string(buffer, 0, length);
        }
    }
}
