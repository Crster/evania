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

    // Blur-specific drag state (independent from Select tool)
    private System.Drawing.Point _blurSelectionStart;
    private System.Drawing.Point _blurSelectionEnd;

    // Draw-specific drag state (independent from Select selection)
    private System.Drawing.Point _drawStart;
    private System.Drawing.Point _drawEnd;

    // Re-usable preview shapes to avoid per-move allocation and layout churn.
    private ShapeRectangle? _previewBox;
    private ShapePath? _previewArrow;
    private ShapeRectangle? _previewBlur;
    private ShapeRectangle? _previewBlurWhite;

    public bool IsCancelled { get; private set; }

    /// <summary>
    /// Path of the file the edited image was written to, or null when nothing was saved.
    /// </summary>
    public string? SavedFilePath { get; private set; }

    /// <summary>
    /// The edited image (annotations and blurs baked in, cropped to the selection when the
    /// user made one). The caller owns it. Null only when the session was cancelled.
    /// </summary>
    public Bitmap? EditedBitmap { get; private set; }

    // The region of _screenshotBitmap that holds the real image. Editing an existing,
    // smaller screenshot pads it onto a screen-sized canvas so on-screen coordinates line
    // up; this is what lets the result be trimmed back to the image itself on the way out.
    private readonly System.Drawing.Rectangle _contentBounds;

    // Set once the session has produced its result, so a second Ctrl+S / Ctrl+C while the
    // first one is still writing cannot emit a duplicate file or clobber the result.
    private bool _resultTaken;

    private bool _isDraggingToolbar;
    private double _toolbarDragOffsetX;
    private double _toolbarDragOffsetY;

    // Text input state
    private TextGroup? _activeTextGroup;
    private DispatcherTimer? _cursorBlinkTimer;
    private bool _cursorVisible;
    private bool _isInTextInputMode;

    // Track tint overlay elements separately so we can clear/redraw them
    // without affecting persistent drawing elements (boxes, arrows, text)
    private readonly List<UIElement> _tintElements = new();

    public EditScreenshotWindow(
        Bitmap screenshotBitmap,
        System.Drawing.Rectangle? contentBounds = null)
    {
        _screenshotBitmap = screenshotBitmap;
        _contentBounds = contentBounds ?? new System.Drawing.Rectangle(
            0, 0, screenshotBitmap.Width, screenshotBitmap.Height);
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

    private bool IsPointerOverToolbar(object originalSource)
    {
        if (originalSource is FrameworkElement fe)
        {
            var parent = fe.Parent;
            while (parent != null)
            {
                if (parent == ToolbarBorder)
                    return true;
                parent = (parent as FrameworkElement)?.Parent;
            }
        }
        return false;
    }

    #endregion

    #region Tool Selection

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        DeactivateTextInput();

        var button = (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)sender;
        if (button == SelectToolButton) _currentTool = EditToolMode.Select;
        else if (button == DrawBoxToolButton) _currentTool = EditToolMode.DrawBox;
        else if (button == DrawArrowToolButton) _currentTool = EditToolMode.DrawArrow;
        else if (button == DrawTextToolButton) _currentTool = EditToolMode.DrawText;
        else if (button == BlurToolButton) _currentTool = EditToolMode.Blur;

        UpdateToolButtonStyles();
        UpdateTintOverlay();
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

        // Ignore presses that originate from the toolbar (buttons, grip, etc.)
        if (IsPointerOverToolbar(e.OriginalSource))
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

        if (_currentTool == EditToolMode.Blur)
        {
            _blurSelectionStart = new System.Drawing.Point((int)point.X, (int)point.Y);
            _blurSelectionEnd = _blurSelectionStart;
        }
        else if (_currentTool == EditToolMode.Select)
        {
            _selectionStart = new System.Drawing.Point((int)point.X, (int)point.Y);
            _selectionEnd = _selectionStart;
        }
        else
        {
            _drawStart = new System.Drawing.Point((int)point.X, (int)point.Y);
            _drawEnd = _drawStart;
        }
        _isDragging = true;

        RootGrid.CapturePointer(e.Pointer);

        if (_currentTool == EditToolMode.DrawBox)
        {
            EnsureDrawBoxPreview();
        }
        else if (_currentTool == EditToolMode.DrawArrow)
        {
            EnsureArrowPreview(_drawStart.X, _drawStart.Y);
        }
        else if (_currentTool == EditToolMode.Blur)
        {
            EnsureBlurPreview();
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(RootGrid).Position;

        if (_currentTool == EditToolMode.Blur)
        {
            _blurSelectionEnd = new System.Drawing.Point((int)point.X, (int)point.Y);
        }
        else if (_currentTool == EditToolMode.Select)
        {
            _selectionEnd = new System.Drawing.Point((int)point.X, (int)point.Y);
        }
        else
        {
            _drawEnd = new System.Drawing.Point((int)point.X, (int)point.Y);
        }

        if (_currentTool == EditToolMode.DrawBox)
        {
            UpdateDrawBoxPreview(point.X, point.Y);
        }
        else if (_currentTool == EditToolMode.DrawArrow)
        {
            UpdateArrowPreview(point.X, point.Y);
        }
        else if (_currentTool == EditToolMode.Blur)
        {
            UpdateBlurPreview(point.X, point.Y);
        }
        else if (_currentTool == EditToolMode.Select)
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

        if (_currentTool == EditToolMode.Blur)
        {
            _blurSelectionEnd = new System.Drawing.Point((int)point.X, (int)point.Y);
            var r = MakeRect(_blurSelectionStart, _blurSelectionEnd);

            if (r.Width > 5 || r.Height > 5)
            {
                RemoveBlurPreview();
                ApplyBlur(r);
                UpdateTintOverlay();
            }
            else
            {
                RemoveBlurPreview();
            }

            return;
        }

        if (_currentTool == EditToolMode.Select)
        {
            _selectionEnd = new System.Drawing.Point((int)point.X, (int)point.Y);
            var r = MakeRect(_selectionStart, _selectionEnd);

            if (r.Width > 5 || r.Height > 5)
            {
                _hasSelection = true;
                _currentSelection = r;
                UpdateTintOverlay();
            }
            else
            {
                RemovePreview();
            }
        }
        else if (_currentTool == EditToolMode.DrawBox)
        {
            _drawEnd = new System.Drawing.Point((int)point.X, (int)point.Y);
            var r = MakeRect(_drawStart, _drawEnd);

            if (r.Width > 5 || r.Height > 5)
            {
                DrawRedBox(r);
                RemovePreview();
            }
            else
            {
                RemovePreview();
            }
        }
        else if (_currentTool == EditToolMode.DrawArrow)
        {
            _drawEnd = new System.Drawing.Point((int)point.X, (int)point.Y);

            int dx = Math.Abs(_drawEnd.X - _drawStart.X);
            int dy = Math.Abs(_drawEnd.Y - _drawStart.Y);

            if (dx > 5 || dy > 5)
            {
                DrawRedArrow(_drawStart, _drawEnd);
                RemovePreview();
            }
            else
            {
                RemovePreview();
            }
        }
    }

    private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_currentTool == EditToolMode.Select)
        {
            // Cancel the drag that PointerPressed started on the second tap.
            _isDragging = false;
            RootGrid.ReleasePointerCaptures();

            double width = RootGrid.ActualWidth;
            double height = RootGrid.ActualHeight;
            _hasSelection = true;
            _currentSelection = new WinRect(0, 0, width, height);
            UpdateTintOverlay();
        }
    }

    #endregion

    #region Tint Overlay

    private static WinRect MakeRect(System.Drawing.Point a, System.Drawing.Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new WinRect(x, y, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    private void UpdateTintOverlay()
    {
        // Remove only tint elements, preserving drawings (boxes, arrows, text)
        foreach (var element in _tintElements)
        {
            OverlayCanvas.Children.Remove(element);
        }
        _tintElements.Clear();

        bool isBlur = _currentTool == EditToolMode.Blur;

        // The Select selection always takes priority for the tint hole.
        // Blur only gets a hole when there is no Select selection.
        WinRect? holeRect = null;
        bool holeIsBlur = false;

        if (!isBlur && _isDragging)
        {
            holeRect = MakeRect(_selectionStart, _selectionEnd);
        }
        else if (_hasSelection)
        {
            holeRect = _currentSelection;
        }
        else if (isBlur && _isDragging)
        {
            holeRect = MakeRect(_blurSelectionStart, _blurSelectionEnd);
            holeIsBlur = true;
        }

        if (holeRect == null)
        {
            AddTintRect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight);
            DrawSelectionBorder(_hasSelection, _currentSelection, dashed: true);
            return;
        }

        var sel = holeRect.Value;
        double canvasW = RootGrid.ActualWidth;
        double canvasH = RootGrid.ActualHeight;

        if (sel.Y > 0)
            AddTintRect(0, 0, canvasW, sel.Y);
        if (sel.Y + sel.Height < canvasH)
            AddTintRect(0, sel.Y + sel.Height, canvasW, canvasH - sel.Y - sel.Height);
        if (sel.X > 0)
            AddTintRect(0, sel.Y, sel.X, sel.Height);
        if (sel.X + sel.Width < canvasW)
            AddTintRect(sel.X + sel.Width, sel.Y, canvasW - sel.X - sel.Width, sel.Height);

        DrawSelectionBorder(true, sel, dashed: holeIsBlur ? false : true);

        if (holeIsBlur && _hasSelection)
            DrawSelectionBorder(true, _currentSelection, dashed: true);
    }

    private void DrawSelectionBorder(bool hasSelection, WinRect rect, bool dashed)
    {
        if (!hasSelection) return;

        // Black base border
        var blackBorder = new ShapeRectangle
        {
            Width = rect.Width,
            Height = rect.Height,
            Stroke = new SolidColorBrush(WinColor.FromArgb(255, 0, 0, 0)),
            StrokeThickness = 1,
            StrokeDashArray = dashed ? new DoubleCollection { 4, 4 } : null
        };
        Canvas.SetLeft(blackBorder, rect.X);
        Canvas.SetTop(blackBorder, rect.Y);
        Canvas.SetZIndex(blackBorder, 0);
        OverlayCanvas.Children.Add(blackBorder);
        _tintElements.Add(blackBorder);

        // White dashed overlay for marching ants effect
        var whiteBorder = new ShapeRectangle
        {
            Width = rect.Width,
            Height = rect.Height,
            Stroke = new SolidColorBrush(WinColor.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            StrokeDashOffset = 4
        };
        Canvas.SetLeft(whiteBorder, rect.X);
        Canvas.SetTop(whiteBorder, rect.Y);
        Canvas.SetZIndex(whiteBorder, 0);
        OverlayCanvas.Children.Add(whiteBorder);
        _tintElements.Add(whiteBorder);
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
        Canvas.SetZIndex(rect, 0);
        OverlayCanvas.Children.Add(rect);
        _tintElements.Add(rect);
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
        Canvas.SetZIndex(_previewBox, 1);
        OverlayCanvas.Children.Add(_previewBox);
    }

    private void UpdateDrawBoxPreview(double endX, double endY)
    {
        if (_previewBox == null) return;

        var r = MakeRect(_drawStart, new System.Drawing.Point((int)endX, (int)endY));
        _previewBox.Width = r.Width;
        _previewBox.Height = r.Height;
        Canvas.SetLeft(_previewBox, r.X);
        Canvas.SetTop(_previewBox, r.Y);
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
        Canvas.SetZIndex(_previewArrow, 1);
        OverlayCanvas.Children.Add(_previewArrow);

        // On the very first click show only a small arrowhead at the click point
        // oriented toward the center of the grid — no shaft yet.
        double centerX = RootGrid.ActualWidth / 2.0;
        double centerY = RootGrid.ActualHeight / 2.0;
        double angle = Math.Atan2(centerY - startY, centerX - startX);
        _previewArrow.Data = BuildHeadOnlyGeometry(startX, startY, angle);
    }

    private static PathFigure BuildArrowHeadFigure(double tipX, double tipY, double angle)
    {
        const double headLength = 12;
        const double headHalfWidth = 5;

        double baseX = tipX - headLength * Math.Cos(angle);
        double baseY = tipY - headLength * Math.Sin(angle);
        double b1x = baseX + headHalfWidth * Math.Cos(angle + Math.PI / 2);
        double b1y = baseY + headHalfWidth * Math.Sin(angle + Math.PI / 2);
        double b2x = baseX - headHalfWidth * Math.Cos(angle + Math.PI / 2);
        double b2y = baseY - headHalfWidth * Math.Sin(angle + Math.PI / 2);

        var figure = new PathFigure
        {
            StartPoint = new WinPoint(tipX, tipY),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new LineSegment { Point = new WinPoint(b1x, b1y) });
        figure.Segments.Add(new LineSegment { Point = new WinPoint(b2x, b2y) });
        return figure;
    }

    private static Geometry BuildHeadOnlyGeometry(double tipX, double tipY, double angle)
    {
        var geometry = new PathGeometry();
        geometry.Figures.Add(BuildArrowHeadFigure(tipX, tipY, angle));
        return geometry;
    }

    private static Geometry BuildArrowGeometry(double startX, double startY, double endX, double endY)
    {
        double angle = Math.Atan2(endY - startY, endX - startX);

        var geometry = new PathGeometry();

        var shaftFigure = new PathFigure
        {
            StartPoint = new WinPoint(startX, startY),
            IsClosed = false
        };
        double baseX = endX - 12 * Math.Cos(angle);
        double baseY = endY - 12 * Math.Sin(angle);
        shaftFigure.Segments.Add(new LineSegment { Point = new WinPoint(baseX, baseY) });
        geometry.Figures.Add(shaftFigure);

        geometry.Figures.Add(BuildArrowHeadFigure(endX, endY, angle));

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
        if (Math.Abs(endX - _drawStart.X) < 3 && Math.Abs(endY - _drawStart.Y) < 3)
        {
            return;
        }

        // Replace the geometry as a single unit each frame; no per-element mutation.
        _previewArrow.Data = BuildArrowGeometry(_drawStart.X, _drawStart.Y, endX, endY);
    }

    private void RemovePreview()
    {
        if (_previewBox != null)
        {
            OverlayCanvas.Children.Remove(_previewBox);
            _previewBox = null;
        }
        if (_previewArrow != null)
        {
            OverlayCanvas.Children.Remove(_previewArrow);
            _previewArrow = null;
        }
    }

    private void EnsureBlurPreview()
    {
        if (_previewBlur != null)
            return;

        // Outer black border (1px solid)
        _previewBlur = new ShapeRectangle
        {
            Stroke = new SolidColorBrush(WinColor.FromArgb(255, 0, 0, 0)),
            StrokeThickness = 1
        };
        Canvas.SetZIndex(_previewBlur, 1);
        OverlayCanvas.Children.Add(_previewBlur);

        // Inner white border (1px solid) with white tint fill
        _previewBlurWhite = new ShapeRectangle
        {
            Stroke = new SolidColorBrush(WinColor.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(WinColor.FromArgb(60, 255, 255, 255))
        };
        Canvas.SetZIndex(_previewBlurWhite, 1);
        OverlayCanvas.Children.Add(_previewBlurWhite);
    }

    private void UpdateBlurPreview(double endX, double endY)
    {
        if (_previewBlur == null) return;

        var r = MakeRect(_blurSelectionStart, new System.Drawing.Point((int)endX, (int)endY));
        _previewBlur.Width = r.Width;
        _previewBlur.Height = r.Height;
        Canvas.SetLeft(_previewBlur, r.X);
        Canvas.SetTop(_previewBlur, r.Y);

        if (_previewBlurWhite != null)
        {
            _previewBlurWhite.Width = Math.Max(0, r.Width - 2);
            _previewBlurWhite.Height = Math.Max(0, r.Height - 2);
            Canvas.SetLeft(_previewBlurWhite, r.X + 1);
            Canvas.SetTop(_previewBlurWhite, r.Y + 1);
        }
    }

    private void RemoveBlurPreview()
    {
        if (_previewBlur != null)
        {
            OverlayCanvas.Children.Remove(_previewBlur);
            _previewBlur = null;
        }
        if (_previewBlurWhite != null)
        {
            OverlayCanvas.Children.Remove(_previewBlurWhite);
            _previewBlurWhite = null;
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
        Canvas.SetZIndex(rect, 1);
        OverlayCanvas.Children.Add(rect);
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
        Canvas.SetZIndex(path, 1);
        OverlayCanvas.Children.Add(path);
    }

    private void ApplyBlur(WinRect selection)
    {
        // Get the actual image display area
        double imageWidth = ScreenshotImage.ActualWidth;
        double imageHeight = ScreenshotImage.ActualHeight;
        double gridWidth = RootGrid.ActualWidth;
        double gridHeight = RootGrid.ActualHeight;

        // Calculate image offset (image is centered)
        double imageOffsetX = (gridWidth - imageWidth) / 2;
        double imageOffsetY = (gridHeight - imageHeight) / 2;

        // Adjust selection coordinates relative to the image
        double relX = selection.X - imageOffsetX;
        double relY = selection.Y - imageOffsetY;

        // Map to bitmap coordinates
        double scaleX = _screenshotBitmap.Width / imageWidth;
        double scaleY = _screenshotBitmap.Height / imageHeight;

        int bmpX = Math.Max(0, (int)(relX * scaleX));
        int bmpY = Math.Max(0, (int)(relY * scaleY));
        int bmpW = Math.Min((int)(selection.Width * scaleX), _screenshotBitmap.Width - bmpX);
        int bmpH = Math.Min((int)(selection.Height * scaleY), _screenshotBitmap.Height - bmpY);

        if (bmpW <= 0 || bmpH <= 0) return;

        const int radius = 8;
        const double sigma = 4.0;

        // Precompute 1D Gaussian kernel.
        int kernelSize = 2 * radius + 1;
        float[] kernel = new float[kernelSize];
        float kernelSum = 0f;
        for (int i = 0; i < kernelSize; i++)
        {
            double d = i - radius;
            kernel[i] = (float)Math.Exp(-(d * d) / (2.0 * sigma * sigma));
            kernelSum += kernel[i];
        }
        for (int i = 0; i < kernelSize; i++)
            kernel[i] /= kernelSum;

        // Lock the entire bitmap.
        var fullRect = new System.Drawing.Rectangle(0, 0, _screenshotBitmap.Width, _screenshotBitmap.Height);
        var bmpData = _screenshotBitmap.LockBits(fullRect, ImageLockMode.ReadWrite, _screenshotBitmap.PixelFormat);
        int bytesPerPixel = System.Drawing.Image.GetPixelFormatSize(_screenshotBitmap.PixelFormat) / 8;
        int stride = bmpData.Stride;
        int size = stride * bmpData.Height;
        byte[] pixels = new byte[size];
        System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, pixels, 0, size);

        byte[] result = new byte[size];
        Array.Copy(pixels, result, size);

        // Temporary buffer for horizontal pass.
        byte[] temp = new byte[size];
        Array.Copy(pixels, temp, size);

        int regionEndY = bmpY + bmpH;
        int regionEndX = bmpX + bmpW;

        // Horizontal pass: blur within the selection region only.
        for (int y = bmpY; y < regionEndY; y++)
        {
            for (int x = bmpX; x < regionEndX; x++)
            {
                float sumR = 0, sumG = 0, sumB = 0, weightSum = 0;

                for (int k = 0; k < kernelSize; k++)
                {
                    int nx = x + k - radius;
                    if (nx < bmpX || nx >= regionEndX) continue;

                    int idx = y * stride + nx * bytesPerPixel;
                    float w = kernel[k];
                    sumB += pixels[idx] * w;
                    sumG += pixels[idx + 1] * w;
                    sumR += pixels[idx + 2] * w;
                    weightSum += w;
                }

                if (weightSum == 0) continue;

                int destIdx = y * stride + x * bytesPerPixel;
                temp[destIdx] = (byte)(sumB / weightSum);
                temp[destIdx + 1] = (byte)(sumG / weightSum);
                temp[destIdx + 2] = (byte)(sumR / weightSum);
            }
        }

        // Vertical pass: blur the horizontal result within the selection region.
        for (int y = bmpY; y < regionEndY; y++)
        {
            for (int x = bmpX; x < regionEndX; x++)
            {
                float sumR = 0, sumG = 0, sumB = 0, weightSum = 0;

                for (int k = 0; k < kernelSize; k++)
                {
                    int ny = y + k - radius;
                    if (ny < bmpY || ny >= regionEndY) continue;

                    int idx = ny * stride + x * bytesPerPixel;
                    float w = kernel[k];
                    sumB += temp[idx] * w;
                    sumG += temp[idx + 1] * w;
                    sumR += temp[idx + 2] * w;
                    weightSum += w;
                }

                if (weightSum == 0) continue;

                int destIdx = y * stride + x * bytesPerPixel;
                result[destIdx] = (byte)(sumB / weightSum);
                result[destIdx + 1] = (byte)(sumG / weightSum);
                result[destIdx + 2] = (byte)(sumR / weightSum);
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(result, 0, bmpData.Scan0, size);
        _screenshotBitmap.UnlockBits(bmpData);

        RefreshScreenshotImage();
    }

    private async void RefreshScreenshotImage()
    {
        var bitmapImage = await ScreenshotService.ToBitmapImageAsync(_screenshotBitmap);
        ScreenshotImage.Source = bitmapImage;
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
            OverlayCanvas.Children.Remove(_activeTextGroup.CursorRect);
            _activeTextGroup.CursorRect = null;
        }

        _activeTextGroup = null;
        _isInTextInputMode = false;
    }

    private void RenderTextGroup(TextGroup group)
    {
        foreach (var tb in group.TextBlocks)
        {
            OverlayCanvas.Children.Remove(tb);
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
            Canvas.SetZIndex(textBlock, 1);
            OverlayCanvas.Children.Add(textBlock);
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
            OverlayCanvas.Children.Remove(group.CursorRect);
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
        Canvas.SetZIndex(group.CursorRect, 1);
        OverlayCanvas.Children.Add(group.CursorRect);
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

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasSelection)
            return;

        await RunOnSelectionAsync(WriteToClipboardAsync);
    }

    private async void CopyTextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasSelection)
            return;

        await RunOnSelectionAsync(async selection =>
        {
            string? text = await OcrService.RecognizeTextAsync(selection);
            if (!string.IsNullOrWhiteSpace(text))
            {
                ScreenshotService.CopyTextToClipboard(text);
            }
        });
    }

    private void ColorPickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasSelection)
            return;

        // Sample the screenshot pixels under the selection (blurs baked in, annotation overlay
        // ignored) rather than the flattened output a copy would emit: a picker reads the
        // colour that is there, not the marks drawn on top of it. The window stays open so
        // several colours can be picked in one session.
        using var selection = CropFromBitmap(_screenshotBitmap, _currentSelection);
        string? hex = ColorPickerService.GetDominantColorHex(selection);
        if (hex is not null)
        {
            ScreenshotService.CopyTextToClipboard(hex);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAndCloseAsync();
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
                    if (_hasSelection)
                    {
                        _ = RunOnSelectionAsync(WriteToClipboardAsync);
                        e.Handled = true;
                        return;
                    }
                }
                break;

            case WinVirtualKey.S:
                if (InputKeyboardSource.GetKeyStateForCurrentThread(WinVirtualKey.Control)
                    is CoreVirtualKeyStates.Down)
                {
                    _ = SaveAndCloseAsync();
                    e.Handled = true;
                    return;
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

    private static async Task WriteToClipboardAsync(Bitmap bitmap)
    {
        // Encode as PNG to memory stream
        using var memStream = new MemoryStream();
        bitmap.Save(memStream, System.Drawing.Imaging.ImageFormat.Png);
        memStream.Position = 0;

        // Convert to RandomAccessStream
        var randomAccessStream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new global::Windows.Storage.Streams.DataWriter(randomAccessStream.GetOutputStreamAt(0));
        var bytes = memStream.ToArray();
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await randomAccessStream.FlushAsync();
        randomAccessStream.Seek(0);

        var dataPackage = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetBitmap(global::Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(randomAccessStream));
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    /// <summary>
    /// Flattens the on-screen annotations into the bitmap and returns what the user is left
    /// with: the selection when they made one, otherwise the image trimmed back to the region
    /// it occupied before being padded to screen size.
    /// </summary>
    private async Task<Bitmap> RenderEditedBitmapAsync()
    {
        using var rendered = await RenderCanvasToBitmap();
        return _hasSelection
            ? CropFromBitmap(rendered, _currentSelection)
            : CropToContent(rendered);
    }

    private Bitmap CropToContent(Bitmap source)
    {
        var bounds = _contentBounds;
        bounds.Intersect(new System.Drawing.Rectangle(0, 0, source.Width, source.Height));

        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            (bounds.Width == source.Width && bounds.Height == source.Height))
        {
            return (Bitmap)source.Clone();
        }

        var cropped = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.DrawImage(source,
                new System.Drawing.Rectangle(0, 0, bounds.Width, bounds.Height),
                bounds,
                GraphicsUnit.Pixel);
        }

        return cropped;
    }

    /// <summary>
    /// Flattens the annotations, hands the cropped selection to <paramref name="actOnSelection"/>
    /// (clipboard image, OCR text, ...), commits the edited image and ends the session. Runs once:
    /// a second invocation while the first is still working is ignored.
    /// </summary>
    private async Task RunOnSelectionAsync(Func<Bitmap, Task> actOnSelection)
    {
        if (_resultTaken)
            return;
        _resultTaken = true;

        try
        {
            using var rendered = await RenderCanvasToBitmap();
            using (var selection = CropFromBitmap(rendered, _currentSelection))
            {
                await actOnSelection(selection);
            }

            EditedBitmap = CropToContent(rendered);
        }
        catch (Exception ex)
        {
            // The result is already claimed, so leaving the window open here would strand it.
            System.Diagnostics.Debug.WriteLine($"Edit action failed: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private async Task SaveAndCloseAsync()
    {
        if (_resultTaken)
            return;
        _resultTaken = true;

        try
        {
            EditedBitmap = await RenderEditedBitmapAsync();
            SavedFilePath = await ScreenshotService.SaveScreenshotAsync(EditedBitmap);
        }
        catch (Exception ex)
        {
            // The edited image survives as an unsaved item, so the list can offer Save again.
            System.Diagnostics.Debug.WriteLine($"Save failed: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private async Task<Bitmap> RenderCanvasToBitmap()
    {
        // Start with a copy of the current (blurred) screenshot
        var result = (Bitmap)_screenshotBitmap.Clone();

        // Hide tint elements so they don't render into the output
        foreach (var tint in _tintElements)
        {
            tint.Opacity = 0;
        }

        // OverlayCanvas is a Canvas inside RootGrid with no explicit size, so
        // RenderTargetBitmap would measure it as 0x0 and render nothing.
        // Temporarily give it the grid's size and force a layout pass so the
        // annotation children (arrows, boxes, text) are measured, arranged, and
        // rasterized. Without the Measure/Arrange, RenderAsync still sees the
        // stale empty layout and would render nothing.
        OverlayCanvas.Width = RootGrid.ActualWidth;
        OverlayCanvas.Height = RootGrid.ActualHeight;

        try
        {
            var renderTarget = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();

            // Force the canvas to re-layout at its new explicit size before
            // rasterizing. This ensures the annotation children render.
            OverlayCanvas.Measure(new WinSize(RootGrid.ActualWidth, RootGrid.ActualHeight));
            OverlayCanvas.Arrange(new WinRect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight));
            OverlayCanvas.UpdateLayout();

            await renderTarget.RenderAsync(OverlayCanvas);

            // Get pixel data from the rendered canvas
            var pixelBuffer = await renderTarget.GetPixelsAsync();
            using var dataReader = global::Windows.Storage.Streams.DataReader.FromBuffer(pixelBuffer);
            byte[] canvasPixels = new byte[pixelBuffer.Length];
            dataReader.ReadBytes(canvasPixels);

            int canvasWidth = renderTarget.PixelWidth;
            int canvasHeight = renderTarget.PixelHeight;

            if (canvasWidth == 0 || canvasHeight == 0)
                return result;

            // Create a bitmap from the canvas pixels
            using var canvasBitmap = new Bitmap(canvasWidth, canvasHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var canvasData = canvasBitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, canvasWidth, canvasHeight),
                ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            // RenderTargetBitmap gives BGRA, System.Drawing expects the same on Windows
            System.Runtime.InteropServices.Marshal.Copy(canvasPixels, 0, canvasData.Scan0, canvasPixels.Length);
            canvasBitmap.UnlockBits(canvasData);

            // Calculate where the image is positioned on the canvas (accounting for centering)
            double imageWidth = ScreenshotImage.ActualWidth;
            double imageHeight = ScreenshotImage.ActualHeight;
            double gridWidth = RootGrid.ActualWidth;
            double gridHeight = RootGrid.ActualHeight;

            double imageOffsetX = (gridWidth - imageWidth) / 2;
            double imageOffsetY = (gridHeight - imageHeight) / 2;

            // Draw only the image area from the canvas onto the bitmap.
            // AlphaBlend so that transparent canvas pixels leave the screenshot untouched.
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;

                // Source rectangle: the image area on the canvas
                var srcRect = new System.Drawing.Rectangle(
                    (int)(imageOffsetX * canvasWidth / gridWidth),
                    (int)(imageOffsetY * canvasHeight / gridHeight),
                    (int)(imageWidth * canvasWidth / gridWidth),
                    (int)(imageHeight * canvasHeight / gridHeight));

                // Destination rectangle: the entire bitmap
                var destRect = new System.Drawing.Rectangle(0, 0, result.Width, result.Height);

                g.DrawImage(canvasBitmap, destRect, srcRect, GraphicsUnit.Pixel);
            }
        }
        finally
        {
            // Restore tint elements visibility
            foreach (var tint in _tintElements)
            {
                tint.Opacity = 1;
            }
            OverlayCanvas.Width = double.NaN;
            OverlayCanvas.Height = double.NaN;
        }

        return result;
    }

    private Bitmap CropFromBitmap(Bitmap source, WinRect selection)
    {
        double scaleX = source.Width / RootGrid.ActualWidth;
        double scaleY = source.Height / RootGrid.ActualHeight;

        int bmpX = Math.Max(0, (int)(selection.X * scaleX));
        int bmpY = Math.Max(0, (int)(selection.Y * scaleY));
        int bmpW = Math.Min((int)(selection.Width * scaleX), source.Width - bmpX);
        int bmpH = Math.Min((int)(selection.Height * scaleY), source.Height - bmpY);

        if (bmpW <= 0 || bmpH <= 0)
        {
            return (Bitmap)source.Clone();
        }

        var cropped = new Bitmap(bmpW, bmpH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.DrawImage(source,
                new System.Drawing.Rectangle(0, 0, bmpW, bmpH),
                new System.Drawing.Rectangle(bmpX, bmpY, bmpW, bmpH),
                GraphicsUnit.Pixel);
        }

        return cropped;
    }

    #endregion
}