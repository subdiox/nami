// Raw P/Invoke surface for libmpv (client API 2.x). Kept 1:1 with client.h;
// anything friendlier lives in MpvPlayer.
using System.Runtime.InteropServices;

namespace Nami.Mpv;

public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    Tick = 14,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public void* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEventProperty
{
    public byte* Name;
    public MpvFormat Format;
    public void* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEventLogMessage
{
    public byte* Prefix;
    public byte* Level;
    public byte* Text;
    public int LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEventClientMessage
{
    public int NumArgs;
    public byte** Args;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEventHook
{
    public byte* Name;
    public ulong Id;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvNode
{
    // union { char* string; int flag; int64 int64; double double_; mpv_node_list* list; mpv_byte_array* ba; }
    public MpvNodeUnion U;
    public MpvFormat Format;
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
internal unsafe struct MpvNodeUnion
{
    [FieldOffset(0)] public byte* String;
    [FieldOffset(0)] public int Flag;
    [FieldOffset(0)] public long Int64;
    [FieldOffset(0)] public double Double;
    [FieldOffset(0)] public MpvNodeList* List;
    [FieldOffset(0)] public MpvByteArray* ByteArray;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvNodeList
{
    public int Num;
    public MpvNode* Values;
    public byte** Keys;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvByteArray
{
    public void* Data;
    public nuint Size;
}

internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    SwSize = 17,
    SwFormat = 18,
    SwStride = 19,
    SwPointer = 20,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public void* Data;
}

internal static unsafe partial class LibMpv
{
    [LibraryImport(Lib)] public static partial int mpv_render_context_create(nint* res, nint mpv, MpvRenderParam* params_);
    [LibraryImport(Lib)] public static partial void mpv_render_context_set_update_callback(nint ctx, delegate* unmanaged<void*, void> callback, void* callbackCtx);
    [LibraryImport(Lib)] public static partial ulong mpv_render_context_update(nint ctx);
    [LibraryImport(Lib)] public static partial int mpv_render_context_render(nint ctx, MpvRenderParam* params_);
    [LibraryImport(Lib)] public static partial void mpv_render_context_free(nint ctx);

    private const string Lib = "libmpv-2";

    [LibraryImport(Lib)] public static partial uint mpv_client_api_version();
    [LibraryImport(Lib)] public static partial byte* mpv_error_string(int error);
    [LibraryImport(Lib)] public static partial void mpv_free(void* data);
    [LibraryImport(Lib)] public static partial void mpv_free_node_contents(MpvNode* node);

    [LibraryImport(Lib)] public static partial nint mpv_create();
    [LibraryImport(Lib)] public static partial int mpv_initialize(nint ctx);
    [LibraryImport(Lib)] public static partial void mpv_terminate_destroy(nint ctx);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_option_string(nint ctx, string name, string data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property_string(nint ctx, string name, string data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property(nint ctx, string name, MpvFormat format, void* data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_get_property(nint ctx, string name, MpvFormat format, void* data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte* mpv_get_property_string(nint ctx, string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte* mpv_get_property_osd_string(nint ctx, string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_observe_property(nint ctx, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(Lib)] public static partial int mpv_unobserve_property(nint ctx, ulong registeredReplyUserdata);

    [LibraryImport(Lib)] public static partial int mpv_command(nint ctx, byte** args);
    [LibraryImport(Lib)] public static partial int mpv_command_ret(nint ctx, byte** args, MpvNode* result);
    [LibraryImport(Lib)] public static partial int mpv_command_async(nint ctx, ulong replyUserdata, byte** args);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_command_string(nint ctx, string args);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_request_log_messages(nint ctx, string minLevel);

    [LibraryImport(Lib)] public static partial int mpv_request_event(nint ctx, MpvEventId eventId, int enable);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_hook_add(nint ctx, ulong replyUserdata, string name, int priority);

    [LibraryImport(Lib)] public static partial int mpv_hook_continue(nint ctx, ulong id);
    [LibraryImport(Lib)] public static partial MpvEvent* mpv_wait_event(nint ctx, double timeout);
    [LibraryImport(Lib)] public static partial void mpv_wakeup(nint ctx);

    public static string Utf8(byte* p) => p == null ? string.Empty : Marshal.PtrToStringUTF8((nint)p) ?? string.Empty;

    public static string ErrorString(int error) => Utf8(mpv_error_string(error));
}
