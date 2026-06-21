using System;
using System.IO;

namespace HDATool.IO
{
    /// <summary>
    ///     Handles the LBA table recalculation on the ELF from Harvest Moon.
    /// </summary>
    internal static class HarvestElf
    {
        const uint LBATableStart = 0x162460;
        const uint LBATableEnd = 0x162d30;
        const int SectorSize = 0x930;
        const int BytesPerSector = 0x800;
        const int EntrySize = 8; // 2 uint32s = 8 bytes

        /// <summary>
        ///     Fixes the LBA table on the main ELF executable of the Harvest Moon: Save the Homeland game.
        /// </summary>
        /// <param name="Elf">The full path to the ELF file</param>
        /// <param name="LBA">The LBA of the modified file</param>
        /// <param name="NewSize">The new size of the file</param>
        public static void Fix(string Elf, uint LBA, uint NewSize)
        {
            using (FileStream input = new FileStream(Elf, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536))
            {
                Fix(input, LBA, NewSize);
            }
        }

        /// <summary>
        ///     Fixes the LBA table on the main ELF executable of the Harvest Moon: Save the Homeland game.
        /// </summary>
        /// <param name="Elf">The Stream with the ELF data</param>
        /// <param name="LBA">The LBA of the modified file</param>
        /// <param name="NewSize">The new size of the file</param>
        public static void Fix(Stream Elf, uint LBA, uint NewSize)
        {
            if (Elf == null)
                throw new ArgumentNullException(nameof(Elf));

            if (!Elf.CanRead || !Elf.CanWrite)
                throw new ArgumentException("Stream must support both reading and writing.", nameof(Elf));

            // Validate that LBA table fits within stream
            if (Elf.Length < LBATableEnd)
                throw new ArgumentException($"Stream is too small. Expected at least {LBATableEnd} bytes.", nameof(Elf));

            using (BinaryReader reader = new BinaryReader(Elf))
            using (BinaryWriter writer = new BinaryWriter(Elf))
            {
                // Calculate table size to determine number of entries
                int tableSize = (int)(LBATableEnd - LBATableStart);
                int entryCount = tableSize / EntrySize;

                Elf.Seek((long)LBATableStart, SeekOrigin.Begin);

                int difference = 0;
                bool found = false;
                uint calculatedNewEnd = 0;

                for (int i = 0; i < entryCount; i++)
                {
                    // Check if we've reached the end
                    if (Elf.Position >= LBATableEnd)
                        break;

                    uint lbaStart = reader.ReadUInt32();
                    uint lbaEnd = reader.ReadUInt32();

                    // Write back updated values
                    Elf.Seek(-EntrySize, SeekOrigin.Current);
                    writer.Write(lbaStart + (uint)difference);
                    writer.Write(lbaEnd + (uint)difference);

                    // Check if this is the entry we're looking for
                    if (lbaStart == LBA)
                    {
                        found = true;
                        uint size = (uint)((NewSize + BytesPerSector - 1) / BytesPerSector); // Ceiling division
                        calculatedNewEnd = (lbaStart + size) - 1;
                        difference = (int)(calculatedNewEnd - lbaEnd);

                        // Update the end LBA for this entry
                        Elf.Seek(-4, SeekOrigin.Current);
                        writer.Write(calculatedNewEnd);
                    }
                }

                writer.Flush();

                if (!found)
                {
                    TextOut.PrintWarning("The LBA you entered was not found on the table!");
                    TextOut.Print("Make sure you typed it in DECIMAL format.");
                }
                else
                {
                    TextOut.PrintSuccess("LBA found and values patched successfully!");
                }
            }
        }
    }
}
