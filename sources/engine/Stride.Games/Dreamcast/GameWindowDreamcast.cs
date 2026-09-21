// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_PLATFORM_DREAMCAST

using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Games;

/// <summary>
///   The game "window" on the SEGA Dreamcast, which is the console's framebuffer.
/// </summary>
/// <remarks>
///   <para>
///     The Dreamcast has no window manager. The game owns the display for its whole lifetime, so
///     most of the <see cref="GameWindow"/> contract — resizing, focus, minimising, borders,
///     opacity, a title, a mouse cursor — describes something the console does not have. Those
///     members are answered with the fixed truths of the platform rather than left to throw, so
///     engine code that queries them keeps working.
///   </para>
///   <para>
///     The one thing that is real here is the video mode, which <see cref="Resize"/> and
///     <see cref="EndScreenDeviceChange"/> record. Selecting a mode on the hardware is the
///     responsibility of the (not yet existing) Dreamcast host layer; see docs/build/dreamcast.md.
///   </para>
/// </remarks>
internal class GameWindowDreamcast : GameWindow<object>
{
    private int width = GameContextDreamcast.DefaultWidth;
    private int height = GameContextDreamcast.DefaultHeight;

    /// <summary>
    ///   Always <c>false</c>: the video mode is chosen by the game, never dragged by a user.
    /// </summary>
    public override bool AllowUserResizing
    {
        get => false;
        set { }
    }

    /// <inheritdoc/>
    public override Rectangle ClientBounds => new(0, 0, width, height);

    /// <inheritdoc/>
    public override DisplayOrientation CurrentOrientation => DisplayOrientation.LandscapeLeft;

    /// <summary>
    ///   Always <c>false</c>: there is nothing to minimise into.
    /// </summary>
    public override bool IsMinimized => false;

    /// <summary>
    ///   Always <c>true</c>: the game is the only thing running, so it always has focus.
    /// </summary>
    public override bool Focused => true;

    /// <summary>
    ///   Always <c>false</c>: the Dreamcast has no system cursor. A game that wants a pointer
    ///   (the mouse and lightgun peripherals do exist) draws one itself.
    /// </summary>
    public override bool IsMouseVisible
    {
        get => false;
        set { }
    }

    /// <summary>
    ///   Always <c>null</c>: there is no native window handle to hand out.
    /// </summary>
    public override WindowHandle NativeWindow => null;

    /// <summary>
    ///   Always <c>true</c>: the framebuffer is always being scanned out.
    /// </summary>
    public override bool Visible
    {
        get => true;
        set { }
    }

    /// <summary>
    ///   Always fully opaque: there is nothing behind the framebuffer to blend with.
    /// </summary>
    public override double Opacity
    {
        get => 1.0;
        set { }
    }

    /// <summary>
    ///   Always <c>true</c>: the display has no chrome.
    /// </summary>
    public override bool IsBorderLess
    {
        get => true;
        set { }
    }

    /// <summary>
    ///   No-op: the console is permanently "fullscreen", so there is no mode switch to prepare for.
    /// </summary>
    public override void BeginScreenDeviceChange(bool willBeFullScreen) { }

    /// <inheritdoc/>
    public override void EndScreenDeviceChange(int clientWidth, int clientHeight)
    {
        width = clientWidth;
        height = clientHeight;
    }

    /// <summary>
    ///   No-op: the console's output is fixed landscape.
    /// </summary>
    protected internal override void SetSupportedOrientations(DisplayOrientation orientations) { }

    /// <summary>
    ///   No-op: there is no title bar to write to.
    /// </summary>
    protected override void SetTitle(string title) { }

    /// <inheritdoc/>
    internal override void Resize(int newWidth, int newHeight)
    {
        width = newWidth;
        height = newHeight;
    }

    /// <inheritdoc/>
    protected override void Initialize(GameContext<object> gameContext)
    {
        width = gameContext.RequestedWidth > 0 ? gameContext.RequestedWidth : GameContextDreamcast.DefaultWidth;
        height = gameContext.RequestedHeight > 0 ? gameContext.RequestedHeight : GameContextDreamcast.DefaultHeight;
    }

    /// <inheritdoc/>
    internal override void Run()
    {
        InitCallback?.Invoke();

        // Nothing to pump: with no window manager there is no OS message queue to drain, so the
        // loop is just the game's own tick until it asks to exit.
        while (!Exiting)
        {
            RunCallback?.Invoke();
        }

        ExitCallback?.Invoke();
    }
}

#endif
