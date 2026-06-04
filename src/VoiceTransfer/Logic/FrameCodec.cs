using System.Security.Cryptography;
using System.Text;
using Serilog;
using VoiceTransfer.Data;

namespace VoiceTransfer.Logic;

/// <summary>
/// Frame codec: builds and parses data frames for reliable transmission.
/// 
/// Frame structure (all bits transmitted MSB-first):
/// +-------------+-----------+------------+--------------+----------+
/// |  Preamble   | Sync Byte | Length (2B) |  Payload...  | CRC16(2B)|
/// | 128 bits    |  0x7E     | big-endian  |  encrypted   | CCITT    |
/// | 10101010... |           |             |  AES-GCM     |          |
/// +-------------+-----------+------------+--------------+----------+
/// 
/// The payload is: AES-256-GCM(base64(raw_file_data), password)
/// - Base64 normalizes the data
/// - AES-256-GCM provides authenticated encryption (confidentiality + integrity)
/// - A random 12-byte nonce is prepended to the ciphertext
/// - If no password is provided, data is XOR-scrambled (whitening only, not secure)
/// </summary>
public static class FrameCodec
{
    private const int AesKeySize = 32;   // 256 bits
    private const int AesNonceSize = 12; // 96 bits (GCM standard)
    private const int AesTagSize = 16;   // 128 bits (GCM standard)
    private const int Pbkdf2Iterations = 100_000;
    private static readonly byte[] Pbkdf2Salt = "VoiceTransfer-v1"u8.ToArray();
    /// <summary>
    /// Encode raw file data into a complete bit stream ready for FSK modulation.
    /// 
    /// Layout:  preamble(raw) | sync_byte(raw) | FEC(interleave(len + payload + crc)) | end(raw)
    /// 
    /// Preamble and sync byte are NOT FEC-encoded -- they're used for clock recovery
    /// and frame detection. FEC only covers the data portion.
    /// </summary>
    public static bool[] Encode(byte[] fileData, TransmissionProfile profile, string? password = null)
    {
        // 1. Base64 encode
        var base64 = Convert.ToBase64String(fileData);
        var b64Bytes = Encoding.ASCII.GetBytes(base64);

        // 2. Encrypt or scramble
        var encrypted = password != null ? Encrypt(b64Bytes, password) : Scramble(b64Bytes);

        // 3. Build data bytes: [lenHi][lenLo][payload...][crcHi][crcLo]
        var payloadLen = encrypted.Length;
        if (payloadLen > 65535)
        {
            throw new InvalidOperationException($"Payload too large: {payloadLen} bytes (max 65535)");
        }

        var lenHi = (byte)(payloadLen >> 8);
        var lenLo = (byte)(payloadLen & 0xFF);

        // CRC is computed over length + payload
        var crcData = new byte[2 + payloadLen];
        crcData[0] = lenHi;
        crcData[1] = lenLo;
        Array.Copy(encrypted, 0, crcData, 2, payloadLen);
        var crc = Crc16Ccitt(crcData);

        // 4. Assemble data bytes (no sync byte -- sync stays raw outside FEC)
        var dataBytes = new byte[2 + payloadLen + 2]; // len + payload + crc
        var idx = 0;
        dataBytes[idx++] = lenHi;
        dataBytes[idx++] = lenLo;
        Array.Copy(encrypted, 0, dataBytes, idx, payloadLen);
        idx += payloadLen;
        dataBytes[idx++] = (byte)(crc >> 8);
        dataBytes[idx++] = (byte)(crc & 0xFF);

        // 5. Convert to bits and apply FEC
        var dataBits = BytesToBits(dataBytes);
        var fecBits = Fec.Encode(dataBits, profile.FecRepeat);
        var interleavedBits = Fec.Interleave(fecBits, profile.FecRepeat);

        // 6. Build final frame: preamble(raw) + sync(raw) + FEC data
        //    No end marker -- length field + CRC already bound the data
        var preamble = GeneratePreamble(profile.PreambleBits);
        var syncBits = ByteToBits(Constants.SyncByte);

        return ConcatBits(preamble, syncBits, interleavedBits);
    }

    /// <summary>
    /// Decode a bit stream back to the original file data.
    /// 
    /// 1. Find raw sync byte (0x7E) 
    /// 2. Extract bits after sync (the FEC-encoded data)
    /// 3. De-interleave -> majority-vote FEC decode
    /// 4. Extract len + payload + CRC, verify CRC
    /// 5. Descramble -> Base64 decode
    /// </summary>
    public static byte[]? Decode(bool[] bits, int fecRepeat, string? password = null)
    {
        var syncPattern = ByteToBits(Constants.SyncByte);
        var searchFrom = 0;
        const int maxSyncAttempts = 10;
        var syncFoundCount = 0;

        for (var attempt = 0; attempt < maxSyncAttempts; attempt++)
        {
            var syncPos = FindPattern(bits, syncPattern, searchFrom);
            if (syncPos < 0)
            {
                break;
            }

            syncFoundCount++;
            // Data starts after the 8-bit sync byte
            var dataStart = syncPos + 8;
            if (dataStart >= bits.Length)
            {
                break;
            }

            // Extract remaining bits (FEC-encoded data)
            // The run may include trailing noise bits beyond the actual FEC data.
            // The de-interleaver requires exact block dimensions, so we try
            // progressively shorter lengths until CRC validates.
            var remainLen = bits.Length - dataStart;

            Log.Debug("Sync byte found at bit {Pos}, {Remain} data bits remaining (attempt {Attempt})",
                    syncPos, remainLen, attempt);

            if (fecRepeat <= 1)
            {
                // No FEC: just decode directly
                var fecBits = new bool[remainLen];
                Array.Copy(bits, dataStart, fecBits, 0, remainLen);
                var result = TryDecodeData(fecBits, password);
                if (result != null)
                {
                    return result;
                }
            }
            else
            {
                // With FEC: try different trimming amounts to find correct block boundary
                var maxTrim = Math.Min(remainLen, fecRepeat * 100);
                var trimsAttempted = 0;
                for (var trim = 0; trim < maxTrim; trim++)
                {
                    var tryLen = remainLen - trim;
                    if (tryLen < fecRepeat * 4)
                    {
                        break; // need at least len(2) + crc(2) = 32 bits after FEC
                    }

                    if (tryLen % fecRepeat != 0)
                    {
                        continue; // must be divisible by repeat factor
                    }

                    trimsAttempted++;
                    var fecBits = new bool[tryLen];
                    Array.Copy(bits, dataStart, fecBits, 0, tryLen);

                    var deinterleaved = Fec.Deinterleave(fecBits, fecRepeat);
                    var corrected = Fec.Decode(deinterleaved, fecRepeat);

                    var result = TryDecodeData(corrected, password);
                    if (result != null)
                    {
                        return result;
                    }
                }


                // Truncation recovery: when the recording is shorter than the
                // original transmission, the de-interleave dimensions are wrong.
                // The interleaver used (origCols × fecRepeat) but we only have
                // remainLen bits. Try different assumed original payload sizes,
                // pad to the correct length, and de-interleave with the right
                // dimensions. This recovers data even when 40% of the signal
                // is missing (FEC provides enough redundancy).
                for (var assumedPayload = 1; assumedPayload <= 200; assumedPayload++)
                {
                    var origDataBits = (assumedPayload + 4) * 8; // len(2) + payload + crc(2)
                    var origFecLen = origDataBits * fecRepeat;
                    if (origFecLen <= remainLen)
                    {
                        continue; // not truncated -- was already handled by trim loop
                    }

                    if (origFecLen > remainLen * 3)
                    {
                        break; // need at least ~1/3 of the FEC data
                    }

                    // Pad received bits to assumed original length
                    // Padding zeros act as "erasures" -- they're wrong for bit=1
                    // but with 3+ correct copies, majority voting still works
                    var padded = new bool[origFecLen];
                    Array.Copy(bits, dataStart, padded, 0, Math.Min(remainLen, origFecLen));

                    var deinterleaved = Fec.Deinterleave(padded, fecRepeat);
                    var corrected = Fec.Decode(deinterleaved, fecRepeat);

                    var result = TryDecodeData(corrected, password);
                    if (result != null)
                    {
                        Log.Information("Truncation recovery: payload={Payload} bytes, padded {Avail}->{Orig} FEC bits",
                            assumedPayload, remainLen, origFecLen);
                        return result;
                    }
                }
            }

            // Try next sync position
            searchFrom = syncPos + 1;
        }

        if (syncFoundCount > 0)
            Log.Information("Found {Count} sync byte(s) but none decoded successfully", syncFoundCount);

        return null;
    }

    /// <summary>
    /// Try decoding FEC data starting at a specific bit position, bypassing
    /// sync byte search. Used when the sync byte is corrupted by reverb but
    /// the data start position is known from preamble detection.
    /// </summary>
    public static byte[]? DecodeFromPosition(bool[] bits, int dataStart, int fecRepeat, string? password = null)
    {
        if (dataStart >= bits.Length) return null;
        var remainLen = bits.Length - dataStart;

        if (fecRepeat <= 1)
        {
            var fecBits = new bool[remainLen];
            Array.Copy(bits, dataStart, fecBits, 0, remainLen);
            return TryDecodeData(fecBits, password);
        }

        // Trim loop (same as Decode)
        var maxTrim = Math.Min(remainLen, fecRepeat * 100);
        for (var trim = 0; trim < maxTrim; trim++)
        {
            var tryLen = remainLen - trim;
            if (tryLen < fecRepeat * 4) break;
            if (tryLen % fecRepeat != 0) continue;

            var fecBits = new bool[tryLen];
            Array.Copy(bits, dataStart, fecBits, 0, tryLen);
            var deinterleaved = Fec.Deinterleave(fecBits, fecRepeat);
            var corrected = Fec.Decode(deinterleaved, fecRepeat);
            var result = TryDecodeData(corrected, password);
            if (result != null) return result;
        }

        // Truncation recovery
        for (var assumedPayload = 1; assumedPayload <= 200; assumedPayload++)
        {
            var origFecLen = (assumedPayload + 4) * 8 * fecRepeat;
            if (origFecLen <= remainLen) continue;
            if (origFecLen > remainLen * 3) break;

            var padded = new bool[origFecLen];
            Array.Copy(bits, dataStart, padded, 0, Math.Min(remainLen, origFecLen));
            var deinterleaved = Fec.Deinterleave(padded, fecRepeat);
            var corrected = Fec.Decode(deinterleaved, fecRepeat);
            var result = TryDecodeData(corrected, password);
            if (result != null) return result;
        }

        return null;
    }

    /// <summary>
    /// Soft-decision variant of DecodeFromPosition.
    /// </summary>
    public static byte[]? DecodeFromPositionSoft(double[] softBits, int dataStart, int fecRepeat, string? password = null)
    {
        if (dataStart >= softBits.Length) return null;
        var remainLen = softBits.Length - dataStart;

        if (fecRepeat <= 1)
        {
            var corrected = new bool[remainLen];
            for (var i = 0; i < remainLen; i++)
                corrected[i] = softBits[dataStart + i] > 0;
            return TryDecodeData(corrected, password);
        }

        var maxTrim = Math.Min(remainLen, fecRepeat * 100);
        for (var trim = 0; trim < maxTrim; trim++)
        {
            var tryLen = remainLen - trim;
            if (tryLen < fecRepeat * 4) break;
            if (tryLen % fecRepeat != 0) continue;

            var fecSoft = new double[tryLen];
            Array.Copy(softBits, dataStart, fecSoft, 0, tryLen);
            var deinterleaved = Fec.DeinterleaveSoft(fecSoft, fecRepeat);
            var corrected = Fec.DecodeSoft(deinterleaved, fecRepeat);
            var result = TryDecodeData(corrected, password);
            if (result != null) return result;
        }

        // Truncation recovery (soft)
        for (var assumedPayload = 1; assumedPayload <= 200; assumedPayload++)
        {
            var origFecLen = (assumedPayload + 4) * 8 * fecRepeat;
            if (origFecLen <= remainLen) continue;
            if (origFecLen > remainLen * 3) break;

            var padded = new double[origFecLen];
            Array.Copy(softBits, dataStart, padded, 0, Math.Min(remainLen, origFecLen));
            var deinterleaved = Fec.DeinterleaveSoft(padded, fecRepeat);
            var corrected = Fec.DecodeSoft(deinterleaved, fecRepeat);
            var result = TryDecodeData(corrected, password);
            if (result != null) return result;
        }

        return null;
    }

    /// <summary>
    /// Soft-decision decode: uses log-likelihood ratios instead of hard bits.
    /// Much more robust for over-the-air reception where bit confidence varies.
    /// The sync byte is still found using hard decisions (derived from LLR sign).
    /// </summary>
    public static byte[]? DecodeSoft(double[] softBits, int fecRepeat, string? password = null)
    {
        // Derive hard bits for sync byte search
        var hardBits = new bool[softBits.Length];
        for (var i = 0; i < softBits.Length; i++)
            hardBits[i] = softBits[i] > 0;

        var syncPattern = ByteToBits(Constants.SyncByte);
        var searchFrom = 0;
        const int maxSyncAttempts = 10;

        for (var attempt = 0; attempt < maxSyncAttempts; attempt++)
        {
            // Also search with 1-bit error tolerance for the sync byte
            var syncPos = FindPattern(hardBits, syncPattern, searchFrom);
            if (syncPos < 0)
            {
                // Try fuzzy sync search (allow 1 bit error)
                syncPos = FindPatternFuzzy(hardBits, syncPattern, searchFrom, 1);
            }
            if (syncPos < 0)
            {
                break;
            }

            var dataStart = syncPos + 8;
            if (dataStart >= softBits.Length)
            {
                break;
            }

            var remainLen = softBits.Length - dataStart;

            if (fecRepeat <= 1)
            {
                // No FEC: hard-decision from LLR
                var corrected = new bool[remainLen];
                for (var i = 0; i < remainLen; i++)
                    corrected[i] = softBits[dataStart + i] > 0;
                var result = TryDecodeData(corrected, password);
                if (result != null) return result;
            }
            else
            {
                var maxTrim = Math.Min(remainLen, fecRepeat * 100);
                for (var trim = 0; trim < maxTrim; trim++)
                {
                    var tryLen = remainLen - trim;
                    if (tryLen < fecRepeat * 4) break;
                    if (tryLen % fecRepeat != 0) continue;

                    var fecSoft = new double[tryLen];
                    Array.Copy(softBits, dataStart, fecSoft, 0, tryLen);

                    var deinterleaved = Fec.DeinterleaveSoft(fecSoft, fecRepeat);
                    var corrected = Fec.DecodeSoft(deinterleaved, fecRepeat);

                    var result = TryDecodeData(corrected, password);
                    if (result != null) return result;
                }

                // Truncation recovery (soft-decision variant)
                for (var assumedPayload = 1; assumedPayload <= 200; assumedPayload++)
                {
                    var origDataBits = (assumedPayload + 4) * 8;
                    var origFecLen = origDataBits * fecRepeat;
                    if (origFecLen <= remainLen) continue;
                    if (origFecLen > remainLen * 3) break;

                    var padded = new double[origFecLen];
                    Array.Copy(softBits, dataStart, padded, 0, Math.Min(remainLen, origFecLen));

                    var deinterleaved = Fec.DeinterleaveSoft(padded, fecRepeat);
                    var corrected = Fec.DecodeSoft(deinterleaved, fecRepeat);

                    var result = TryDecodeData(corrected, password);
                    if (result != null)
                    {
                        Log.Information("Truncation recovery (soft): payload={Payload} bytes", assumedPayload);
                        return result;
                    }
                }
            }

            searchFrom = syncPos + 1;
        }

        return null;
    }

    /// <summary>
    /// Find a pattern allowing up to maxErrors mismatches.
    /// </summary>
    private static int FindPatternFuzzy(bool[] bits, bool[] pattern, int startFrom, int maxErrors)
    {
        for (var i = startFrom; i <= bits.Length - pattern.Length; i++)
        {
            var errors = 0;
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (bits[i + j] != pattern[j])
                {
                    errors++;
                    if (errors > maxErrors)
                    {
                        match = false;
                        break;
                    }
                }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>
    /// Attempt to extract data from FEC-decoded bits: [lenHi][lenLo][payload...][crcHi][crcLo]
    /// </summary>
    private static byte[]? TryDecodeData(bool[] bits, string? password)
    {
        if (bits.Length < 32) // need at least len(16) + crc(16)
        {
            return null;
        }

        var lenHi = BitsToOneByte(bits, 0);
        var lenLo = BitsToOneByte(bits, 8);
        var payloadLen = (lenHi << 8) | lenLo;

        if (payloadLen is <= 0 or > 65535)
        {
            return null;
        }

        var needed = 16 + payloadLen * 8 + 16; // len + payload + crc in bits
        if (needed > bits.Length)
        {
            return null;
        }

        var pos = 16; // skip length
        var encrypted = new byte[payloadLen];
        for (var i = 0; i < payloadLen; i++)
        {
            encrypted[i] = BitsToOneByte(bits, pos);
            pos += 8;
        }

        var crcHi = BitsToOneByte(bits, pos); pos += 8;
        var crcLo = BitsToOneByte(bits, pos);
        var receivedCrc = (ushort)((crcHi << 8) | crcLo);

        var crcData = new byte[2 + payloadLen];
        crcData[0] = lenHi;
        crcData[1] = lenLo;
        Array.Copy(encrypted, 0, crcData, 2, payloadLen);
        var computedCrc = Crc16Ccitt(crcData);

        if (receivedCrc != computedCrc)
        {
            return null;
        }

        byte[] b64Bytes;
        try
        {
            b64Bytes = password != null ? Decrypt(encrypted, password) : Scramble(encrypted);
        }
        catch (CryptographicException)
        {
            return null; // wrong password or tampered data
        }

        try
        {
            var base64 = Encoding.ASCII.GetString(b64Bytes);
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Encrypt data with AES-256-GCM. Output: [nonce(12)] [ciphertext(N)] [tag(16)]
    /// Key is derived from the password via PBKDF2-SHA256.
    /// </summary>
    public static byte[] Encrypt(byte[] plaintext, string password)
    {
        var key = DeriveKey(password);
        var nonce = new byte[AesNonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesTagSize];

        using var aes = new AesGcm(key, AesTagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Output: nonce + ciphertext + tag
        var result = new byte[AesNonceSize + ciphertext.Length + AesTagSize];
        Array.Copy(nonce, 0, result, 0, AesNonceSize);
        Array.Copy(ciphertext, 0, result, AesNonceSize, ciphertext.Length);
        Array.Copy(tag, 0, result, AesNonceSize + ciphertext.Length, AesTagSize);
        return result;
    }

    /// <summary>
    /// Decrypt data encrypted with AES-256-GCM. Input: [nonce(12)] [ciphertext(N)] [tag(16)]
    /// Throws CryptographicException if password is wrong or data is tampered.
    /// </summary>
    public static byte[] Decrypt(byte[] data, string password)
    {
        if (data.Length < AesNonceSize + AesTagSize)
        {
            throw new CryptographicException("Data too short for AES-GCM.");
        }

        var key = DeriveKey(password);

        var nonce = new byte[AesNonceSize];
        var ciphertextLen = data.Length - AesNonceSize - AesTagSize;
        var ciphertext = new byte[ciphertextLen];
        var tag = new byte[AesTagSize];

        Array.Copy(data, 0, nonce, 0, AesNonceSize);
        Array.Copy(data, AesNonceSize, ciphertext, 0, ciphertextLen);
        Array.Copy(data, AesNonceSize + ciphertextLen, tag, 0, AesTagSize);

        var plaintext = new byte[ciphertextLen];
        using var aes = new AesGcm(key, AesTagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static byte[] DeriveKey(string password)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Pbkdf2Salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            AesKeySize);
    }

    /// <summary>
    /// XOR stream cipher with a simple PRNG. Same function for scramble/descramble.
    /// Used as fallback when no password is provided (whitening only, not secure).
    /// </summary>
    public static byte[] Scramble(byte[] data)
    {
        var result = new byte[data.Length];
        var state = Constants.ScrambleKey;
        for (var i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ state);
            state = (byte)((state * 7 + 13) & 0xFF);
        }
        return result;
    }

    /// <summary>
    /// CRC-16/CCITT (polynomial 0x1021, init 0xFFFF).
    /// Standard error-detection code for telecommunications.
    /// </summary>
    public static ushort Crc16Ccitt(byte[] data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (ushort)((crc << 1) ^ 0x1021);
                }
                else
                {
                    crc <<= 1;
                }
            }
        }
        return crc;
    }

    // --- Bit manipulation helpers ---

    public static bool[] GeneratePreamble(int numBits)
    {
        var bits = new bool[numBits];
        for (var i = 0; i < numBits; i++)
            bits[i] = (i % 2) == 0; // 1,0,1,0,...
        return bits;
    }

    public static bool[] ByteToBits(byte b)
    {
        var bits = new bool[8];
        for (var i = 0; i < 8; i++)
            bits[i] = ((b >> (7 - i)) & 1) == 1; // MSB first
        return bits;
    }

    public static bool[] BytesToBits(byte[] bytes)
    {
        var bits = new bool[bytes.Length * 8];
        for (var i = 0; i < bytes.Length; i++)
        {
            var byteBits = ByteToBits(bytes[i]);
            Array.Copy(byteBits, 0, bits, i * 8, 8);
        }
        return bits;
    }

    public static byte BitsToOneByte(bool[] bits, int offset)
    {
        byte b = 0;
        for (var i = 0; i < 8; i++)
        {
            if (bits[offset + i])
            {
                b |= (byte)(1 << (7 - i));
            }
        }
        return b;
    }

    public static bool[] ConcatBits(params bool[][] arrays)
    {
        var total = 0;
        foreach (var a in arrays)
        {
            total += a.Length;
        }

        var result = new bool[total];
        var offset = 0;
        foreach (var a in arrays)
        {
            Array.Copy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }
        return result;
    }

    /// <summary>
    /// Find a bit pattern in a bit stream. Returns the index or -1.
    /// </summary>
    public static int FindPattern(bool[] bits, bool[] pattern, int startFrom)
    {
        for (var i = startFrom; i <= bits.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (bits[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return i;
            }
        }
        return -1;
    }
}
