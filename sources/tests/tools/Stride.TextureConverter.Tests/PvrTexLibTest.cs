// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Runtime.InteropServices;
using Stride.Graphics;
using Stride.TextureConverter.PvrWrapper;
using Xunit;

namespace Stride.TextureConverter.Tests
{
    /// <summary>
    ///   Tests for the Dreamcast PVR converter, wrapper and exporter.
    /// </summary>
    /// <remarks>
    ///   The twiddle orders and packed texel values below are hand-derived from the format
    ///   description rather than captured from this implementation, so they fail if the encoder
    ///   drifts. What they cannot prove is that the description matches real hardware — there is
    ///   no Dreamcast and no reference file corpus here. See docs/build/dreamcast.md.
    /// </remarks>
    public class PvrTexLibTest
    {
        // -------------------------------------------------------------------------------------
        // Twiddling
        // -------------------------------------------------------------------------------------

        [Fact]
        public void TwiddleIndexRunsDownColumnsInA2x2()
        {
            // Y supplies the low bit, so the walk is (0,0), (0,1), (1,0), (1,1).
            Assert.Equal(0, PvrEncoder.TwiddleIndex(0, 0, 2));
            Assert.Equal(1, PvrEncoder.TwiddleIndex(0, 1, 2));
            Assert.Equal(2, PvrEncoder.TwiddleIndex(1, 0, 2));
            Assert.Equal(3, PvrEncoder.TwiddleIndex(1, 1, 2));
        }

        [Fact]
        public void TwiddleProducesTheKnownOrderForA4x4()
        {
            // Raster values 0..15 laid out row-major, then Morton-ordered.
            var raster = new ushort[16];
            for (ushort i = 0; i < 16; i++) raster[i] = i;

            var twiddled = new ushort[16];
            PvrEncoder.Twiddle(raster, twiddled, 4, 4);

            Assert.Equal(new ushort[] { 0, 4, 1, 5, 8, 12, 9, 13, 2, 6, 3, 7, 10, 14, 11, 15 }, twiddled);
        }

        [Theory]
        [InlineData(2, 2)]
        [InlineData(4, 4)]
        [InlineData(8, 8)]
        [InlineData(64, 64)]
        [InlineData(8, 32)]   // taller than wide: four 8x8 tiles stacked
        [InlineData(32, 8)]   // wider than tall: four 8x8 tiles side by side
        public void TwiddleRoundTrips(int width, int height)
        {
            var raster = new ushort[width * height];
            for (var i = 0; i < raster.Length; i++) raster[i] = (ushort)(i * 7 + 1);

            var twiddled = new ushort[raster.Length];
            var restored = new ushort[raster.Length];

            PvrEncoder.Twiddle(raster, twiddled, width, height);
            PvrEncoder.Untwiddle(twiddled, restored, width, height);

            Assert.Equal(raster, restored);
        }

        [Fact]
        public void TwiddleIsAPermutation()
        {
            // Every source texel must land somewhere, exactly once.
            var raster = new ushort[64];
            for (ushort i = 0; i < 64; i++) raster[i] = (ushort)(i + 1);

            var twiddled = new ushort[64];
            PvrEncoder.Twiddle(raster, twiddled, 8, 8);

            Array.Sort(twiddled);
            for (ushort i = 0; i < 64; i++) Assert.Equal((ushort)(i + 1), twiddled[i]);
        }

        [Fact]
        public void TwiddleRejectsNonPowerOfTwoTiles()
        {
            var raster = new ushort[12 * 12];
            var twiddled = new ushort[12 * 12];
            Assert.Throws<ArgumentException>(() => PvrEncoder.Twiddle(raster, twiddled, 12, 12));
        }

        // -------------------------------------------------------------------------------------
        // Texel packing
        // -------------------------------------------------------------------------------------

        [Theory]
        // RGB565: red occupies the top 5 bits, green the middle 6, blue the bottom 5.
        [InlineData(255, 0, 0, 255, PvrPixelFormat.Rgb565, 0xF800)]
        [InlineData(0, 255, 0, 255, PvrPixelFormat.Rgb565, 0x07E0)]
        [InlineData(0, 0, 255, 255, PvrPixelFormat.Rgb565, 0x001F)]
        [InlineData(255, 255, 255, 255, PvrPixelFormat.Rgb565, 0xFFFF)]
        [InlineData(0, 0, 0, 255, PvrPixelFormat.Rgb565, 0x0000)]
        // ARGB1555: one alpha bit on top, then 5 bits each.
        [InlineData(255, 255, 255, 255, PvrPixelFormat.Argb1555, 0xFFFF)]
        [InlineData(255, 0, 0, 255, PvrPixelFormat.Argb1555, 0xFC00)]
        [InlineData(0, 0, 0, 0, PvrPixelFormat.Argb1555, 0x0000)]
        // ARGB4444: four bits each, alpha first.
        [InlineData(255, 255, 255, 255, PvrPixelFormat.Argb4444, 0xFFFF)]
        [InlineData(0x10, 0x20, 0x30, 0x40, PvrPixelFormat.Argb4444, 0x4123)]
        public void PackTexelMatchesTheDocumentedBitLayout(int r, int g, int b, int a, PvrPixelFormat format, int expected)
        {
            Assert.Equal((ushort)expected, PvrEncoder.PackTexel((byte)r, (byte)g, (byte)b, (byte)a, format));
        }

        [Theory]
        [InlineData(127, 0x0000)] // just below half: rounds to transparent
        [InlineData(128, 0x8000)] // at half: rounds to opaque
        public void OneBitAlphaRoundsAtHalf(int alpha, int expected)
        {
            Assert.Equal((ushort)expected, PvrEncoder.PackTexel(0, 0, 0, (byte)alpha, PvrPixelFormat.Argb1555));
        }

        [Theory]
        [InlineData(PvrPixelFormat.Rgb565)]
        [InlineData(PvrPixelFormat.Argb1555)]
        [InlineData(PvrPixelFormat.Argb4444)]
        public void FullScaleChannelsSurviveARoundTrip(PvrPixelFormat format)
        {
            // Bit replication has to carry 31/63/15 back to 255, or white drifts grey.
            var packed = PvrEncoder.PackTexel(255, 255, 255, 255, format);
            PvrEncoder.UnpackTexel(packed, format, out var r, out var g, out var b, out var a);

            Assert.Equal(255, r);
            Assert.Equal(255, g);
            Assert.Equal(255, b);
            Assert.Equal(255, a);
        }

        [Theory]
        [InlineData(PvrPixelFormat.Rgb565)]
        [InlineData(PvrPixelFormat.Argb1555)]
        [InlineData(PvrPixelFormat.Argb4444)]
        public void PackingIsIdempotentOnceQuantised(PvrPixelFormat format)
        {
            // A value that survived one round trip must survive every later one unchanged,
            // otherwise repeated conversions would walk the colour away from itself.
            for (var v = 0; v < 256; v += 17)
            {
                var first = PvrEncoder.PackTexel((byte)v, (byte)v, (byte)v, 255, format);
                PvrEncoder.UnpackTexel(first, format, out var r, out var g, out var b, out var a);
                var second = PvrEncoder.PackTexel(r, g, b, a, format);

                Assert.Equal(first, second);
            }
        }

        // -------------------------------------------------------------------------------------
        // Container
        // -------------------------------------------------------------------------------------

        [Fact]
        public void ContainerHeaderCarriesTheDeclaredFieldsAndLength()
        {
            var textureData = new byte[8 * 8 * 2];
            var file = PvrEncoder.WriteContainer(textureData, 8, 8, PvrPixelFormat.Rgb565, PvrDataFormat.SquareTwiddled);

            Assert.Equal(PvrFormat.PvrtHeaderSize + textureData.Length, file.Length);
            Assert.Equal("PVRT"u8.ToArray(), file[..4]);
            // The length field counts the 8 header bytes after it, plus the texture data.
            Assert.Equal((uint)(PvrFormat.PvrtCountedHeaderSize + textureData.Length), BitConverter.ToUInt32(file, 4));
            Assert.Equal((byte)PvrPixelFormat.Rgb565, file[8]);
            Assert.Equal((byte)PvrDataFormat.SquareTwiddled, file[9]);
            Assert.Equal(8, BitConverter.ToUInt16(file, 12));
            Assert.Equal(8, BitConverter.ToUInt16(file, 14));
        }

        [Fact]
        public void ContainerOmitsGbixUnlessAGlobalIndexIsGiven()
        {
            var textureData = new byte[2 * 2 * 2];

            var without = PvrEncoder.WriteContainer(textureData, 2, 2, PvrPixelFormat.Rgb565, PvrDataFormat.SquareTwiddled);
            Assert.Equal("PVRT"u8.ToArray(), without[..4]);

            var with = PvrEncoder.WriteContainer(textureData, 2, 2, PvrPixelFormat.Rgb565, PvrDataFormat.SquareTwiddled, globalIndex: 0xDEADBEEF);
            Assert.Equal("GBIX"u8.ToArray(), with[..4]);
            Assert.Equal(4u, BitConverter.ToUInt32(with, 4));
            Assert.Equal(0xDEADBEEF, BitConverter.ToUInt32(with, 8));
            Assert.Equal("PVRT"u8.ToArray(), with[12..16]);
            Assert.Equal(without.Length + PvrFormat.GbixChunkSize, with.Length);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0x12345678u)]
        public void ContainerRoundTrips(uint? globalIndex)
        {
            var textureData = new byte[4 * 4 * 2];
            for (var i = 0; i < textureData.Length; i++) textureData[i] = (byte)(i * 3);

            var file = PvrEncoder.WriteContainer(textureData, 4, 4, PvrPixelFormat.Argb4444, PvrDataFormat.SquareTwiddled, globalIndex);
            var read = PvrEncoder.ReadContainer(file, out var width, out var height, out var pixelFormat, out var dataFormat, out var readIndex);

            Assert.Equal(4, width);
            Assert.Equal(4, height);
            Assert.Equal(PvrPixelFormat.Argb4444, pixelFormat);
            Assert.Equal(PvrDataFormat.SquareTwiddled, dataFormat);
            Assert.Equal(globalIndex, readIndex);
            Assert.Equal(textureData, read.ToArray());
        }

        [Fact]
        public void ReadContainerRejectsRubbish()
        {
            Assert.Throws<InvalidOperationException>(
                () => PvrEncoder.ReadContainer(new byte[32], out _, out _, out _, out _, out _));
        }

        [Fact]
        public void ReadContainerRejectsATruncatedFile()
        {
            var file = PvrEncoder.WriteContainer(new byte[4 * 4 * 2], 4, 4, PvrPixelFormat.Rgb565, PvrDataFormat.SquareTwiddled);
            var truncated = file[..^8];

            Assert.Throws<InvalidOperationException>(
                () => PvrEncoder.ReadContainer(truncated, out _, out _, out _, out _, out _));
        }

        // -------------------------------------------------------------------------------------
        // Vector quantisation
        // -------------------------------------------------------------------------------------

        [Fact]
        public void VectorQuantisationIsLosslessBelowTheCodebookLimit()
        {
            // 8x8 is 16 blocks, well under the 256 entries available, so every distinct block
            // gets its own entry and nothing is approximated.
            const int side = 8;
            var raster = new ushort[side * side];
            for (var i = 0; i < raster.Length; i++) raster[i] = (ushort)(i * 257 + 3);

            var codebook = new ushort[PvrFormat.VqCodebookEntries * PvrFormat.VqBlockTexels];
            var indices = new byte[side / 2 * (side / 2)];
            PvrEncoder.BuildVqCodebook(raster, side, side, PvrPixelFormat.Argb4444, codebook, indices);

            // Rebuild from the codebook the way the hardware would and compare.
            var gridSide = side / PvrFormat.VqBlockSide;
            for (var by = 0; by < gridSide; by++)
            {
                for (var bx = 0; bx < gridSide; bx++)
                {
                    var entry = indices[PvrEncoder.TwiddleIndex(bx, by, gridSide)];
                    for (var k = 0; k < PvrFormat.VqBlockTexels; k++)
                    {
                        var x = bx * PvrFormat.VqBlockSide + (k >> 1);
                        var y = by * PvrFormat.VqBlockSide + (k & 1);
                        Assert.Equal(raster[y * side + x], codebook[entry * PvrFormat.VqBlockTexels + k]);
                    }
                }
            }
        }

        [Fact]
        public void VectorQuantisationHandlesMoreBlocksThanEntries()
        {
            // 64x64 is 1024 blocks of mostly distinct noise, so the clusterer has to run.
            const int side = 64;
            var raster = new ushort[side * side];
            for (var i = 0; i < raster.Length; i++) raster[i] = (ushort)(i * 2654435761u % 65536);

            var codebook = new ushort[PvrFormat.VqCodebookEntries * PvrFormat.VqBlockTexels];
            var indices = new byte[side / 2 * (side / 2)];

            PvrEncoder.BuildVqCodebook(raster, side, side, PvrPixelFormat.Rgb565, codebook, indices);

            Assert.Equal(side / 2 * (side / 2), indices.Length);
        }

        [Fact]
        public void VectorQuantisationIsDeterministic()
        {
            // A build that produces different bytes from one run to the next breaks incremental
            // builds and content hashing, so this is a hard requirement, not a nicety.
            const int side = 32;
            var raster = new ushort[side * side];
            for (var i = 0; i < raster.Length; i++) raster[i] = (ushort)(i * 40503 % 65536);

            var firstBook = new ushort[PvrFormat.VqCodebookEntries * PvrFormat.VqBlockTexels];
            var firstIndices = new byte[side / 2 * (side / 2)];
            var secondBook = new ushort[PvrFormat.VqCodebookEntries * PvrFormat.VqBlockTexels];
            var secondIndices = new byte[side / 2 * (side / 2)];

            PvrEncoder.BuildVqCodebook(raster, side, side, PvrPixelFormat.Rgb565, firstBook, firstIndices);
            PvrEncoder.BuildVqCodebook(raster, side, side, PvrPixelFormat.Rgb565, secondBook, secondIndices);

            Assert.Equal(firstBook, secondBook);
            Assert.Equal(firstIndices, secondIndices);
        }

        [Fact]
        public void VectorQuantisationRejectsNonSquareTextures()
        {
            var raster = new ushort[8 * 4];
            var codebook = new ushort[PvrFormat.VqCodebookEntries * PvrFormat.VqBlockTexels];
            var indices = new byte[8];

            Assert.Throws<ArgumentException>(
                () => PvrEncoder.BuildVqCodebook(raster, 8, 4, PvrPixelFormat.Rgb565, codebook, indices));
        }

        // -------------------------------------------------------------------------------------
        // Export and load, end to end through TextureTool
        // -------------------------------------------------------------------------------------

        [Theory]
        [InlineData(PvrDataFormat.SquareTwiddled)]
        [InlineData(PvrDataFormat.Rectangle)]
        [InlineData(PvrDataFormat.Vq)]
        public void ExportThenLoadRoundTripsTheTexels(PvrDataFormat dataFormat)
        {
            const int side = 16;
            var texels = new ushort[side * side];
            // Repeat a small palette so vector quantisation stays lossless and all three layouts
            // can be compared against the same expectation.
            for (var i = 0; i < texels.Length; i++)
                texels[i] = (ushort)(0x0821 * (i % 8 + 1));

            var path = GetOutputPath($"roundtrip-{dataFormat}.pvr");
            using (var tool = new TextureTool())
            using (var image = CreateImage(texels, side, side, PixelFormat.B5G6R5_UNorm))
            {
                tool.SavePvr(image, path, dataFormat);
            }

            Assert.True(File.Exists(path));

            using var reloadTool = new TextureTool();
            using var reloaded = reloadTool.Load(path);

            Assert.Equal(side, reloaded.Width);
            Assert.Equal(side, reloaded.Height);
            Assert.Equal(PixelFormat.B5G6R5_UNorm, reloaded.Format);
            Assert.Equal(texels, ReadTexels(reloaded));
        }

        [Fact]
        public void ExportWritesTheLayoutItWasAskedFor()
        {
            const int side = 16;
            var texels = new ushort[side * side];
            var path = GetOutputPath("layout.pvr");

            using (var tool = new TextureTool())
            using (var image = CreateImage(texels, side, side, PixelFormat.B5G5R5A1_UNorm))
            {
                tool.SavePvr(image, path, PvrDataFormat.Vq, globalIndex: 42);
            }

            var file = File.ReadAllBytes(path);
            PvrEncoder.ReadContainer(file, out _, out _, out var pixelFormat, out var dataFormat, out var globalIndex);

            Assert.Equal(PvrPixelFormat.Argb1555, pixelFormat);
            Assert.Equal(PvrDataFormat.Vq, dataFormat);
            Assert.Equal(42u, globalIndex);
            // Codebook plus one index byte per 2x2 block.
            Assert.Equal(PvrFormat.PvrtHeaderSize + PvrFormat.GbixChunkSize + PvrFormat.VqCodebookSize + side / 2 * (side / 2), file.Length);
        }

        [Fact]
        public void ExportPacksAnEightBitSourceOnTheWayOut()
        {
            // Save should work straight from an RGBA image, without the caller converting first.
            const int side = 8;
            var rgba = new byte[side * side * 4];
            for (var i = 0; i < side * side; i++)
            {
                rgba[i * 4] = 255;      // R
                rgba[i * 4 + 3] = 255;  // A
            }

            var path = GetOutputPath("packed.pvr");
            var data = Marshal.AllocHGlobal(rgba.Length);
            try
            {
                Marshal.Copy(rgba, 0, data, rgba.Length);
                using var tool = new TextureTool();
                using var image = new TexImage(data, rgba.Length, side, side, 1, PixelFormat.R8G8B8A8_UNorm, 1, 1,
                    TexImage.TextureDimension.Texture2D, alphaDepth: 0);

                tool.SavePvr(image, path, PvrDataFormat.SquareTwiddled);
            }
            finally
            {
                // PvrTexLib replaces the buffer when it packs, freeing this one as it goes.
            }

            var file = File.ReadAllBytes(path);
            PvrEncoder.ReadContainer(file, out _, out _, out var pixelFormat, out _, out _);

            // Alpha depth 0 means the source has no alpha to keep, so RGB565 is the right fit.
            Assert.Equal(PvrPixelFormat.Rgb565, pixelFormat);
        }

        [Fact]
        public void ExportIsByteForByteReproducible()
        {
            const int side = 32;
            var texels = new ushort[side * side];
            for (var i = 0; i < texels.Length; i++) texels[i] = (ushort)(i * 40503 % 65536);

            var first = GetOutputPath("repro-1.pvr");
            var second = GetOutputPath("repro-2.pvr");

            foreach (var path in new[] { first, second })
            {
                using var tool = new TextureTool();
                using var image = CreateImage(texels, side, side, PixelFormat.B5G6R5_UNorm);
                tool.SavePvr(image, path, PvrDataFormat.Vq);
            }

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }

        [Fact]
        public void ExportWritesAMipmapChainSmallestFirst()
        {
            // 8x8 down to 1x1 is four levels; the container stores them in the reverse of the
            // order Stride keeps them, behind one texel of padding.
            const int side = 8;
            var path = GetOutputPath("mipmapped.pvr");

            using (var tool = new TextureTool())
            using (var image = CreateMipmappedImage(side, PixelFormat.B5G6R5_UNorm))
            {
                tool.SavePvr(image, path, PvrDataFormat.SquareTwiddled);
            }

            var file = File.ReadAllBytes(path);
            var data = PvrEncoder.ReadContainer(file, out _, out _, out _, out var dataFormat, out _);

            Assert.Equal(PvrDataFormat.SquareTwiddledMipmap, dataFormat);

            var expected = PvrFormat.GetMipmapPadding(dataFormat);
            for (var s = 1; s <= side; s *= 2) expected += s * s * PvrFormat.BytesPerTexel;
            Assert.Equal(expected, data.Length);
        }

        [Fact]
        public void ExportFallsBackToTheBaseLevelWhenTheChainIsIncomplete()
        {
            // A chain that stops before 1x1 cannot be written as a mipmapped PVR, so the exporter
            // writes the base level rather than an invalid file.
            const int side = 8;
            var texels = new ushort[side * side];
            var path = GetOutputPath("partial-chain.pvr");

            using (var tool = new TextureTool())
            using (var image = CreateImage(texels, side, side, PixelFormat.B5G6R5_UNorm))
            {
                // Claim a chain the SubImageArray does not actually carry down to 1x1.
                image.MipmapCount = 2;
                tool.SavePvr(image, path, PvrDataFormat.SquareTwiddled);
            }

            var file = File.ReadAllBytes(path);
            var data = PvrEncoder.ReadContainer(file, out _, out _, out _, out var dataFormat, out _);

            Assert.Equal(PvrDataFormat.SquareTwiddled, dataFormat);
            Assert.Equal(side * side * PvrFormat.BytesPerTexel, data.Length);
        }

        [Fact]
        public void VectorQuantisationNeedsASquareTexture()
        {
            var texels = new ushort[16 * 8];
            var path = GetOutputPath("vq-rect.pvr");

            using var tool = new TextureTool();
            using var image = CreateImage(texels, 16, 8, PixelFormat.B5G6R5_UNorm);

            Assert.Throws<TextureToolsException>(() => tool.SavePvr(image, path, PvrDataFormat.Vq));
        }

        // -------------------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------------------

        private static string GetOutputPath(string fileName)
        {
            Directory.CreateDirectory(Module.PathToOutputImages);
            return Path.Combine(Module.PathToOutputImages, fileName);
        }

        private static TexImage CreateImage(ushort[] texels, int width, int height, PixelFormat format)
        {
            var byteCount = texels.Length * sizeof(ushort);
            var data = Marshal.AllocHGlobal(byteCount);
            var bytes = new byte[byteCount];
            System.Buffer.BlockCopy(texels, 0, bytes, 0, byteCount);
            Marshal.Copy(bytes, 0, data, byteCount);

            return new TexImage(data, byteCount, width, height, 1, format, 1, 1, TexImage.TextureDimension.Texture2D);
        }

        /// <summary>
        ///   Builds a square image with a complete mipmap chain down to 1x1.
        /// </summary>
        /// <remarks>
        ///   The sub-images are filled in here rather than left to the <see cref="TexImage"/>
        ///   constructor, which gives every level the base level's dimensions and offset.
        /// </remarks>
        private static TexImage CreateMipmappedImage(int side, PixelFormat format)
        {
            var levels = 1;
            for (var s = side; s > 1; s >>= 1) levels++;

            var totalTexels = 0;
            for (var s = side; s >= 1; s >>= 1) totalTexels += s * s;

            var byteCount = totalTexels * sizeof(ushort);
            var data = Marshal.AllocHGlobal(byteCount);
            for (var i = 0; i < totalTexels; i++)
                Marshal.WriteInt16(data, i * sizeof(ushort), (short)(i * 37 + 1));

            var image = new TexImage(data, byteCount, side, side, 1, format, levels, 1, TexImage.TextureDimension.Texture2D);

            var subImages = new TexImage.SubImage[levels];
            var offset = 0;
            var size = side;
            for (var level = 0; level < levels; level++)
            {
                subImages[level] = new TexImage.SubImage
                {
                    Width = size,
                    Height = size,
                    RowPitch = size * PvrFormat.BytesPerTexel,
                    SlicePitch = size * size * PvrFormat.BytesPerTexel,
                    DataSize = size * size * PvrFormat.BytesPerTexel,
                    Data = data + offset,
                };

                offset += size * size * PvrFormat.BytesPerTexel;
                size >>= 1;
            }

            image.SubImageArray = subImages;
            return image;
        }

        private static ushort[] ReadTexels(TexImage image)
        {
            var count = image.Width * image.Height;
            var bytes = new byte[count * sizeof(ushort)];
            Marshal.Copy(image.Data, bytes, 0, bytes.Length);

            var texels = new ushort[count];
            System.Buffer.BlockCopy(bytes, 0, texels, 0, bytes.Length);
            return texels;
        }
    }
}
