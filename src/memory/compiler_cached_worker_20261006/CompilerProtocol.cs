using System;
using System.Collections;
using System.Collections.Generic;
using System.CodeDom.Compiler;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mono.CSharp;

namespace VaM.CompilerIsolation
{
    // Wire format is shared by the parent and the original-Mono compiler process.
    public sealed class CompileRequest
    {
        public const int Magic = 0x56434D32;
        public string Directory, Output, Key;
        public bool Cacheable = true;
        public readonly List<string> Fields = new List<string>();
        public readonly List<string> Values = new List<string>();
        public readonly List<string> References = new List<string>();
        public readonly List<string> ReferenceIdentities = new List<string>();
        public readonly List<string> Defines = new List<string>();
        public readonly List<string> Names = new List<string>();
        public readonly List<string> Paths = new List<string>();
        public readonly List<byte[]> Sources = new List<byte[]>();
        static readonly BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo Symbols = typeof(CompilerSettings).GetField("conditional_symbols", All);

        public static CompileRequest Capture(CompilerSettings settings)
        {
            var r = new CompileRequest { Directory = System.IO.Directory.GetCurrentDirectory(), Output = settings.OutputFile };
            FieldInfo[] fields = typeof(CompilerSettings).GetFields(All);
            Array.Sort(fields, delegate(FieldInfo a, FieldInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            foreach (var f in fields)
            {
                if (f.Name == "OutputFile" || f.Name == "source_files" || f.Name == "conditional_symbols" || f.Name == "AssemblyReferences") continue;
                object value = f.GetValue(settings);
                if (f.FieldType == typeof(string) || f.FieldType == typeof(bool) || f.FieldType == typeof(int) || f.FieldType.IsEnum)
                {
                    r.Fields.Add(f.Name);
                    r.Values.Add(value == null ? null : (f.FieldType.IsEnum ? Convert.ToInt32(value).ToString(System.Globalization.CultureInfo.InvariantCulture) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
                }
                else if (f.FieldType == typeof(Encoding))
                {
                    if (!ReferenceEquals(value, Encoding.UTF8)) throw new NotSupportedException("Compiler encoding differs from original UTF8");
                }
                else
                {
                    // GetSettings supplies no resources/aliases/modules or per-warning overrides.
                    var collection = value as ICollection;
                    if (value != null && (collection == null || collection.Count != 0)) throw new NotSupportedException("Compiler setting: " + f.Name);
                }
            }
            var symbols = (List<string>)Symbols.GetValue(settings);
            if (symbols != null) r.Defines.AddRange(symbols);
            r.References.AddRange(settings.AssemblyReferences);
            foreach (SourceFile s in settings.SourceFiles)
            {
                r.Names.Add(s.Name); r.Paths.Add(s.FullPathName);
                try
                {
                    using (Stream input = s.GetDataStream())
                    using (var data = new MemoryStream())
                    {
                        byte[] buffer = new byte[32768]; int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) != 0) data.Write(buffer, 0, n);
                        r.Sources.Add(data.ToArray());
                    }
                }
                catch (IOException) { r.Sources.Add(null); r.Cacheable = false; }
                catch (UnauthorizedAccessException) { r.Sources.Add(null); r.Cacheable = false; }
            }
            if (settings.GenerateDebugInfo || settings.HasKeyFileOrContainer || settings.TargetExt != ".dll") r.Cacheable = false;
            return r;
        }

        public CompilerSettings Restore()
        {
            var settings = new CompilerSettings();
            for (int i = 0; i < Fields.Count; i++)
            {
                FieldInfo f = typeof(CompilerSettings).GetField(Fields[i], All);
                if (f == null) throw new InvalidDataException("Compiler settings schema");
                object v = Values[i];
                if (f.FieldType == typeof(bool)) v = bool.Parse(Values[i]);
                else if (f.FieldType == typeof(int)) v = int.Parse(Values[i], System.Globalization.CultureInfo.InvariantCulture);
                else if (f.FieldType.IsEnum) v = System.Enum.ToObject(f.FieldType, int.Parse(Values[i], System.Globalization.CultureInfo.InvariantCulture));
                f.SetValue(settings, v);
            }
            settings.OutputFile = Output; settings.Encoding = Encoding.UTF8;
            settings.AssemblyReferences.AddRange(References);
            foreach (string d in Defines) settings.AddConditionalSymbol(d);
            for (int i = 0; i < Sources.Count; i++)
            {
                byte[] source = Sources[i];
                var s = new SourceFile(Names[i], Paths[i], i + 1, delegate {
                    if (source == null) throw new FileNotFoundException("Source snapshot unavailable");
                    return new MemoryStream(source, false);
                });
                settings.SourceFiles.Add(s);
            }
            return settings;
        }

        public void MakeKey()
        {
            using (var hash = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteBody(w, false); w.Flush(); stream.FlushFinalBlock();
                Key = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        public void Write(BinaryWriter w) { w.Write(Magic); Text(w, Key); WriteBody(w, true); w.Flush(); }
        void WriteBody(BinaryWriter w, bool output)
        {
            Text(w, Directory); Text(w, output ? Output : "<per-load-identity>"); w.Write(Cacheable);
            w.Write(Fields.Count);
            for (int i = 0; i < Fields.Count; i++) { Text(w, Fields[i]); Text(w, Values[i]); }
            Strings(w, References); Strings(w, ReferenceIdentities); Strings(w, Defines);
            w.Write(Sources.Count);
            for (int i = 0; i < Sources.Count; i++) { Text(w, Names[i]); Text(w, Paths[i]); Bytes(w, Sources[i]); }
        }
        public static CompileRequest Read(BinaryReader reader)
        {
            int magic;
            try { magic = reader.ReadInt32(); } catch (EndOfStreamException) { return null; }
            if (magic != Magic) throw new InvalidDataException("Compiler request magic");
            var r = new CompileRequest { Key = Text(reader), Directory = Text(reader), Output = Text(reader), Cacheable = reader.ReadBoolean() };
            int count = Count(reader, 128);
            for (int i = 0; i < count; i++) { r.Fields.Add(Text(reader)); r.Values.Add(Text(reader)); }
            r.References.AddRange(Strings(reader)); r.ReferenceIdentities.AddRange(Strings(reader)); r.Defines.AddRange(Strings(reader));
            count = Count(reader, 4096);
            for (int i = 0; i < count; i++) { r.Names.Add(Text(reader)); r.Paths.Add(Text(reader)); r.Sources.Add(Bytes(reader)); }
            return r;
        }
        public static int Count(BinaryReader r, int max) { int n = r.ReadInt32(); if (n < 0 || n > max) throw new InvalidDataException("Compiler payload count"); return n; }
        public static void Text(BinaryWriter w, string s) { w.Write(s != null); if (s != null) w.Write(s); }
        public static string Text(BinaryReader r) { return r.ReadBoolean() ? r.ReadString() : null; }
        public static void Strings(BinaryWriter w, IList<string> list) { w.Write(list.Count); foreach (string s in list) Text(w, s); }
        public static string[] Strings(BinaryReader r) { int n = Count(r, 4096); var list = new string[n]; for (int i = 0; i < n; i++) list[i] = Text(r); return list; }
        public static void Bytes(BinaryWriter w, byte[] b) { w.Write(b == null ? -1 : b.Length); if (b != null) w.Write(b); }
        public static byte[] Bytes(BinaryReader r)
        {
            int n = r.ReadInt32(); if (n == -1) return null;
            if (n < 0 || n > 128 * 1024 * 1024) throw new InvalidDataException("Compiler byte payload");
            byte[] b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return b;
        }
    }

    public sealed class CompileReply
    {
        public byte[] Assembly, Symbols;
        public bool Hit;
        public long PrivateBytes;
        public readonly CompilerErrorCollection Errors = new CompilerErrorCollection();
        public void Write(BinaryWriter w)
        {
            w.Write(CompileRequest.Magic); w.Write(Hit); w.Write(PrivateBytes);
            CompileRequest.Bytes(w, Assembly); CompileRequest.Bytes(w, Symbols); w.Write(Errors.Count);
            foreach (CompilerError e in Errors)
            {
                CompileRequest.Text(w, e.ErrorNumber); CompileRequest.Text(w, e.ErrorText); CompileRequest.Text(w, e.FileName);
                w.Write(e.Line); w.Write(e.Column); w.Write(e.IsWarning);
            }
            w.Flush();
        }
        public static CompileReply Read(BinaryReader r)
        {
            if (r.ReadInt32() != CompileRequest.Magic) throw new InvalidDataException("Compiler reply magic");
            var result = new CompileReply { Hit = r.ReadBoolean(), PrivateBytes = r.ReadInt64(), Assembly = CompileRequest.Bytes(r), Symbols = CompileRequest.Bytes(r) };
            int n = CompileRequest.Count(r, 4096);
            for (int i = 0; i < n; i++)
            {
                string code = CompileRequest.Text(r), message = CompileRequest.Text(r), file = CompileRequest.Text(r);
                result.Errors.Add(new CompilerError(file, r.ReadInt32(), r.ReadInt32(), code, message) { IsWarning = r.ReadBoolean() });
            }
            return result;
        }
    }
}
