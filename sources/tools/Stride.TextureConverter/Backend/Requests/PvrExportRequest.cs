// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.TextureConverter.PvrWrapper;

namespace Stride.TextureConverter.Requests
{
    /// <summary>
    ///   Request to export a texture as a Dreamcast PVR file with an explicitly chosen layout.
    /// </summary>
    /// <remarks>
    ///   A plain <see cref="ExportRequest"/> naming a <c>.pvr</c> file reaches the same library and
    ///   gets a layout inferred from the texture's shape. This exists for the choices that cannot
    ///   be inferred: asking for vector quantisation, which trades quality for a quarter of the
    ///   size, and setting the global index.
    /// </remarks>
    internal class PvrExportRequest : ExportRequest
    {
        /// <summary>
        ///   How texels should be arranged in the file.
        /// </summary>
        /// <remarks>
        ///   Promoted to the mipmapped variant of itself when the texture has a mipmap chain to
        ///   write, so callers do not have to pick between the paired values.
        /// </remarks>
        public PvrDataFormat DataFormat { get; }

        /// <summary>
        ///   The global index to record in a <c>GBIX</c> chunk, or <c>null</c> to omit the chunk.
        /// </summary>
        public uint? GlobalIndex { get; }

        /// <summary>
        ///   Initializes a new instance of the <see cref="PvrExportRequest"/> class.
        /// </summary>
        /// <param name="filePath">The file path.</param>
        /// <param name="dataFormat">The layout to write.</param>
        /// <param name="globalIndex">The global index, or <c>null</c> for no <c>GBIX</c> chunk.</param>
        /// <param name="minimumMipMapSize">Minimum size of the mip map.</param>
        public PvrExportRequest(string filePath, PvrDataFormat dataFormat, uint? globalIndex = null, int minimumMipMapSize = 1)
            : base(filePath, minimumMipMapSize)
        {
            DataFormat = dataFormat;
            GlobalIndex = globalIndex;
        }
    }
}
