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
        private string? _outputPath;
        private bool _audioPending;

        public bool IsRecording { get; private set; }

        public bool IsPaused { get; private set; }

        /// <summary>
        /// True between <see cref="Start"/> and the first resume: the session is prepared but the
        /// recorder has not opened its output file yet, so stopping now produces nothing.
        /// </summary>
        public bool IsArmed { get; private set; }

        /// <summary>
        /// True when the session was abandoned before it ever started capturing. Nothing was
        /// written and no file was created, so such a stop is a cancel rather than a failure.
        /// </summary>
        public bool CancelledEmptySession { get; private set; }

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

            // Toggles made while the session is still armed only set the managed flags, which the
            // recorder copied to its native side when it was created. A dynamic update is rejected
            // until the recording is running, so push what was queued on that first transition.
            _recorder.OnStatusChanged += (s, e) =>
            {
                if (e.Status == RecorderStatus.Recording)
                {
                    FlushAudioState();
                }
            };
        }

        /// <summary>
        /// Prepares a session that writes to <paramref name="outputPath"/> once the toolbar resumes
        /// it. The recorder is not touched here and no file is created: the session stays armed
        /// until its first resume, which is what leaves time to arrange the desktop, and stopping
        /// before then simply gives up. False when a session is already in progress.
        /// </summary>
        public bool Start(string outputPath)
        {
            if (IsRecording || IsArmed)
            {
                return false;
            }

            _failure = null;
            _stopped = null;
            _clock.Reset();
            CancelledEmptySession = false;
            _outputPath = outputPath;
            IsArmed = true;
            IsPaused = true;
            return true;
        }

        /// <summary>
        /// Holds or resumes the session. The first resume is what opens the output file and starts
        /// capturing; False when the recorder refused it (see <see cref="Failure"/>), true once the
        /// requested state is in effect.
        /// </summary>
        public bool SetPaused(bool paused)
        {
            if (paused)
            {
                // An armed session has nothing running to hold.
                if (!IsRecording || IsPaused)
                {
                    return true;
                }

                _recorder.Pause();
                _clock.Stop();
                IsPaused = true;
                return true;
            }

            if (IsRecording)
            {
                if (!IsPaused)
                {
                    return true;
                }

                _recorder.Resume();
                _clock.Start();
                IsPaused = false;

                // Any audio switch made while held only becomes real now.
                FlushAudioState();
                return true;
            }

            if (!OpenOutput())
            {
                return false;
            }

            // The flags set while armed are still queued; the first Recording status flush applies
            // them, which is the earliest point a dynamic update is accepted.
            _clock.Start();
            IsPaused = false;
            return true;
        }

        /// <summary>
        /// Hands the output path to the recorder. Until this runs the sources are only staged, so
        /// the capture flags already set on them are picked up as they are.
        /// </summary>
        private bool OpenOutput()
        {
            if (_outputPath is null)
            {
                _failure ??= "There is nothing to record.";
                return false;
            }

            _stopped = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                _recorder.Record(_outputPath);
            }
            catch (Exception ex)
            {
                _stopped = null;
                _failure = ex.Message;
                return false;
            }

            IsRecording = true;
            IsArmed = false;
            return true;
        }

        /// <summary>False when the recorder refused the change mid-session.</summary>
        public bool SetSystemAudio(bool enabled) => ApplyAudio(_systemAudio, enabled);

        /// <summary>False when the recorder refused the change mid-session.</summary>
        public bool SetMicrophone(bool enabled) => ApplyAudio(_microphone, enabled);

        /// <summary>
        /// Ends the session and returns the file the recorder finished writing, or null when
        /// nothing was captured, it failed (see <see cref="Failure"/>) or never started. A session
        /// still armed when this is called never opened a file, so it is simply dropped.
        /// </summary>
        public async Task<string?> StopAsync()
        {
            if (IsArmed)
            {
                // No recorder call was ever made, so there is no stream to stop and no file to
                // clean up.
                IsArmed = false;
                IsPaused = false;
                _outputPath = null;
                CancelledEmptySession = true;
                return null;
            }

            TaskCompletionSource<string?>? stopped = _stopped;
            if (!IsRecording || stopped is null)
            {
                return null;
            }

            IsRecording = false;
            IsPaused = false;
            _clock.Stop();
            _outputPath = null;

            try
            {
                _recorder.Stop();
            }
            catch (Exception ex)
            {
                _failure ??= ex.Message;
            }

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
                // Still armed: the recorder is not running, so there is nowhere to apply the
                // change yet. Queue it; the status handler pushes it the moment recording starts.
                _audioPending = true;
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

        public void Dispose()
        {
            // Disposed while still armed means the bar was dismissed without ever resuming: nothing
            // was written, so this counts as a cancel too.
            if (IsArmed)
            {
                IsArmed = false;
                IsPaused = false;
                _outputPath = null;
                CancelledEmptySession = true;
            }

            _recorder.Dispose();
        }
    }
}
