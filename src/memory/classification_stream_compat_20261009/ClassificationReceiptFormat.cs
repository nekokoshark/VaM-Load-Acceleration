using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VaM.Memory {
    internal static class ClassificationReceiptFormat {
        internal const int MaxFileBytes=2*1024*1024, MaxRows=8192;
        private const long Magic=0x31545043524d4156L;
        internal sealed class Row {
            internal string Signature;
            internal MorphClassificationMemo.Result Result;
        }
        private static void WriteText(BinaryWriter writer,string text) {
            byte[] bytes=Encoding.UTF8.GetBytes(text);writer.Write(bytes.Length);writer.Write(bytes);
        }
        private static string ReadText(BinaryReader reader,int max) {
            int length=reader.ReadInt32();
            if(length<0 || length>max || length>reader.BaseStream.Length-reader.BaseStream.Position)throw new InvalidDataException("receipt text bound");
            return new UTF8Encoding(false,true).GetString(reader.ReadBytes(length));
        }
        internal static bool IsHash(string text) {
            if(text==null || text.Length!=64)return false;
            foreach(char c in text)if(!((c>='0'&&c<='9')||(c>='A'&&c<='F')))return false;
            return true;
        }
        internal static Dictionary<string,Row> Decode(byte[] bytes,string key) {
            if(bytes.Length<52 || bytes.Length>MaxFileBytes)throw new InvalidDataException("receipt size bound");
            int length=bytes.Length-32;byte[] body=new byte[length];Array.Copy(bytes,body,length);
            string checksum=ClassificationSourceStamp.Hash(body);
            string saved=BitConverter.ToString(bytes,length,32).Replace("-", "");
            if(checksum!=saved)throw new InvalidDataException("receipt checksum");
            using(var reader=new BinaryReader(new MemoryStream(body,false),Encoding.UTF8)) {
                if(reader.ReadInt64()!=Magic || reader.ReadInt32()!=1 || ReadText(reader,64)!=key)throw new InvalidDataException("receipt namespace");
                int count=reader.ReadInt32();
                if(count<0 || count>MaxRows || count>(body.Length-reader.BaseStream.Position)/81)throw new InvalidDataException("receipt row bound");
                var rows=new Dictionary<string,Row>(StringComparer.Ordinal);
                for(int i=0;i<count;i++) {
                    string name=ReadText(reader,32768),signature=ReadText(reader,64);
                    byte value=reader.ReadByte();long vertices=reader.ReadInt64();
                    if(name.Length>8193 || !IsHash(signature) || value>1 || vertices<0 || vertices>(int.MaxValue-4L)/16 || rows.ContainsKey(name))
                        throw new InvalidDataException("receipt row");
                    rows.Add(name,new Row {Signature=signature,Result=new MorphClassificationMemo.Result {Value=value==1,Vertices=vertices}});
                }
                if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException("receipt tail");
                return rows;
            }
        }
        internal static byte[] Encode(string key,Dictionary<string,Row> rows) {
            if(!IsHash(key) || rows.Count>MaxRows)throw new InvalidDataException("receipt write bound");
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,Encoding.UTF8)) {
                writer.Write(Magic);writer.Write(1);WriteText(writer,key);writer.Write(rows.Count);
                var names=new List<string>(rows.Keys);names.Sort(StringComparer.Ordinal);
                foreach(string name in names) {
                    Row row=rows[name];WriteText(writer,name);WriteText(writer,row.Signature);
                    writer.Write((byte)(row.Result.Value?1:0));writer.Write(row.Result.Vertices);
                    if(stream.Length>MaxFileBytes-32)throw new InvalidDataException("receipt file bound");
                }
                writer.Flush();byte[] body=stream.ToArray();
                string checksum=ClassificationSourceStamp.Hash(body);
                for(int i=0;i<32;i++)writer.Write(Convert.ToByte(checksum.Substring(i*2,2),16));
                return stream.ToArray();
            }
        }
    }
}
