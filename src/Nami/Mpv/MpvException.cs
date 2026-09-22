namespace Nami.Mpv;

public sealed class MpvException : Exception
{
    public int ErrorCode { get; }

    public MpvException(string what, int errorCode)
        : base($"{what}: {LibMpv.ErrorString(errorCode)} ({errorCode})")
    {
        ErrorCode = errorCode;
    }

    internal static void ThrowIfError(int result, string what)
    {
        if (result < 0)
            throw new MpvException(what, result);
    }
}
