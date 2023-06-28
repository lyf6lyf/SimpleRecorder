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
        public EncoderWithWasapi(IDirect3DDevice device, GraphicsCaptureItem item, IList<Interop.AudioFrame> audioFrames)
        {
            _device = device;
            _captureItem = item;
            _audioFrames = audioFrames;
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
            _audioDescriptor = new AudioStreamDescriptor(audioProps);


            // Create our MediaStreamSource
            _mediaStreamSource = new MediaStreamSource(_videoDescriptor, _audioDescriptor);
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
                            //Debug.WriteLine("video frame " + timeStamp);
                        }
                    }
                    else if (args.Request.StreamDescriptor is AudioStreamDescriptor)
                    {
                        MediaStreamSourceSampleRequestDeferral deferal = args.Request.GetDeferral();

                        loop:
                        var count = 0;
                        while (_audioFrames.Count == 0)
                        {
                            Debug.WriteLine("Wait audio");
                            await Task.Delay(10);
                            if (count++ > 5)
                            {
                                return;
                                throw new Exception("audio size is not enough");
                            }
                        }

                        var frame = _audioFrames[0];
                        
                        if ((long)frame.timestamp < _videoStartedTimestamp.Ticks)
                        {
                            _audioFrames.RemoveAt(0);
                            goto loop;
                        }

                        var frameCount = _audioFrames.Count;
                        var frames = new Interop.AudioFrame[frameCount];
                        for (var i = 0; i < frameCount; i++)
                        {
                            frames[i] = _audioFrames[0];
                            _audioFrames.RemoveAt(0);
                        }

                        var frameBytes = frames.SelectMany(x => x.data).ToArray();

                        // create the MediaStreamSample and assign to the request object. 
                        // You could also create the MediaStreamSample using createFromBuffer(...)
                        IBuffer buffer = frameBytes.AsBuffer();
                        MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(buffer, TimeSpan.FromTicks((long)frames[0].timestamp));
                        
                        //Debug.WriteLine("audio frame " + sample.Timestamp);

                        sample.Duration = TimeSpan.FromSeconds(1) * ((double)frameBytes.Length * 8 / (48000 * 16 * 2));
                        sample.KeyFrame = true;

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

        private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            using (var frame = _frameGenerator.WaitForNewFrame())
            {
                _videoStartedTimestamp = frame.SystemRelativeTime;
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

        private AudioStreamDescriptor _audioDescriptor;
        private readonly IList<Interop.AudioFrame> _audioFrames;
        private TimeSpan _videoStartedTimestamp;
    }
}
