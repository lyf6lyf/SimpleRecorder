// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
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
using Windows.Media.MediaProperties;
using Windows.Storage;
using System.Collections.Generic;
using System.IO;

namespace CaptureEncoder
{
    public sealed class AudioClient : IDisposable
    {
        private AudioCapture _wasapiLoopbackCapture;
        private AudioGraph _audioGraph;
        private AudioFrameInputNode _loopbackInputNode;
        private AudioFrameInputNode _emptyInputNode;
        private AudioFileInputNode _audioFileInputNode;
        private AudioDeviceInputNode _deviceInputNode;
        private AudioFrameOutputNode _frameOutputNode;
        private AudioSubmixNode _submixNode;
        private Stopwatch _stopwatch = new Stopwatch();
        private int _frameCount = 0;
        private bool disposedValue;
        private bool _isStarted;
        private TimeSpan _startTimestamp;
        private TimeSpan _emptyOffsetTime;
        private bool _isHandling;
        private AudioEncodingProperties _wasapiAudioEncodingProperties;

        public IAsyncAction InitializeAsync()
        {
            return InitializeInternal().AsAsyncAction();
        }

        public IAsyncAction StartAsync()
        {
            return StartAsyncInternal().AsAsyncAction();
        }

        private async Task StartAsyncInternal()
        {
            // 开始录制.
            ShowMessage("开始录制");
            _startTimestamp = TimeSpan.Zero;
            await _wasapiLoopbackCapture?.StartCaptureAsync();
            _audioGraph.Start();
            _loopbackInputNode?.Start();
            _frameOutputNode.Start();
            _stopwatch.Start();
            _isStarted = true;
        }

        public IAsyncAction StopAsync()
        {
            return StopAsyncInternal().AsAsyncAction();
        }

        private async Task StopAsyncInternal()
        {
            // 结束录制.
            ShowMessage("录制结束");
            var duration = _stopwatch.Elapsed.TotalSeconds;
            ShowMessage($"总计音频帧数：{_frameCount}\n用时：{duration:0.0}s\n频率：{_frameCount / duration}");
            _loopbackInputNode?.Stop();
            _frameOutputNode?.Stop();
            _audioGraph?.Stop();
            if (_wasapiLoopbackCapture != null)
            {
                await _wasapiLoopbackCapture.StopCaptureAsync();
            }
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
                _wasapiLoopbackCapture = new AudioCapture();
                await _wasapiLoopbackCapture.InitializeAsync();
                _wasapiAudioEncodingProperties = _wasapiLoopbackCapture.AudioEncodingProperties;
            }

            ShowMessage("Loopback capture initialized");
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

            _emptyOffsetTime = TimeSpan.Zero;
            _audioGraph = result.Graph;
            await CreateFileInputNodeAsync();
            CreateFrameOutputNode();
            await CreateDeviceInputNodeAsync();
            CreateLoopbackFrameInputNode();
            //CreateEmptyFrameInputNode();

            if (_frameOutputNode == null)
            {
                return;
            }


            var subNode = _audioGraph.CreateSubmixNode();
            _audioFileInputNode.AddOutgoingConnection(subNode);
            _deviceInputNode.AddOutgoingConnection(subNode);
            _loopbackInputNode.AddOutgoingConnection(subNode);
            //_emptyInputNode.AddOutgoingConnection(subNode);
            _submixNode = subNode;
            subNode.AddOutgoingConnection(_frameOutputNode);
        }

        private void ShowMessage(string msg)
        {
            Debug.WriteLine(msg);
        }

        private void CreateLoopbackFrameInputNode()
        {
            _loopbackInputNode = _audioGraph.CreateFrameInputNode(_wasapiAudioEncodingProperties);
            _loopbackInputNode.Stop();
            _loopbackInputNode.QuantumStarted += OnLoopbackInputNodeQuantumStarted;
        }

        private void CreateEmptyFrameInputNode()
        {
            _emptyInputNode = _audioGraph.CreateFrameInputNode();
            _emptyInputNode.QuantumStarted += OnEmptyInputNodeQuantumStarted;
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
            _audioFileInputNode.Start();
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

        unsafe private Windows.Media.AudioFrame GenerateLoopbackAudioData(uint samples)
        {
            // Buffer size is (number of samples) * (size of each sample)
            // We choose to generate single channel (mono) audio. For multi-channel, multiply by number of channels
            uint bufferSize = samples * (_loopbackInputNode.EncodingProperties.BitsPerSample / 8) * _loopbackInputNode.EncodingProperties.ChannelCount;
            
            var audioData = _wasapiLoopbackCapture.GetNextAudioBytes(bufferSize);
            if (audioData is null || audioData.Length == 0)
            {
                return default;
            }

            var frame = new Windows.Media.AudioFrame(bufferSize);

            //frame.SystemRelativeTime = TimeSpan.FromTicks((long)localFrames.FirstOrDefault().timestamp);
            using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                byte* dataInBytes;
                uint capacityInBytes;

                // Get the buffer from the AudioFrame
                ((IMemoryBufferByteAccess)reference).GetBuffer(out dataInBytes, out capacityInBytes);

                for (int i = 0; i < bufferSize; i++)
                {
                    dataInBytes[i] = audioData[i];
                }
            }

            frame.Duration = TimeSpan.FromMilliseconds(10);
            return frame;
        }

        unsafe private Windows.Media.AudioFrame GenerateEmptyAudioData(uint samples)
        {
            uint bufferSize = _audioGraph.EncodingProperties.SampleRate;
            // Buffer size is (number of samples) * (size of each sample)
            // We choose to generate single channel (mono) audio. For multi-channel, multiply by number of channels
            var frame = new Windows.Media.AudioFrame(bufferSize);

            //frame.SystemRelativeTime = TimeSpan.FromTicks((long)localFrames.FirstOrDefault().timestamp);
            using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                byte* dataInBytes;
                uint capacityInBytes;

                // Get the buffer from the AudioFrame
                ((IMemoryBufferByteAccess)reference).GetBuffer(out dataInBytes, out capacityInBytes);

                var bytes = new byte[bufferSize];
            }

            frame.Duration = TimeSpan.FromMilliseconds(1000.0 * samples / _audioGraph.EncodingProperties.SampleRate);
            frame.RelativeTime = _emptyOffsetTime;
            _emptyOffsetTime = (_emptyOffsetTime + frame.Duration).Value;
            return frame;
        }

        unsafe private Windows.Media.AudioFrame GenerateTestEmptyAudioData(uint samples)
        {
            // 缓冲区大小是 (采样数) * (每个采样的字节数) * (通道数)
            uint bufferSize = samples * sizeof(float) * _audioGraph.EncodingProperties.ChannelCount;

            // 创建一个新的 AudioFrame 对象
            var frame = new Windows.Media.AudioFrame(bufferSize);

            // 获取缓冲区的指针
            using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                byte* dataInBytes;
                uint capacityInBytes;
                ((IMemoryBufferByteAccess)reference).GetBuffer(out dataInBytes, out capacityInBytes);

                // 将缓冲区清零
                for (int i = 0; i < capacityInBytes; i++)
                {
                    dataInBytes[i] = 0;
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

            var frame = GenerateLoopbackAudioData((uint)args.RequiredSamples);

            if (frame == null)
            {
                return;
            }
                
            sender.AddFrame(frame);
            _frameCount++;
        }

        private void OnEmptyInputNodeQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            if (args.RequiredSamples == 0)
            {
                return;
            }

            var frame = GenerateEmptyAudioData((uint)args.RequiredSamples);
            if (frame == null)
            {
                return;
            }

            sender.AddFrame(frame);
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
                StopAsync().GetAwaiter().GetResult();

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
