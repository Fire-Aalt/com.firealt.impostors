using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace EntityPainter.Editor
{
    /// <summary>
    /// Edits the packed arrays of a <see cref="PaintedEntities"/> component. Callers record Undo before calling.
    /// </summary>
    internal static class PaintedRecordsEditing
    {
        public static ReadOnlySpan<PaintedRecord> AsRecords(byte[] bytes)
        {
            return MemoryMarshal.Cast<byte, PaintedRecord>(bytes);
        }

        /// <summary>Returns the prototype index of <paramref name="prefab"/>, appending it with no instances when absent.</summary>
        public static int EnsurePrototype(PaintedEntities target, GameObject prefab)
        {
            int index = Array.IndexOf(target.Prototypes, prefab);
            if (index >= 0)
            {
                return index;
            }

            target.Prototypes = Append(target.Prototypes, prefab);
            target.Counts = Append(target.Counts, 0);
            return target.Prototypes.Length - 1;
        }

        /// <summary>Appends each prototype's new records to the end of that prototype's range.</summary>
        public static void Append(PaintedEntities target, IReadOnlyDictionary<int, List<PaintedRecord>> additions)
        {
            int added = 0;
            foreach (List<PaintedRecord> records in additions.Values)
            {
                added += records.Count;
            }

            ReadOnlySpan<PaintedRecord> source = AsRecords(target.Records);
            var bytes = new byte[target.Records.Length + added * PaintedRecord.SIZE];
            Span<PaintedRecord> destination = MemoryMarshal.Cast<byte, PaintedRecord>(bytes);
            var counts = (int[])target.Counts.Clone();

            int read = 0;
            int write = 0;
            for (int prototype = 0; prototype < counts.Length; prototype++)
            {
                int count = target.Counts[prototype];
                source.Slice(read, count).CopyTo(destination.Slice(write));
                read += count;
                write += count;

                if (additions.TryGetValue(prototype, out List<PaintedRecord> records))
                {
                    foreach (PaintedRecord record in records)
                    {
                        destination[write++] = record;
                    }

                    counts[prototype] += records.Count;
                }
            }

            target.Records = bytes;
            target.Counts = counts;
        }

        /// <summary>Removes every record whose flag in <paramref name="removed"/> is set.</summary>
        public static void Remove(PaintedEntities target, bool[] removed, int removedCount)
        {
            ReadOnlySpan<PaintedRecord> source = AsRecords(target.Records);
            var bytes = new byte[(source.Length - removedCount) * PaintedRecord.SIZE];
            Span<PaintedRecord> destination = MemoryMarshal.Cast<byte, PaintedRecord>(bytes);
            var counts = new int[target.Counts.Length];

            int read = 0;
            int write = 0;
            for (int prototype = 0; prototype < counts.Length; prototype++)
            {
                for (int i = 0; i < target.Counts[prototype]; i++, read++)
                {
                    if (!removed[read])
                    {
                        destination[write++] = source[read];
                        counts[prototype]++;
                    }
                }
            }

            target.Records = bytes;
            target.Counts = counts;
        }

        /// <summary>Removes every instance and keeps the prototype list.</summary>
        public static void Clear(PaintedEntities target)
        {
            target.Records = Array.Empty<byte>();
            target.Counts = new int[target.Prototypes.Length];
        }

        private static T[] Append<T>(T[] array, T value)
        {
            var result = new T[array.Length + 1];
            Array.Copy(array, result, array.Length);
            result[array.Length] = value;
            return result;
        }
    }
}
