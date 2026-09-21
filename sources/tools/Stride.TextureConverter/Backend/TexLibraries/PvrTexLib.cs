// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Runtime.InteropServices;
using Stride.Core.Diagnostics;
using Stride.Graphics;
using Stride.TextureConverter.PvrWrapper;
using Stride.TextureConverter.Requests;

namespace Stride.TextureConverter.TexLibraries
{
    /// <summary>
    ///   Tracks the buffer this library handed to a <see cref="TexImage"/>, so it can be freed
    ///   exactly once however the image is torn down.
    /// </summary>
    internal sealed class PvrTextureLibraryData : ITextureLibraryData
    {
        /// <summary>The unmanaged allocation backing the image, or <see cref="IntPtr.Zero"/> once freed.</summary>
        public IntPtr OwnedData;
    }

    /// <summary>
    ///   Converts textures to and from the Dreamcast's PowerVR2 texture formats, and reads and
    ///   writes the PVR container.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///     Handles three things: converting between R8G8B8A8 and the console's 16-bit colour
    ///     formats, exporting a <c>.pvr</c> file with the layout the hardware samples (Morton
    ///     order, optionally vector quantised), and loading one back.
    ///   </para>
    ///   <para>
    ///     Nothing on the Dreamcast can run a Stride game yet, so what this produces is asset
    ///     data ahead of a PowerVR2 backend rather than something the engine loads today. See
    ///     docs/build/dreamcast.md.
    ///   </para>
    /// </remarks>
    internal sealed class PvrTexLib : ITexLibrary
    {
        private static readonly Logger Log = GlobalLogger.GetLogger(nameof(PvrTexLib));

        private const string FileExtension = ".pvr";

        public void Dispose() { }

        public void Dispose(TexImage image)
        {
            if (!image.LibraryData.TryGetValue(this, out var libData))
                return;

            var data = (PvrTextureLibraryData)libData;
            if (data.OwnedData == IntPtr.Zero)
                return;

            Marshal.FreeHGlobal(data.OwnedData);
            data.OwnedData = IntPtr.Zero;
        }

        public void StartLibrary(TexImage image) { }

        public void EndLibrary(TexImage image) { }

        // Channel order is carried by the format itself here: the container's "ARGB" formats are
        // the BGRA-ordered PixelFormat values, and the RGBA sources are read a channel at a time.
        public bool SupportBGRAOrder() => false;

        public bool CanHandleRequest(TexImage image, IRequest request) => CanHandleRequest(image.Format, request);

        public bool CanHandleRequest(PixelFormat format, IRequest request)
        {
            switch (request.Type)
            {
                case RequestType.Loading:
                    return request is FileLoadingRequest loader && HasPvrExtension(loader.FilePath);

                case RequestType.Converting:
                    var converting = (ConvertingRequest)request;
                    // Either direction, as long as one side is a PVR colour format — the other
                    // side has to be the 8-bit-per-channel form everything else here speaks.
                    return PvrFormat.IsSupportedPixelFormat(converting.Format)
                        ? IsEightBitRgba(format) || PvrFormat.IsSupportedPixelFormat(format)
                        : PvrFormat.IsSupportedPixelFormat(format) && IsEightBitRgba(converting.Format);

                case RequestType.Export:
                    return HasPvrExtension(((ExportRequest)request).FilePath)
                        && (PvrFormat.IsSupportedPixelFormat(format) || IsEightBitRgba(format));

                default:
                    return false;
            }
        }

        public void Execute(TexImage image, IRequest request)
        {
            switch (request.Type)
            {
                case RequestType.Loading:
                    Load(image, (FileLoadingRequest)request);
                    break;

                case RequestType.Converting:
                    Convert(image, ((ConvertingRequest)request).Format);
                    break;

                case RequestType.Export:
                    Export(image, (ExportRequest)request);
                    break;

                default:
                    throw new TextureToolsException($"PvrTexLib can't handle request: {request.Type}");
            }
        }

        private static bool HasPvrExtension(string path)
            => FileExtension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase);

        private static bool IsEightBitRgba(PixelFormat format)
            => format is PixelFormat.R8G8B8A8_UNorm or PixelFormat.R8G8B8A8_UNorm_SRgb
                      or PixelFormat.B8G8R8A8_UNorm or PixelFormat.B8G8R8A8_UNorm_SRgb;

        private static bool IsBgraOrdered(PixelFormat format)
            => format is PixelFormat.B8G8R8A8_UNorm or PixelFormat.B8G8R8A8_UNorm_SRgb;

        /// <summary>
        ///   Picks the colour format that keeps as much of the source's alpha as the container can.
        /// </summary>
        /// <remarks>
        ///   Driven by the alpha depth the pipeline already measured: no alpha at all fits RGB565,
        ///   a cutout fits ARGB1555, and anything else — including an unmeasured -1 — needs the
        ///   four alpha bits of ARGB4444. Callers that want a specific format convert first.
        /// </remarks>
        private static PvrPixelFormat ChooseColourFormat(TexImage image)
        {
            var existing = PvrFormat.FromPixelFormat(image.Format);
            if (existing.HasValue)
                return existing.Value;

            return image.OriginalAlphaDepth switch
            {
                0 => PvrPixelFormat.Rgb565,
                1 => PvrPixelFormat.Argb1555,
                _ => PvrPixelFormat.Argb4444,
            };
        }

        /// <summary>
        ///   Picks the layout that suits the texture's shape, for an export that did not ask for one.
        /// </summary>
        private static PvrDataFormat ChooseDataFormat(int width, int height)
        {
            var squarePot = width == height && PvrEncoder.IsPowerOfTwo(width);
            if (squarePot)
                return PvrDataFormat.SquareTwiddled;

            // Twiddling a non-square texture still needs both edges to be powers of two, since it
            // is tiled with squares; anything else can only be stored in raster order.
            return PvrEncoder.IsPowerOfTwo(width) && PvrEncoder.IsPowerOfTwo(height)
                ? PvrDataFormat.RectangleTwiddled
                : PvrDataFormat.Rectangle;
        }

        // ---------------------------------------------------------------------------------------
        // Converting
        // ---------------------------------------------------------------------------------------

        private unsafe void Convert(TexImage image, PixelFormat destinationFormat)
        {
            if (image.Format == destinationFormat)
                return;

            var toPvr = PvrFormat.FromPixelFormat(destinationFormat);
            var fromPvr = PvrFormat.FromPixelFormat(image.Format);

            if (toPvr.HasValue && fromPvr.HasValue)
            {
                // Going 16-bit to 16-bit would quantise twice; widen through 8-bit first so the
                // second pack starts from the best values the first one could reconstruct.
                Convert(image, PixelFormat.R8G8B8A8_UNorm);
                fromPvr = null;
            }

            if (toPvr.HasValue)
                Pack(image, toPvr.Value, destinationFormat);
            else if (fromPvr.HasValue)
                Unpack(image, fromPvr.Value, destinationFormat);
            else
                throw new TextureToolsException($"PvrTexLib cannot convert {image.Format} to {destinationFormat}.");
        }

        private unsafe void Pack(TexImage image, PvrPixelFormat pvrFormat, PixelFormat destinationFormat)
        {
            Log.Verbose($"Packing to {destinationFormat} ...");

            var swapRedBlue = IsBgraOrdered(image.Format);
            var subImages = image.SubImageArray;
            var outSizes = new int[subImages.Length];
            long totalOut = 0;

            for (var i = 0; i < subImages.Length; i++)
            {
                outSizes[i] = subImages[i].Width * subImages[i].Height * PvrFormat.BytesPerTexel;
                totalOut += outSizes[i];
            }

            var outBuf = Marshal.AllocHGlobal((nint)totalOut);
            try
            {
                var newSubImages = new TexImage.SubImage[subImages.Length];
                long writeOffset = 0;

                for (var i = 0; i < subImages.Length; i++)
                {
                    var sub = subImages[i];
                    var texelCount = sub.Width * sub.Height;
                    var source = new ReadOnlySpan<byte>((void*)sub.Data, texelCount * 4);
                    var destination = new Span<ushort>((byte*)outBuf + writeOffset, texelCount);

                    for (var t = 0; t < texelCount; t++)
                    {
                        var o = t * 4;
                        var r = source[o];
                        var b = source[o + 2];
                        if (swapRedBlue)
                            (r, b) = (b, r);

                        destination[t] = PvrEncoder.PackTexel(r, source[o + 1], b, source[o + 3], pvrFormat);
                    }

                    newSubImages[i] = new TexImage.SubImage
                    {
                        Width = sub.Width,
                        Height = sub.Height,
                        RowPitch = sub.Width * PvrFormat.BytesPerTexel,
                        SlicePitch = outSizes[i],
                        DataSize = outSizes[i],
                        Data = (IntPtr)((byte*)outBuf + writeOffset),
                    };

                    writeOffset += outSizes[i];
                }

                ReplaceImageData(image, outBuf, (int)totalOut, destinationFormat, newSubImages);
            }
            catch
            {
                Marshal.FreeHGlobal(outBuf);
                throw;
            }
        }

        private unsafe void Unpack(TexImage image, PvrPixelFormat pvrFormat, PixelFormat destinationFormat)
        {
            if (!IsEightBitRgba(destinationFormat))
                throw new TextureToolsException($"PvrTexLib can only expand {image.Format} to an 8-bit-per-channel format, not {destinationFormat}.");

            Log.Verbose($"Expanding {image.Format} to {destinationFormat} ...");

            var swapRedBlue = IsBgraOrdered(destinationFormat);
            var subImages = image.SubImageArray;
            var outSizes = new int[subImages.Length];
            long totalOut = 0;

            for (var i = 0; i < subImages.Length; i++)
            {
                outSizes[i] = subImages[i].Width * subImages[i].Height * 4;
                totalOut += outSizes[i];
            }

            var outBuf = Marshal.AllocHGlobal((nint)totalOut);
            try
            {
                var newSubImages = new TexImage.SubImage[subImages.Length];
                long writeOffset = 0;

                for (var i = 0; i < subImages.Length; i++)
                {
                    var sub = subImages[i];
                    var texelCount = sub.Width * sub.Height;
                    var source = new ReadOnlySpan<ushort>((void*)sub.Data, texelCount);
                    var destination = new Span<byte>((byte*)outBuf + writeOffset, outSizes[i]);

                    for (var t = 0; t < texelCount; t++)
                    {
                        PvrEncoder.UnpackTexel(source[t], pvrFormat, out var r, out var g, out var b, out var a);
                        if (swapRedBlue)
                            (r, b) = (b, r);

                        var o = t * 4;
                        destination[o] = r;
                        destination[o + 1] = g;
                        destination[o + 2] = b;
                        destination[o + 3] = a;
                    }

                    newSubImages[i] = new TexImage.SubImage
                    {
                        Width = sub.Width,
                        Height = sub.Height,
                        RowPitch = sub.Width * 4,
                        SlicePitch = outSizes[i],
                        DataSize = outSizes[i],
                        Data = (IntPtr)((byte*)outBuf + writeOffset),
                    };

                    writeOffset += outSizes[i];
                }

                ReplaceImageData(image, outBuf, (int)totalOut, destinationFormat, newSubImages);
            }
            catch
            {
                Marshal.FreeHGlobal(outBuf);
                throw;
            }
        }

        private void ReplaceImageData(TexImage image, IntPtr newData, int newSize, PixelFormat format, TexImage.SubImage[] subImages)
        {
            // Hand the old buffer back to whoever allocated it before taking ownership, matching
            // what the other libraries do; an image with no owning library was allocated raw.
            if (image.DisposingLibrary != null)
                image.DisposingLibrary.Dispose(image);
            else
                Marshal.FreeHGlobal(image.Data);

            image.Data = newData;
            image.DataSize = newSize;
            image.Format = format;
            image.SubImageArray = subImages;
            Tools.ComputePitch(format, image.Width, image.Height, out var rowPitch, out var slicePitch);
            image.RowPitch = rowPitch;
            image.SlicePitch = slicePitch;

            if (!image.LibraryData.TryGetValue(this, out var libData))
            {
                libData = new PvrTextureLibraryData();
                image.LibraryData[this] = libData;
            }
            ((PvrTextureLibraryData)libData).OwnedData = newData;

            image.DisposingLibrary = this;
        }

        // ---------------------------------------------------------------------------------------
        // Exporting
        // ---------------------------------------------------------------------------------------

        private unsafe void Export(TexImage image, ExportRequest request)
        {
            if (image.ArraySize > 1 || image.FaceCount > 1 || image.Dimension == TexImage.TextureDimension.Texture3D)
                throw new TextureToolsException("The PVR container holds a single 2D image; array, cube and volume textures have no representation here.");

            // The container stores 16-bit texels, so an 8-bit source has to be packed first. Done
            // here rather than refused so that Save(image, "x.pvr") works without a conversion step.
            var colourFormat = ChooseColourFormat(image);
            if (!PvrFormat.IsSupportedPixelFormat(image.Format))
            {
                Log.Verbose($"Packing {image.Format} to {colourFormat} for PVR export.");
                Convert(image, PvrFormat.ToPixelFormat(colourFormat));
            }

            var explicitRequest = request as PvrExportRequest;
            var dataFormat = explicitRequest?.DataFormat ?? ChooseDataFormat(image.Width, image.Height);

            if (PvrFormat.IsVectorQuantised(dataFormat) && (image.Width != image.Height || !PvrEncoder.IsPowerOfTwo(image.Width)))
                throw new TextureToolsException($"Vector quantisation needs a square power-of-two texture; this one is {image.Width}x{image.Height}.");

            var levels = CollectMipLevels(image, dataFormat, request.MinimumMipMapSize, out var writeMipmaps);
            if (writeMipmaps)
                dataFormat = PvrFormat.WithMipmaps(dataFormat);

            Log.Verbose($"Exporting {request.FilePath} as {colourFormat}/{dataFormat} ...");

            var textureData = PvrFormat.IsVectorQuantised(dataFormat)
                ? EncodeVq(image, levels, colourFormat, writeMipmaps)
                : EncodeUncompressed(image, levels, dataFormat, writeMipmaps);

            var file = PvrEncoder.WriteContainer(
                textureData, image.Width, image.Height, colourFormat, dataFormat, explicitRequest?.GlobalIndex);

            File.WriteAllBytes(request.FilePath, file);
        }

        /// <summary>
        ///   Returns the mip levels to write, ordered smallest first as the container expects.
        /// </summary>
        /// <remarks>
        ///   The hardware wants a chain that runs all the way down to 1x1, so a partial chain is
        ///   not written as one: if the image stops short, or the caller asked to stop short with
        ///   <see cref="ExportRequest.MinimumMipMapSize"/>, or the chosen layout has no mipmapped
        ///   form, only the base level goes in the file and <paramref name="writeMipmaps"/> is false.
        /// </remarks>
        private static TexImage.SubImage[] CollectMipLevels(TexImage image, PvrDataFormat dataFormat, int minimumMipMapSize, out bool writeMipmaps)
        {
            var baseLevel = image.SubImageArray[0];
            writeMipmaps = false;

            if (image.MipmapCount <= 1)
                return [baseLevel];

            if (PvrFormat.WithMipmaps(dataFormat) == dataFormat && !PvrFormat.IsMipmapped(dataFormat))
            {
                Log.Warning($"{dataFormat} has no mipmapped form in the PVR container; writing the base level only.");
                return [baseLevel];
            }

            if (minimumMipMapSize > 1)
            {
                Log.Warning($"A PVR mipmap chain has to reach 1x1, so it cannot stop at {minimumMipMapSize}; writing the base level only.");
                return [baseLevel];
            }

            var expected = ExpectedMipCount(image.Width, image.Height);
            if (image.MipmapCount != expected || image.SubImageArray.Length < expected)
            {
                Log.Warning($"The mipmap chain stops at {image.MipmapCount} of {expected} levels, short of 1x1; writing the base level only.");
                return [baseLevel];
            }

            // Stride orders levels largest first; the container wants the reverse.
            var levels = new TexImage.SubImage[expected];
            for (var i = 0; i < expected; i++)
                levels[i] = image.SubImageArray[expected - 1 - i];

            writeMipmaps = true;
            return levels;
        }

        private static int ExpectedMipCount(int width, int height)
        {
            var count = 1;
            var size = Math.Max(width, height);
            while (size > 1)
            {
                size >>= 1;
                count++;
            }
            return count;
        }

        private static unsafe byte[] EncodeUncompressed(TexImage image, TexImage.SubImage[] levels, PvrDataFormat dataFormat, bool writeMipmaps)
        {
            var twiddled = PvrFormat.IsTwiddled(dataFormat);
            var padding = writeMipmaps ? PvrFormat.GetMipmapPadding(dataFormat) : 0;

            var total = padding;
            foreach (var level in levels)
                total += level.Width * level.Height * PvrFormat.BytesPerTexel;

            var output = new byte[total];
            var offset = padding;

            foreach (var level in levels)
            {
                var texelCount = level.Width * level.Height;
                var source = new ReadOnlySpan<ushort>((void*)level.Data, texelCount);
                var destination = MemoryMarshal.Cast<byte, ushort>(output.AsSpan(offset, texelCount * PvrFormat.BytesPerTexel));

                if (twiddled)
                    PvrEncoder.Twiddle(source, destination, level.Width, level.Height);
                else
                    source.CopyTo(destination);

                offset += texelCount * PvrFormat.BytesPerTexel;
            }

            return output;
        }

        private static unsafe byte[] EncodeVq(TexImage image, TexImage.SubImage[] levels, PvrPixelFormat colourFormat, bool writeMipmaps)
        {
            // One codebook serves the whole chain, built from the base level — the level that has
            // the detail worth spending entries on, and the one the hardware samples most.
            var baseLevel = levels[^1];
            var codebook = new ushort[PvrFormat.VqCodebookEntries * PvrFormat.VqBlockTexels];
            var baseTexels = new ReadOnlySpan<ushort>((void*)baseLevel.Data, baseLevel.Width * baseLevel.Height);
            var baseIndices = new byte[VqIndexCount(baseLevel.Width, baseLevel.Height)];

            PvrEncoder.BuildVqCodebook(baseTexels, baseLevel.Width, baseLevel.Height, colourFormat, codebook, baseIndices);

            var padding = writeMipmaps ? PvrFormat.GetMipmapPadding(PvrDataFormat.VqMipmap) : 0;
            var total = PvrFormat.VqCodebookSize + padding;
            foreach (var level in levels)
                total += VqIndexCount(level.Width, level.Height);

            var output = new byte[total];
            MemoryMarshal.Cast<ushort, byte>(codebook).CopyTo(output);

            var offset = PvrFormat.VqCodebookSize + padding;
            for (var i = 0; i < levels.Length; i++)
            {
                var level = levels[i];
                var indexCount = VqIndexCount(level.Width, level.Height);
                var destination = output.AsSpan(offset, indexCount);

                // The base level is last, the chain running 1x1 upwards, and its indices are
                // already in hand from building the codebook.
                if (i == levels.Length - 1)
                {
                    baseIndices.AsSpan(0, indexCount).CopyTo(destination);
                }
                else
                {
                    // Smaller levels reuse the base level's codebook: match each block to the
                    // closest entry rather than rebuilding, which is what keeps one codebook valid
                    // for the whole chain.
                    var texels = new ReadOnlySpan<ushort>((void*)level.Data, level.Width * level.Height);
                    MatchToCodebook(texels, level.Width, level.Height, colourFormat, codebook, destination);
                }

                offset += indexCount;
            }

            return output;
        }

        private static int VqIndexCount(int width, int height)
            => Math.Max(1, width / PvrFormat.VqBlockSide * (height / PvrFormat.VqBlockSide));

        /// <summary>
        ///   Assigns each 2x2 block of a mip level the codebook entry closest to it.
        /// </summary>
        private static void MatchToCodebook(
            ReadOnlySpan<ushort> texels, int width, int height, PvrPixelFormat format, ushort[] codebook, Span<byte> indices)
        {
            // Levels below 2x2 have no whole block; the single index stands for the level as a
            // whole, so match the one texel it does have against each entry's first texel.
            if (width < PvrFormat.VqBlockSide || height < PvrFormat.VqBlockSide)
            {
                indices[0] = (byte)NearestEntry(format, codebook, [texels[0], texels[0], texels[0], texels[0]]);
                return;
            }

            var gridWidth = width / PvrFormat.VqBlockSide;
            var gridHeight = height / PvrFormat.VqBlockSide;
            Span<ushort> block = stackalloc ushort[PvrFormat.VqBlockTexels];

            for (var by = 0; by < gridHeight; by++)
            {
                for (var bx = 0; bx < gridWidth; bx++)
                {
                    for (var k = 0; k < PvrFormat.VqBlockTexels; k++)
                    {
                        var x = bx * PvrFormat.VqBlockSide + (k >> 1);
                        var y = by * PvrFormat.VqBlockSide + (k & 1);
                        block[k] = texels[y * width + x];
                    }

                    var entry = (byte)NearestEntry(format, codebook, block);
                    // Vector quantisation is square-only, so the block grid is too.
                    indices[PvrEncoder.TwiddleIndex(bx, by, gridWidth)] = entry;
                }
            }
        }

        private static int NearestEntry(PvrPixelFormat format, ushort[] codebook, ReadOnlySpan<ushort> block)
        {
            var best = 0;
            var bestDistance = long.MaxValue;

            for (var entry = 0; entry < PvrFormat.VqCodebookEntries; entry++)
            {
                long distance = 0;
                for (var k = 0; k < PvrFormat.VqBlockTexels; k++)
                {
                    PvrEncoder.UnpackTexel(codebook[entry * PvrFormat.VqBlockTexels + k], format, out var cr, out var cg, out var cb, out var ca);
                    PvrEncoder.UnpackTexel(block[k], format, out var br, out var bg, out var bb, out var ba);

                    long dr = cr - br, dg = cg - bg, db = cb - bb, da = ca - ba;
                    distance += dr * dr + dg * dg + db * db + da * da;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = entry;
                    if (distance == 0) break;
                }
            }

            return best;
        }

        // ---------------------------------------------------------------------------------------
        // Loading
        // ---------------------------------------------------------------------------------------

        private unsafe void Load(TexImage image, FileLoadingRequest request)
        {
            Log.Verbose($"Loading {request.FilePath} ...");

            var file = File.ReadAllBytes(request.FilePath);
            var textureData = PvrEncoder.ReadContainer(file, out var width, out var height, out var colourFormat, out var dataFormat, out _);

            if (PvrFormat.IsMipmapped(dataFormat))
                textureData = textureData[PvrFormat.GetMipmapPadding(dataFormat)..];

            var texelCount = width * height;
            var raster = new ushort[texelCount];

            if (PvrFormat.IsVectorQuantised(dataFormat))
            {
                DecodeVq(textureData, width, height, raster);
            }
            else
            {
                // A mipmapped file stores the base level last, since the chain runs 1x1 upwards.
                var baseOffset = PvrFormat.IsMipmapped(dataFormat)
                    ? textureData.Length - texelCount * PvrFormat.BytesPerTexel
                    : 0;

                var stored = MemoryMarshal.Cast<byte, ushort>(textureData.Slice(baseOffset, texelCount * PvrFormat.BytesPerTexel));
                if (PvrFormat.IsTwiddled(dataFormat))
                    PvrEncoder.Untwiddle(stored, raster, width, height);
                else
                    stored.CopyTo(raster);
            }

            var dataSize = texelCount * PvrFormat.BytesPerTexel;
            var buffer = Marshal.AllocHGlobal(dataSize);
            try
            {
                raster.AsSpan().CopyTo(new Span<ushort>((void*)buffer, texelCount));

                image.Width = width;
                image.Height = height;
                image.Depth = 1;
                image.ArraySize = 1;
                image.FaceCount = 1;
                // Only the base level is reconstructed: the pipeline regenerates mips from it, and
                // a chain read back out of a quantised file is strictly worse than a fresh one.
                image.MipmapCount = 1;
                image.Dimension = TexImage.TextureDimension.Texture2D;
                image.Name = Path.GetFileName(request.FilePath);

                var subImage = new TexImage.SubImage
                {
                    Width = width,
                    Height = height,
                    RowPitch = width * PvrFormat.BytesPerTexel,
                    SlicePitch = dataSize,
                    DataSize = dataSize,
                    Data = buffer,
                };

                ReplaceImageData(image, buffer, dataSize, PvrFormat.ToPixelFormat(colourFormat), [subImage]);
            }
            catch
            {
                Marshal.FreeHGlobal(buffer);
                throw;
            }
        }

        private static void DecodeVq(ReadOnlySpan<byte> textureData, int width, int height, Span<ushort> raster)
        {
            if (textureData.Length < PvrFormat.VqCodebookSize)
                throw new InvalidOperationException("PVR file is truncated: no room for a vector-quantisation codebook.");

            var codebook = MemoryMarshal.Cast<byte, ushort>(textureData[..PvrFormat.VqCodebookSize]);
            var indices = textureData[PvrFormat.VqCodebookSize..];

            var gridSide = width / PvrFormat.VqBlockSide;
            var indexCount = VqIndexCount(width, height);
            // A mipmapped file puts the base level's indices last.
            indices = indices[(indices.Length - indexCount)..];

            for (var by = 0; by < gridSide; by++)
            {
                for (var bx = 0; bx < gridSide; bx++)
                {
                    var entry = indices[PvrEncoder.TwiddleIndex(bx, by, gridSide)];
                    for (var k = 0; k < PvrFormat.VqBlockTexels; k++)
                    {
                        var x = bx * PvrFormat.VqBlockSide + (k >> 1);
                        var y = by * PvrFormat.VqBlockSide + (k & 1);
                        raster[y * width + x] = codebook[entry * PvrFormat.VqBlockTexels + k];
                    }
                }
            }
        }
    }
}
