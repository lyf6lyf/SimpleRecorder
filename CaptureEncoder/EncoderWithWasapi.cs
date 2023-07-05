// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Interop;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace CaptureEncoder
{
    public sealed class EncoderWithWasapi : IDisposable
    {
        public EncoderWithWasapi(IDirect3DDevice device, GraphicsCaptureItem item)
        {
            _device = device;
            _captureItem = item;
        }

        public IAsyncAction EncodeAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate, MediaEncodingProfile videoProfile)
        {
            return EncodeInternalAsync(stream, width, height, bitrateInBps, frameRate, videoProfile).AsAsyncAction();
        }

        public void ChangeMicMute(bool mute)
        {
            if (mute)
            {
                _audioClient.MuteDeviceInput();
            }
            else
            {
                _audioClient.UnmuteDeviceInput();
            }
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
                    encodingProfile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
                    encodingProfile.Video.Subtype = MediaEncodingSubtypes.H264;
                    encodingProfile.Video.Width = width;
                    encodingProfile.Video.Height = height;
                    encodingProfile.Video.Bitrate = bitrateInBps;
                    encodingProfile.Video.FrameRate.Numerator = frameRate;
                    encodingProfile.Video.FrameRate.Denominator = 1;
                    encodingProfile.Video.PixelAspectRatio.Numerator = 1;
                    encodingProfile.Video.PixelAspectRatio.Denominator = 1;
                    encodingProfile.Audio = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High).Audio;
                    //encodingProfile.Audio = MediaEncodingProfile.CreateWma(AudioEncodingQuality.High).Audio;

                    if (_audioClient == null)
                    {
                        _audioClient = new AudioClient();
                        await _audioClient.InitializeAsync();
                    }

                    _audioDescriptor = new AudioStreamDescriptor(_audioClient.GetEncodingProperties());
                    _mediaStreamSource.AddStreamDescriptor(_audioDescriptor);
                    var transcode = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mediaStreamSource, stream, encodingProfile);

                    if (transcode.CanTranscode)
                    {
                        _audioClient?.Start();
                        await transcode.TranscodeAsync();
                    }
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

            DisposeInternal();
            _isRecording = false;            
        }

        private void DisposeInternal()
        {
            _frameGenerator?.Dispose();
            _audioClient?.Dispose();
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

            // Create our MediaStreamSource
            _mediaStreamSource = new MediaStreamSource(_videoDescriptor);
            _mediaStreamSource.BufferTime = TimeSpan.Zero;
            _mediaStreamSource.Starting += OnMediaStreamSourceStarting;
            _mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;

            // Create our transcoder
            _transcoder = new MediaTranscoder();
            _transcoder.HardwareAccelerationEnabled = true;

            return Task.CompletedTask;
        }

        private void OnMediaStreamSourceSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_isRecording && !_closed)
            {
                var def = args.Request.GetDeferral();
                if (args.Request.StreamDescriptor is VideoStreamDescriptor)
                {
                    if (_lastSampleIsVideo.HasValue && _lastSampleIsVideo.Value)
                    {
                        //args.Request.Sample = null;
                        //def.Complete();
                        //DisposeInternal();
                        //return;
                        //Debug.WriteLine("Repeat video");
                    }

                    using (var frame = _frameGenerator.WaitForNewFrame())
                    {
                        if (frame == null)
                        {
                            args.Request.Sample = null;
                            def.Complete();
                            DisposeInternal();
                            return;
                        }

                        var timeStamp = frame.SystemRelativeTime - _timeOffset;

                        var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timeStamp);
                        args.Request.Sample = sample;
                        _lastSampleIsVideo = true;
                        //Debug.WriteLine("video frame " + timeStamp);
                    }
                }
                else if (args.Request.StreamDescriptor is AudioStreamDescriptor)
                {
                    if (_lastSampleIsVideo.HasValue && !_lastSampleIsVideo.Value)
                    {
                        //args.Request.Sample = null;
                        //def.Complete();
                        //DisposeInternal();
                        //return;
                        //Debug.WriteLine("Repeat Audio");
                    }

                    var frame = _audioClient.GetAudioFrame();
                    if (frame == null)
                    {
                        args.Request.Sample = null;
                        def.Complete();
                        return;
                    }

                    var buffer = _audioClient.ConvertFrameToBuffer(frame);
                    if (buffer == null)
                    {
                        args.Request.Sample = null;
                        def.Complete();
                        return;
                    }

                    var timeStamp = frame.RelativeTime.GetValueOrDefault();
                    var sample = MediaStreamSample.CreateFromBuffer(buffer, timeStamp);

                    //sample.Duration = TimeSpan.FromSeconds(1) * ((double)buffer.Length * 8 / (48000 * 16 * 2));
                    sample.KeyFrame = true;
                    args.Request.Sample = sample;
                    _lastSampleIsVideo = false;
                    //Debug.WriteLine("audio frame " + timeStamp + " " + buffer.Length);
                }

                def.Complete();
            }
            else
            {
                args.Request.Sample = null;
                DisposeInternal();
            }
        }

        private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            using (var frame = _frameGenerator.WaitForNewFrame())
            {
                _videoStartedTimestamp = frame.SystemRelativeTime;
                _audioClient.SetStartTime(_videoStartedTimestamp);
                // args.Request.SetActualStartPosition(frame.SystemRelativeTime);
                _timeOffset = frame.SystemRelativeTime;

                Debug.WriteLine("starting " + frame.SystemRelativeTime);
            }

            if (_audioClient != null)
            {
                using (var frame = _audioClient.GetAudioFrame())
                {
                    _timeOffset += frame.RelativeTime.GetValueOrDefault();
                }
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

        private TimeSpan _timeOffset = default;
        private AudioStreamDescriptor _audioDescriptor;
        private AudioClient _audioClient;
        private TimeSpan _videoStartedTimestamp;
        private bool? _lastSampleIsVideo = default;
    }
}
