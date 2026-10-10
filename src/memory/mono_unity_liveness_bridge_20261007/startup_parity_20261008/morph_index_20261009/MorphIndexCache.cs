using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace StartupMorphIndex {
    // Cache only the original consumer's eleven scalar fields. Never retain a
    // bank, a DAZMorph, a formula, JSON nodes, a GameObject or native resources.
    public static class ScalarCache {
        public const int MaxPayload = 16 * 1024 * 1024;
        private const int Magic = 0x534d4931;
        public static readonly string[] Names = { "_uid", "displayName", "isInPackage", "version", "packageUid",
            "packageLicense", "overrideName", "morphName", "startValue", "min", "max" };
        private static readonly Type[] Types = { typeof(string), typeof(string), typeof(bool), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(float), typeof(float), typeof(float) };

        public static FieldInfo[] Schema(Type type) {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fields.Length != Names.Length) throw new InvalidDataException("Scalar map field count changed");
            var ordered = new FieldInfo[Names.Length];
            for (int i = 0; i < Names.Length; i++) {
                ordered[i] = type.GetField(Names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ordered[i] == null || ordered[i].FieldType != Types[i]) throw new InvalidDataException("Scalar map field changed: " + Names[i]);
            }
            return ordered;
        }
        public static void String(BinaryWriter writer, string value) {
            // Preserve null/empty, UTF-16 code units (including lone surrogates),
            // and embedded NUL. BinaryWriter's UTF-8 string codec would lose this.
            writer.Write(value == null ? -1 : value.Length);
            if (value != null) foreach (char c in value) writer.Write((ushort)c);
        }
        private static string String(BinaryReader reader) {
            int length = reader.ReadInt32();
            if (length == -1) return null;
            if (length < 0 || length > 1024 * 1024 || (long)length * 2 > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid scalar string length");
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = (char)reader.ReadUInt16();
            return new string(chars);
        }
        public static byte[] Payload(IList list, int offset, FieldInfo[] fields) {
            if (offset < 0 || offset > list.Count || list.Count - offset > 250000) throw new InvalidDataException("Invalid scalar row count");
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(list.Count - offset);
                    for (int row = offset; row < list.Count; row++) {
                        for (int f = 0; f < fields.Length; f++) {
                            object value = fields[f].GetValue(list[row]);
                            if (Types[f] == typeof(string)) String(writer, (string)value);
                            else if (Types[f] == typeof(bool)) writer.Write((bool)value);
                            else writer.Write((float)value);
                        }
                        if (stream.Length > MaxPayload) throw new InvalidDataException("Scalar index exceeds bound");
                    }
                    writer.Flush();
                    return stream.ToArray();
                }
            }
        }
        public static string Digest(byte[] data) {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
        public static void Write(string path, string key, byte[] payload) {
            if (payload.Length > MaxPayload) throw new InvalidDataException("Scalar index exceeds bound");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".writing";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream)) {
                writer.Write(Magic); String(writer, key); String(writer, Digest(payload));
                writer.Write(payload.Length); writer.Write(payload);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        public static bool Read(string path, string key, Type type, FieldInfo[] fields, out ArrayList rows, out string digest) {
            rows = null; digest = null;
            if (!File.Exists(path)) return false;
            try {
                byte[] payload;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream)) {
                    if (stream.Length > MaxPayload + 4096 || reader.ReadInt32() != Magic || String(reader) != key) return false;
                    string expected = String(reader);
                    int bytes = reader.ReadInt32();
                    if (bytes < 4 || bytes > MaxPayload || bytes != stream.Length - stream.Position) return false;
                    payload = reader.ReadBytes(bytes);
                    digest = Digest(payload);
                    if (digest != expected) return false;
                }
                var restored = new ArrayList();
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream)) {
                    int count = reader.ReadInt32();
                    if (count < 0 || count > 250000 || (long)count * 41 > stream.Length - 4) return false;
                    for (int row = 0; row < count; row++) {
                        object value = Activator.CreateInstance(type, true);
                        for (int f = 0; f < fields.Length; f++) {
                            object scalar;
                            if (Types[f] == typeof(string)) scalar = String(reader);
                            else if (Types[f] == typeof(bool)) scalar = reader.ReadBoolean();
                            else scalar = reader.ReadSingle();
                            fields[f].SetValue(value, scalar);
                        }
                        restored.Add(value);
                    }
                    if (stream.Position != stream.Length) return false;
                }
                rows = restored;
                return true;
            } catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidDataException) { return false; }
        }
        public static void Append(IList destination, IList rows) {
            int before = destination.Count;
            try { foreach (object row in rows) destination.Add(row); }
            catch {
                while (destination.Count > before) destination.RemoveAt(destination.Count - 1);
                throw;
            }
        }
    }
}
