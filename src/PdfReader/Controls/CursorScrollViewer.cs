using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace PdfReader.Controls;

/// <summary>
/// A ScrollViewer that can hide the mouse cursor over itself — used while the
/// presentation laser pointer is active. ProtectedCursor is a protected
/// member, hence the subclass; assigning a disposed InputCursor is the
/// supported WinUI way to blank the pointer for an element.
/// </summary>
public sealed partial class CursorScrollViewer : ScrollViewer
{
    public void SetCursorHidden(bool hidden)
    {
        if (hidden)
        {
            var cursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            cursor.Dispose();
            ProtectedCursor = cursor; // disposed cursor => no visible pointer
        }
        else
        {
            ProtectedCursor = null; // back to inherited/default behavior
        }
    }
}
