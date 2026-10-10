using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VaM.Memory {
 public static partial class MorphSnapshotState {
  private const int CompactMagic=0x324D5056;
  private sealed class TextPool {
   internal readonly Dictionary<string,int> Ids=new Dictionary<string,int>(StringComparer.Ordinal);
   internal readonly List<string> Values=new List<string>();
   internal void Add(string value){if(value!=null && !Ids.ContainsKey(value))Ids.Add(value,0);}
   internal void Freeze(){Values.AddRange(Ids.Keys);Values.Sort(StringComparer.Ordinal);for(int i=0;i<Values.Count;i++)Ids[Values[i]]=i;}
   internal int Index(string value){return value==null?-1:Ids[value];}
  }
  private static object CompactPureCopy(object original,out long bytes) {
   bytes=0;var input=original as IList;if(input==null || input.Count>100000)return null;
   var strings=new TextPool();
   try {
    foreach(object row in input) {
     if(row==null || row.GetType()!=SnapshotType)throw new CacheLimitException();
     foreach(var field in SnapshotFields) {
      object value=field.GetValue(row);
      if(field.FieldType==typeof(string))strings.Add((string)value);
      else if(field.FieldType==typeof(DAZMorphFormula[])) {
       var formulas=(DAZMorphFormula[])value;if(formulas==null)continue;if(formulas.Length>65536)throw new CacheLimitException();
       foreach(var formula in formulas){if(formula==null)throw new CacheLimitException();strings.Add(formula.target);}
      }
     }
    }
    strings.Freeze();
    using(var stream=new MemoryStream())using(var limited=new LimitedStream(stream,MetadataBudget-32))using(var writer=new BulkMetadataWriter(limited)) {
     writer.Write(CompactMagic);writer.Write(strings.Values.Count);string previous="";
     foreach(string text in strings.Values) {
      if(text.Length>1048576)throw new CacheLimitException();int prefix=0;
      while(prefix<previous.Length && prefix<text.Length && previous[prefix]==text[prefix])prefix++;
      writer.Write(prefix);WriteText(writer,text.Substring(prefix));previous=text;
     }
     writer.Write(input.Count);
     foreach(object row in input)foreach(var field in SnapshotFields) {
      object value=field.GetValue(row);Type type=field.FieldType;
      if(type==typeof(string))writer.Write(strings.Index((string)value));
      else if(type==typeof(bool))writer.Write((bool)value);
      else if(type==typeof(int))writer.Write((int)value);
      else if(type==typeof(float))WriteFloat(writer,(float)value);
      else {
       var formulas=(DAZMorphFormula[])value;writer.Write(formulas==null?-1:formulas.Length);
       if(formulas!=null)foreach(var formula in formulas){writer.Write((int)formula.targetType);writer.Write(strings.Index(formula.target));WriteFloat(writer,formula.multiplier);}
      }
     }
     writer.Flush();byte[] content=stream.ToArray(),payload=new byte[content.Length+32];
     Buffer.BlockCopy(CacheSha256.Hash(content,0,content.Length),0,payload,0,32);Buffer.BlockCopy(content,0,payload,32,content.Length);bytes=payload.LongLength;return payload;
    }
   }catch(CacheLimitException){return null;}
  }
  private sealed class CompactMetadataReader:BinaryReader {
   internal readonly string[] Texts;
   internal CompactMetadataReader(Stream stream,string[] texts):base(stream,Encoding.UTF8){Texts=texts;}
   internal string Text(){int n=ReadInt32();if(n==-1)return null;if(n<0 || n>=Texts.Length)throw new InvalidDataException("metadata string reference");return Texts[n];}
  }
  private static unsafe string FrontText(BinaryReader reader,byte[] payload,string previous,int prefix) {
   int length=reader.ReadInt32();
   if(length<0 || length>1048576 || prefix+length>1048576)throw new InvalidDataException("metadata text length");
   long position=reader.BaseStream.Position;
   if(position<0 || position>payload.Length-32 || length>(payload.Length-32-position)/2)throw new EndOfStreamException();
   string value;
   if(length==0 && prefix==previous.Length)value=previous;
   else {
    value=new string('\0',prefix+length);
    fixed(char* target=value)fixed(char* source=previous)fixed(byte* bytes=payload) {
     for(int i=0;i<prefix;i++)target[i]=source[i];
     byte* text=bytes+32+(int)position;
     if(BitConverter.IsLittleEndian){char* suffix=(char*)text;for(int i=0;i<length;i++)target[prefix+i]=suffix[i];}
     else for(int i=0;i<length;i++)target[prefix+i]=(char)(text[i*2]|text[i*2+1]<<8);
    }
   }
   reader.BaseStream.Position=position+length*2L;BulkTextReads++;return value;
  }
  private static object DecodeCompactMetadata(byte[] payload) {
   using(var stream=new MemoryStream(payload,32,payload.Length-32,false))using(var header=new BulkMetadataReader(stream)) {
    if(header.ReadInt32()!=CompactMagic)throw new InvalidDataException("metadata compact schema");
    int count=header.ReadInt32();if(count<0 || count>1000000 || count>(payload.Length-40)/8)throw new InvalidDataException("metadata string table");
    var texts=new string[count];string previous="";long expandedChars=0;
    for(int i=0;i<count;i++) {
     int prefix=header.ReadInt32();if(prefix<0 || prefix>previous.Length)throw new InvalidDataException("metadata text prefix");
     string text=FrontText(header,payload,previous,prefix);
     expandedChars+=text.Length;if(expandedChars>ExpandedMetadataLimit/2)throw new InvalidDataException("metadata expanded text limit");
     texts[i]=previous=text;
    }
    int rows=header.ReadInt32();if(rows<0 || rows>100000)throw new InvalidDataException("metadata rows");
    var list=(IList)Activator.CreateInstance(ListType);
    using(var reader=new CompactMetadataReader(stream,texts)) {
     for(int i=0;i<rows;i++){object row=Activator.CreateInstance(SnapshotType,true);decodeRow(reader,row);DirectRows++;list.Add(row);}
     if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException("metadata trailing bytes");
    }
    return list;
   }
  }
 }
}
