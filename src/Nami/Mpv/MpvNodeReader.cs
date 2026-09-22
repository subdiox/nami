using System.Runtime.InteropServices;

namespace Nami.Mpv;

/// <summary>
/// Converts an mpv_node tree into plain managed objects:
/// string / bool / long / double / byte[] / List of object / Dictionary of string to object.
/// </summary>
internal static unsafe class MpvNodeReader
{
    public static object? ToManaged(MpvNode* node)
    {
        switch (node->Format)
        {
            case MpvFormat.String:
            case MpvFormat.OsdString:
                return LibMpv.Utf8(node->U.String);
            case MpvFormat.Flag:
                return node->U.Flag != 0;
            case MpvFormat.Int64:
                return node->U.Int64;
            case MpvFormat.Double:
                return node->U.Double;
            case MpvFormat.NodeArray:
            {
                var list = node->U.List;
                var result = new List<object?>(list->Num);
                for (int i = 0; i < list->Num; i++)
                    result.Add(ToManaged(&list->Values[i]));
                return result;
            }
            case MpvFormat.NodeMap:
            {
                var list = node->U.List;
                var result = new Dictionary<string, object?>(list->Num, StringComparer.Ordinal);
                for (int i = 0; i < list->Num; i++)
                    result[LibMpv.Utf8(list->Keys[i])] = ToManaged(&list->Values[i]);
                return result;
            }
            case MpvFormat.ByteArray:
            {
                var ba = node->U.ByteArray;
                var bytes = new byte[(int)ba->Size];
                Marshal.Copy((nint)ba->Data, bytes, 0, bytes.Length);
                return bytes;
            }
            default:
                return null;
        }
    }
}
