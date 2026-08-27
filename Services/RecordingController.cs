using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using ScreenRecorderLib;

namespace Evania.Services
{
    /// <summary>
    /// One recording session, driven by the floating toolbar.
    ///
    /// Everything ScreenRecorderLib related lives here so the rest of the app deals only in
    /// "start / pause / toggle audio / stop". Audio sources are built up front and switched off
    /// (<see cref="AudioSourceBase.IsAudioCaptureEnabled"/>) so the toolbar can enable them
    /// mid-recording through <see cref="DynamicOptionsBuilder"/>.
    ///
    /// The webcam is deliberately not a recorder source: sources are tiled into one canvas, so
    /// a second source widens the frame and paints the camera into the new black margin.
    /// Instead the toolbar shows a live camera view (<see cref="CameraPreview"/>) and is
    /// recorded along with the desktop, so the camera lands wherever the bar sits.
    /// </summary>
    public sealed class RecordingController : IDisposable
    {
        // A stalled muxer would otherwise leave the toolbar hanging with the main window hidden.
        private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(20);

        private readonly Recorder _recorder;
        private readonly LoopbackAudioSource? _systemAudio;
        private readonly CaptureAudioSource? _microphone;
        private readonly Stopwatch _clock = new();

        private TaskCompletionSource<string?>? _stopped;
        private string? _failure;
        private bool _audioPending;

        public bool IsRecording { get; private set; }

        public bool IsPaused { get; private set; }

        /// <summary>Failure text from the last session, or null when it ended cleanly.</summary>
        public string? Failure => _failure;

        public TimeSpan Elapsed => _clock.Elapsed;

        public bool CanRecordSystemAudio => _systemAudio is not null;

        public bool CanRecordMicrophone => _microphone is not null;

        public RecordingController()
        {
            _systemAudio = LoopbackAudioSource.Default;
            _microphone = CaptureAudioSource.Default;

            DisplayRecordingSource display = DisplayRecordingSource.MainMonitor;
            display.IsBorderRequired = false;

            var sources = new List<RecordingSourceBase> { display };

            var audioSources = new List<AudioSourceBase>();
            if (_systemAudio is not null)
            {
                audioSources.Add(_systemAudio);
            }
            if (_microphone is not null)
            {
                audioSources.Add(_microphone);
            }

            // Every source starts muted; the toolbar turns them on.
            foreach (AudioSourceBase source in audioSources)
            {
                source.IsAudioCaptureEnabled = false;
            }

            // Pin the frame to the monitor itself. Anything larger would be recorded as a black
            // border around the desktop.
            OutputOptions output = new()
            {
                OutputFrameSize = Recorder.GetOutputDimensionsForRecordingSources(sources).CombinedOutputSize,
                Stretch = StretchMode.Fill,
            };

            _recorder = Recorder.CreateRecorder(new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = sources },
                OutputOptions = output,
                AudioOptions = new AudioOptions
                {
                    AudioSources = audioSources,
                    IsAudioEnabled = audioSources.Count > 0,
                },
                MouseOptions = new MouseOptions { IsMousePointerEnabled = true },
                VideoEncoderOptions = new VideoEncoderOptions { Framerate = 30 },
            });

            _recorder.OnRecordingComplete += (s, e) => _stopped?.TrySetResult(e.FilePath);
            _recorder.OnRecordingFailed += (s, e) =>
            {
                _failure = e.Error;
                _stopped?.TrySetResult(null);
            };
        }

        /// <summary>
        /// Opens <paramref name="outputPath"/> but holds the session paused, so nothing is
        /// captured until the toolbar resumes it — that leaves time to arrange the desktop. False
        /// when the recorder refused to start; the reason is then in <see cref="Failure"/>.
        /// </summary>
        public bool Start(string outputPath)
        {
            if (IsRecording)
            {
                return false;
            }

            _failure = null;
            _stopped = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _clock.Reset();

            try
            {
                _recorder.Record(outputPath);
                _recorder.Pause();
            }
            catch (Exception ex)
            {
                _stopped = null;
                _failure = ex.Message;
                return false;
            }

            IsRecording = true;
            IsPaused = true;
            return true;
        }

        public void SetPaused(bool paused)
        {
            if (!IsRecording || IsPaused == paused)
            {
                return;
            }

            if (paused)
            {
                _recorder.Pause();
                _clock.Stop();
            }
            else
            {
                _recorder.Resume();
                _clock.Start();

                // Any audio switch made while held only becomes real now.
                FlushAudioState();
            }

            IsPaused = paused;
        }

        /// <summary>False when the recorder refused the change mid-session.</summary>
        public bool SetSystemAudio(bool enabled) => ApplyAudio(_systemAudio, enabled);

        /// <summary>False when the recorder refused the change mid-session.</summary>
        public bool SetMicrophone(bool enabled) => ApplyAudio(_microphone, enabled);

        /// <summary>
        /// Ends the session and returns the file the recorder finished writing, or null when
        /// it failed (see <see cref="Failure"/>) or never started.
        /// </summary>
        public async Task<string?> StopAsync()
        {
            TaskCompletionSource<string?>? stopped = _stopped;
            if (!IsRecording || stopped is null)
            {
                return null;
            }

            IsRecording = false;
            IsPaused = false;
            _clock.Stop();
            _recorder.Stop();

            Task finished = await Task.WhenAny(stopped.Task, Task.Delay(StopGrace));
            return finished == stopped.Task ? stopped.Task.Result : null;
        }

        private bool ApplyAudio(AudioSourceBase? source, bool enabled)
        {
            if (source is null)
            {
                return false;
            }

            if (source.IsAudioCaptureEnabled == enabled)
            {
                return true;
            }

            source.IsAudioCaptureEnabled = enabled;

            if (!IsRecording)
            {
                // No session yet: the flags are read from these same objects when it starts.
                return true;
            }

            if (IsPaused)
            {
                // While held the recorder rebuilds its audio pipeline on resume from the sources
                // it was started with, so a dynamic change here is silently discarded. Queue it.
                _audioPending = true;
                return true;
            }

            return _recorder.GetDynamicOptionsBuilder().SetUpdatedAudioSource(source).Apply();
        }

        private void FlushAudioState()
        {
            if (!_audioPending)
            {
                return;
            }

            _audioPending = false;

            DynamicOptionsBuilder builder = _recorder.GetDynamicOptionsBuilder();
            if (_systemAudio is { } systemAudio)
            {
                builder.SetUpdatedAudioSource(systemAudio);
            }
            if (_microphone is { } microphone)
            {
                builder.SetUpdatedAudioSource(microphone);
            }
            builder.Apply();
        }

        public void Dispose() => _recorder.Dispose();
    }
}
