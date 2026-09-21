// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_PLATFORM_DREAMCAST

namespace Stride.Games;

/// <summary>
///   The SEGA Dreamcast implementation of the Game Platform.
/// </summary>
/// <remarks>
///   <para>
///     The console has no application lifecycle to marshal — there is no suspend, no resume and
///     no task switch — so unlike the mobile platforms this one adds nothing on top of the shared
///     <see cref="GamePlatform"/> behaviour beyond naming the framebuffer window and the content
///     root. The run loop blocks, as it does on desktop: the game is the only thing running.
///   </para>
///   <para>See docs/build/dreamcast.md for what a Dreamcast build can and cannot do today.</para>
/// </remarks>
internal class GamePlatformDreamcast : GamePlatform
{
    /// <summary>
    ///   Initializes a new instance of the <see cref="GamePlatformDreamcast"/> class.
    /// </summary>
    /// <param name="game">The Game associated with this platform.</param>
    public GamePlatformDreamcast(GameBase game) : base(game)
    {
        IsBlockingRun = true;
        FullName = "SEGA Dreamcast";
    }

    /// <summary>
    ///   The root the game's content is read from.
    /// </summary>
    /// <remarks>
    ///   On a real console this is the GD-ROM mount (<c>/cd</c> under KallistiOS, or <c>/pc</c>
    ///   when running over dc-load). It is left as the current directory here because nothing
    ///   mounts those volumes yet — there is no SH-4 .NET runtime to do it.
    /// </remarks>
    public override string DefaultAppDirectory => ".";

    /// <inheritdoc/>
    internal override GameWindow GetSupportedGameWindow(AppContextType type)
    {
        return type switch
        {
            AppContextType.Dreamcast => new GameWindowDreamcast(),
            // Headless shares the "no window manager" shape and is what the test harness asks
            // for, so it maps onto the same framebuffer window rather than failing.
            AppContextType.Headless => new GameWindowHeadless(),
            _ => null,
        };
    }
}

#endif
