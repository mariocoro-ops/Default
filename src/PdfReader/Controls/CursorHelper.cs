using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace PdfReader.Controls;

/// <summary>
/// Hides/restores the mouse cursor over an arbitrary element — used for the
/// presentation laser pointer. UIElement.ProtectedCursor is protected and
/// ScrollViewer is sealed, so the property is reached via reflection here;
/// assigning a disposed InputCursor is the supported WinUI way to blank the
/// pointer. Fails silently (cursor stays visible) if the property ever moves.
/// </summary>
public static class CursorHelper
{
    private static readonly PropertyInfo? ProtectedCursorProperty =
        typeof(UIElement).GetProperty(
            "ProtectedCursor",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    public static void SetHidden(UIElement element, bool hidden)
    {
        if (ProtectedCursorProperty is null)
        {
            return;
        }

        try
        {
            if (hidden)
            {
                var cursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
                cursor.Dispose();
                ProtectedCursorProperty.SetValue(element, cursor); // disposed => no pointer
            }
            else
            {
                ProtectedCursorProperty.SetValue(element, null); // back to default
            }
        }
        catch
        {
            // Best effort — a visible cursor is better than a crash.
        }
    }
}
