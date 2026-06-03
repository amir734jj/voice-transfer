namespace VoiceTransfer.Logic;

/// <summary>
/// Forward Error Correction using bit repetition coding with majority voting.
///
/// Each data bit is transmitted N times (the "repeat factor"). The receiver
/// groups bits into blocks of N and takes the majority vote. This means
/// up to floor(N/2) bit errors per data bit can be corrected automatically.
///
/// Trade-off: repeat factor of 3 triples transmission time but can correct
/// any single-bit error per group. Factor of 5 can correct 2 errors per group.
///
/// Combined with CRC-16, this gives both error correction AND detection:
/// - FEC fixes minor noise corruption
/// - CRC catches anything FEC couldn't fix
/// </summary>
public static class Fec
{
    /// <summary>
    /// Encode bits with repetition: each bit is repeated N times.
    /// </summary>
    public static bool[] Encode(bool[] bits, int repeatFactor)
    {
        if (repeatFactor <= 1)
        {
            return bits;
        }

        var encoded = new bool[bits.Length * repeatFactor];
        for (var i = 0; i < bits.Length; i++)
        {
            for (var r = 0; r < repeatFactor; r++)
            {
                encoded[i * repeatFactor + r] = bits[i];
            }
        }
        return encoded;
    }

    /// <summary>
    /// Decode bits with majority voting: groups of N bits vote on the original bit.
    /// If the input length isn't divisible by N, trailing bits are discarded.
    /// </summary>
    public static bool[] Decode(bool[] bits, int repeatFactor)
    {
        if (repeatFactor <= 1)
        {
            return bits;
        }

        var dataLen = bits.Length / repeatFactor;
        var decoded = new bool[dataLen];

        for (var i = 0; i < dataLen; i++)
        {
            var onesCount = 0;
            for (var r = 0; r < repeatFactor; r++)
            {
                if (bits[i * repeatFactor + r])
                {
                    onesCount++;
                }
            }
            // Majority vote: if more than half are 1, result is 1
            decoded[i] = onesCount > repeatFactor / 2;
        }

        return decoded;
    }

    /// <summary>
    /// Interleave bits to spread burst errors across different FEC groups.
    /// Without interleaving, a burst of N corrupted bits kills one entire
    /// FEC group. With interleaving, those N errors are spread across N
    /// different groups, each of which can be corrected independently.
    /// 
    /// Uses a simple block interleaver: write row-by-row, read column-by-column.
    /// </summary>
    public static bool[] Interleave(bool[] bits, int repeatFactor)
    {
        if (repeatFactor <= 1)
        {
            return bits;
        }

        var blockSize = repeatFactor;
        var numBlocks = bits.Length / blockSize;
        var remainder = bits.Length % blockSize;

        // Only interleave complete blocks
        var interleaved = new bool[bits.Length];
        for (var col = 0; col < blockSize; col++)
        {
            for (var row = 0; row < numBlocks; row++)
            {
                interleaved[col * numBlocks + row] = bits[row * blockSize + col];
            }
        }

        // Copy remainder as-is
        for (var i = numBlocks * blockSize; i < bits.Length; i++)
        {
            interleaved[i] = bits[i];
        }

        return interleaved;
    }

    /// <summary>
    /// De-interleave: inverse of Interleave.
    /// </summary>
    public static bool[] Deinterleave(bool[] bits, int repeatFactor)
    {
        if (repeatFactor <= 1)
        {
            return bits;
        }

        var blockSize = repeatFactor;
        var numBlocks = bits.Length / blockSize;
        var remainder = bits.Length % blockSize;

        var deinterleaved = new bool[bits.Length];
        for (var col = 0; col < blockSize; col++)
        {
            for (var row = 0; row < numBlocks; row++)
            {
                deinterleaved[row * blockSize + col] = bits[col * numBlocks + row];
            }
        }

        // Copy remainder as-is
        for (var i = numBlocks * blockSize; i < bits.Length; i++)
        {
            deinterleaved[i] = bits[i];
        }

        return deinterleaved;
    }
}
