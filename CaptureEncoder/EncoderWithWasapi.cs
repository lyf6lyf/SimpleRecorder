// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CaptureEncoder
{
    public sealed class EncoderWithWasapi : IDisposable
    {
        public EncoderWithWasapi(IDirect3DDevice device, GraphicsCaptureItem item, IList<Interop.AudioFrame> micAudioFrames, IList<Interop.AudioFrame> loopbackAudioFrames)
        {
            _device = device;
            _captureItem = item;
            _micAudioFrames = micAudioFrames;
            _loopbackAudioFrames = loopbackAudioFrames;
        }

        public IAsyncAction EncodeAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate, MediaEncodingProfile videoProfile)
        {
            return EncodeInternalAsync(stream, width, height, bitrateInBps, frameRate, videoProfile).AsAsyncAction();
        }

        private async Task EncodeInternalAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate, MediaEncodingProfile videoProfile)
        {
            if (!_isRecording)
            {
                _isRecording = true;

                _frameGenerator = new CaptureFrameWait(
                    _device,
                    _captureItem,
                    _captureItem.Size);

                using (_frameGenerator)
                {
                    var encodingProfile = new MediaEncodingProfile();
                    encodingProfile.Container.Subtype = "MPEG4";
                    //encodingProfile.Video.Subtype = "H264";
                    //encodingProfile.Video.Width = width;
                    //encodingProfile.Video.Height = height;
                    //encodingProfile.Video.Bitrate = bitrateInBps;
                    encodingProfile.Video.FrameRate.Numerator = frameRate;
                    encodingProfile.Video.FrameRate.Denominator = 1;
                    //encodingProfile.Video.PixelAspectRatio.Numerator = 1;
                    //encodingProfile.Video.PixelAspectRatio.Denominator = 1;
                    encodingProfile.Video = videoProfile.Video;
                    encodingProfile.Audio = AudioEncodingProperties.CreateAac(44100, 2, 16);
                    var transcode = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mediaStreamSource, stream, encodingProfile);

                    await transcode.TranscodeAsync();
                }
            }
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;

            if (!_isRecording)
            {
                DisposeInternal();
            }

            _isRecording = false;
            Debug.WriteLine(_output);
        }

        private void DisposeInternal()
        {
            _frameGenerator.Dispose();
        }

        public IAsyncAction CreateMediaObjects()
        {
            return CreateMediaObjectsInternal().AsAsyncAction();
        }

        private Task CreateMediaObjectsInternal()
        {
            // Create our encoding profile based on the size of the item
            int width = _captureItem.Size.Width;
            int height = _captureItem.Size.Height;

            // Describe our input: uncompressed BGRA8 buffers
            var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
            _videoDescriptor = new VideoStreamDescriptor(videoProperties);

            AudioEncodingProperties audioProps = AudioEncodingProperties.CreatePcm(48000, 2, 16);
            _micAudioDescriptor = new AudioStreamDescriptor(audioProps);

            AudioEncodingProperties audioProps2 = AudioEncodingProperties.CreatePcm(48000, 2, 16);
            _loopbackAudioDescriptor = new AudioStreamDescriptor(audioProps2);

            // Create our MediaStreamSource
            _mediaStreamSource = new MediaStreamSource(_videoDescriptor);
            _mediaStreamSource.AddStreamDescriptor(_loopbackAudioDescriptor);
            _mediaStreamSource.AddStreamDescriptor(_micAudioDescriptor);

            var selected = _micAudioDescriptor.IsSelected;
            var selected2 = _loopbackAudioDescriptor.IsSelected;

            _mediaStreamSource.BufferTime = TimeSpan.FromSeconds(0);
            _mediaStreamSource.Starting += OnMediaStreamSourceStarting;
            _mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;

            // Create our transcoder
            _transcoder = new MediaTranscoder();
            _transcoder.HardwareAccelerationEnabled = true;

            return Task.CompletedTask;
        }

        private async void OnMediaStreamSourceSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_isRecording && !_closed)
            {
                try
                {
                    if (args.Request.StreamDescriptor is VideoStreamDescriptor)
                    {
                        using (var frame = _frameGenerator.WaitForNewFrame())
                        {
                            if (frame == null)
                            {
                                args.Request.Sample = null;
                                DisposeInternal();
                                return;
                            }

                            var timeStamp = frame.SystemRelativeTime;

                            var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timeStamp);
                            args.Request.Sample = sample;
                            _currentVideoFrameTimestamp = timeStamp;
                            OutputDebugString("video frame " + timeStamp);
                        }
                    }
                    else if (args.Request.StreamDescriptor == _micAudioDescriptor)
                    {
                        MediaStreamSourceSampleRequestDeferral deferal = args.Request.GetDeferral();

                        loop:
                        var count = 0;
                        while (_micAudioFrames.Count == 0)
                        {
                            OutputDebugString($"Wait audio {count}");
                            if (count++ > 1)
                            {
                                // No new samples in last 20ms, fill 0s bytes
                                var gap = _currentVideoFrameTimestamp - _lastMicAudioFrameEndTimestamp;
                                if (gap < TimeSpan.FromMilliseconds(20)) gap = TimeSpan.FromMilliseconds(20);

                                var zeroBytes = new byte[(int)((48000 * 16 * 2 / 8) * (gap / TimeSpan.FromSeconds(1)))];
                                var zeroBuffer = zeroBytes.AsBuffer();
                                var zeroSample = MediaStreamSample.CreateFromBuffer(zeroBuffer, _lastMicAudioFrameEndTimestamp);
                                zeroSample.Duration = gap;
                                zeroSample.KeyFrame = true;
                                args.Request.Sample = zeroSample;
                                OutputDebugString("empty audio frame " + _lastMicAudioFrameEndTimestamp);
                                _lastMicAudioFrameEndTimestamp = _lastMicAudioFrameEndTimestamp.Add(gap);
                                deferal.Complete();
                                return;
                                throw new Exception("audio size is not enough");
                            }
                            await Task.Delay(10);

                        }

                        var frame = _micAudioFrames[0];
                        
                        if ((long)frame.timestamp < _videoFrameStartTimestamp.Ticks)
                        {
                            _micAudioFrames.RemoveAt(0);
                            goto loop;
                        }

                        var frameCount = _micAudioFrames.Count;
                        var frames = new Interop.AudioFrame[frameCount];
                        for (var i = 0; i < frameCount; i++)
                        {
                            frames[i] = _micAudioFrames[0];
                            _micAudioFrames.RemoveAt(0);
                        }

                        var frameBytes = frames.SelectMany(x => x.data).ToArray();

                        // create the MediaStreamSample and assign to the request object. 
                        // You could also create the MediaStreamSample using createFromBuffer(...)
                        IBuffer buffer = frameBytes.AsBuffer();
                        MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(buffer, TimeSpan.FromTicks((long)frames[0].timestamp));
                        

                        sample.Duration = TimeSpan.FromSeconds(1) * ((double)frameBytes.Length * 8 / (48000 * 16 * 2));
                        sample.KeyFrame = true;

                        _lastMicAudioFrameEndTimestamp = sample.Timestamp + sample.Duration;
                        // Debug.WriteLine($"Now {TimeSpan.FromTicks(Stopwatch.GetTimestamp())}, timestamp {sample.Timestamp}, duration {sample.Duration}, next expected timestamp {sample.Timestamp + sample.Duration} ");
                        OutputDebugString("mic audio frame " + sample.Timestamp);
                        args.Request.Sample = sample;
                        deferal.Complete();
                    }
                    else if (args.Request.StreamDescriptor == _loopbackAudioDescriptor)
                    {
                        MediaStreamSourceSampleRequestDeferral deferal = args.Request.GetDeferral();

                        loop:
                        var count = 0;
                        while (_loopbackAudioFrames.Count == 0)
                        {
                            OutputDebugString($"Wait audio {count}");
                            if (count++ > 1)
                            {
                                // No new samples in last 20ms, fill 0s bytes
                                var gap = _currentVideoFrameTimestamp - _lastLoopbackAudioFrameEndTimestamp;
                                if (gap < TimeSpan.FromMilliseconds(20)) gap = TimeSpan.FromMilliseconds(20);

                                var zeroBytes = new byte[(int)((48000 * 16 * 2 / 8) * (gap / TimeSpan.FromSeconds(1)))];
                                var zeroBuffer = zeroBytes.AsBuffer();
                                var zeroSample = MediaStreamSample.CreateFromBuffer(zeroBuffer, _lastLoopbackAudioFrameEndTimestamp);
                                zeroSample.Duration = gap;
                                zeroSample.KeyFrame = true;
                                args.Request.Sample = zeroSample;
                                OutputDebugString("empty audio frame " + _lastLoopbackAudioFrameEndTimestamp);
                                _lastLoopbackAudioFrameEndTimestamp = _lastLoopbackAudioFrameEndTimestamp.Add(gap);
                                deferal.Complete();
                                return;
                                throw new Exception("audio size is not enough");
                            }

                            await Task.Delay(10);

                        }

                        var frame = _loopbackAudioFrames[0];

                        if ((long)frame.timestamp < _videoFrameStartTimestamp.Ticks)
                        {
                            _loopbackAudioFrames.RemoveAt(0);
                            goto loop;
                        }

                        var frameCount = _loopbackAudioFrames.Count;
                        var frames = new Interop.AudioFrame[frameCount];
                        for (var i = 0; i < frameCount; i++)
                        {
                            frames[i] = _loopbackAudioFrames[0];
                            _loopbackAudioFrames.RemoveAt(0);
                        }

                        var frameBytes = frames.SelectMany(x => x.data).ToArray();

                        // create the MediaStreamSample and assign to the request object. 
                        // You could also create the MediaStreamSample using createFromBuffer(...)
                        IBuffer buffer = frameBytes.AsBuffer();
                        MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(buffer, TimeSpan.FromTicks((long)frames[0].timestamp));


                        sample.Duration = TimeSpan.FromSeconds(1) * ((double)frameBytes.Length * 8 / (48000 * 16 * 2));
                        sample.KeyFrame = true;

                        _lastLoopbackAudioFrameEndTimestamp = sample.Timestamp + sample.Duration;
                        // Debug.WriteLine($"Now {TimeSpan.FromTicks(Stopwatch.GetTimestamp())}, timestamp {sample.Timestamp}, duration {sample.Duration}, next expected timestamp {sample.Timestamp + sample.Duration} ");
                        OutputDebugString("loopback audio frame " + sample.Timestamp);
                        args.Request.Sample = sample;
                        deferal.Complete();
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                    Debug.WriteLine(e.StackTrace);
                    Debug.WriteLine(e);
                    args.Request.Sample = null;
                    DisposeInternal();
                }
            }
            else
            {
                args.Request.Sample = null;
                DisposeInternal();
            }
        }

        private string _output = "";
        private void OutputDebugString(string message)
        {
            _output += $"{Stopwatch.GetTimestamp()} {message}\n";
            //Debug.WriteLine(message);
        }   

        private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            using (var frame = _frameGenerator.WaitForNewFrame())
            {
                _lastMicAudioFrameEndTimestamp = frame.SystemRelativeTime;
                _lastLoopbackAudioFrameEndTimestamp = frame.SystemRelativeTime;
                _videoFrameStartTimestamp = frame.SystemRelativeTime;
                _currentVideoFrameTimestamp = frame.SystemRelativeTime;
                args.Request.SetActualStartPosition(frame.SystemRelativeTime);

                Debug.WriteLine("starting " + frame.SystemRelativeTime);
            }
        }

        private IDirect3DDevice _device;

        private GraphicsCaptureItem _captureItem;
        private CaptureFrameWait _frameGenerator;

        private VideoStreamDescriptor _videoDescriptor;
        private MediaStreamSource _mediaStreamSource;
        private MediaTranscoder _transcoder;
        private bool _isRecording;
        private bool _closed = false;

        private AudioStreamDescriptor _micAudioDescriptor;
        private AudioStreamDescriptor _loopbackAudioDescriptor;
        private readonly IList<Interop.AudioFrame> _micAudioFrames;
        private readonly IList<Interop.AudioFrame> _loopbackAudioFrames;
        private TimeSpan _lastMicAudioFrameEndTimestamp;
        private TimeSpan _lastLoopbackAudioFrameEndTimestamp;
        private TimeSpan _videoFrameStartTimestamp;
        private TimeSpan _currentVideoFrameTimestamp;
    }
}
