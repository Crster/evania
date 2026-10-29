using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// The floating pills that add and remove whole table rows and columns.
/// </summary>
/// <remarks>
/// Row and column edits are structural - they move text the WYSIWYG view never shows - so they cannot
/// be expressed as typing. Instead, whenever the caret sits inside a table rendered as a grid, two
/// icon-only pills float outside it: the column pill above the table's top edge, sliding along the
/// caret's column, and the row pill to the left of the table's left edge, tracking the caret's row -
/// each on the axis it edits. They are XAML overlays rather than something drawn on the Win2D surface,
/// because the surface is a paint-only region and cannot receive pointer input per glyph; the overlays
/// sit in the same <see cref="Grid"/> as the scroller and are repositioned relative to the scroll offset
/// so they track the caret.
/// </remarks>
public sealed partial class MarkdownEditor
{
    /// <summary>Adds and removes columns; a horizontal strip floated above the table's top edge.</summary>
    private Border? topToolbar;

    /// <summary>Adds and removes rows; a vertical strip floated to the left of the table's left edge.</summary>
    private Border? leftToolbar;

    /// <summary>Gap between a pill and the table edge it is anchored to.</summary>
    private const float ToolbarGap = 4f;

    /// <summary>
    /// Segoe Fluent Icons. Both pills use the same plus and minus because position and layout carry the
    /// axis: a horizontal strip above the table edits columns, a vertical strip beside it edits rows.
    /// </summary>
    private const string AddGlyph = "\uE710";

    private const string RemoveGlyph = "\uE738";

    private void InitializeTableToolbar()
    {
        topToolbar = CreateToolbarPill(
            Orientation.Horizontal,
            CreateToolbarButton("Adds a column to the right of the current one", AddGlyph, InsertTableColumn),
            CreateToolbarButton("Removes the current column", RemoveGlyph, DeleteTableColumn));

        leftToolbar = CreateToolbarPill(
            Orientation.Vertical,
            CreateToolbarButton("Adds an empty row below the current one", AddGlyph, InsertTableRow),
            CreateToolbarButton("Removes the current row", RemoveGlyph, DeleteTableRow));

        ApplyTableToolbarTheme();
    }

    private Border CreateToolbarPill(Orientation orientation, params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = orientation, Spacing = 1 };
        foreach (Button button in buttons)
        {
            panel.Children.Add(button);
        }

        var pill = new Border
        {
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = panel,
            IsHitTestVisible = true,
        };

        // Pills start collapsed, so the first arrange is what tells us their size; place again once it
        // arrives rather than leaving them at the default corner.
        pill.SizeChanged += (_, _) => UpdateTableToolbarPlacement();
        Children.Add(pill);

        return pill;
    }

    /// <summary>
    /// Resolves the pills' fill and stroke from the theme resources the rest of the control reads. The
    /// buttons themselves need nothing here: they keep the default theme-aware button style.
    /// </summary>
    private void ApplyTableToolbarTheme()
    {
        SolidColorBrush fill = new(GetThemeColor("CardBackgroundFillColorSecondary", theme.CodeBackground));
        SolidColorBrush stroke = new(GetThemeColor("CardStrokeColorDefault", theme.Divider));

        if (topToolbar is not null)
        {
            topToolbar.Background = fill;
            topToolbar.BorderBrush = stroke;
        }

        if (leftToolbar is not null)
        {
            leftToolbar.Background = fill;
            leftToolbar.BorderBrush = stroke;
        }
    }

    private static Button CreateToolbarButton(string tooltip, string glyph, Action action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Width = 26,
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            AllowFocusOnInteraction = false,
            IsTabStop = false,
        };

        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => action();

        return button;
    }

    /// <summary>
    /// Shows the pills over the caret's table, or hides them when the caret is not in a grid-rendered
    /// one. Called after every selection change and layout rebuild so they can never outlive their table.
    /// </summary>
    private void UpdateTableToolbar()
    {
        if (topToolbar is null || leftToolbar is null)
        {
            return;
        }

        bool inGridTable = !IsReadOnly && TryGetCaretGridCell(out _, out _, out _);
        topToolbar.Visibility = inGridTable ? Visibility.Visible : Visibility.Collapsed;
        leftToolbar.Visibility = inGridTable ? Visibility.Visible : Visibility.Collapsed;

        if (inGridTable)
        {
            UpdateTableToolbarPlacement();
        }
    }

    private void UpdateTableToolbarPlacement()
    {
        bool topVisible = topToolbar is { Visibility: Visibility.Visible };
        bool leftVisible = leftToolbar is { Visibility: Visibility.Visible };

        if (!topVisible && !leftVisible)
        {
            return;
        }

        if (!TryGetCaretGridCell(out BlockLayout block, out int row, out int column))
        {
            if (topToolbar is not null)
            {
                topToolbar.Visibility = Visibility.Collapsed;
            }

            if (leftToolbar is not null)
            {
                leftToolbar.Visibility = Visibility.Collapsed;
            }

            return;
        }

        // Both pills float outside the table's bounding box, each on the axis it edits: the column pill
        // above the table's very top edge (sliding with the caret's column), the row pill to the left of
        // the table's very left edge (tracking the caret's row). Margins are relative to the control's
        // padding box, so x = 0 is the table's left edge and y = 0 is the top of the visible content.
        double tableTop = block.Y + GetRowTop(block, 0) - scroller.VerticalOffset;
        double cellTop = block.Y + GetRowTop(block, row) - scroller.VerticalOffset;
        double cellLeft = column < block.ColumnOffsets.Count ? block.ColumnOffsets[column] : 0;

        // The row pill sits entirely left of the table, so its right edge clears the table's left edge by
        // the gap; the negative margin hangs it over the control's left padding rather than the cells.
        if (leftToolbar is not null && leftVisible)
        {
            double x = -(leftToolbar.ActualWidth + ToolbarGap);
            double y = Math.Clamp(cellTop, 0, Math.Max(0, ActualHeight - leftToolbar.ActualHeight));

            leftToolbar.Margin = new Thickness(x, y, 0, 0);
        }

        if (topToolbar is not null && topVisible)
        {
            double height = topToolbar.ActualHeight;

            // Float above the whole table; when the table is scrolled to the top of the view, pin the
            // pill to the top edge instead of pushing it off screen.
            double y = tableTop - height - ToolbarGap;
            if (y < 0)
            {
                y = 0;
            }

            double x = Math.Clamp(cellLeft, 0, Math.Max(0, ActualWidth - topToolbar.ActualWidth));

            topToolbar.Margin = new Thickness(x, y, 0, 0);
        }
    }

    private static float GetRowTop(BlockLayout block, int row)
        => row < block.RowOffsets.Count ? block.RowOffsets[row] : 0f;
}
