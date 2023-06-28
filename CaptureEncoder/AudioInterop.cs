// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO;
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Audio;
using Windows.Media;
using Windows.Storage.Streams;
using Interop;
using System.Linq;
using System.Collections.Generic;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace CaptureEncoder
{
    public sealed class AudioClient : IDisposable
    {
        private AudioCapture _wasapiLoopbackCapture;
        private AudioGraph _audioGraph;
        private AudioFrameInputNode _loopbackInputNode;
        private AudioFileInputNode _audioFileInputNode;
        private AudioDeviceInputNode _deviceInputNode;
        private AudioFrameOutputNode _frameOutputNode;
        private AudioSubmixNode _submixNode;
        private object _lockObject = new object();
        private Stopwatch _stopwatch = new Stopwatch();
        private int _frameCount = 0;
        private bool disposedValue;
        private bool _isStarted;
        private TimeSpan _startTimestamp;

        public IAsyncAction InitializeAsync()
        {
            return InitializeInternal().AsAsyncAction();
        }

        public void Start()
        {
            // 开始录制.
            ShowMessage("开始录制");
            _startTimestamp = TimeSpan.Zero;
            _audioGraph.Start();
            _audioFileInputNode.Start();
            _loopbackInputNode?.Start();
            _frameOutputNode.Start();
            _stopwatch.Start();
            _wasapiLoopbackCapture?.StartCapture();
            _isStarted = true;
        }

        public void Stop()
        {
            // 结束录制.
            ShowMessage("录制结束");
            var duration = _stopwatch.Elapsed.TotalSeconds;
            ShowMessage($"总计音频帧数：{_frameCount}\n用时：{duration:0.0}s\n频率：{_frameCount / duration}");
            _audioFileInputNode?.Stop();
            _loopbackInputNode?.Stop();
            _frameOutputNode?.Stop();
            _audioGraph?.Stop();
            _wasapiLoopbackCapture.StopCapture();
            _isStarted = false;
        }

        public void SetStartTime(TimeSpan stamp)
        {
            _startTimestamp = stamp;
        }

        public AudioEncodingProperties GetEncodingProperties()
            => _audioGraph.EncodingProperties;

        public Windows.Media.AudioFrame GetAudioFrame()
        {
            try
            {
                var frame = _frameOutputNode.GetFrame();
                var ts = Stopwatch.GetTimestamp();
                ts = ts * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
                frame.SystemRelativeTime = new TimeSpan(ts);
                return frame;
            }
            catch (Exception)
            {
                return default;
            }
        }

        public IBuffer ConvertFrameToBuffer(Windows.Media.AudioFrame frame)
        {
            try
            {
                return ProcessFrameOutput(frame);
            }
            catch (Exception)
            {
                return default;
            }
        }

        public void MuteDeviceInput()
        {
            if (!_isStarted)
            {
                return;
            }


            _deviceInputNode.OutgoingGain = 0;
        }

        public void UnmuteDeviceInput()
        {
            if (!_isStarted)
            {
                return;
            }

            _deviceInputNode.OutgoingGain = 1;
        }

        private async Task InitializeInternal()
        {
            if (_wasapiLoopbackCapture == null)
            {
                _wasapiLoopbackCapture = new AudioCapture(false);
            }

            ShowMessage("NAudio initialized");
            await InitializeAudioGraphAsync();
            ShowMessage("AudioGraph initialized");
        }

        private async Task InitializeAudioGraphAsync()
        {
            AudioGraphSettings settings = new AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.Media);
            settings.QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired;
            CreateAudioGraphResult result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success)
            {
                ShowMessage("AudioGraph creation error: " + result.Status.ToString());
            }

            _audioGraph = result.Graph;
            CreateFrameOutputNode();
            await CreateDeviceInputNodeAsync();
            await CreateFileInputNodeAsync();
            CreateFrameInputNode();

            if (_frameOutputNode == null || _deviceInputNode == null)
            {
                return;
            }


            var subNode = _audioGraph.CreateSubmixNode();
            _deviceInputNode.AddOutgoingConnection(subNode);
            _loopbackInputNode.AddOutgoingConnection(subNode);
            _audioFileInputNode.AddOutgoingConnection(subNode);
            _submixNode = subNode;
            subNode.AddOutgoingConnection(_frameOutputNode);
        }

        private void ShowMessage(string msg)
        {
            Debug.WriteLine(msg);
        }

        private void CreateFrameInputNode()
        {
            _loopbackInputNode = _audioGraph.CreateFrameInputNode();
            _loopbackInputNode.Stop();
            _loopbackInputNode.QuantumStarted += OnLoopbackInputNodeQuantumStarted;
        }

        private async Task CreateFileInputNodeAsync()
        {
            var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/emptyAudio.mp3"));
            var result = await _audioGraph.CreateFileInputNodeAsync(file);
            if (result.Status != AudioFileNodeCreationStatus.Success)
            {
                ShowMessage(result.Status.ToString());
            }

            _audioFileInputNode = result.FileInputNode;
            _audioFileInputNode.LoopCount = null;
        }

        private async Task CreateDeviceInputNodeAsync()
        {
            var result = await _audioGraph.CreateDeviceInputNodeAsync(Windows.Media.Capture.MediaCategory.Other).AsTask();
            if (result.Status != AudioDeviceNodeCreationStatus.Success)
            {
                // Cannot create device output node
                ShowMessage(result.Status.ToString());
                return;
            }

            _deviceInputNode = result.DeviceInputNode;
        }

        private void CreateFrameOutputNode()
        {
            _frameOutputNode = _audioGraph.CreateFrameOutputNode();
        }

        unsafe private Windows.Media.AudioFrame GenerateAudioData(uint samples)
        {
            if (_startTimestamp == TimeSpan.Zero)
            {
                _wasapiLoopbackCapture.AudioFrames.Clear();
                return default;
            }

            uint bufferSize = _audioGraph.EncodingProperties.SampleRate;
            // Buffer size is (number of samples) * (size of each sample)
            // We choose to generate single channel (mono) audio. For multi-channel, multiply by number of channels
            var frame = new Windows.Media.AudioFrame(bufferSize);
            var tempFrames = _wasapiLoopbackCapture.AudioFrames.Where(p => Convert.ToInt64(p.timestamp) >= _startTimestamp.Ticks).ToList();
            if (tempFrames.Count == 0)
            {
                return default;
            }

            frame.SystemRelativeTime = TimeSpan.FromTicks((long)tempFrames.FirstOrDefault().timestamp);
            using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                byte* dataInBytes;
                uint capacityInBytes;

                // Get the buffer from the AudioFrame
                ((IMemoryBufferByteAccess)reference).GetBuffer(out dataInBytes, out capacityInBytes);

                var bytes = new List<byte>();
                lock (_lockObject)
                {
                    var count = tempFrames.Count;
                    var totalSize = tempFrames.Select(p => p.data.Length).Sum();
                    if (totalSize < bufferSize)
                    {
                        return default;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        var currentFrame = tempFrames[i];

                        if (bytes.Count >= bufferSize)
                        {
                            count = i;
                            break;
                        }

                        bytes.AddRange(tempFrames[i].data);
                    }

                    for (int i = 0; i < bufferSize; i++)
                    {
                        dataInBytes[i] = bytes[i];
                    }

                    for (int i = count - 1; i >= 0; i--)
                    {
                        _wasapiLoopbackCapture.AudioFrames.RemoveAt(i);
                    }
                }
            }


            return frame;
        }

        private void OnLoopbackInputNodeQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            if (args.RequiredSamples == 0)
            {
                return;
            }

            var frame = GenerateAudioData((uint)args.RequiredSamples);
            if (frame == null)
            {
                return;
            }

            sender.AddFrame(frame);
            _frameCount++;
        }

        unsafe private IBuffer ProcessFrameOutput(Windows.Media.AudioFrame frame)
        {
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                byte* dataInBytes;
                uint capacityInBytes;

                // Get the buffer from the AudioFrame
                ((IMemoryBufferByteAccess)reference).GetBuffer(out dataInBytes, out capacityInBytes);
                var bytes = new byte[capacityInBytes];
                Marshal.Copy((IntPtr)dataInBytes, bytes, 0, (int)capacityInBytes);
                return bytes.AsBuffer();
            }
        }

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                Stop();

                if (disposing)
                {
                    try
                    {
                        _stopwatch?.Stop();
                        _audioGraph?.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }

                _audioGraph = null;
                _deviceInputNode = null;
                _loopbackInputNode = null;
                _frameOutputNode = null;
                _audioFileInputNode = null;
                _wasapiLoopbackCapture = null;
                _stopwatch = null;
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
