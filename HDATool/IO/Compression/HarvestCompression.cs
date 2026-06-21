using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace HDATool.IO.Compression
{
    /// <summary>
    /// Codec kompresi milik Harvest Moon: Save the Homeland (PS2).
    ///
    /// Format ini adalah varian LZO/LZ77 dengan 4 jenis opcode berdasarkan nibble tinggi
    /// dari byte header pertama:
    ///
    ///  Header  │ Jenis          │ Length                     │ Back-distance
    ///  ────────┼────────────────┼────────────────────────────┼─────────────────────────────
    ///  0x00-0F │ Literal copy   │ (H &amp; 0x0F) + 3            │ — (tidak ada back-ref)
    ///          │                │ 0x00 → baca byte ekstra    │
    ///  0x10-1F │ Back-ref jauh  │ (H &amp; 0x07) + 2            │ ((B0>>2)|(B1&lt;&lt;6)|((H&amp;8)&lt;&lt;11)) + 0x4000
    ///          │                │ 0 → baca byte ekstra       │ 0x4000 tanpa extra = end-of-stream
    ///  0x20-3F │ Back-ref dekat │ (H &amp; 0x1F) + 2            │ ((B0>>2)|(B1&lt;&lt;6)) + 1
    ///          │                │ 0 → baca byte ekstra       │
    ///  0x40-FF │ Back-ref mini  │ (H >> 5) + 1               │ (((H>>2)&amp;7) | (B0&lt;&lt;3)) + 1
    ///
    /// Setiap blok compressed diakhiri literal-copy 3 byte kosong (0x00 0x00 0x00),
    /// yang dibaca sebagai header=0 → length=3 → direct-copy; ini tanda EOF alami.
    /// Blok 0x10-0x1F yang menghasilkan Back==0x4000 (tanpa high-bit) adalah
    /// EOF marker eksplisit yang dipakai oleh encoder PS2.
    ///
    /// Setelah blok back-reference, selalu ada 0-3 byte literal (DirectCopy),
    /// diambil dari bit 0-1 byte pertama 2-byte back-distance (B0 &amp; 3).
    /// </summary>
    public static class HarvestCompression
    {
        // ─────────────────────────────────────────────────────────────
        // Konstanta encoder
        // ─────────────────────────────────────────────────────────────

        /// <summary>Ukuran window hash untuk encoder (harus pangkat 2).</summary>
        private const int HashSize = 1 << 14; // 16 384 slot

        /// <summary>Panjang minimum match yang mau di-encode sebagai back-ref.</summary>
        private const int MinMatch = 3;

        /// <summary>Panjang maksimum match satu pass (dibatasi agar loop tidak tak terbatas).</summary>
        private const int MaxMatch = 264;

        /// <summary>Batas back-distance untuk opcode 0x40-0xFF (2 byte, 11-bit distance).</summary>
        private const int MaxDistMini = 2048;

        /// <summary>Batas back-distance untuk opcode 0x20-0x3F (2 byte, 14-bit distance).</summary>
        private const int MaxDistNear = 16384;

        // ─────────────────────────────────────────────────────────────
        // DECOMPRESS
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Mendekompresi data Harvest Compression menjadi byte array asli.
        /// </summary>
        /// <param name="data">Data terkompresi.</param>
        /// <returns>Data mentah hasil dekompresi.</returns>
        /// <exception cref="ArgumentNullException">data null.</exception>
        /// <exception cref="InvalidDataException">Format data tidak valid.</exception>
        public static byte[] Decompress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0) return Array.Empty<byte>();

            // Alokasi output awal — perkiraan 3× input cukup untuk kebanyakan file PS2;
            // List<byte> dipakai agar tidak perlu tahu ukuran akhir di muka.
            // Untuk performa back-copy kita butuh akses random, jadi simpan sebagai byte[].
            // Strategi: buffer yang tumbuh secara eksponensial.
            int outCapacity = Math.Max(data.Length * 3, 256);
            byte[] outBuf   = new byte[outCapacity];
            int    outPos   = 0;

            int ip = 0; // instruction pointer ke dalam data[]

            // ── Helper: pastikan outBuf punya minimal 'needed' byte kosong ──
            void EnsureOutput(int needed)
            {
                if (outPos + needed <= outBuf.Length) return;
                int newCap = Math.Max(outBuf.Length * 2, outPos + needed + 256);
                Array.Resize(ref outBuf, newCap);
            }

            // ── Helper: baca satu byte dari input (dengan validasi) ──
            byte ReadByte()
            {
                if (ip >= data.Length)
                    throw new InvalidDataException(
                        $"[HarvestDecompress] Unexpected end of input at offset 0x{ip:X}.");
                return data[ip++];
            }

            // ── Helper: baca length ekstra (format zero-run) ──
            // Dipakai saat length awal dari header == nilai sentinel (2 atau 3).
            int ReadExtraLength(int baseLen, int addPerZero, int addFinal)
            {
                int len = baseLen;
                byte b;
                while ((b = ReadByte()) == 0) len += addPerZero;   // setiap 0x00 tambah 255
                return len + b + addFinal;
            }

            // ── Helper: copy literal dari input ke output ──
            void CopyLiteral(int length)
            {
                if (ip + length > data.Length)
                    throw new InvalidDataException(
                        $"[HarvestDecompress] Literal copy melampaui input (ip=0x{ip:X}, len={length}).");
                EnsureOutput(length);
                Buffer.BlockCopy(data, ip, outBuf, outPos, length);
                ip     += length;
                outPos += length;
            }

            // ── Helper: copy dari window output (back-reference) ──
            // Mendukung overlapping copy (back < length) yang menghasilkan pengulangan.
            void CopyBack(int back, int length)
            {
                if (back <= 0 || back > outPos)
                    throw new InvalidDataException(
                        $"[HarvestDecompress] Back-distance tidak valid: back={back}, outPos={outPos}.");
                EnsureOutput(length);

                int src = outPos - back;
                // Copy byte per byte agar overlapping benar (tidak bisa pakai BlockCopy).
                for (int i = 0; i < length; i++)
                    outBuf[outPos++] = outBuf[src++];
            }

            // ════════════════════════════════════════════════════════
            // Loop utama dekompresi
            // ════════════════════════════════════════════════════════
            while (ip < data.Length)
            {
                byte header = ReadByte();

                // ── Cabang 1: 0x00-0x0F — literal copy ──────────────
                if (header < 0x10)
                {
                    int length = header & 0x0F;

                    if (length == 0)
                        // Sentinel: panjang diteruskan via zero-run
                        length = ReadExtraLength(3, 0xFF, 0x0F);
                    else
                        length += 3;

                    CopyLiteral(length);
                }
                // ── Cabang 2: 0x10-0x1F — back-ref JAUH (hingga ~48 KB) ─
                else if (header < 0x20)
                {
                    int length = header & 0x07;
                    if (length == 0)
                        length = ReadExtraLength(2, 0xFF, 0x07);
                    else
                        length += 2;

                    // Baca 2 byte back-distance secara eksplisit
                    // (menghindari undefined-order dalam satu ekspresi C#)
                    byte b0 = ReadByte();
                    byte b1 = ReadByte();

                    int directCopy = b0 & 3;
                    int back = ((b0 >> 2) | (b1 << 6) | ((header & 8) << 11)) + 0x4000;

                    // EOF marker: back == 0x4000 berarti high-bit 0, tidak ada extra
                    if (back == 0x4000) break;

                    CopyBack(back, length);
                    CopyLiteral(directCopy);
                }
                // ── Cabang 3: 0x20-0x3F — back-ref DEKAT (hingga ~16 KB) ─
                else if (header < 0x40)
                {
                    int length = header & 0x1F;
                    if (length == 0)
                        length = ReadExtraLength(2, 0xFF, 0x1F);
                    else
                        length += 2;

                    byte b0 = ReadByte();
                    byte b1 = ReadByte();

                    int directCopy = b0 & 3;
                    int back = ((b0 >> 2) | (b1 << 6)) + 1;

                    CopyBack(back, length);
                    CopyLiteral(directCopy);
                }
                // ── Cabang 4: 0x40-0xFF — back-ref MINI (1-byte distance, fast) ─
                else
                {
                    // Panjang di-pack di bit 7-5 header
                    int length = (header >> 5) + 1;

                    // DirectCopy di bit 3-2 header (BUKAN bit 1-0!)
                    // Bug asli: (Header & 3) salah — seharusnya (Header >> 2) & 3
                    int directCopy = (header >> 2) & 3;

                    byte b0 = ReadByte();
                    // Back-distance: bit 4-2 header (3-bit) | b0 (8-bit) geser 3
                    int back = (((header >> 2) & 7) | (b0 << 3)) + 1;

                    CopyBack(back, length);
                    CopyLiteral(directCopy);
                }
            }

            // Potong buffer ke ukuran aktual
            byte[] result = new byte[outPos];
            Buffer.BlockCopy(outBuf, 0, result, 0, outPos);
            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // COMPRESS
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Mengompresi data mentah menggunakan format Harvest Compression.
        ///
        /// Strategi: greedy LZ77 dengan hash-chain 3-byte.
        /// Setiap posisi di-hash, lalu kita cari back-reference terpanjang
        /// dalam window. Jika tidak ada match ≥ MinMatch, byte disimpan
        /// sebagai literal pending dan ditulis saat match ditemukan / akhir data.
        ///
        /// Pemilihan opcode (dari yang paling hemat):
        ///   1. 0x40-0xFF  jika dist ≤ 2048  dan len ≤ 8
        ///   2. 0x20-0x3F  jika dist ≤ 16384 dan len berapapun
        ///   3. 0x10-0x1F  jika dist ≤ 49151 dan len berapapun
        /// </summary>
        /// <param name="data">Data mentah yang akan dikompresi.</param>
        /// <returns>Byte array terkompresi, termasuk EOF marker.</returns>
        public static byte[] Compress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
            {
                // EOF marker minimal: opcode 0x11 dengan back==0x4000
                return new byte[] { 0x11, 0x00, 0x00 };
            }

            // ── Output buffer — alokasi konservatif (worst-case ~ 1.1× input) ──
            // Dalam skenario terburuk (data tidak bisa dikompresi),
            // overhead literal header kurang dari 10% ukuran input.
            int    outCap = data.Length + (data.Length / 8) + 64;
            byte[] outBuf = new byte[outCap];
            int    op     = 0; // output pointer

            // ── Hash table — slot berisi posisi terakhir yang di-hash ──
            // Collision diatasi dengan linked-list (chain) per slot.
            int[] hashHead  = new int[HashSize];
            int[] hashChain = new int[data.Length];
            for (int i = 0; i < HashSize; i++) hashHead[i] = -1;

            // ── Helper: pastikan outBuf punya cukup ruang ──
            void EnsureOut(int needed)
            {
                if (op + needed <= outBuf.Length) return;
                int newCap = Math.Max(outBuf.Length * 2, op + needed + 64);
                Array.Resize(ref outBuf, newCap);
            }

            // ── Helper: hash 3-byte ──
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            int Hash3(int pos)
            {
                uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16));
                return (int)((v * 0x1E35A7BD) >> (32 - 14)) & (HashSize - 1);
            }

            // ── Helper: tulis literal-run (pendahulu sebelum back-ref) ──
            // Literal diawali opcode 0x00-0x0F (tanpa back-ref).
            void WriteLiterals(int litStart, int litLen)
            {
                if (litLen == 0) return;

                // Opcode literal memakai header nibble rendah sebagai panjang−3.
                // Jika panjang ≤ 18 (0x0F+3): header = (len-3) langsung.
                // Jika lebih panjang: header = 0x00 diikuti zero-run.
                while (litLen > 0)
                {
                    int chunk = Math.Min(litLen, 18 + 255 * 256); // satu run maksimum

                    EnsureOut(chunk + 4);

                    if (chunk <= 18)
                    {
                        outBuf[op++] = (byte)(chunk - 3);
                    }
                    else
                    {
                        // 0x00 header + zero-run
                        outBuf[op++] = 0x00;
                        int extra = chunk - 3 - 0x0F;
                        while (extra >= 0xFF) { outBuf[op++] = 0x00; extra -= 0xFF; }
                        outBuf[op++] = (byte)extra;
                    }

                    Buffer.BlockCopy(data, litStart, outBuf, op, chunk);
                    op       += chunk;
                    litStart += chunk;
                    litLen   -= chunk;
                }
            }

            // ── Helper: tulis back-reference + directCopy literal ──
            void WriteBackRef(int dist, int matchLen, int litStart, int litLenInline)
            {
                // litLenInline adalah 0-3 byte literal yang ikut di dalam opcode back-ref
                // (disimpan di bit 1-0 byte distance pertama).
                // Pastikan litLenInline ≤ 3.
                int dc = Math.Min(litLenInline, 3);

                EnsureOut(matchLen + 8);

                if (dist <= MaxDistMini && matchLen <= 8)
                {
                    // ── Opcode 0x40-0xFF (mini, paling efisien) ──
                    // Header: bit7-5 = (len-1), bit4-3 = (dist-1)>>3 bit tinggi, bit2-1 = dc, bit0 = 0
                    // Wait — format asli: bit7-5=len-1, bit4-2=(dist-1 low 3 bit), bit1-0=dc
                    // Back byte: (dist-1)>>3
                    int d = dist - 1;
                    outBuf[op++] = (byte)(((matchLen - 1) << 5) | ((d & 7) << 2) | dc);
                    outBuf[op++] = (byte)(d >> 3);
                }
                else if (dist <= MaxDistNear)
                {
                    // ── Opcode 0x20-0x3F (near) ──
                    int d   = dist - 1;
                    int lh  = matchLen - 2;
                    if (lh <= 0x1F)
                    {
                        outBuf[op++] = (byte)(0x20 | lh);
                    }
                    else
                    {
                        // Zero-run length
                        outBuf[op++] = 0x20;
                        int extra = lh - 0x1F;
                        while (extra >= 0xFF) { outBuf[op++] = 0x00; extra -= 0xFF; }
                        outBuf[op++] = (byte)extra;
                    }
                    // 2 byte distance: low = (d<<2)|dc, high = d>>6
                    outBuf[op++] = (byte)((d << 2) | dc);
                    outBuf[op++] = (byte)(d >> 6);
                }
                else
                {
                    // ── Opcode 0x10-0x1F (far) ──
                    int d   = dist - 0x4000;
                    int lh  = matchLen - 2;
                    byte hdrBase = (byte)(0x10 | ((d >> 11) & 8));
                    if (lh <= 7)
                    {
                        outBuf[op++] = (byte)(hdrBase | lh);
                    }
                    else
                    {
                        outBuf[op++] = hdrBase;
                        int extra = lh - 7;
                        while (extra >= 0xFF) { outBuf[op++] = 0x00; extra -= 0xFF; }
                        outBuf[op++] = (byte)extra;
                    }
                    outBuf[op++] = (byte)((d << 2) | dc);
                    outBuf[op++] = (byte)(d >> 6);
                }

                // Tulis dc byte literal inline
                if (dc > 0)
                {
                    Buffer.BlockCopy(data, litStart, outBuf, op, dc);
                    op += dc;
                }
            }

            // ════════════════════════════════════════════════════════
            // Loop utama kompresi (greedy LZ77)
            // ════════════════════════════════════════════════════════
            int ip       = 0;
            int litStart = 0; // awal akumulasi literal pending

            while (ip < data.Length)
            {
                // Tidak cukup byte untuk hash 3-byte → flush sisa sebagai literal
                if (ip + MinMatch > data.Length)
                {
                    WriteLiterals(litStart, data.Length - litStart);
                    litStart = ip = data.Length;
                    break;
                }

                int h = Hash3(ip);

                // ── Cari match terpanjang via hash chain ──
                int bestLen  = 0;
                int bestDist = 0;
                int chainLen = 0;
                const int MaxChain = 64; // kedalaman chain (trade-off kecepatan vs rasio)

                int candidate = hashHead[h];
                while (candidate >= 0 && chainLen < MaxChain)
                {
                    int dist = ip - candidate;
                    if (dist > MaxDistNear + 0x8000) break; // terlalu jauh, chain sudah tua

                    // Cocokkan panjang match dimulai dari karakter ke-3
                    // (karena hash sudah menjamin 3 karakter pertama sama — verify dulu)
                    if (data[candidate]     == data[ip]     &&
                        data[candidate + 1] == data[ip + 1] &&
                        data[candidate + 2] == data[ip + 2])
                    {
                        int maxL = Math.Min(MaxMatch, data.Length - ip);
                        int ml   = 3;
                        while (ml < maxL && data[candidate + ml] == data[ip + ml]) ml++;

                        if (ml > bestLen)
                        {
                            bestLen  = ml;
                            bestDist = dist;
                            if (ml == MaxMatch) break; // tidak mungkin lebih panjang
                        }
                    }

                    candidate = hashChain[candidate];
                    chainLen++;
                }

                // ── Simpan posisi ip ke hash chain ──
                hashChain[ip] = hashHead[h];
                hashHead[h]   = ip;

                if (bestLen < MinMatch)
                {
                    // Tidak ada match — akumulasi sebagai literal, maju 1 byte
                    ip++;
                    continue;
                }

                // ── Ada match — flush literal yang terkumpul dulu ──
                int pendingLit = ip - litStart;

                // DirectCopy inline: maksimum 3 byte literal *setelah* back-ref,
                // tetapi hanya jika literal itu adalah sisa yang datang setelah match.
                // Kita flush semua literal pending SEBELUM back-ref sebagai literal-run,
                // kecuali ≤3 byte terakhir yang bisa di-inline.
                int inlineLit   = 0;
                int prefixLit   = pendingLit;

                if (pendingLit > 0 && pendingLit <= 3)
                {
                    // Sedikit literal → simpan semua sebagai inline di back-ref
                    // HANYA jika ini adalah literal *sebelum* back-ref pertama setelah EOF.
                    // Untuk keamanan, kita tulis sebagai literal-run biasa saja,
                   
                        Output.Write(Data, DataOffset, Length);
                        DataOffset += Length;
                    }
                    else
                    {
                        //Compressed
                        if (Header < 0x20)
                        {
                            //0x10 ~ 0x1f
                            Back = (Header & 8) << 11;

                            if ((Length = (Header & 7) + 2) == 2)
                            {
                                while ((Header = Data[DataOffset++]) == 0) Length += 0xff;
                                Length += Header + 7;
                            }

                            DirectCopy = Data[DataOffset] & 3;
                            Back = ((Data[DataOffset++] >> 2) | (Data[DataOffset++] << 6) | Back) + 0x4000;
                            if (Back == 0x4000) break; //Compression end
                        }
                        else if (Header < 0x40)
                        {
                            //0x20 ~ 0x3f
                            if ((Length = (Header & 0x1f) + 2) == 2)
                            {
                                while ((Header = Data[DataOffset++]) == 0) Length += 0xff;
                                Length += Header + 0x1f;
                            }

                            DirectCopy = Data[DataOffset] & 3;
                            Back = ((Data[DataOffset++] >> 2) | (Data[DataOffset++] << 6)) + 1;
                        }
                        else
                        {
                            //0x40 ~ 0xff
                            Length = (Header >> 5) + 1;
                            DirectCopy = Header & 3;
                            Back = (((Header >> 2) & 7) | (Data[DataOffset++] << 3)) + 1;
                        }

                        //Go back and writes compressed data
                        long Position = Output.Position;
                        while (Length-- > 0)
                        {
                            Output.Seek(Position - Back, SeekOrigin.Begin);
                            int Value = Output.ReadByte();
                            Output.Seek(Position, SeekOrigin.Begin);
                            Output.WriteByte((byte)Value);
                            Position++;
                        }

                        //Writes remaining direct copy data
                        Output.Write(Data, DataOffset, DirectCopy);
                        DataOffset += DirectCopy;
                    }
                }

                return Output.ToArray();
            }
        }

        //TODO: Compression
    }
}
