using System;
using System.Runtime.InteropServices;
using Evania.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;

namespace Evania.Windows;

/// <summary>
/// The small floating bar shown while a recording runs: elapsed time, the mic / PC sound / camera
/// toggles, pause and stop. Encoding is <see cref="RecordingController"/>'s business; this window
/// only drives one session and reports the finished file through <see cref="SavedFilePath"/> once
/// the recorder has flushed it.
///
/// The camera is shown here rather than composited by the recorder: the bar is part of the
/// desktop, so a live view inside it is recorded along with everything else, and the frame stays
/// exactly the size of the monitor.
/// </summary>
public sealed partial class RecordToolbarWindow : Window
{
    // Only the pre-measure seed: ResizeBar sizes the window to the bar's own measured size as
    // soon as it can be measured.
    private const int SeedWidthDip = 340;
    private const int SeedHeightDip = 46;
    private const int TopOffsetDip = 12;
    private const string TimeFormat = @"hh\:mm\:ss";

    private readonly RecordingController _controller;
    private readonly string _outputPath;
    private readonly CameraPreview _camera = new();
    private DispatcherTimer? _ticker;
    private bool _isDragging;
    private int _grabX;
    private int _grabY;
    private double _scale = 1.0;

    /// <summary>Path the recorder wrote, or null when the session failed or never started.</summary>
    public string? SavedFilePath { get; private set; }

    // Two-digit hours keep the string the same width for the whole session, so the controls to the
    // right of the clock never shuffle.
    private string ElapsedDisplay => _controller.Elapsed.ToString(TimeFormat);

    public RecordToolbarWindow(RecordingController controller, string outputPath)
    {
        _controller = controller;
        _outputPath = outputPath;
        InitializeComponent();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Resize(new SizeInt32(SeedWidthDip, SeedHeightDip));

        DisableIfMissing(MicButton, _controller.CanRecordMicrophone, "Record microphone");
        DisableIfMissing(SystemAudioButton, _controller.CanRecordSystemAudio, "Record PC sound");

        // The bitmap is replaced whenever the camera changes resolution, which happens the very
        // first frame arrives — that is when the Image finally gets a source.
        _camera.BitmapChanged += BindCameraFrame;

        Closed += (s, e) =>
        {
            _ticker?.Stop();
            _camera.Dispose();
        };
    }

    private static void DisableIfMissing(ToggleButton button, bool available, string tooltip)
    {
        if (available)
        {
            return;
        }

        button.IsEnabled = false;
        ToolTipService.SetToolTip(button, $"{tooltip} (device not available)");
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        ResizeBar();
        CenterTop();

        if (!_controller.Start(_outputPath))
        {
            Close();
            return;
        }

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _ticker.Tick += (_, _) => ElapsedText.Text = ElapsedDisplay;
        _ticker.Start();

        // The session opens held, so the bar has to show resume rather than pause from the start.
        ApplyPauseState();
    }

    private void CenterTop()
    {
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        int x = area.WorkArea.X + (area.WorkArea.Width - AppWindow.Size.Width) / 2;
        int y = area.WorkArea.Y + (int)(TopOffsetDip * _scale);
        AppWindow.Move(new PointInt32(x, y));
    }

    // The window becomes exactly the size of the bar's content, so the grip sits against the left
    // edge and there is no dead space to either side. Measuring freely rather than reading
    // ActualWidth keeps this right for the state we just switched to, whatever the last layout
    // pass was sized for.
    private void ResizeBar()
    {
        Thickness edge = BarPanel.Margin;
        BarPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        int width = (int)Math.Ceiling((BarPanel.DesiredSize.Width + edge.Left + edge.Right) * _scale);
        int height = (int)Math.Ceiling((BarPanel.DesiredSize.Height + edge.Top + edge.Bottom) * _scale);

        SizeInt32 previous = AppWindow.Size;
        PointInt32 location = AppWindow.Position;
        AppWindow.Resize(new SizeInt32(width, height));

        // A resize keeps the top-left corner, so the bar grows downward. If revealing the camera
        // ever does change the width, shift half the difference so it still reads as stationary.
        int shift = (previous.Width - width) / 2;
        if (shift != 0)
        {
            AppWindow.Move(new PointInt32(location.X + shift, location.Y));
        }
    }

    #region Toolbar Dragging

    // Screen coordinates, because moving the window under a held pointer leaves a
    // window-relative position unchanged and the bar would fight itself back to the start.
    private void GripIcon_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = true;
        GetCursorPos(out POINT cursor);
        _grabX = cursor.X - AppWindow.Position.X;
        _grabY = cursor.Y - AppWindow.Position.Y;
        GripIcon.CapturePointer(e.Pointer);
    }

    private void GripIcon_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        GetCursorPos(out POINT cursor);
        AppWindow.Move(new PointInt32(cursor.X - _grabX, cursor.Y - _grabY));
    }

    private void GripIcon_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        GripIcon.ReleasePointerCapture(e.Pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    #endregion

    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        KeepCheckedIf(MicButton, _controller.SetMicrophone(MicButton.IsChecked == true));
    }

    private void SystemAudioButton_Click(object sender, RoutedEventArgs e)
    {
        KeepCheckedIf(SystemAudioButton, _controller.SetSystemAudio(SystemAudioButton.IsChecked == true));
    }

    // A refused change leaves the button off, so the bar never claims a source is being
    // recorded when it is not.
    private static void KeepCheckedIf(ToggleButton button, bool applied)
    {
        if (!applied)
        {
            button.IsChecked = false;
        }
    }

    private async void CameraButton_Click(object sender, RoutedEventArgs e)
    {
        if (CameraButton.IsChecked != true)
        {
            await _camera.StopAsync();
            CameraBox.Visibility = Visibility.Collapsed;
            ResizeBar();
            return;
        }

        string? failure = await _camera.StartAsync();
        if (failure is not null)
        {
            CameraButton.IsChecked = false;
            ToolTipService.SetToolTip(CameraButton, $"Camera unavailable: {failure}");
            return;
        }

        BindCameraFrame();
        CameraBox.Visibility = Visibility.Visible;
        ResizeBar();
    }

    private void BindCameraFrame() => CameraImage.Source = _camera.Bitmap;

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.SetPaused(!_controller.IsPaused);
        ApplyPauseState();
    }

    private void ApplyPauseState()
    {
        bool paused = _controller.IsPaused;
        PauseGlyph.Glyph = paused ? "\uE768" : "\uE769";
        ToolTipService.SetToolTip(PauseButton, paused ? "Resume recording" : "Pause recording");
        RecordDot.Opacity = paused ? 0.3 : 1.0;
        ElapsedText.Text = ElapsedDisplay;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        CameraButton.IsEnabled = false;
        _ticker?.Stop();

        // Hide the camera before the recorder stops, so the file does not end on a frozen face.
        CameraBox.Visibility = Visibility.Collapsed;
        ResizeBar();
        await _camera.StopAsync();

        SavedFilePath = await _controller.StopAsync();
        Close();
    }
}
