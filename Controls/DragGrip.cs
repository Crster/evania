using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Evania.Controls;

/// <summary>
/// The handle a floating bar is dragged by. Subclassing is what makes the cursor possible:
/// <c>ProtectedCursor</c> is protected on <see cref="UIElement"/> — WinUI has no public cursor
/// property — so it can only be assigned from inside a derived type. A grid rather than a border
/// because <see cref="Border"/> is sealed.
/// </summary>
public sealed class DragGrip : Grid
{
    public DragGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }
}
