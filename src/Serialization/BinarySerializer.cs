﻿using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

namespace DatabaseQueue.Serialization
{
    /// <summary>
    /// A serializer which serializes <typeparamref name="T"/> -> byte[] and vice versa. 
    /// </summary>
    /// <typeparam name="T">The item type to serialize.</typeparam>
    public class BinarySerializer<T> : SerializerBase<T, byte[]>
    {
        private readonly IFormatter _formatter = new BinaryFormatter();

        public override bool TrySerialize(T item, out byte[] serialized)
        {
            serialized = default(byte[]);

            try
            {
                using (var stream = new MemoryStream())
                {
                    _formatter.Serialize(stream, item);

                    var bytes = new byte[stream.Length];
                    stream.Position = 0;
                    stream.Read(bytes, 0, bytes.Length);

                    serialized = bytes;
                }

                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public override bool TryDeserialize(byte[] serialized, out T item)
        {
            item = default(T);

            try
            {
                using (var stream = new MemoryStream(serialized))
                {
                    stream.Position = 0;

                    item = (T)_formatter.Deserialize(stream);
                }

                return item != null;
            }
            catch (Exception ex)
            {
                return false;
            }
        }
    }
}
