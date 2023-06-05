// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CaptureEncoder
{
    public sealed class EncoderWithAudioFile : IDisposable
    {
        public EncoderWithAudioFile(IDirect3DDevice device, GraphicsCaptureItem item, StorageFile mp3)
        {
            _device = device;
            _captureItem = item;
            _isRecording = false;
            _inputMP3File = mp3;
        }

        public IAsyncAction EncodeAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate)
        {
            return EncodeInternalAsync(stream, width, height, bitrateInBps, frameRate).AsAsyncAction();
        }

        private async Task EncodeInternalAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate)
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
                    encodingProfile.Video.Subtype = "H264";
                    encodingProfile.Video.Width = width;
                    encodingProfile.Video.Height = height;
                    encodingProfile.Video.Bitrate = bitrateInBps;
                    encodingProfile.Video.FrameRate.Numerator = frameRate;
                    encodingProfile.Video.FrameRate.Denominator = 1;
                    encodingProfile.Video.PixelAspectRatio.Numerator = 1;
                    encodingProfile.Video.PixelAspectRatio.Denominator = 1;
                    encodingProfile.Audio.Subtype = "MP3";
                    encodingProfile.Audio = _audioDescriptor.EncodingProperties;
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

        private async Task CreateMediaObjectsInternal()
        {
            // Create our encoding profile based on the size of the item
            int width = _captureItem.Size.Width;
            int height = _captureItem.Size.Height;

            // Describe our input: uncompressed BGRA8 buffers
            var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
            _videoDescriptor = new VideoStreamDescriptor(videoProperties);

            List<string> encodingPropertiesToRetrieve = new List<string>();

            encodingPropertiesToRetrieve.Add("System.Audio.SampleRate");
            encodingPropertiesToRetrieve.Add("System.Audio.ChannelCount");
            encodingPropertiesToRetrieve.Add("System.Audio.EncodingBitrate");

            IDictionary<string, object> encodingProperties = await _inputMP3File.Properties.RetrievePropertiesAsync(encodingPropertiesToRetrieve);

            var sampleRate = (uint)encodingProperties["System.Audio.SampleRate"];
            var channelCount = (uint)encodingProperties["System.Audio.ChannelCount"];
            var bitRate = (uint)encodingProperties["System.Audio.EncodingBitrate"];

            AudioEncodingProperties audioProps = AudioEncodingProperties.CreateMp3(sampleRate, channelCount, bitRate);
            _audioDescriptor = new AudioStreamDescriptor(audioProps);

            audioStream = await _inputMP3File.OpenAsync(FileAccessMode.Read);

            // Create our MediaStreamSource
            _mediaStreamSource = new MediaStreamSource(_videoDescriptor, _audioDescriptor);
            _mediaStreamSource.BufferTime = TimeSpan.FromSeconds(0);
            _mediaStreamSource.Starting += OnMediaStreamSourceStarting;
            _mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;

            // Create our transcoder
            _transcoder = new MediaTranscoder();
            _transcoder.HardwareAccelerationEnabled = true;
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
                            Debug.WriteLine("video frame " + timeStamp);
                        }
                    }
                    else if (args.Request.StreamDescriptor is AudioStreamDescriptor)
                    {
                        uint sampleSize = _audioDescriptor.EncodingProperties.Bitrate / 8 / 10;
                        var sampleDuration = new TimeSpan(0, 0, 0, 0, 100);
                        if (_audioByteOffset + sampleSize <= audioStream.Size)
                        {
                            MediaStreamSourceSampleRequestDeferral deferal = args.Request.GetDeferral();
                            var inputStream = audioStream.GetInputStreamAt(_audioByteOffset);

                            // create the MediaStreamSample and assign to the request object. 
                            // You could also create the MediaStreamSample using createFromBuffer(...)

                            MediaStreamSample sample = await MediaStreamSample.CreateFromStreamAsync(inputStream, sampleSize, _audioTimeOffset);

                            Debug.WriteLine("audio frame " + _audioTimeOffset + " " + sample.Timestamp);

                            sample.Duration = sampleDuration;
                            sample.KeyFrame = true;

                            // increment the time and byte offset

                            _audioByteOffset += sampleSize;
                            _audioTimeOffset = _audioTimeOffset.Add(sampleDuration);
                            args.Request.Sample = sample;
                            deferal.Complete();
                        }
                        else
                        {
                            args.Request.Sample = null;
                            DisposeInternal();
                        }
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
                args.Request.SetActualStartPosition(frame.SystemRelativeTime);
                _audioTimeOffset = frame.SystemRelativeTime;
                _audioByteOffset = 0;

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

        private StorageFile _inputMP3File;
        private TimeSpan _audioTimeOffset;
        private ulong _audioByteOffset;
        private IRandomAccessStream audioStream;
        private AudioStreamDescriptor _audioDescriptor;
    }
}
