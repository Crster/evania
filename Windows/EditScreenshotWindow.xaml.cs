using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Evania.Services;
using WinColor = Windows.UI.Color;
using WinSize = Windows.Foundation.Size;
using WinRect = Windows.Foundation.Rect;
using WinPoint = Windows.Foundation.Point;
using WinVirtualKey = Windows.System.VirtualKey;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
using ShapeRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Evania.Windows;

public enum EditToolMode
{
    Select,
    DrawBox,
    DrawArrow,
    DrawText,
    Blur
}

internal sealed class TextGroup
{
    public List<string> Lines { get; } = new() { "" };
    public double X { get; set; }
    public double Y { get; set; }
    public double FontSize { get; set; } = 16;
    public int CursorLine { get; set; }
    public int CursorColumn { get; set; }
    public readonly List<TextBlock> TextBlocks = new();
    public ShapeRectangle? CursorRect { get; set; }
    public double CharWidth { get; set; }
}

public sealed partial class EditScreenshotWindow : Window
{
    private readonly Bitmap _screenshotBitmap;
    private EditToolMode _currentTool = EditToolMode.Select;
    private System.Drawing.Point _selectionStart;
    private System.Drawing.Point _selectionEnd;
    private bool _isDragging;
    private WinRect _currentSelection;
    private bool _hasSelection;

    // Re-usable preview shapes to avoid per-move allocation and layout churn.
    private ShapeRectangle? _previewBox;
    private ShapePath? _previewArrow;

    public bool IsCancelled { get; private set; }

    private bool _isDraggingToolbar;
    private double _toolbarDragOffsetX;
    private double _toolbarDragOffsetY;

    // Text input state
    private TextGroup? _activeTextGroup;
    private DispatcherTimer? _cursorBlinkTimer;
    private bool _cursorVisible;
    private bool _isInTextInputMode;

    public EditScreenshotWindow(Bitmap screenshotBitmap)
    {
        _screenshotBitmap = screenshotBitmap;
        InitializeComponent();

        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        LoadScreenshotImage();

        RootGrid.Focus(FocusState.Programmatic);

                UpdateToolButtonStyles();
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateTintOverlay();
        CenterToolbar();
    }

    private void CenterToolbar()
    {
        var gridWidth = RootGrid.ActualWidth;
        var toolbarWidth = ToolbarBorder.ActualWidth;
        if (gridWidth > 0 && toolbarWidth > 0)
        {
            var centeredLeft = (gridWidth - toolbarWidth) / 2;
            ToolbarBorder.Margin = new Thickness(centeredLeft, ToolbarBorder.Margin.Top, 0, 0);
        }
    }

    private async void LoadScreenshotImage()
    {
        var bitmapImage = await ScreenshotService.ToBitmapImageAsync(_screenshotBitmap);
        ScreenshotImage.Source = bitmapImage;
    }

    #region Toolbar Dragging

    private void GripIcon_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingToolbar = true;
        var position = e.GetCurrentPoint(RootGrid).Position;
        _toolbarDragOffsetX = position.X - ToolbarBorder.Margin.Left;
        _toolbarDragOffsetY = position.Y - ToolbarBorder.Margin.Top;
        GripIcon.CapturePointer(e.Pointer);
    }

    private void GripIcon_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingToolbar)
        {
            return;
        }

        var position = e.GetCurrentPoint(RootGrid).Position;
        double newLeft = position.X - _toolbarDragOffsetX;
        double newTop = position.Y - _toolbarDragOffsetY;

        ToolbarBorder.Margin = new Thickness(newLeft, newTop, 0, 0);
    }

    private void GripIcon_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingToolbar = false;
        GripIcon.ReleasePointerCapture(e.Pointer);
    }

    #endregion

    #region Tool Selection

    private void SelectToolButton_Click(object sender, RoutedEventArgs e)
    {
        DeactivateTextInput();
        _currentTool = EditToolMode.Select;
        UpdateToolButtonStyles();
    }

    private void DrawBoxToolButton_Click(object sender, RoutedEventArgs e)
    {
        DeactivateTextInput();
        _currentTool = EditToolMode.DrawBox;
        UpdateToolButtonStyles();
    }

    private void DrawArrowToolButton_Click(object sender, RoutedEventArgs e)
    {
        DeactivateTextInput();
        _currentTool = EditToolMode.DrawArrow;
        UpdateToolButtonStyles();
    }

    private void DrawTextToolButton_Click(object sender, RoutedEventArgs e)
    {
        _currentTool = EditToolMode.DrawText;
        UpdateToolButtonStyles();
    }

    private void BlurToolButton_Click(object sender, RoutedEventArgs e)
    {
        DeactivateTextInput();
        _currentTool = EditToolMode.Blur;
        UpdateToolButtonStyles();
    }

    private void UpdateToolButtonStyles()
    {
        SelectToolButton.IsChecked = _currentTool == EditToolMode.Select;
        DrawBoxToolButton.IsChecked = _currentTool == EditToolMode.DrawBox;
        DrawArrowToolButton.IsChecked = _currentTool == EditToolMode.DrawArrow;
        DrawTextToolButton.IsChecked = _currentTool == EditToolMode.DrawText;
        BlurToolButton.IsChecked = _currentTool == EditToolMode.Blur;
    }

    #endregion

    #region Mouse Input

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingToolbar)
        {
            return;
        }

        var point = e.GetCurrentPoint(RootGrid).Position;

        if (_currentTool == EditToolMode.DrawText)
        {
            var existingGroup = HitTestTextGroup(point.X, point.Y);
            if (existingGroup != null)
            {
                ActivateTextGroup(existingGroup);
                e.Handled = true;
                return;
            }

            StartNewTextInput(point.X, point.Y);
            e.Handled = true;
            return;
        }

        _selectionStart = new System.Drawing.Point((int)point.X, (int)point.Y);
        _selectionEnd = _selectionStart;
        _isDragging = true;

        RootGrid.CapturePointer(e.Pointer);

        if (_currentTool == EditToolMode.DrawBox)
        {
            EnsureDrawBoxPreview();
        }
        else if (_currentTool == EditToolMode.DrawArrow)
        {
            EnsureArrowPreview(_selectionStart.X, _selectionStart.Y);
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(RootGrid).Position;
        _selectionEnd = new System.Drawing.Point((int)point.X, (int)point.Y);

        if (_currentTool == EditToolMode.DrawBox)
        {
            UpdateDrawBoxPreview(point.X, point.Y);
        }
        else if (_currentTool == EditToolMode.DrawArrow)
        {
            UpdateArrowPreview(point.X, point.Y);
        }
        else if (_currentTool == EditToolMode.Select || _currentTool == EditToolMode.Blur)
        {
            UpdateTintOverlay();
        }
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

        var point = e.GetCurrentPoint(RootGrid).Position;
        _selectionEnd = new System.Drawing.Point((int)point.X, (int)point.Y);

        int x = Math.Min(_selectionStart.X, _selectionEnd.X);
        int y = Math.Min(_selectionStart.Y, _selectionEnd.Y);
        int w = Math.Abs(_selectionEnd.X - _selectionStart.X);
        int h = Math.Abs(_selectionEnd.Y - _selectionStart.Y);

        if (w > 5 || h > 5)
        {
            _hasSelection = true;
            _currentSelection = new WinRect(x, y, w, h);

            switch (_currentTool)
            {
                case EditToolMode.Select:
                case EditToolMode.Blur:
                    UpdateTintOverlay();
                    break;
                case EditToolMode.DrawBox:
                    DrawRedBox(_currentSelection);
                    RemovePreview();
                    break;
                case EditToolMode.DrawArrow:
                    DrawRedArrow(_selectionStart, _selectionEnd);
                    RemovePreview();
                    break;
            }
        }
        else
        {
            // Drag was too small to commit; drop any leftover preview shape.
            RemovePreview();
        }
    }

    private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_currentTool == EditToolMode.Select)
        {
            double width = RootGrid.ActualWidth;
            double height = RootGrid.ActualHeight;
            _hasSelection = true;
            _currentSelection = new WinRect(0, 0, width, height);
            UpdateTintOverlay();
        }
    }

    #endregion

    #region Tint Overlay

    private void UpdateTintOverlay()
    {
        TintCanvas.Children.Clear();

        if (!_hasSelection && !_isDragging)
        {
            AddTintRect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight);
            return;
        }

        WinRect sel;
        if (_isDragging)
        {
            int x = Math.Min(_selectionStart.X, _selectionEnd.X);
            int y = Math.Min(_selectionStart.Y, _selectionEnd.Y);
            int w = Math.Abs(_selectionEnd.X - _selectionStart.X);
            int h = Math.Abs(_selectionEnd.Y - _selectionStart.Y);
            sel = new WinRect(x, y, w, h);
        }
        else
        {
            sel = _currentSelection;
        }

        double canvasW = RootGrid.ActualWidth;
        double canvasH = RootGrid.ActualHeight;

        if (sel.Y > 0)
        {
            AddTintRect(0, 0, canvasW, sel.Y);
        }
        if (sel.Y + sel.Height < canvasH)
        {
            AddTintRect(0, sel.Y + sel.Height, canvasW, canvasH - sel.Y - sel.Height);
        }
        if (sel.X > 0)
        {
            AddTintRect(0, sel.Y, sel.X, sel.Height);
        }
        if (sel.X + sel.Width < canvasW)
        {
            AddTintRect(sel.X + sel.Width, sel.Y, canvasW - sel.X - sel.Width, sel.Height);
        }

        var border = new ShapeRectangle
        {
            Width = sel.Width,
            Height = sel.Height,
            Stroke = new SolidColorBrush(WinColor.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 }
        };
        Canvas.SetLeft(border, sel.X);
        Canvas.SetTop(border, sel.Y);
        TintCanvas.Children.Add(border);
    }

    private void AddTintRect(double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var rect = new ShapeRectangle
        {
            Width = w,
            Height = h,
            Fill = new SolidColorBrush(WinColor.FromArgb(160, 0, 0, 0))
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        TintCanvas.Children.Add(rect);
    }

    #endregion

    #region Drawing Tools

        private void EnsureDrawBoxPreview()
        {
            if (_previewBox != null)
            {
                return;
            }

            _previewBox = new ShapeRectangle
            {
                Stroke = new SolidColorBrush(WinColor.FromArgb(255, 255, 0, 0)),
                StrokeThickness = 2
            };
            DrawingCanvas.Children.Add(_previewBox);
        }

        private void UpdateDrawBoxPreview(double endX, double endY)
        {
            if (_previewBox == null)
            {
                return;
            }

            int x = Math.Min(_selectionStart.X, (int)endX);
            int y = Math.Min(_selectionStart.Y, (int)endY);
            int w = Math.Abs((int)endX - _selectionStart.X);
            int h = Math.Abs((int)endY - _selectionStart.Y);

            _previewBox.Width = w;
            _previewBox.Height = h;
            Canvas.SetLeft(_previewBox, x);
            Canvas.SetTop(_previewBox, y);
        }

        private void EnsureArrowPreview(double startX, double startY)
        {
            if (_previewArrow != null)
            {
                return;
            }

            // A single Path draws both the shaft and the head, so the two can never
            // de-sync or tear while dragging (the source of the earlier flicker).
            _previewArrow = new ShapePath
            {
                Stroke = new SolidColorBrush(WinColor.FromArgb(255, 255, 0, 0)),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = new SolidColorBrush(WinColor.FromArgb(255, 255, 0, 0))
            };
            DrawingCanvas.Children.Add(_previewArrow);

            // On the very first click show only a small arrowhead at the click point
            // oriented toward the center of the grid — no shaft yet.
            double centerX = RootGrid.ActualWidth / 2.0;
            double centerY = RootGrid.ActualHeight / 2.0;
            double angle = Math.Atan2(centerY - startY, centerX - startX);
            _previewArrow.Data = BuildHeadOnlyGeometry(startX, startY, angle);
        }

        /// <summary>Arrowhead triangle only (no shaft), used for the initial click preview.</summary>
        private static Geometry BuildHeadOnlyGeometry(double tipX, double tipY, double angle)
        {
            const double headLength = 12;
            const double headHalfWidth = 5;

            double baseX = tipX - headLength * Math.Cos(angle);
            double baseY = tipY - headLength * Math.Sin(angle);
            double b1x = baseX + headHalfWidth * Math.Cos(angle + Math.PI / 2);
            double b1y = baseY + headHalfWidth * Math.Sin(angle + Math.PI / 2);
            double b2x = baseX - headHalfWidth * Math.Cos(angle + Math.PI / 2);
            double b2y = baseY - headHalfWidth * Math.Sin(angle + Math.PI / 2);

            var geometry = new PathGeometry();
            var headFigure = new PathFigure
            {
                StartPoint = new WinPoint(tipX, tipY),
                IsClosed = true,
                IsFilled = true
            };
            headFigure.Segments.Add(new LineSegment { Point = new WinPoint(b1x, b1y) });
            headFigure.Segments.Add(new LineSegment { Point = new WinPoint(b2x, b2y) });
            geometry.Figures.Add(headFigure);

            return geometry;
        }

        private static Geometry BuildArrowGeometry(double startX, double startY, double endX, double endY)
        {
            const double headLength = 12;
            const double headHalfWidth = 5;
            double angle = Math.Atan2(endY - startY, endX - startX);

            double tipX = endX;
            double tipY = endY;
            double baseX = endX - headLength * Math.Cos(angle);
            double baseY = endY - headLength * Math.Sin(angle);
            double b1x = baseX + headHalfWidth * Math.Cos(angle + Math.PI / 2);
            double b1y = baseY + headHalfWidth * Math.Sin(angle + Math.PI / 2);
            double b2x = baseX - headHalfWidth * Math.Cos(angle + Math.PI / 2);
            double b2y = baseY - headHalfWidth * Math.Sin(angle + Math.PI / 2);

            var geometry = new PathGeometry();

            var shaftFigure = new PathFigure
            {
                StartPoint = new WinPoint(startX, startY),
                IsClosed = false
            };
            shaftFigure.Segments.Add(new LineSegment
            {
                Point = new WinPoint(baseX, baseY)
            });
            geometry.Figures.Add(shaftFigure);

            var headFigure = new PathFigure
            {
                StartPoint = new WinPoint(tipX, tipY),
                IsClosed = true,
                IsFilled = true
            };
            headFigure.Segments.Add(new LineSegment { Point = new WinPoint(b1x, b1y) });
            headFigure.Segments.Add(new LineSegment { Point = new WinPoint(b2x, b2y) });
            geometry.Figures.Add(headFigure);

            return geometry;
        }

        private void UpdateArrowPreview(double endX, double endY)
        {
            if (_previewArrow == null)
            {
                return;
            }

            // Don't update if the cursor hasn't meaningfully moved from the press
            // point — preserves the initial center-oriented arrowhead and avoids
            // a zero-length arrow snapping to the right due to Atan2(0,0) = 0.
            if (Math.Abs(endX - _selectionStart.X) < 3 && Math.Abs(endY - _selectionStart.Y) < 3)
            {
                return;
            }

            // Replace the geometry as a single unit each frame; no per-element mutation.
            _previewArrow.Data = BuildArrowGeometry(_selectionStart.X, _selectionStart.Y, endX, endY);
        }

        private void RemovePreview()
        {
            if (_previewBox != null)
            {
                DrawingCanvas.Children.Remove(_previewBox);
                _previewBox = null;
            }
            if (_previewArrow != null)
            {
                DrawingCanvas.Children.Remove(_previewArrow);
                _previewArrow = null;
            }
        }

        private void DrawRedBox(WinRect bounds)
        {
        var rect = new ShapeRectangle
        {
            Width = bounds.Width,
            Height = bounds.Height,
            Stroke = new SolidColorBrush(WinColor.FromArgb(255, 255, 0, 0)),
            StrokeThickness = 2
        };
        Canvas.SetLeft(rect, bounds.X);
        Canvas.SetTop(rect, bounds.Y);
        DrawingCanvas.Children.Add(rect);
    }

    private void DrawRedArrow(System.Drawing.Point start, System.Drawing.Point end)
    {
        var path = new ShapePath
        {
            Stroke = new SolidColorBrush(WinColor.FromArgb(255, 255, 0, 0)),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = new SolidColorBrush(WinColor.FromArgb(255, 255, 0, 0)),
            Data = BuildArrowGeometry(start.X, start.Y, end.X, end.Y)
        };
        DrawingCanvas.Children.Add(path);
    }

    private readonly List<TextGroup> _textGroups = new();

    private void StartNewTextInput(double x, double y)
    {
        var group = new TextGroup
        {
            X = x,
            Y = y
        };
        _textGroups.Add(group);
        RenderTextGroup(group);
        ActivateTextGroup(group);
    }

    private void ActivateTextGroup(TextGroup group)
    {
        DeactivateTextInput();

        _activeTextGroup = group;
        _isInTextInputMode = true;
        group.CursorLine = group.Lines.Count - 1;
        group.CursorColumn = group.Lines[group.CursorLine].Length;
        UpdateCursorPosition(group);
        StartCursorBlink();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void DeactivateTextInput()
    {
        StopCursorBlink();

        if (_activeTextGroup?.CursorRect != null)
        {
            DrawingCanvas.Children.Remove(_activeTextGroup.CursorRect);
            _activeTextGroup.CursorRect = null;
        }

        _activeTextGroup = null;
        _isInTextInputMode = false;
    }

    private void RenderTextGroup(TextGroup group)
    {
        foreach (var tb in group.TextBlocks)
        {
            DrawingCanvas.Children.Remove(tb);
        }
        group.TextBlocks.Clear();

        for (int i = 0; i < group.Lines.Count; i++)
        {
            var textBlock = new TextBlock
            {
                Text = group.Lines[i],
                FontSize = group.FontSize,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Foreground = new SolidColorBrush(WinColor.FromArgb(255, 255, 0, 0))
            };
            Canvas.SetLeft(textBlock, group.X);
            Canvas.SetTop(textBlock, group.Y + i * (group.FontSize + 4));
            DrawingCanvas.Children.Add(textBlock);
            group.TextBlocks.Add(textBlock);
        }

        if (group.CharWidth <= 0)
        {
            group.CharWidth = MeasureCharWidth(group.FontSize);
        }
    }

    private void UpdateCursorPosition(TextGroup group)
    {
        if (group.CursorRect != null)
        {
            DrawingCanvas.Children.Remove(group.CursorRect);
            group.CursorRect = null;
        }

        string prefix = group.Lines[group.CursorLine][..group.CursorColumn];
        double cursorX = group.X + MeasureTextWidth(prefix, group.FontSize, group.CharWidth);
        double cursorY = group.Y + group.CursorLine * (group.FontSize + 4);

        group.CursorRect = new ShapeRectangle
        {
            Width = group.CharWidth,
            Height = group.FontSize + 2,
            Fill = new SolidColorBrush(WinColor.FromArgb(100, 255, 0, 0))
        };
        Canvas.SetLeft(group.CursorRect, cursorX);
        Canvas.SetTop(group.CursorRect, cursorY);
        DrawingCanvas.Children.Add(group.CursorRect);
        _cursorVisible = true;
    }

    private TextGroup? HitTestTextGroup(double x, double y)
    {
        double hitPadding = 8;
        for (int i = _textGroups.Count - 1; i >= 0; i--)
        {
            var group = _textGroups[i];
            double maxLineWidth = 0;
            foreach (var line in group.Lines)
            {
                double w = MeasureTextWidth(line, group.FontSize, group.CharWidth);
                if (w > maxLineWidth) maxLineWidth = w;
            }
            double totalHeight = group.Lines.Count * (group.FontSize + 4);

            if (x >= group.X - hitPadding &&
                x <= group.X + maxLineWidth + hitPadding &&
                y >= group.Y - hitPadding &&
                y <= group.Y + totalHeight + hitPadding)
            {
                int lineIdx = (int)((y - group.Y) / (group.FontSize + 4));
                if (lineIdx < 0) lineIdx = 0;
                if (lineIdx >= group.Lines.Count) lineIdx = group.Lines.Count - 1;

                group.CursorLine = lineIdx;

                double offsetX = x - group.X;
                string lineText = group.Lines[lineIdx];
                int col = 0;
                for (int c = 1; c <= lineText.Length; c++)
                {
                    if (MeasureTextWidth(lineText[..c], group.FontSize, group.CharWidth) >= offsetX)
                        break;
                    col = c;
                }
                group.CursorColumn = col;

                return group;
            }
        }
        return null;
    }

    private static double MeasureTextWidth(string text, double fontSize, double charWidth)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            // TextBlock.Measure collapses trailing whitespace, so strip trailing
            // spaces and add them back arithmetically.
            int trailSpaces = 0;
            while (trailSpaces < text.Length && text[text.Length - 1 - trailSpaces] == ' ')
                trailSpaces++;

            double measured = 0;
            if (trailSpaces < text.Length)
            {
                var tb = new TextBlock
                {
                    Text = text[..^trailSpaces],
                    FontSize = fontSize,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextTrimming = Microsoft.UI.Xaml.TextTrimming.None,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap
                };
                tb.Measure(new WinSize(double.PositiveInfinity, double.PositiveInfinity));
                measured = tb.DesiredSize.Width;
            }

            return measured + trailSpaces * charWidth;
        }

        private static double MeasureCharWidth(double fontSize)
        {
            // Measure a long calibration string and divide to get an accurate
            // per-character advance. Measuring a single "W" would accumulate
            // rounding error as text grows longer.
            const int calibrationLength = 100;
            var tb = new TextBlock
            {
                Text = new string('W', calibrationLength),
                FontSize = fontSize,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.None,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap
            };
            tb.Measure(new WinSize(double.PositiveInfinity, double.PositiveInfinity));
            return tb.DesiredSize.Width / calibrationLength;
        }

    private void StartCursorBlink()
    {
        StopCursorBlink();
        _cursorBlinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530)
        };
        _cursorBlinkTimer.Tick += (_, _) => ToggleCursor();
        _cursorBlinkTimer.Start();
    }

    private void StopCursorBlink()
    {
        if (_cursorBlinkTimer != null)
        {
            _cursorBlinkTimer.Stop();
            _cursorBlinkTimer = null;
        }
    }

    private void ToggleCursor()
    {
        if (_activeTextGroup?.CursorRect == null) return;

        _cursorVisible = !_cursorVisible;
        _activeTextGroup.CursorRect.Opacity = _cursorVisible ? 1.0 : 0.0;
    }

    private void HandleTextInputCharacter(char c)
    {
        if (_activeTextGroup == null) return;

        string line = _activeTextGroup.Lines[_activeTextGroup.CursorLine];
        if (_activeTextGroup.CursorColumn > line.Length)
            _activeTextGroup.CursorColumn = line.Length;

        _activeTextGroup.Lines[_activeTextGroup.CursorLine] =
            line.Insert(_activeTextGroup.CursorColumn, c.ToString());
        _activeTextGroup.CursorColumn++;

        RenderTextGroup(_activeTextGroup);
        UpdateCursorPosition(_activeTextGroup);
    }

    private void HandleTextInputEnter()
    {
        if (_activeTextGroup == null) return;

        string line = _activeTextGroup.Lines[_activeTextGroup.CursorLine];
        if (_activeTextGroup.CursorColumn > line.Length)
            _activeTextGroup.CursorColumn = line.Length;

        string remainder = line[_activeTextGroup.CursorColumn..];
        _activeTextGroup.Lines[_activeTextGroup.CursorLine] = line[.._activeTextGroup.CursorColumn];
        _activeTextGroup.Lines.Insert(_activeTextGroup.CursorLine + 1, remainder);
        _activeTextGroup.CursorLine++;
        _activeTextGroup.CursorColumn = 0;

        RenderTextGroup(_activeTextGroup);
        UpdateCursorPosition(_activeTextGroup);
    }

    private void HandleTextInputBackspace()
    {
        if (_activeTextGroup == null) return;

        if (_activeTextGroup.CursorColumn > 0)
        {
            string line = _activeTextGroup.Lines[_activeTextGroup.CursorLine];
            _activeTextGroup.Lines[_activeTextGroup.CursorLine] =
                line.Remove(_activeTextGroup.CursorColumn - 1, 1);
            _activeTextGroup.CursorColumn--;
        }
        else if (_activeTextGroup.CursorLine > 0)
        {
            string currentLine = _activeTextGroup.Lines[_activeTextGroup.CursorLine];
            _activeTextGroup.Lines.RemoveAt(_activeTextGroup.CursorLine);
            _activeTextGroup.CursorLine--;
            string prevLine = _activeTextGroup.Lines[_activeTextGroup.CursorLine];
            _activeTextGroup.CursorColumn = prevLine.Length;
            _activeTextGroup.Lines[_activeTextGroup.CursorLine] = prevLine + currentLine;
        }

        RenderTextGroup(_activeTextGroup);
        UpdateCursorPosition(_activeTextGroup);
    }

    #endregion

    #region Action Buttons

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        CopySelectionToClipboard();
    }

    private void CopyTextButton_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Implement OCR text copy later
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSelectionAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsCancelled = true;
        Close();
    }

    #endregion

    #region Keyboard Shortcuts

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isInTextInputMode)
        {
            switch (e.Key)
            {
                case WinVirtualKey.Escape:
                    DeactivateTextInput();
                    e.Handled = true;
                    return;

                case WinVirtualKey.Enter:
                    HandleTextInputEnter();
                    e.Handled = true;
                    return;

                case WinVirtualKey.Back:
                    HandleTextInputBackspace();
                    e.Handled = true;
                    return;

                case WinVirtualKey.Space:
                    HandleTextInputCharacter(' ');
                    e.Handled = true;
                    return;

                case WinVirtualKey.Left:
                    if (_activeTextGroup != null && _activeTextGroup.CursorColumn > 0)
                    {
                        _activeTextGroup.CursorColumn--;
                        UpdateCursorPosition(_activeTextGroup);
                    }
                    e.Handled = true;
                    return;

                case WinVirtualKey.Right:
                    if (_activeTextGroup != null)
                    {
                        string line = _activeTextGroup.Lines[_activeTextGroup.CursorLine];
                        if (_activeTextGroup.CursorColumn < line.Length)
                        {
                            _activeTextGroup.CursorColumn++;
                            UpdateCursorPosition(_activeTextGroup);
                        }
                    }
                    e.Handled = true;
                    return;

                case WinVirtualKey.Up:
                    if (_activeTextGroup != null && _activeTextGroup.CursorLine > 0)
                    {
                        _activeTextGroup.CursorLine--;
                        string line = _activeTextGroup.Lines[_activeTextGroup.CursorLine];
                        if (_activeTextGroup.CursorColumn > line.Length)
                            _activeTextGroup.CursorColumn = line.Length;
                        UpdateCursorPosition(_activeTextGroup);
                    }
                    e.Handled = true;
                    return;

                case WinVirtualKey.Down:
                    if (_activeTextGroup != null && _activeTextGroup.CursorLine < _activeTextGroup.Lines.Count - 1)
                    {
                        _activeTextGroup.CursorLine++;
                        string line = _activeTextGroup.Lines[_activeTextGroup.CursorLine];
                        if (_activeTextGroup.CursorColumn > line.Length)
                            _activeTextGroup.CursorColumn = line.Length;
                        UpdateCursorPosition(_activeTextGroup);
                    }
                    e.Handled = true;
                    return;

                case WinVirtualKey.Tab:
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case WinVirtualKey.Escape:
                IsCancelled = true;
                Close();
                break;

            case WinVirtualKey.C:
                if (InputKeyboardSource.GetKeyStateForCurrentThread(WinVirtualKey.Control)
                    is CoreVirtualKeyStates.Down)
                {
                    CopySelectionToClipboard();
                }
                break;

            case WinVirtualKey.S:
                if (InputKeyboardSource.GetKeyStateForCurrentThread(WinVirtualKey.Control)
                    is CoreVirtualKeyStates.Down)
                {
                    _ = SaveSelectionAsync();
                }
                break;
        }
    }

    private void RootGrid_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_isInTextInputMode && !char.IsControl(args.Character))
        {
            HandleTextInputCharacter(args.Character);
            args.Handled = true;
        }
    }

    #endregion

    #region Clipboard and Save

    private void CopySelectionToClipboard()
    {
        Bitmap bitmapToCopy;
        if (_hasSelection)
        {
            bitmapToCopy = CropBitmap(_currentSelection);
        }
        else
        {
            bitmapToCopy = (Bitmap)_screenshotBitmap.Clone();
        }

        using var stream = new MemoryStream();
        bitmapToCopy.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var dataPackage = new DataPackage();
        dataPackage.SetData("image/png", stream.AsRandomAccessStream());
        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();

        if (_hasSelection)
        {
            bitmapToCopy.Dispose();
        }
    }

    private async Task SaveSelectionAsync()
    {
        Bitmap bitmapToSave;
        if (_hasSelection)
        {
            bitmapToSave = CropBitmap(_currentSelection);
        }
        else
        {
            bitmapToSave = (Bitmap)_screenshotBitmap.Clone();
        }

        try
        {
            await ScreenshotService.SaveScreenshotAsync(bitmapToSave);
        }
        finally
        {
            if (_hasSelection)
            {
                bitmapToSave.Dispose();
            }
        }
    }

    private Bitmap CropBitmap(WinRect selection)
    {
        double scaleX = _screenshotBitmap.Width / RootGrid.ActualWidth;
        double scaleY = _screenshotBitmap.Height / RootGrid.ActualHeight;

        int bmpX = Math.Max(0, (int)(selection.X * scaleX));
        int bmpY = Math.Max(0, (int)(selection.Y * scaleY));
        int bmpW = Math.Min((int)(selection.Width * scaleX), _screenshotBitmap.Width - bmpX);
        int bmpH = Math.Min((int)(selection.Height * scaleY), _screenshotBitmap.Height - bmpY);

        if (bmpW <= 0 || bmpH <= 0)
        {
            return (Bitmap)_screenshotBitmap.Clone();
        }

        var cropped = new Bitmap(bmpW, bmpH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.DrawImage(_screenshotBitmap,
                new System.Drawing.Rectangle(0, 0, bmpW, bmpH),
                new System.Drawing.Rectangle(bmpX, bmpY, bmpW, bmpH),
                GraphicsUnit.Pixel);
        }

        return cropped;
    }

    #endregion
}