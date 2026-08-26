using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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

    // Blur-specific selection state (independent from Select tool)
    private System.Drawing.Point _blurSelectionStart;
    private System.Drawing.Point _blurSelectionEnd;
    private WinRect _blurSelection;
    private bool _hasBlurSelection;

    // Draw-specific drag state (independent from Select selection)
    private System.Drawing.Point _drawStart;
    private System.Drawing.Point _drawEnd;

    // Re-usable preview shapes to avoid per-move allocation and layout churn.
    private ShapeRectangle? _previewBox;
    private ShapePath? _previewArrow;
    private ShapeRectangle? _previewBlur;

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

    private void SelectToolButton_Click(object sender, RoutedEventArgs e)
    {
        DeactivateTextInput();
        _currentTool = EditToolMode.Select;
        UpdateToolButtonStyles();
        UpdateTintOverlay();
    }

    private void DrawBoxToolButton_Click(object sender, RoutedEventArgs e)
    {
        DeactivateTextInput();
        _currentTool = EditToolMode.DrawBox;
        UpdateToolButtonStyles();
        UpdateTintOverlay();
    }

    private void DrawArrowToolButton_Click(object sender, RoutedEventArgs e)
    {
        DeactivateTextInput();
        _currentTool = EditToolMode.DrawArrow;
        UpdateToolButtonStyles();
        UpdateTintOverlay();
    }

    private void DrawTextToolButton_Click(object sender, RoutedEventArgs e)
    {
        _currentTool = EditToolMode.DrawText;
        UpdateToolButtonStyles();
        UpdateTintOverlay();
    }

    private void BlurToolButton_Click(object sender, RoutedEventArgs e)
    {
        DeactivateTextInput();
        _currentTool = EditToolMode.Blur;
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

            int x = Math.Min(_blurSelectionStart.X, _blurSelectionEnd.X);
            int y = Math.Min(_blurSelectionStart.Y, _blurSelectionEnd.Y);
            int w = Math.Abs(_blurSelectionEnd.X - _blurSelectionStart.X);
            int h = Math.Abs(_blurSelectionEnd.Y - _blurSelectionStart.Y);

            if (w > 5 || h > 5)
            {
                RemoveBlurPreview();
                _blurSelection = new WinRect(x, y, w, h);
                ApplyBlur(_blurSelection);
                _hasBlurSelection = false;
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

            int sx = Math.Min(_selectionStart.X, _selectionEnd.X);
            int sy = Math.Min(_selectionStart.Y, _selectionEnd.Y);
            int sw = Math.Abs(_selectionEnd.X - _selectionStart.X);
            int sh = Math.Abs(_selectionEnd.Y - _selectionStart.Y);

            if (sw > 5 || sh > 5)
            {
                _hasSelection = true;
                _currentSelection = new WinRect(sx, sy, sw, sh);
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

            int sx = Math.Min(_drawStart.X, _drawEnd.X);
            int sy = Math.Min(_drawStart.Y, _drawEnd.Y);
            int sw = Math.Abs(_drawEnd.X - _drawStart.X);
            int sh = Math.Abs(_drawEnd.Y - _drawStart.Y);

            if (sw > 5 || sh > 5)
            {
                DrawRedBox(new WinRect(sx, sy, sw, sh));
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

    private void UpdateTintOverlay()
    {
        TintCanvas.Children.Clear();

        bool isBlur = _currentTool == EditToolMode.Blur;

        // The Select selection always takes priority for the tint hole.
        // Blur only gets a hole when there is no Select selection.
        WinRect? holeRect = null;
        bool holeIsBlur = false;

        if (!isBlur && _isDragging)
        {
            // Live drag preview always takes priority.
            int sx = _selectionStart.X;
            int sy = _selectionStart.Y;
            int ex = _selectionEnd.X;
            int ey = _selectionEnd.Y;
            int x = Math.Min(sx, ex);
            int y = Math.Min(sy, ey);
            int w = Math.Abs(ex - sx);
            int h = Math.Abs(ey - sy);
            holeRect = new WinRect(x, y, w, h);
        }
        else if (_hasSelection)
        {
            holeRect = _currentSelection;
        }
        else if (isBlur && _isDragging)
        {
            int sx = _blurSelectionStart.X;
            int sy = _blurSelectionStart.Y;
            int ex = _blurSelectionEnd.X;
            int ey = _blurSelectionEnd.Y;
            int x = Math.Min(sx, ex);
            int y = Math.Min(sy, ey);
            int w = Math.Abs(ex - sx);
            int h = Math.Abs(ey - sy);
            holeRect = new WinRect(x, y, w, h);
            holeIsBlur = true;
        }
        else if (isBlur && _hasBlurSelection)
        {
            holeRect = _blurSelection;
            holeIsBlur = true;
        }

        if (holeRect == null)
        {
            AddTintRect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight);
            DrawSelectionBorder(_hasSelection, _currentSelection, dashed: true);
            DrawSelectionBorder(_hasBlurSelection, _blurSelection, dashed: false);
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

        // Also draw the other selection border if it exists and isn't the hole.
        if (!holeIsBlur && _hasBlurSelection)
            DrawSelectionBorder(true, _blurSelection, dashed: false);
        else if (holeIsBlur && _hasSelection)
            DrawSelectionBorder(true, _currentSelection, dashed: true);
    }

    private void DrawSelectionBorder(bool hasSelection, WinRect rect, bool dashed)
    {
        if (!hasSelection) return;

        var border = new ShapeRectangle
        {
            Width = rect.Width,
            Height = rect.Height,
            Stroke = new SolidColorBrush(WinColor.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 1,
            StrokeDashArray = dashed ? new DoubleCollection { 4, 4 } : null
        };
        Canvas.SetLeft(border, rect.X);
        Canvas.SetTop(border, rect.Y);
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

        int x = Math.Min(_drawStart.X, (int)endX);
        int y = Math.Min(_drawStart.Y, (int)endY);
        int w = Math.Abs((int)endX - _drawStart.X);
        int h = Math.Abs((int)endY - _drawStart.Y);

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
            DrawingCanvas.Children.Remove(_previewBox);
            _previewBox = null;
        }
        if (_previewArrow != null)
        {
            DrawingCanvas.Children.Remove(_previewArrow);
            _previewArrow = null;
        }
    }

    private void EnsureBlurPreview()
    {
        if (_previewBlur != null)
            return;

        _previewBlur = new ShapeRectangle
        {
            Stroke = new SolidColorBrush(WinColor.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(WinColor.FromArgb(60, 255, 255, 255))
        };
        DrawingCanvas.Children.Add(_previewBlur);
    }

    private void UpdateBlurPreview(double endX, double endY)
    {
        if (_previewBlur == null)
            return;

        int x = Math.Min(_blurSelectionStart.X, (int)endX);
        int y = Math.Min(_blurSelectionStart.Y, (int)endY);
        int w = Math.Abs((int)endX - _blurSelectionStart.X);
        int h = Math.Abs((int)endY - _blurSelectionStart.Y);

        _previewBlur.Width = w;
        _previewBlur.Height = h;
        Canvas.SetLeft(_previewBlur, x);
        Canvas.SetTop(_previewBlur, y);
    }

    private void RemoveBlurPreview()
    {
        if (_previewBlur != null)
        {
            DrawingCanvas.Children.Remove(_previewBlur);
            _previewBlur = null;
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

    private void ApplyBlur(WinRect selection)
    {
        double scaleX = _screenshotBitmap.Width / RootGrid.ActualWidth;
        double scaleY = _screenshotBitmap.Height / RootGrid.ActualHeight;

        int bmpX = Math.Max(0, (int)(selection.X * scaleX));
        int bmpY = Math.Max(0, (int)(selection.Y * scaleY));
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
        if (!_hasSelection)
            return;

        CopySelectionToClipboard();
        Close();
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
                    if (_hasSelection)
                    {
                        CopySelectionToClipboard();
                        Close();
                    }
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
        using var bitmapToCopy = CropBitmap(_currentSelection);

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        if (!NativeMethods.OpenClipboard(hWnd))
            return;

        try
        {
            NativeMethods.EmptyClipboard();

            using var dibStream = new MemoryStream();
            var dibData = ConvertToDib(bitmapToCopy);
            dibStream.Write(dibData, 0, dibData.Length);
            dibStream.Position = 0;

            var hGlobal = Marshal.AllocHGlobal(dibData.Length);
            Marshal.Copy(dibData, 0, hGlobal, dibData.Length);

            NativeMethods.SetClipboardData(8 /* CF_DIB */, hGlobal);
            // hGlobal is now owned by the clipboard; do not free it.
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static byte[] ConvertToDib(Bitmap bitmap)
    {
        using var bmp = new Bitmap(bitmap);
        var bmpData = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            int colorTableSize = 0;
            int headerSize = 40; // BITMAPINFOHEADER
            int imageSize = Math.Abs(bmpData.Stride) * bmp.Height;
            int totalSize = headerSize + imageSize;
            var dib = new byte[totalSize];

            // BITMAPINFOHEADER
            BitConverter.GetBytes(headerSize).CopyTo(dib, 0);
            BitConverter.GetBytes(bmp.Width).CopyTo(dib, 4);
            BitConverter.GetBytes(bmp.Height).CopyTo(dib, 8); // positive = bottom-up
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);
            BitConverter.GetBytes((short)32).CopyTo(dib, 14);
            BitConverter.GetBytes(0).CopyTo(dib, 16); // BI_RGB
            BitConverter.GetBytes(imageSize).CopyTo(dib, 20);
            BitConverter.GetBytes(0).CopyTo(dib, 24); // XPelsPerMeter
            BitConverter.GetBytes(0).CopyTo(dib, 28); // YPelsPerMeter
            BitConverter.GetBytes(0).CopyTo(dib, 32); // ColorsUsed
            BitConverter.GetBytes(0).CopyTo(dib, 36); // ColorsImportant

            // Pixel data
            Marshal.Copy(bmpData.Scan0, dib, headerSize, imageSize);

            return dib;
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
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