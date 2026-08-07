namespace AdbDesktop
{
    /// <summary>
    /// Where a window is tiled on the desktop. <see cref="None"/> is a free-floating
    /// window: the only state in which its own X/Y/Width/Height are its own business.
    ///
    /// <see cref="Full"/> is the same thing as maximised, kept in this enum rather than
    /// beside it so there is one answer to "is this window laid out by the shell", and
    /// so the maximise button and the tiling shortcuts share a single state machine.
    /// </summary>
    public enum SnapZone
    {
        None,
        Full,
        Left,
        Right,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    /// <summary>Arrow direction of a tiling shortcut.</summary>
    public enum TileDirection
    {
        Left,
        Right,
        Up,
        Down,
    }
}
