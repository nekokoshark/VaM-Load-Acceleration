using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using System.Runtime.InteropServices;
using System.Text;

namespace VaM.Memory {
 public static partial class MorphSnapshotState {
  private const long ExpandedMetadataLimit=512L*1024*1024;
  private const int CodecMagic=0x314D5056;
  [StructLayout(LayoutKind.Explicit)]private struct FloatBits {
   [FieldOffset(0)]internal float Value;
   [FieldOffset(0)]internal int Bits;
  }
  private sealed class CacheLimitException:IOException { }
  private sealed class LimitedStream:Stream {
   private readonly Stream inner;private readonly long limit;private long used;
   internal LimitedStream(Stream stream,long maximum){inner=stream;limit=maximum;}
   public override bool CanRead{get{return inner.CanRead;}}
   public override bool CanWrite{get{return inner.CanWrite;}}
   public override bool CanSeek{get{return false;}}
   public override long Length{get{throw new NotSupportedException();}}
   public override long Position{get{return used;}set{throw new NotSupportedException();}}
   public override void Flush(){inner.Flush();}
   public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
   public override void SetLength(long value){throw new NotSupportedException();}
   public override int Read(byte[] buffer,int offset,int count){int n=inner.Read(buffer,offset,count);if(n>limit-used)throw new CacheLimitException();used+=n;return n;}
   public override void Write(byte[] buffer,int offset,int count){if(count>limit-used)throw new CacheLimitException();inner.Write(buffer,offset,count);used+=count;}
   protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
  }
  // UTF-16 code units and raw float bits preserve even unusual existing metadata.
  private static void WriteText(BinaryWriter writer,string value) {
   if(value==null){writer.Write(-1);return;}
   if(value.Length>1048576)throw new CacheLimitException();writer.Write(value.Length);
   var bulk=writer as BulkMetadataWriter;
   if(bulk==null){foreach(char c in value)writer.Write((ushort)c);return;}
   for(int offset=0;offset<value.Length;) {
    int count=Math.Min(bulk.Scratch.Length/2,value.Length-offset);
    for(int i=0;i<count;i++){char c=value[offset+i];bulk.Scratch[i*2]=(byte)c;bulk.Scratch[i*2+1]=(byte)(c>>8);}
    writer.Write(bulk.Scratch,0,count*2);offset+=count;
   }
  }
  private static string ReadText(BinaryReader reader) {
   var compact=reader as CompactMetadataReader;if(compact!=null)return compact.Text();
   int length=reader.ReadInt32();if(length==-1)return null;
   if(length<0 || length>1048576)throw new InvalidDataException("metadata text length");
   char[] chars=new char[length];var bulk=reader as BulkMetadataReader;
   if(bulk==null){for(int n=0;n<length;n++)chars[n]=(char)reader.ReadUInt16();return new string(chars);}
   for(int offset=0;offset<length;) {
    int count=Math.Min(bulk.Scratch.Length/2,length-offset),bytes=count*2,read=0;
    while(read<bytes){int n=reader.Read(bulk.Scratch,read,bytes-read);if(n==0)throw new EndOfStreamException();read+=n;}
    for(int i=0;i<count;i++)chars[offset+i]=(char)(bulk.Scratch[i*2]|(bulk.Scratch[i*2+1]<<8));
    offset+=count;
   }
   BulkTextReads++;return new string(chars);
  }
  private static void WriteFloat(BinaryWriter writer,float value){writer.Write(new FloatBits {Value=value}.Bits);}
  private static float ReadFloat(BinaryReader reader){return new FloatBits {Bits=reader.ReadInt32()}.Value;}
  private static object PureCopy(object original,out long bytes) {return CompactPureCopy(original,out bytes);}
  private static object LegacyPureCopy(object original,out long bytes) {
   bytes=0;var input=original as IList;if(input==null || input.Count>100000)return null;
   try {
    using(var output=new MemoryStream()) {
     using(var compressed=new LimitedStream(output,MetadataBudget))
     using(var gzip=new DeflaterOutputStream(compressed,new Deflater(1,true),65536))
     using(var expanded=new LimitedStream(gzip,ExpandedMetadataLimit))
     using(var buffered=new BufferedStream(expanded,65536))
     using(var writer=new BulkMetadataWriter(buffered)) {
      writer.Write(CodecMagic);writer.Write(input.Count);
      foreach(object row in input) {
       if(row==null || row.GetType()!=SnapshotType)throw new CacheLimitException();
       foreach(var field in SnapshotFields) {
        object value=field.GetValue(row);Type type=field.FieldType;
        if(type==typeof(string))WriteText(writer,(string)value);
        else if(type==typeof(bool))writer.Write((bool)value);
        else if(type==typeof(int))writer.Write((int)value);
        else if(type==typeof(float))WriteFloat(writer,(float)value);
        else {
         var formulas=(DAZMorphFormula[])value;
         writer.Write(formulas==null?-1:formulas.Length);
         if(formulas==null)continue;if(formulas.Length>65536)throw new CacheLimitException();
         foreach(var formula in formulas) {
          if(formula==null)throw new CacheLimitException();
          writer.Write((int)formula.targetType);WriteText(writer,formula.target);WriteFloat(writer,formula.multiplier);
         }
        }
       }
      }
     }
     byte[] compressedBytes=output.ToArray();byte[] payload=new byte[32+compressedBytes.Length];
     using(var hash=SHA256.Create())Buffer.BlockCopy(hash.ComputeHash(compressedBytes),0,payload,0,32);
     Buffer.BlockCopy(compressedBytes,0,payload,32,compressedBytes.Length);bytes=32L+payload.Length;return payload;
    }
   }catch(CacheLimitException){return null;}
  }
  private static object DecodeMetadata(object cached) {
   var payload=cached as byte[];if(payload==null || payload.Length<32 || payload.Length>MetadataBudget)throw new InvalidDataException("metadata payload");
   byte[] expected=CacheSha256.Hash(payload,32,payload.Length-32);for(int i=0;i<32;i++)if(expected[i]!=payload[i])throw new InvalidDataException("metadata checksum");
   if(payload.Length>=36 && BitConverter.ToInt32(payload,32)==CompactMagic)return DecodeCompactMetadata(payload);
   using(var input=new MemoryStream(payload,32,payload.Length-32,false))using(var gzip=new InflaterInputStream(input,new Inflater(true),65536))
   using(var expanded=new LimitedStream(gzip,ExpandedMetadataLimit))using(var buffered=new BufferedStream(expanded,65536))using(var reader=new BulkMetadataReader(buffered)) {
    if(reader.ReadInt32()!=CodecMagic)throw new InvalidDataException("metadata schema");
    int count=reader.ReadInt32();if(count<0 || count>100000)throw new InvalidDataException("metadata rows");
    var list=(IList)Activator.CreateInstance(ListType);
    for(int row=0;row<count;row++) {
     object copy=Activator.CreateInstance(SnapshotType,true);
     if(decodeRow!=null){decodeRow(reader,copy);DirectRows++;list.Add(copy);continue;}
     foreach(var field in SnapshotFields) {
      object value;Type type=field.FieldType;
      if(type==typeof(string))value=ReadText(reader);
      else if(type==typeof(bool))value=reader.ReadBoolean();
      else if(type==typeof(int))value=reader.ReadInt32();
      else if(type==typeof(float))value=ReadFloat(reader);
      else {
       int size=reader.ReadInt32();if(size<-1 || size>65536)throw new InvalidDataException("metadata formulas");
       DAZMorphFormula[] formulas=size==-1?null:new DAZMorphFormula[size];
       if(formulas!=null)for(int n=0;n<size;n++)formulas[n]=new DAZMorphFormula {targetType=(DAZMorphFormulaTargetType)reader.ReadInt32(),target=ReadText(reader),multiplier=ReadFloat(reader)};
       value=formulas;
      }
      field.SetValue(copy,value);
     }
     list.Add(copy);
    }
    if(buffered.ReadByte()!=-1)throw new InvalidDataException("metadata trailing bytes");return list;
   }
  }
 }
}
