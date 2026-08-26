using System;
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

        GripIcon.PointerMoved += GripIcon_PointerMoved;
        GripIcon.PointerReleased += GripIcon_PointerReleased;
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
        GripIcon.PointerMoved -= GripIcon_PointerMoved;
        GripIcon.PointerReleased -= GripIcon_PointerReleased;
    }

    #endregion

    #region Tool Selection

    private void SelectToolButton_Click(object sender, RoutedEventArgs e)
    {
        _currentTool = EditToolMode.Select;
        UpdateToolButtonStyles();
    }

    private void DrawBoxToolButton_Click(object sender, RoutedEventArgs e)
    {
        _currentTool = EditToolMode.DrawBox;
        UpdateToolButtonStyles();
    }

    private void DrawArrowToolButton_Click(object sender, RoutedEventArgs e)
    {
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
                case EditToolMode.DrawText:
                    DrawRedText(_currentSelection);
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

    private void DrawRedText(WinRect bounds)
    {
        var textBlock = new TextBlock
        {
            Text = "Text",
            FontSize = 10,
            Foreground = new SolidColorBrush(WinColor.FromArgb(255, 255, 0, 0))
        };
        Canvas.SetLeft(textBlock, bounds.X);
        Canvas.SetTop(textBlock, bounds.Y);
        DrawingCanvas.Children.Add(textBlock);
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