using System;
using System.IO;
using System.Linq;
using HDATool.IO.Compression;

namespace HDATool.IO
{
    /// <summary>
    /// Menangani baca/tulis arsip .HDA milik Harvest Moon: Save the Homeland (PS2).
    ///
    /// Struktur arsip HDA:
    ///   [0x00] uint32  BaseOffset  — offset awal tabel entry (biasanya 0x10)
    ///   [0x04..BaseOffset-1]       — padding / header tambahan (umumnya 0x00)
    ///
    ///   Tabel offset (mulai BaseOffset):
    ///   [BaseOffset + i*4] uint32  RelativeOffset[i]  — offset relatif dari BaseOffset ke data entry ke-i
    ///                                                    nilai 0 setelah entri terakhir = penanda akhir
    ///
    ///   Setiap data entry (di BaseOffset + RelativeOffset[i]):
    ///   [+0x00] uint32  IsCompressed       — 1 = terkompresi (Harvest Compression), 0 = mentah
    ///   [+0x04] uint32  DecompressedLength — ukuran setelah dekompresi
    ///   [+0x08] uint32  CompressedLength   — ukuran data yang tersimpan (yang dibaca)
    ///   [+0x0C] uint32  Padding            — selalu 0x00
    ///   [+0x10] byte[]  Data               — payload sebesar CompressedLength byte
    /// </summary>
    internal static class HarvestDataArchive
    {
        // Ukuran header tiap entry data (16 byte = 4 field × 4 byte)
        private const int EntryHeaderSize = 0x10;

        // Alignment boundary PS2 (16 byte)
        private const int AlignBoundary = 0x10;

        // Ukuran minimum arsip HDA yang valid
        private const int MinArchiveSize = 8;

        #region Unpack

        /// <summary>
        /// Membuka file HDA dan mengekstrak semua entry ke <paramref name="outputFolder"/>.
        /// </summary>
        public static void Unpack(string hdaFilePath, string outputFolder)
        {
            if (string.IsNullOrEmpty(hdaFilePath))
                throw new ArgumentNullException(nameof(hdaFilePath));

            if (!File.Exists(hdaFilePath))
                throw new FileNotFoundException("File HDA tidak ditemukan.", hdaFilePath);

            using (FileStream input = new FileStream(
                hdaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Unpack(input, outputFolder);
            }
        }

        /// <summary>
        /// Membaca stream HDA dan mengekstrak semua entry ke <paramref name="outputFolder"/>.
        /// </summary>
        public static void Unpack(Stream data, string outputFolder)
        {
            if (data == null)        throw new ArgumentNullException(nameof(data));
            if (!data.CanRead)       throw new ArgumentException("Stream tidak bisa dibaca.", nameof(data));
            if (!data.CanSeek)       throw new ArgumentException("Stream harus mendukung Seek.", nameof(data));
            if (outputFolder == null) throw new ArgumentNullException(nameof(outputFolder));

            if (data.Length < MinArchiveSize)
                throw new InvalidDataException(
                    $"File terlalu kecil ({data.Length} byte) untuk menjadi arsip HDA yang valid.");

            if (!Directory.Exists(outputFolder))
                Directory.CreateDirectory(outputFolder);

            // BinaryReader dibuat dengan leaveOpen:true agar stream dikelola dari luar
            using (BinaryReader reader = new BinaryReader(data, System.Text.Encoding.Default, leaveOpen: true))
            {
                // --- Baca BaseOffset ---
                data.Seek(0, SeekOrigin.Begin);
                uint baseOffset = reader.ReadUInt32();

                // Validasi: BaseOffset harus di dalam file dan masuk akal
                if (baseOffset < 4 || baseOffset >= (uint)data.Length)
                    throw new InvalidDataException(
                        $"BaseOffset 0x{baseOffset:X8} tidak valid (ukuran file: 0x{data.Length:X8}).");

                // --- Hitung jumlah entry dari tabel offset ---
                // Tabel offset ada di [BaseOffset .. FirstDataOffset).
                // Kita baca semua offset relatif sampai ketemu nilai 0 yang menandai akhir tabel,
                // atau sampai mencapai entry pertama.
                data.Seek(baseOffset, SeekOrigin.Begin);
                uint firstRelOffset = reader.ReadUInt32();

                if (firstRelOffset == 0)
                {
                    // Arsip kosong — tidak ada entry sama sekali
                    Console.WriteLine("[HDA] Arsip kosong, tidak ada entry untuk diekstrak.");
                    return;
                }

                // Hitung berapa slot offset ada sebelum data entry pertama.
                // Tiap slot 4 byte. Jumlah entry = firstRelOffset / 4
                // (karena entry ke-0 offset-nya langsung mengarah ke data pertama,
                //  dan slot ke-0 ada di BaseOffset, maka panjang tabel = firstRelOffset byte)
                int entryCount = (int)(firstRelOffset / 4);

                if (entryCount <= 0 || entryCount > 65535)
                    throw new InvalidDataException(
                        $"Jumlah entry tidak valid: {entryCount}.");

                // Baca seluruh tabel offset sekaligus
                uint[] relOffsets = new uint[entryCount];
                data.Seek(baseOffset, SeekOrigin.Begin);
                for (int i = 0; i < entryCount; i++)
                    relOffsets[i] = reader.ReadUInt32();

                // --- Ekstrak setiap entry ---
                for (int index = 0; index < entryCount; index++)
                {
                    uint relOffset = relOffsets[index];

                    // Hitung posisi absolut entry di dalam file
                    long entryAbsPos = (long)baseOffset + (long)relOffset;

                    // Validasi offset tidak melebihi ukuran file
                    if (entryAbsPos + EntryHeaderSize > data.Length)
                    {
                        Console.Error.WriteLine(
                            $"[HDA] Entry #{index}: offset 0x{entryAbsPos:X8} melewati batas file, dilewati.");
                        continue;
                    }

                    data.Seek(entryAbsPos, SeekOrigin.Begin);

                    // Baca header entry (16 byte)
                    bool   isCompressed       = reader.ReadUInt32() == 1;
                    uint   decompressedLength = reader.ReadUInt32();
                    uint   compressedLength   = reader.ReadUInt32();
                    /* padding */               reader.ReadUInt32(); // selalu 0x00

                    // Validasi panjang data
                    if (compressedLength == 0 || compressedLength > 64 * 1024 * 1024 /* 64 MB */)
                    {
                        Console.Error.WriteLine(
                            $"[HDA] Entry #{index}: CompressedLength {compressedLength} tidak masuk akal, dilewati.");
                        continue;
                    }

                    if (entryAbsPos + EntryHeaderSize + compressedLength > (ulong)data.Length)
                    {
                        Console.Error.WriteLine(
                            $"[HDA] Entry #{index}: data melewati batas file, dilewati.");
                        continue;
                    }

                    // Baca payload — gunakan ReadBytes agar benar-benar penuh
                    // (tidak seperti Stream.Read() yang bisa partial)
                    byte[] buffer = reader.ReadBytes((int)compressedLength);

                    if (buffer.Length != (int)compressedLength)
                        throw new EndOfStreamException(
                            $"Entry #{index}: Diharapkan {compressedLength} byte, terbaca {buffer.Length} byte.");

                    // Dekompresi bila perlu
                    if (isCompressed)
                    {
                        try
                        {
                            buffer = HarvestCompression.Decompress(buffer);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(
                                $"[HDA] Entry #{index}: Gagal dekompresi — {ex.Message}. Menyimpan data mentah.");
                            // Jangan lempar exception; simpan data mentah agar tidak kehilangan semua file
                        }
                    }

                    // Tulis file output
                    string fileName = Path.Combine(outputFolder, $"File_{index:D5}.bin");
                    File.WriteAllBytes(fileName, buffer);

                    Console.WriteLine(
                        $"[HDA] Ekstrak: {Path.GetFileName(fileName)} " +
                        $"({compressedLength} byte{(isCompressed ? $" → {buffer.Length} byte" : "")})");
                }
            }
        }

        #endregion

        #region Pack

        /// <summary>
        /// Mengemas semua file di <paramref name="inputFolder"/> menjadi arsip HDA.
        /// File diurutkan berdasarkan nama agar konsisten di semua platform.
        /// </summary>
        public static void Pack(string hdaFilePath, string inputFolder)
        {
            if (string.IsNullOrEmpty(hdaFilePath))
                throw new ArgumentNullException(nameof(hdaFilePath));
            if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder))
                throw new DirectoryNotFoundException($"Folder input tidak ditemukan: {inputFolder}");

            using (FileStream output = new FileStream(
                hdaFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Pack(output, inputFolder);
            }
        }

        /// <summary>
        /// Menulis arsip HDA ke <paramref name="data"/> dari file-file di <paramref name="inputFolder"/>.
        /// </summary>
        public static void Pack(Stream data, string inputFolder)
        {
            if (data == null)         throw new ArgumentNullException(nameof(data));
            if (!data.CanWrite)       throw new ArgumentException("Stream tidak bisa ditulis.", nameof(data));
            if (!data.CanSeek)        throw new ArgumentException("Stream harus mendukung Seek.", nameof(data));
            if (inputFolder == null)  throw new ArgumentNullException(nameof(inputFolder));

            // Urutkan berdasarkan nama agar urutan index konsisten di semua OS
            string[] files = Directory.GetFiles(inputFolder)
                                      .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                      .ToArray();

            if (files.Length == 0)
            {
                Console.WriteLine("[HDA] Tidak ada file untuk dikemas.");
                return;
            }

            using (BinaryWriter writer = new BinaryWriter(data, System.Text.Encoding.Default, leaveOpen: true))
            {
                // --- Tulis BaseOffset (0x10) di posisi 0x00 ---
                // BaseOffset selalu 0x10: 4 byte BaseOffset + 12 byte padding header
                const uint baseOffset = 0x10u;
                writer.Write(baseOffset);

                // Tulis 12 byte padding header (0x00 × 12)
                writer.Write(0u); // 0x04
                writer.Write(0u); // 0x08
                writer.Write(0u); // 0x0C

                // --- Hitung letak tabel offset ---
                // Tabel offset ada di [0x10 .. 0x10 + files.Length * 4).
                // Ukuran tabel (dalam byte) = files.Length * 4, lalu di-align ke 16.
                int offsetTableSize = Align(files.Length * 4);

                // DataOffset pertama = offsetTableSize (relatif dari BaseOffset)
                // Posisi absolut pertama data = BaseOffset + offsetTableSize
                int firstRelDataOffset = offsetTableSize;

                // --- Tulis payload setiap file dan kumpulkan offset-nya ---
                int[] relDataOffsets = new int[files.Length];
                int   currentRelOffset = firstRelDataOffset;

                for (int index = 0; index < files.Length; index++)
                {
                    relDataOffsets[index] = currentRelOffset;

                    byte[] buffer = File.ReadAllBytes(files[index]);

                    // Posisi absolut entry data = BaseOffset + currentRelOffset
                    long absEntryPos = baseOffset + currentRelOffset;
                    data.Seek(absEntryPos, SeekOrigin.Begin);

                    // Tulis header entry (tidak dikompresi)
                    writer.Write(0u);              // IsCompressed = 0
                    writer.Write(buffer.Length);   // DecompressedLength
                    writer.Write(buffer.Length);   // CompressedLength (sama karena tidak dikompresi)
                    writer.Write(0u);              // Padding

                    // Tulis data
                    data.Write(buffer, 0, buffer.Length);

                    // Hitung offset entry berikutnya (align ke 16 byte)
                    int entryTotalSize = EntryHeaderSize + buffer.Length;
                    currentRelOffset += Align(entryTotalSize);

                    Console.WriteLine(
                        $"[HDA] Kemas: {Path.GetFileName(files[index])} ({buffer.Length} byte)");
                }

                // --- Tulis tabel offset ---
                // Kembali ke BaseOffset dan isi tabel offset relatif
                data.Seek(baseOffset, SeekOrigin.Begin);
                for (int i = 0; i < files.Length; i++)
                    writer.Write(relDataOffsets[i]);

                // Tulis penanda akhir tabel (0x00) bila ada ruang di padding area
                // (opsional tapi baik untuk kompatibilitas parser yang mencari terminator)
                if (files.Length * 4 < offsetTableSize)
                    writer.Write(0u);

                // --- Padding akhir file ke boundary 16 byte ---
                data.Seek(0, SeekOrigin.End);
                while ((data.Position & (AlignBoundary - 1)) != 0)
                    data.WriteByte(0);
            }

            Console.WriteLine($"[HDA] Selesai mengemas {files.Length} file.");
        }

        #endregion

        #region Helper

        /// <summary>
        /// Membulatkan <paramref name="value"/> ke atas ke kelipatan 16 terdekat.
        /// Nilai yang sudah kelipatan 16 dikembalikan apa adanya.
        /// </summary>
        private static int Align(int value)
        {
            int remainder = value & (AlignBoundary - 1);
            return remainder == 0 ? value : value + (AlignBoundary - remainder);
        }

        #endregion
    }
}
