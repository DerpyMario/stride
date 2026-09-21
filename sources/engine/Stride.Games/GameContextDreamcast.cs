// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Games;

/// <summary>
///   A game context for the SEGA Dreamcast, which draws straight to the console's framebuffer.
/// </summary>
/// <remarks>
///   The Dreamcast has no window manager, so there is no native window to wrap: the requested
///   size is a video mode, and the console picks the closest one it can scan out. Sizes other
///   than the defaults below are unlikely to be honoured by real hardware.
/// </remarks>
public class GameContextDreamcast : GameContext<object>
{
    /// <summary>
    ///   Width in pixels of the Dreamcast's standard 640x480 video mode.
    /// </summary>
    public const int DefaultWidth = 640;

    /// <summary>
    ///   Height in pixels of the Dreamcast's standard 640x480 video mode.
    /// </summary>
    public const int DefaultHeight = 480;

    /// <inheritdoc/>
    public GameContextDreamcast(int requestedWidth = 0, int requestedHeight = 0)
        : base(null,
               requestedWidth > 0 ? requestedWidth : DefaultWidth,
               requestedHeight > 0 ? requestedHeight : DefaultHeight)
    {
        ContextType = AppContextType.Dreamcast;
    }
}
