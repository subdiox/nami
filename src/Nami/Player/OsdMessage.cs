namespace Nami.Player;

/// <summary>One on-screen notification, IINA style: icon, text, optional second line and bar.</summary>
public sealed record OsdMessage(string Icon, string Text, string? Detail = null, double? Progress = null, double? Seconds = null, string? ImagePath = null)
{
    // Segoe Fluent Icons glyphs used by the OSD
    public const string IconPlay = "";
    public const string IconPause = "";
    public const string IconVolume = "";
    public const string IconVolumeLow = "";
    public const string IconVolumeMid = "";
    public const string IconMute = "";
    public const string IconSpeed = "";
    public const string IconForward = "";
    public const string IconBack = "";
    public const string IconSubtitle = "";
    public const string IconAudio = "";
    public const string IconVideo = "";
    public const string IconChapter = "";
    public const string IconLoop = "";
    public const string IconLoopOne = "";
    public const string IconShuffle = "";
    public const string IconRotate = "";
    public const string IconCrop = "";
    public const string IconAspect = "";
    public const string IconBrightness = "";
    public const string IconColor = "";
    public const string IconScreenshot = "";
    public const string IconPlaylist = "";
    public const string IconInfo = "";
    public const string IconDelay = "";
    public const string IconFile = "";
    public const string IconStop = "";
    public const string IconZoom = "";
    public const string IconEq = "";
}
