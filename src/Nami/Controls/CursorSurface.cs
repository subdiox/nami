using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Nami.Controls;

/// <summary>The transparent input layer over the video; exposes ProtectedCursor so the pointer can be hidden on it.</summary>
public sealed partial class CursorSurface : Grid
{
    public void SetCursor(InputCursor? cursor) => ProtectedCursor = cursor;
}
