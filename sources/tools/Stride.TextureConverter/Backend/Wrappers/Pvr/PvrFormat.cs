// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Graphics;

namespace Stride.TextureConverter.PvrWrapper
{
    /// <summary>
    ///   The colour layout of a single texel, as written in the PVR header's pixel-format byte.
    /// </summary>
    /// <remarks>
    ///   All three are 16 bits little-endian, and each maps exactly onto a <see cref="PixelFormat"/>
    ///   Stride already has — the bit order the Dreamcast calls ARGB is what DXGI calls BGRA. That
    ///   is why the encoder never reorders channels: see <see cref="PvrFormat.ToPixelFormat"/>.
    /// </remarks>
    public enum PvrPixelFormat : byte
    {
        /// <summary>1-bit alpha, 5 bits per colour channel. <see cref="PixelFormat.B5G5R5A1_UNorm"/>.</summary>
        Argb1555 = 0x00,

        /// <summary>No alpha, 5/6/5 bits. <see cref="PixelFormat.B5G6R5_UNorm"/>.</summary>
        Rgb565 = 0x01,

        /// <summary>4 bits per channel including alpha. <see cref="PixelFormat.B4G4R4A4_UNorm"/>.</summary>
        Argb4444 = 0x02,

        // 0x03 YUV422, 0x04 BUMP and the 0x05/0x06 palettised formats are part of the container
        // but not produced here — none of them is a target Stride's pipeline can reach from an
        // RGBA source without a separate quantisation or colour-space step.
    }

    /// <summary>
    ///   How texels are arranged in the data that follows the PVR header, as written in the
    ///   header's data-format byte.
    /// </summary>
    /// <remarks>
    ///   Orthogonal to <see cref="PvrPixelFormat"/>: the pixel-format byte says what a texel looks
    ///   like, this says where to find it. The values are the container's, not sequential.
    /// </remarks>
    public enum PvrDataFormat : byte
    {
        /// <summary>Square, power-of-two, Morton-ordered. The format the hardware samples fastest.</summary>
        SquareTwiddled = 0x01,

        /// <summary>As <see cref="SquareTwiddled"/>, with a mipmap chain ordered 1x1 upwards.</summary>
        SquareTwiddledMipmap = 0x02,

        /// <summary>Vector quantised: a 2048-byte codebook of 2x2 texel blocks, then one index byte per block.</summary>
        Vq = 0x03,

        /// <summary>As <see cref="Vq"/>, with a mipmap chain ordered 1x1 upwards.</summary>
        VqMipmap = 0x04,

        /// <summary>Plain raster order. The only layout that accepts non-power-of-two sizes.</summary>
        Rectangle = 0x09,

        /// <summary>Non-square, Morton-ordered in square tiles of side <c>min(width, height)</c>.</summary>
        RectangleTwiddled = 0x0D,

        // Small VQ (0x10/0x11) is deliberately absent: it shrinks the codebook for small textures,
        // but the entry count per size differs between the tools that write it, and there is no
        // hardware here to settle which is right. Vq always emits a full 256-entry codebook.
    }

    /// <summary>
    ///   Constants and conversions for the Dreamcast PVR container.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///     A PVR file is a 16-byte <c>PVRT</c> chunk header followed by texture data, optionally
    ///     preceded by a <c>GBIX</c> chunk carrying a global index (an asset id the Dreamcast SDK
    ///     uses for texture management; games that do not use it omit the chunk).
    ///   </para>
    ///   <para>
    ///     This layout is implemented from the published format description. It has not been
    ///     validated against real hardware or against files produced by Sega's own tools — see
    ///     docs/build/dreamcast.md.
    ///   </para>
    /// </remarks>
    internal static class PvrFormat
    {
        /// <summary>Magic of the chunk that holds the texture itself.</summary>
        public static ReadOnlySpan<byte> PvrtMagic => "PVRT"u8;

        /// <summary>Magic of the optional global-index chunk that may precede <see cref="PvrtMagic"/>.</summary>
        public static ReadOnlySpan<byte> GbixMagic => "GBIX"u8;

        /// <summary>Bytes in a <c>PVRT</c> chunk header: magic, length, and the 8 counted bytes.</summary>
        public const int PvrtHeaderSize = 16;

        /// <summary>
        ///   The part of the <c>PVRT</c> header counted by its length field: the pixel-format and
        ///   data-format bytes, two reserved bytes, then width and height.
        /// </summary>
        public const int PvrtCountedHeaderSize = 8;

        /// <summary>Bytes in a <c>GBIX</c> chunk carrying a 4-byte index.</summary>
        public const int GbixChunkSize = 12;

        /// <summary>Every colour format this container supports is 16 bits wide.</summary>
        public const int BytesPerTexel = 2;

        /// <summary>Texels along each edge of one vector-quantised block.</summary>
        public const int VqBlockSide = 2;

        /// <summary>Texels in one vector-quantised block.</summary>
        public const int VqBlockTexels = VqBlockSide * VqBlockSide;

        /// <summary>Entries in a full vector-quantisation codebook, one per possible index byte.</summary>
        public const int VqCodebookEntries = 256;

        /// <summary>Bytes in a full vector-quantisation codebook.</summary>
        public const int VqCodebookSize = VqCodebookEntries * VqBlockTexels * BytesPerTexel;

        /// <summary>
        ///   Padding that precedes the 1x1 level of a mipmapped texture.
        /// </summary>
        /// <remarks>
        ///   One texel's worth for the uncompressed layouts, one index byte for the quantised ones.
        ///   Isolated here because it is the one part of the layout this implementation takes on
        ///   documentation alone; if a real Dreamcast disagrees, this is the constant to change.
        /// </remarks>
        public static int GetMipmapPadding(PvrDataFormat dataFormat)
            => IsVectorQuantised(dataFormat) ? 1 : BytesPerTexel;

        /// <summary>Whether <paramref name="dataFormat"/> stores a codebook and index bytes.</summary>
        public static bool IsVectorQuantised(PvrDataFormat dataFormat)
            => dataFormat is PvrDataFormat.Vq or PvrDataFormat.VqMipmap;

        /// <summary>Whether <paramref name="dataFormat"/> stores a mipmap chain.</summary>
        public static bool IsMipmapped(PvrDataFormat dataFormat)
            => dataFormat is PvrDataFormat.SquareTwiddledMipmap or PvrDataFormat.VqMipmap;

        /// <summary>Whether <paramref name="dataFormat"/> stores texels in Morton order.</summary>
        public static bool IsTwiddled(PvrDataFormat dataFormat)
            => dataFormat is PvrDataFormat.SquareTwiddled
                          or PvrDataFormat.SquareTwiddledMipmap
                          or PvrDataFormat.RectangleTwiddled
                          // VQ index bytes are themselves laid out in Morton order over the block grid.
                          or PvrDataFormat.Vq
                          or PvrDataFormat.VqMipmap;

        /// <summary>
        ///   The mipmapped counterpart of <paramref name="dataFormat"/>, or the value itself when
        ///   that layout has none.
        /// </summary>
        public static PvrDataFormat WithMipmaps(PvrDataFormat dataFormat) => dataFormat switch
        {
            PvrDataFormat.SquareTwiddled or PvrDataFormat.SquareTwiddledMipmap => PvrDataFormat.SquareTwiddledMipmap,
            PvrDataFormat.Vq or PvrDataFormat.VqMipmap => PvrDataFormat.VqMipmap,
            // Rectangle and RectangleTwiddled have no mipmapped form in the container.
            _ => dataFormat,
        };

        /// <summary>
        ///   The <see cref="PixelFormat"/> a <paramref name="pixelFormat"/> texel already is.
        /// </summary>
        public static PixelFormat ToPixelFormat(PvrPixelFormat pixelFormat) => pixelFormat switch
        {
            PvrPixelFormat.Argb1555 => PixelFormat.B5G5R5A1_UNorm,
            PvrPixelFormat.Rgb565 => PixelFormat.B5G6R5_UNorm,
            PvrPixelFormat.Argb4444 => PixelFormat.B4G4R4A4_UNorm,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat), pixelFormat, "Not a PVR colour format."),
        };

        /// <summary>
        ///   The PVR colour format matching <paramref name="format"/>, or <c>null</c> if the
        ///   container cannot represent it.
        /// </summary>
        public static PvrPixelFormat? FromPixelFormat(PixelFormat format) => format switch
        {
            PixelFormat.B5G5R5A1_UNorm => PvrPixelFormat.Argb1555,
            PixelFormat.B5G6R5_UNorm => PvrPixelFormat.Rgb565,
            PixelFormat.B4G4R4A4_UNorm => PvrPixelFormat.Argb4444,
            _ => null,
        };

        /// <summary>Whether <paramref name="format"/> is one this container can hold.</summary>
        public static bool IsSupportedPixelFormat(PixelFormat format) => FromPixelFormat(format) is not null;
    }
}
