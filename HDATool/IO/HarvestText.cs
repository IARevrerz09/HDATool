using System;
using System.Globalization;
using System.IO;
using System.Text;

using HDATextTool.Properties;

namespace HDATextTool.IO
{
    class HarvestText
    {
        /// <summary>
        ///     Decodes a text from Harvest Moon: Save the Homeland.
        /// </summary>
        /// <param name="Data">The full path to the data file</param>
        /// <param name="Pointers">The full path to the pointers file</param>
        /// <returns>The decoded text as a string</returns>
        public static string Decode(string Data, string Pointers)
        {
            using (FileStream DataStream = new FileStream(Data, FileMode.Open))
            {
                using (FileStream PointersStream = new FileStream(Pointers, FileMode.Open))
                {
                    return Decode(DataStream, PointersStream);
                }
            }
        }

        /// <summary>
        ///     Decodes a text from Harvest Moon: Save the Homeland.
        /// </summary>
        /// <param name="Data">The Stream with the text to be decoded</param>
        /// <param name="Pointers">The Stream with the pointers to the text</param>
        /// <returns>The decoded text as a string</returns>
        public static string Decode(Stream Data, Stream Pointers)
        {
            BinaryReader Reader = new BinaryReader(Data);
            BinaryReader Pointer = new BinaryReader(Pointers);
            StringBuilder Output = new StringBuilder();

            string[] Table = GetTable();

            uint NextOffset = Pointer.ReadUInt32();
            while (Pointers.Position < Pointers.Length)
            {
                uint Offset = NextOffset;
                NextOffset = Pointer.ReadUInt32();
                if (NextOffset == 0) break;
                Data.Seek(Offset, SeekOrigin.Begin);

                uint Value = 0;
                byte Header = 0;
                byte Mask = 0;
                while (Data.Position < Data.Length && Value != 2)
                {
                    if ((Mask >>= 1) == 0)
                    {
                        Header = (byte)Data.ReadByte();
                        Mask = 0x80;
                    }

                    //Read 8 or 16 bits character
                    if ((Header & Mask) == 0)
                        Value = (byte)Data.ReadByte();
                    else
                        Value = Reader.ReadUInt16();

                    //Append character (or hex code) to output
                    if (Value == 7)
                        Output.Append(string.Format("[var]\\x{0:X4}", Data.ReadByte()));
                    else
                    {
                        if (Table[Value] == null)
                            Output.Append(string.Format("\\x{0:X4}", Value));
                        else
                            Output.Append(Table[Value]);
                    }
                }
            }

            return Output.ToString();
        }

        /// <summary>
        ///     Represents the encoded Harvest Moon text.
        ///     "Data" contains the encoded text itself.
        ///     "Pointers" contains pointers to the dialogs.
        /// </summary>
        public struct EncodedText
        {
            public byte[] Data;
            public byte[] Pointers;
        }

        /// <summary>
        ///     Encodes a text from Harvest Moon: Save the Homeland.
        /// </summary>
        /// <param name="Text">The string to be encoded</param>
        /// <param name="Data">The full path to the data file that should be created</param>
        /// <param name="Pointers">The full path to the pointers file that should be created</param>
        public static void Encode(string Text, string Data, string Pointers)
        {
            EncodedText Encoded = Encode(Text);
            File.WriteAllBytes(Data, Encoded.Data);
            File.WriteAllBytes(Pointers, Encoded.Pointers);
        }

        /// <summary>
        ///     Encodes a text from Harvest Moon: Save the Homeland.
        /// </summary>
        /// <param name="Text">The string to be encoded</param>
        /// <returns>The encoded data</returns>
        public static EncodedText Encode(string Text)
        {
            EncodedText Output = new EncodedText();

            string[] Table = GetTable();

            using (MemoryStream Data = new MemoryStream())
            {
                using (MemoryStream Pointers = new MemoryStream())
                {
                    BinaryWriter Writer = new BinaryWriter(Data);
                    BinaryWriter Pointer = new BinaryWriter(Pointers);

                    string[] Dialogs = Text.Split(new string[] { Table[2] }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (string Dialog in Dialogs)
                    {
                        Align(Data, 4);
                        Pointer.Write((uint)Data.Position);
  
                        byte Header = 0;
                        int Mask = 0;
                        long Position = 0;
                        long HeaderPosition = Data.Position;
                        for (int Index = 0; Index < Dialog.Length; Index++)
                        {
                            if ((Mask >>= 1) == 0)
                            {
                                Data.WriteByte(0);
                                Position = Data.Position;
                                Data.Seek(HeaderPosition, SeekOrigin.Begin);
                                Data.WriteByte(Header);
                                Data.Seek(Position, SeekOrigin.Begin);
                                HeaderPosition = Position - 1;

                                Header = 0;
                                Mask = 0x80;
                            }

                            if (Index + 2 <= Dialog.Length && Dialog.Substring(Index, 2) == "\r\n")
                            {
                                //Line break (Windows)
                                Data.WriteByte(0); Index++;
                            }
                            else if (Dialog[Index] == '\n')
                            {
                                //Line break (Linux)
                                Data.WriteByte(0);
                            }
                            else if (Index + 2 <= Dialog.Length && Dialog.Substring(Index, 2) == "\\x")
                            {
                                //Unknown data = Hex code
                                string Hex = Dialog.Substring(Index + 2, 4);
                                ushort Value = ushort.Parse(Hex, NumberStyles.HexNumber);

                                if (Value > 0xff)
                                {
                                    Writer.Write(Value);
                                    Header |= (byte)Mask;
                                }
                                else
                                    Data.WriteByte((byte)Value);

                                Index += 5;
                            }
                            else
                            {
                                //Character
                                int Value = -1;
                                string Character = Dialog.Substring(Index, 1);
                                if (Character == "[")
                                {
                                    //Slow search method for table elements with more than 1 character
                                    for (int TblIndex = 0; TblIndex < Table.Length; TblIndex++)
                                    {
                                        string TblValue = Table[TblIndex];

                                        if (TblValue == null || Index + TblValue.Length > Dialog.Length) continue;

                                        if (Dialog.Substring(Index, TblValue.Length) == TblValue)
                                        {
                                            Value = TblIndex;
                                            Index += TblValue.Length - 1;
                                            break;
                                        }
                                    }
                                }
                                else
                                    Value = Array.IndexOf(Table, Character);

                                if (Value > -1)
                                {
                                    if (Value > 0xff)
                                    {
                                        Writer.Write((ushort)Value);
                                        Header |= (byte)Mask;
                                    }
                                    else
                                    {
                                        Data.WriteByte((byte)Value);
                                        if (Value == 7) Mask <<= 1;
                                    }
                                }
                                else
                                    Data.WriteByte(0x10); //Unknown, add space
                            }
                        }

                        //End of dialog
                        Position = Data.Position;
                        if (Header != 0)
                        {
                            Data.Seek(HeaderPosition, SeekOrigin.Begin);
                            Data.WriteByte((byte)Header);
                        }

                        Data.Seek(Position, SeekOrigin.Begin);
                        if (Mask == 1) Data.WriteByte(0);
                        Data.WriteByte(2);
                    }

                    Align(Data, 4);
                    Pointer.Write((uint)Data.Length);
                    Align(Data, 0x10);
                    Align(Pointers, 0x10);

                    Output.Data = Data.ToArray();
                    Output.Pointers = Pointers.ToArray();
                }
            }

            return Output;
        }

        private static string[] GetTable()
        {
            string[] Table = new string[0x10000];
            string[] LineBreaks = new string[] { "\n", "\r\n" };
            string[] TableElements = Resources.CharacterTable.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries);

            foreach (string Element in TableElements)
            {
                string[] Parameters = Element.Split('=');
                int Value = Convert.ToInt32(Parameters[0], 16);
                string Character = Parameters[1];

                //Replace some codes that needs to be "escaped" on the tbl due to the way how it is parsed
                Character = Character.Replace("\\n", "\r\n");
                Character = Character.Replace("\\equal", "=");

                Table[Value] = Character;
            }

            return Table;
        }

        private static void Align(Stream Stream, int Bytes)
        {
            int Mask = Bytes - 1;
            while ((Stream.Position & Mask) != 0) Stream.WriteByte(0);
        }
    }
}
