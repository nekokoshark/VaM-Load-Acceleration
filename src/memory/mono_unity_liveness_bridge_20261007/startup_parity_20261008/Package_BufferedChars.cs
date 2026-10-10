using System;
using System.IO;
using System.Text;

namespace StartupPackageJson
{
    internal sealed class BufferedChars
    {
        internal const int Capacity = 16384;
        private readonly TextReader reader;
        private readonly char[] buffer = new char[Capacity];
        private int offset, length;
        private bool ended;
        internal bool Failed;

        internal BufferedChars(TextReader reader) { this.reader = reader; }

        internal int Next()
        {
            if (ended) return -1;
            if (offset == length)
            {
                try { length = reader.Read(buffer, 0, buffer.Length); }
                catch { Failed = true; throw; }
                offset = 0;
                if (length == 0) { ended = true; return -1; }
            }
            return buffer[offset++];
        }

        internal string UnicodeDigits()
        {
            var chars = new char[4];
            for (int i = 0; i < chars.Length; i++)
            {
                int next = Next();
                // Invoke the original Substring failure for a truncated \u.
                if (next < 0) return string.Empty.Substring(0, 4);
                chars[i] = (char)next;
            }
            return new string(chars);
        }

        internal void Drain()
        {
            if (ended) return;
            offset = length;
            while (reader.Read(buffer, 0, buffer.Length) != 0) { }
            ended = true;
        }
    }

}
