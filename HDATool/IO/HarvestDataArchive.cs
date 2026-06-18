using System.IO;

using HDATextTool.IO.Compression;

namespace HDATextTool.IO
{
    /// <summary>
    ///     Handles the HDA format from Harvest Moon.
    /// </summary>
    class HarvestDataArchive
    {
        /// <summary>
        ///     Unpacks the data inside a HDA file.
        /// </summary>
        /// <param name="Data">The full path to the HDA file</param>
        /// <param name="OutputFolder">The output folder where the files should be placed</param>
        public static void Unpack(string Data, string OutputFolder)
        {
            using (FileStream Input = new FileStream(Data, FileMode.Open))
            {
                Unpack(Input, OutputFolder);
            }
        }

        /// <summary>
        ///     Unpacks the data inside a HDA file.
        /// </summary>
        /// <param name="Data">The HDA stream</param>
        /// <param name="OutputFolder">The output folder where the files should be placed</param>
        public static void Unpack(Stream Data, string OutputFolder)
        {
            if (!Directory.Exists(OutputFolder)) Directory.CreateDirectory(OutputFolder);
            BinaryReader Reader = new BinaryReader(Data);

            uint BaseOffset = Reader.ReadUInt32();

            int Index = 0;
            Data.Seek(BaseOffset, SeekOrigin.Begin);
            uint Offset = Reader.ReadUInt32();
            uint FirstOffset = BaseOffset + Offset;
            while (Data.Position < FirstOffset + 4)
            {
                long Position = Data.Position;
                Data.Seek(BaseOffset + Offset, SeekOrigin.Begin);
                bool IsCompressed = Reader.ReadUInt32() == 1;
                uint DecompressedLength = Reader.ReadUInt32();
                uint CompressedLength = Reader.ReadUInt32();
                uint Padding = Reader.ReadUInt32(); //0x0

                byte[] Buffer = new byte[CompressedLength];
                Data.Read(Buffer, 0, Buffer.Length);
                if (IsCompressed) Buffer = HarvestCompression.Decompress(Buffer);

                string Name = string.Format("File_{0:D5}.bin", Index++);
                string FileName = Path.Combine(OutputFolder, Name);
                File.WriteAllBytes(FileName, Buffer);

                Data.Seek(Position, SeekOrigin.Begin);
                Offset = Reader.ReadUInt32();
                if (Offset == 0 && Data.Position - 4 > BaseOffset) break;
            }
        }

        /// <summary>
        ///     Packs the data inside a folder into a HDA file.
        /// </summary>
        /// <param name="Data">The full path to the output HDA file</param>
        /// <param name="InputFolder">The folder with the data to be packed</param>
        public static void Pack(string Data, string InputFolder)
        {
            using (FileStream Output = new FileStream(Data, FileMode.Create))
            {
                Pack(Output, InputFolder);
            }
        }

        /// <summary>
        ///     Packs the data inside a folder into a HDA file.
        /// </summary>
        /// <param name="Data">The output HDA file</param>
        /// <param name="InputFolder">The folder with the data to be packed</param>
        public static void Pack(Stream Data, string InputFolder)
        {
            string[] Files = Directory.GetFiles(InputFolder);

            BinaryWriter Writer = new BinaryWriter(Data);

            Writer.Write(0x10u);
            Data.Seek(0xc, SeekOrigin.Current);

            int DataOffset = Align(Files.Length * 4);
            for (int Index = 0; Index < Files.Length; Index++)
            {
                Data.Seek(0x10 + Index * 4, SeekOrigin.Begin);
                Writer.Write(DataOffset);

                byte[] Buffer = File.ReadAllBytes(Files[Index]);
                Data.Seek(DataOffset + 0x10, SeekOrigin.Begin);
                DataOffset += Buffer.Length + 0x10;
                DataOffset = Align(DataOffset);

                Writer.Write(0u); //Uncompressed
                Writer.Write(Buffer.Length);
                Writer.Write(Buffer.Length);
                Writer.Write(0u);

                Data.Write(Buffer, 0, Buffer.Length);
            }

            while ((Data.Position & 0xf) != 0) Data.WriteByte(0);
        }

        private static int Align(int Value)
        {
            if ((Value & 0xf) != 0) Value = ((Value & ~0xf) + 0x10);
            return Value;
        }
    }
}
