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
using Windows.Devices.Enumeration;

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
        private Stream _loopingAudioStream;
        private Stopwatch _stopwatch = new Stopwatch();
        private int _frameCount = 0;
        private bool disposedValue;
        private bool _isStarted;
        private long _readPosition = 0;
        private TimeSpan _startTimestamp;
        private TimeSpan _emptyOffsetTime;
        private bool _isHandling;

        public IAsyncAction InitializeAsync()
        {
            return InitializeInternal().AsAsyncAction();
        }

        public void Start()
        {
            // 开始录制.
            ShowMessage("开始录制");
            _startTimestamp = TimeSpan.Zero;
            _wasapiLoopbackCapture?.StartCapture();
            _audioGraph.Start();
            _loopbackInputNode?.Start();
            _frameOutputNode.Start();
            _stopwatch.Start();
            _isStarted = true;
        }

        public void Stop()
        {
            // 结束录制.
            ShowMessage("录制结束");
            var duration = _stopwatch.Elapsed.TotalSeconds;
            ShowMessage($"总计音频帧数：{_frameCount}\n用时：{duration:0.0}s\n频率：{_frameCount / duration}");
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

            _loopingAudioStream = new MemoryStream();
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

            if (_frameOutputNode == null)
            {
                return;
            }


            var subNode = _audioGraph.CreateSubmixNode();
            _audioFileInputNode.AddOutgoingConnection(subNode);
            _deviceInputNode.AddOutgoingConnection(subNode);
            _loopbackInputNode.AddOutgoingConnection(subNode);
            _submixNode = subNode;
            subNode.AddOutgoingConnection(_frameOutputNode);
        }

        private void ShowMessage(string msg)
        {
            Debug.WriteLine(msg);
        }

        private void CreateLoopbackFrameInputNode()
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
            _audioFileInputNode.Start();
        }

        private async Task CreateDeviceInputNodeAsync()
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            var firstDevice = devices.FirstOrDefault();
            var encoding = AudioEncodingProperties.CreatePcm(48000, 2, 32);
            var result = await _audioGraph.CreateDeviceInputNodeAsync(Windows.Media.Capture.MediaCategory.Other, encoding, firstDevice).AsTask();
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
            var sourceFrames = _wasapiLoopbackCapture.AudioFrames;
            var frameCounts = sourceFrames.Count;
            _loopingAudioStream.Seek(0, SeekOrigin.End);
            var testCount = 0;
            for (var i = 0; i < frameCounts; i++)
            {
                _loopingAudioStream.Write(sourceFrames[i].data, 0, sourceFrames[i].data.Length);
                testCount += sourceFrames[i].data.Length;
            }

            for (var i = frameCounts - 1; i >= 0; i--)
            {
                sourceFrames.RemoveAt(i);
            }

            uint bufferSize = samples * (_loopbackInputNode.EncodingProperties.BitsPerSample / 8) * _loopbackInputNode.EncodingProperties.ChannelCount;
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

                if (_loopingAudioStream.Length < _readPosition + bufferSize)
                {
                    return default;
                }

                var bytes = new byte[bufferSize];
                _loopingAudioStream.Seek(Convert.ToInt64(_readPosition), SeekOrigin.Begin);
                _loopingAudioStream.Read(bytes, 0, (int)bufferSize);
                for (int i = 0; i < bufferSize; i++)
                {
                    dataInBytes[i] = bytes[i];
                }

                _readPosition += bufferSize;
            }

            frame.Duration = TimeSpan.FromMilliseconds(10);
            return frame;
        }

        unsafe private Windows.Media.AudioFrame GenerateTestEmptyAudioData(uint samples)
        {
            // 缓冲区大小是 (采样数) * (每个采样的字节数) * (通道数)
            uint bufferSize = samples * (_loopbackInputNode.EncodingProperties.BitsPerSample / 8) * _loopbackInputNode.EncodingProperties.ChannelCount;

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

            var frame = GenerateLoopbackAudioData((uint)args.RequiredSamples)
                ?? GenerateTestEmptyAudioData((uint)args.RequiredSamples);

            //if (frame == null)
            //{
            //    return;
            //}
                
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
                        _loopingAudioStream?.Dispose();
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
