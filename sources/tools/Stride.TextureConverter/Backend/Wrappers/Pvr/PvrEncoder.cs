// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Stride.TextureConverter.PvrWrapper
{
    /// <summary>
    ///   Encoding and decoding primitives for the Dreamcast PVR texture container.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///     This plays the role the P/Invoke surfaces in the sibling <c>DxtWrapper</c> and
    ///     <c>AstcEncWrapper</c> namespaces play for their libraries: everything
    ///     <see cref="TexLibraries.PvrTexLib"/> needs to turn Stride's RGBA buffers into what the
    ///     hardware expects, kept apart from the request plumbing. It is managed rather than a
    ///     binding because there is no native encoder to bind — the PowerVR2's formats are a
    ///     texel packing, a Morton order and a vector quantiser, all of which are cheaper to do
    ///     here than to ship a native library for on three host platforms.
    ///   </para>
    ///   <para>Implemented from the published format description; not validated on hardware.</para>
    /// </remarks>
    internal static class PvrEncoder
    {
        /// <summary>
        ///   The offset of the texel at <paramref name="x"/>, <paramref name="y"/> within a
        ///   Morton-ordered square of <paramref name="size"/> texels a side.
        /// </summary>
        /// <remarks>
        ///   Y supplies the even bits and X the odd ones, which is what makes the Dreamcast's
        ///   order run down each 2x2 column before moving right: (0,0), (0,1), (1,0), (1,1).
        /// </remarks>
        public static int TwiddleIndex(int x, int y, int size)
        {
            var index = 0;
            for (var bit = 0; 1 << bit < size; bit++)
            {
                index |= ((y >> bit) & 1) << (2 * bit);
                index |= ((x >> bit) & 1) << (2 * bit + 1);
            }
            return index;
        }

        /// <summary>
        ///   Rewrites a raster-ordered texel buffer into the Morton order the hardware samples.
        /// </summary>
        /// <remarks>
        ///   A non-square texture is covered by square tiles of side <c>min(width, height)</c>,
        ///   each twiddled on its own and stored one after another along the longer edge. A square
        ///   texture is the single-tile case of the same walk.
        /// </remarks>
        public static void Twiddle(ReadOnlySpan<ushort> source, Span<ushort> destination, int width, int height)
            => TwiddleCore(source, destination, width, height, untwiddle: false);

        /// <summary>
        ///   Reverses <see cref="Twiddle"/>, restoring raster order.
        /// </summary>
        public static void Untwiddle(ReadOnlySpan<ushort> source, Span<ushort> destination, int width, int height)
            => TwiddleCore(source, destination, width, height, untwiddle: true);

        // One walk serves both directions: the mapping between a raster position and its twiddled
        // offset is the same either way, only which side of the copy it indexes changes.
        private static void TwiddleCore(ReadOnlySpan<ushort> source, Span<ushort> destination, int width, int height, bool untwiddle)
        {
            if (source.Length < width * height || destination.Length < width * height)
                throw new ArgumentException("Twiddle buffers are smaller than the texture.");

            var side = Math.Min(width, height);
            if (!IsPowerOfTwo(side))
                throw new ArgumentException($"Twiddled textures need a power-of-two tile side; got {width}x{height}.");

            var texelsPerTile = side * side;
            var tilesX = width / side;
            var tilesY = height / side;

            if (tilesX * side != width || tilesY * side != height)
                throw new ArgumentException($"{width}x{height} is not a whole number of {side}x{side} tiles.");

            for (var tileY = 0; tileY < tilesY; tileY++)
            {
                for (var tileX = 0; tileX < tilesX; tileX++)
                {
                    // Tiles run along the longer edge; for a square texture this is tile 0 of 1.
                    var tileBase = (tileY * tilesX + tileX) * texelsPerTile;

                    for (var y = 0; y < side; y++)
                    {
                        var rasterRow = (tileY * side + y) * width + tileX * side;
                        for (var x = 0; x < side; x++)
                        {
                            var rasterIndex = rasterRow + x;
                            var twiddledIndex = tileBase + TwiddleIndex(x, y, side);

                            if (untwiddle)
                                destination[rasterIndex] = source[twiddledIndex];
                            else
                                destination[twiddledIndex] = source[rasterIndex];
                        }
                    }
                }
            }
        }

        /// <summary>
        ///   Packs one 8-bit-per-channel texel into its 16-bit representation.
        /// </summary>
        public static ushort PackTexel(byte r, byte g, byte b, byte a, PvrPixelFormat format) => format switch
        {
            // Alpha is a single bit, so anything at or above half opacity rounds to opaque.
            PvrPixelFormat.Argb1555 => (ushort)((a >= 128 ? 1 : 0) << 15 | (r >> 3) << 10 | (g >> 3) << 5 | b >> 3),
            PvrPixelFormat.Rgb565 => (ushort)((r >> 3) << 11 | (g >> 2) << 5 | b >> 3),
            PvrPixelFormat.Argb4444 => (ushort)((a >> 4) << 12 | (r >> 4) << 8 | (g >> 4) << 4 | b >> 4),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a PVR colour format."),
        };

        /// <summary>
        ///   Expands a 16-bit texel back to 8 bits per channel.
        /// </summary>
        /// <remarks>
        ///   Each channel is widened by replicating its high bits into the low ones, so full-scale
        ///   values stay full-scale (5-bit 31 becomes 255, not 248) and a pack/unpack round trip
        ///   is stable.
        /// </remarks>
        public static void UnpackTexel(ushort texel, PvrPixelFormat format, out byte r, out byte g, out byte b, out byte a)
        {
            switch (format)
            {
                case PvrPixelFormat.Argb1555:
                    a = (byte)((texel >> 15) != 0 ? 255 : 0);
                    r = Expand5((texel >> 10) & 0x1F);
                    g = Expand5((texel >> 5) & 0x1F);
                    b = Expand5(texel & 0x1F);
                    break;

                case PvrPixelFormat.Rgb565:
                    a = 255;
                    r = Expand5((texel >> 11) & 0x1F);
                    g = Expand6((texel >> 5) & 0x3F);
                    b = Expand5(texel & 0x1F);
                    break;

                case PvrPixelFormat.Argb4444:
                    a = Expand4((texel >> 12) & 0x0F);
                    r = Expand4((texel >> 8) & 0x0F);
                    g = Expand4((texel >> 4) & 0x0F);
                    b = Expand4(texel & 0x0F);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, "Not a PVR colour format.");
            }
        }

        /// <summary>
        ///   Packs an R8G8B8A8 buffer into 16-bit texels, preserving raster order.
        /// </summary>
        public static void PackRgba(ReadOnlySpan<byte> rgba, Span<ushort> destination, PvrPixelFormat format)
        {
            var count = destination.Length;
            if (rgba.Length < count * 4)
                throw new ArgumentException("Source is smaller than the texel count being packed.", nameof(rgba));

            for (var i = 0; i < count; i++)
            {
                var o = i * 4;
                destination[i] = PackTexel(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3], format);
            }
        }

        /// <summary>
        ///   Expands 16-bit texels back into an R8G8B8A8 buffer, preserving raster order.
        /// </summary>
        public static void UnpackRgba(ReadOnlySpan<ushort> source, Span<byte> rgba, PvrPixelFormat format)
        {
            if (rgba.Length < source.Length * 4)
                throw new ArgumentException("Destination is smaller than the texel count being unpacked.", nameof(rgba));

            for (var i = 0; i < source.Length; i++)
            {
                UnpackTexel(source[i], format, out var r, out var g, out var b, out var a);
                var o = i * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = a;
            }
        }

        /// <summary>
        ///   Builds a vector-quantisation codebook for a square texture and the index byte per
        ///   2x2 block that selects from it.
        /// </summary>
        /// <param name="rasterTexels">The texture in raster order, already packed to 16 bits.</param>
        /// <param name="width">Texture width; must equal <paramref name="height"/> and be a power of two.</param>
        /// <param name="height">Texture height.</param>
        /// <param name="format">The colour format the texels are packed in, used when averaging.</param>
        /// <param name="codebook">
        ///   Receives <see cref="PvrFormat.VqCodebookEntries"/> entries of
        ///   <see cref="PvrFormat.VqBlockTexels"/> texels. Unused entries are left zeroed.
        /// </param>
        /// <param name="indices">Receives one byte per block, in Morton order over the block grid.</param>
        /// <remarks>
        ///   <para>
        ///     When the texture has no more distinct blocks than the codebook has entries — which
        ///     is the common case for UI art, sprites and anything with flat colour — each distinct
        ///     block gets its own entry and the encoding is lossless at 16-bit precision.
        ///   </para>
        ///   <para>
        ///     Otherwise blocks are clustered by median cut: repeatedly split the bucket with the
        ///     widest spread along that widest channel, until the codebook is full. Chosen over
        ///     k-means because a block's bucket is its index, so no nearest-neighbour pass over
        ///     every block is needed, and because it has no seeding to make random — the same
        ///     texture always encodes to the same bytes, which a build system depends on.
        ///   </para>
        /// </remarks>
        public static void BuildVqCodebook(
            ReadOnlySpan<ushort> rasterTexels,
            int width,
            int height,
            PvrPixelFormat format,
            Span<ushort> codebook,
            Span<byte> indices)
        {
            if (width != height)
                throw new ArgumentException($"Vector quantisation needs a square texture; got {width}x{height}.");
            if (!IsPowerOfTwo(width) || width < PvrFormat.VqBlockSide)
                throw new ArgumentException($"Vector quantisation needs a power-of-two size of at least {PvrFormat.VqBlockSide}; got {width}.");
            if (codebook.Length < PvrFormat.VqCodebookEntries * PvrFormat.VqBlockTexels)
                throw new ArgumentException("Codebook span is too small.", nameof(codebook));

            var gridSide = width / PvrFormat.VqBlockSide;
            var blockCount = gridSide * gridSide;
            if (indices.Length < blockCount)
                throw new ArgumentException("Index span is too small.", nameof(indices));

            // Gather every 2x2 block. Texels within a block follow the same Morton order as the
            // texture at large, so entry slot k holds the texel at (k >> 1, k & 1).
            var blocks = new ushort[blockCount * PvrFormat.VqBlockTexels];
            for (var by = 0; by < gridSide; by++)
            {
                for (var bx = 0; bx < gridSide; bx++)
                {
                    var blockIndex = by * gridSide + bx;
                    for (var k = 0; k < PvrFormat.VqBlockTexels; k++)
                    {
                        var x = bx * PvrFormat.VqBlockSide + (k >> 1);
                        var y = by * PvrFormat.VqBlockSide + (k & 1);
                        blocks[blockIndex * PvrFormat.VqBlockTexels + k] = rasterTexels[y * width + x];
                    }
                }
            }

            var blockEntry = new byte[blockCount];
            var entryCount = TryAssignDistinctBlocks(blocks, blockCount, codebook, blockEntry);
            if (entryCount < 0)
                ClusterBlocks(blocks, blockCount, format, codebook, blockEntry);

            // The index bytes are themselves twiddled, over the grid of blocks rather than texels.
            for (var by = 0; by < gridSide; by++)
            {
                for (var bx = 0; bx < gridSide; bx++)
                {
                    indices[TwiddleIndex(bx, by, gridSide)] = blockEntry[by * gridSide + bx];
                }
            }
        }

        /// <summary>
        ///   Gives every distinct block its own codebook entry, or returns -1 if there are more
        ///   distinct blocks than the codebook can hold.
        /// </summary>
        private static int TryAssignDistinctBlocks(ushort[] blocks, int blockCount, Span<ushort> codebook, byte[] blockEntry)
        {
            var seen = new Dictionary<(ushort, ushort, ushort, ushort), byte>();

            for (var i = 0; i < blockCount; i++)
            {
                var o = i * PvrFormat.VqBlockTexels;
                var key = (blocks[o], blocks[o + 1], blocks[o + 2], blocks[o + 3]);

                if (!seen.TryGetValue(key, out var entry))
                {
                    if (seen.Count == PvrFormat.VqCodebookEntries)
                        return -1;

                    entry = (byte)seen.Count;
                    seen.Add(key, entry);

                    var dst = entry * PvrFormat.VqBlockTexels;
                    codebook[dst] = key.Item1;
                    codebook[dst + 1] = key.Item2;
                    codebook[dst + 2] = key.Item3;
                    codebook[dst + 3] = key.Item4;
                }

                blockEntry[i] = entry;
            }

            return seen.Count;
        }

        // A block as 16 channel values (4 texels x RGBA), which is the space the split happens in:
        // comparing unpacked channels rather than packed words keeps "close colours" meaningful.
        private const int BlockDimensions = PvrFormat.VqBlockTexels * 4;

        private static void ClusterBlocks(ushort[] blocks, int blockCount, PvrPixelFormat format, Span<ushort> codebook, byte[] blockEntry)
        {
            var channels = new byte[blockCount * BlockDimensions];
            for (var i = 0; i < blockCount; i++)
            {
                for (var k = 0; k < PvrFormat.VqBlockTexels; k++)
                {
                    UnpackTexel(blocks[i * PvrFormat.VqBlockTexels + k], format, out var r, out var g, out var b, out var a);
                    var o = i * BlockDimensions + k * 4;
                    channels[o] = r;
                    channels[o + 1] = g;
                    channels[o + 2] = b;
                    channels[o + 3] = a;
                }
            }

            // Buckets partition `order`; splitting a bucket only permutes its own slice.
            var order = new int[blockCount];
            for (var i = 0; i < blockCount; i++) order[i] = i;

            var buckets = new List<(int Start, int Length)>(PvrFormat.VqCodebookEntries) { (0, blockCount) };

            while (buckets.Count < PvrFormat.VqCodebookEntries)
            {
                var bestBucket = -1;
                var bestDimension = -1;
                var bestSpread = 0;

                for (var i = 0; i < buckets.Count; i++)
                {
                    var (start, length) = buckets[i];
                    if (length < 2) continue;

                    FindWidestDimension(channels, order, start, length, out var dimension, out var spread);
                    if (spread > bestSpread)
                    {
                        bestSpread = spread;
                        bestBucket = i;
                        bestDimension = dimension;
                    }
                }

                // Every remaining bucket is a single block or holds identical blocks: splitting
                // further would only duplicate entries.
                if (bestBucket < 0) break;

                var (bucketStart, bucketLength) = buckets[bestBucket];
                SortBucketByDimension(channels, order, bucketStart, bucketLength, bestDimension);

                var half = bucketLength / 2;
                buckets[bestBucket] = (bucketStart, half);
                buckets.Add((bucketStart + half, bucketLength - half));
            }

            for (var entry = 0; entry < buckets.Count; entry++)
            {
                var (start, length) = buckets[entry];
                for (var i = 0; i < length; i++)
                    blockEntry[order[start + i]] = (byte)entry;

                WriteBucketAverage(channels, order, start, length, format, codebook, entry);
            }
        }

        private static void FindWidestDimension(byte[] channels, int[] order, int start, int length, out int dimension, out int spread)
        {
            dimension = 0;
            spread = 0;

            for (var d = 0; d < BlockDimensions; d++)
            {
                int min = byte.MaxValue, max = 0;
                for (var i = 0; i < length; i++)
                {
                    var v = channels[order[start + i] * BlockDimensions + d];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }

                var range = max - min;
                if (range > spread)
                {
                    spread = range;
                    dimension = d;
                }
            }
        }

        private static void SortBucketByDimension(byte[] channels, int[] order, int start, int length, int dimension)
        {
            var keys = new long[length];
            var slice = new int[length];
            for (var i = 0; i < length; i++)
            {
                slice[i] = order[start + i];
                // Tie-break on the block index so equal channel values keep a stable, and therefore
                // reproducible, order. The channel occupies the high word so the index can never
                // carry into it, however many blocks the texture has.
                keys[i] = ((long)channels[slice[i] * BlockDimensions + dimension] << 32) | (uint)slice[i];
            }

            Array.Sort(keys, slice);
            Array.Copy(slice, 0, order, start, length);
        }

        private static void WriteBucketAverage(
            byte[] channels, int[] order, int start, int length, PvrPixelFormat format, Span<ushort> codebook, int entry)
        {
            Span<int> totals = stackalloc int[BlockDimensions];
            for (var i = 0; i < length; i++)
            {
                var blockBase = order[start + i] * BlockDimensions;
                for (var d = 0; d < BlockDimensions; d++)
                    totals[d] += channels[blockBase + d];
            }

            var dst = entry * PvrFormat.VqBlockTexels;
            for (var k = 0; k < PvrFormat.VqBlockTexels; k++)
            {
                var o = k * 4;
                codebook[dst + k] = PackTexel(
                    (byte)(totals[o] / length),
                    (byte)(totals[o + 1] / length),
                    (byte)(totals[o + 2] / length),
                    (byte)(totals[o + 3] / length),
                    format);
            }
        }

        /// <summary>
        ///   Writes a PVR file: an optional <c>GBIX</c> chunk, the <c>PVRT</c> header, then
        ///   <paramref name="textureData"/> unchanged.
        /// </summary>
        /// <param name="globalIndex">
        ///   The global index to record in a <c>GBIX</c> chunk, or <c>null</c> to omit the chunk.
        /// </param>
        public static byte[] WriteContainer(
            ReadOnlySpan<byte> textureData,
            int width,
            int height,
            PvrPixelFormat pixelFormat,
            PvrDataFormat dataFormat,
            uint? globalIndex = null)
        {
            if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
                throw new ArgumentException($"{width}x{height} does not fit the container's 16-bit dimensions.");

            var gbixSize = globalIndex.HasValue ? PvrFormat.GbixChunkSize : 0;
            var file = new byte[gbixSize + PvrFormat.PvrtHeaderSize + textureData.Length];
            var span = file.AsSpan();

            if (globalIndex.HasValue)
            {
                PvrFormat.GbixMagic.CopyTo(span);
                // The length counts only what follows it, which is the 4-byte index.
                BinaryPrimitives.WriteUInt32LittleEndian(span[4..], sizeof(uint));
                BinaryPrimitives.WriteUInt32LittleEndian(span[8..], globalIndex.Value);
                span = span[PvrFormat.GbixChunkSize..];
            }

            PvrFormat.PvrtMagic.CopyTo(span);
            // Likewise counts what follows: the 8 header bytes below, plus the texture data.
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(PvrFormat.PvrtCountedHeaderSize + textureData.Length));
            span[8] = (byte)pixelFormat;
            span[9] = (byte)dataFormat;
            span[10] = 0;
            span[11] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)height);
            textureData.CopyTo(span[PvrFormat.PvrtHeaderSize..]);

            return file;
        }

        /// <summary>
        ///   Reads a PVR file's header, skipping a <c>GBIX</c> chunk if present.
        /// </summary>
        /// <returns>The texture data, as a slice of <paramref name="file"/>.</returns>
        public static ReadOnlySpan<byte> ReadContainer(
            ReadOnlySpan<byte> file,
            out int width,
            out int height,
            out PvrPixelFormat pixelFormat,
            out PvrDataFormat dataFormat,
            out uint? globalIndex)
        {
            globalIndex = null;

            if (file.Length >= PvrFormat.GbixChunkSize && file[..4].SequenceEqual(PvrFormat.GbixMagic))
            {
                var gbixLength = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
                // Some writers pad the chunk past the index; trust the length field, not the size.
                var gbixTotal = 8 + (int)gbixLength;
                if (gbixLength < sizeof(uint) || gbixTotal > file.Length)
                    throw new InvalidOperationException("PVR file has a malformed GBIX chunk.");

                globalIndex = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
                file = file[gbixTotal..];
            }

            if (file.Length < PvrFormat.PvrtHeaderSize || !file[..4].SequenceEqual(PvrFormat.PvrtMagic))
                throw new InvalidOperationException("Not a PVR file: no PVRT chunk found.");

            var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
            if (dataLength < PvrFormat.PvrtCountedHeaderSize)
                throw new InvalidOperationException("PVR file has a PVRT chunk shorter than its own header.");

            pixelFormat = (PvrPixelFormat)file[8];
            dataFormat = (PvrDataFormat)file[9];
            width = BinaryPrimitives.ReadUInt16LittleEndian(file[12..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(file[14..]);

            var textureLength = (int)dataLength - PvrFormat.PvrtCountedHeaderSize;
            var available = file.Length - PvrFormat.PvrtHeaderSize;
            if (textureLength > available)
                throw new InvalidOperationException($"PVR file is truncated: header declares {textureLength} bytes of texture data, {available} present.");

            return file.Slice(PvrFormat.PvrtHeaderSize, textureLength);
        }

        internal static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

        private static byte Expand5(int v) => (byte)(v << 3 | v >> 2);
        private static byte Expand6(int v) => (byte)(v << 2 | v >> 4);
        private static byte Expand4(int v) => (byte)(v << 4 | v);
    }
}
