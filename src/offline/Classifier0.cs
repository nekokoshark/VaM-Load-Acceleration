using System;using System.Collections.Generic;using System.IO;using UnityEngine;using MVR.FileManagementSecure;
namespace NAT.Common { public class MorphListener { HashSet<int> _vertexIndices; long _classificationFiles; long _classificationVertices; const float FILTER_STRENGTH=0.005f;
bool ClassifyMorphFile(string path)
        {
            using(var file = new System.IO.MemoryStream(MVR.FileManagementSecure.FileManagerSecure.ReadAllBytes(path), false))
            using(var reader = new System.IO.BinaryReader(file))
            {
                int count = reader.ReadInt32();
                if(count < 0 || (long)count * 16 > file.Length - 4)
                {
                    throw new System.IO.InvalidDataException("Invalid morph delta payload");
                }

                bool included = false;
                for(int i = 0; i < count; i++)
                {
                    int vertex = reader.ReadInt32();
                    var delta = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if(_vertexIndices.Contains(vertex) && delta.magnitude > FILTER_STRENGTH)
                    {
                        included = true;
                    }
                }

                _classificationFiles++;
                _classificationVertices += count;
                return included;
            }
        }
}}
