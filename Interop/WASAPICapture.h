﻿// Copyright © Microsoft. All rights reserved.

#pragma once
//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

#pragma once
#include <mmdeviceapi.h>
#include <wil/resource.h>

#include "AudioFrame.h"
#include "Common.h"

namespace winrt::internal
{
    enum class DeviceState
    {
        Uninitialized,
        Error,
        Discontinuity,
        Flushing,
        Activated,

        Initialized,
        Starting,
        Playing,
        Capturing,
        Pausing,
        Paused,
        Stopping,
        Stopped,
    };

    // Primary WASAPI Capture Class
    struct WASAPICapture : winrt::implements<WASAPICapture, IActivateAudioInterfaceCompletionHandler>
    {
    public:
        WASAPICapture(bool isMic, winrt::Windows::Foundation::Collections::IVector<winrt::Interop::AudioFrame> audioFrames);

        void SetLowLatencyCapture(bool value) { m_isLowLatency = value; }
        void AsyncInitializeAudioDevice() noexcept;
        void AsyncStartCapture();
        void AsyncStopCapture();

        // IActivateAudioInterfaceCompletionHandler
        STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation);

    private:
        HRESULT OnStartCapture(IMFAsyncResult* pResult);
        HRESULT OnStopCapture(IMFAsyncResult* pResult);
        HRESULT OnFinishCapture(IMFAsyncResult* pResult);
        HRESULT OnSampleReady(IMFAsyncResult* pResult);
        HRESULT OnSendScopeData(IMFAsyncResult* pResult);

        EmbeddedMFAsyncCallback<&WASAPICapture::OnStartCapture> m_StartCaptureCallback{ this };
        EmbeddedMFAsyncCallback<&WASAPICapture::OnStopCapture> m_StopCaptureCallback{ this };
        EmbeddedMFAsyncCallback<&WASAPICapture::OnSampleReady> m_SampleReadyCallback{ this };
        EmbeddedMFAsyncCallback<&WASAPICapture::OnFinishCapture> m_FinishCaptureCallback{ this };
        EmbeddedMFAsyncCallback<&WASAPICapture::OnSendScopeData> m_SendScopeDataCallback{ this };

        fire_and_forget CreateWAVFile();
        fire_and_forget FixWAVHeader();
        void OnAudioSampleRequested();
        void InitializeScopeData();
        void ProcessScopeData(array_view<uint8_t> rawBytes);
        fire_and_forget AsyncStoreData();

    private:
        winrt::Windows::Foundation::Collections::IVector<winrt::Interop::AudioFrame> _audioFrames;
        uint32_t m_bufferFrames = 0;

        // Event for sample ready or user stop
        handle m_SampleReadyEvent{ check_pointer(CreateEventEx(nullptr, nullptr, 0, EVENT_ALL_ACCESS)) };

        MFWORKITEM_KEY m_sampleReadyKey = 0;
        slim_mutex m_lock;
        unique_shared_work_queue m_queueId{ L"Capture" };

        uint32_t m_headerSize = 0;
        uint32_t m_dataSize = 0;
        uint32_t m_bytesSinceLastFlush = 0;
        bool m_writing = false;
        bool m_isLowLatency = false;
        bool _isMic = false;

        Windows::Storage::Streams::IRandomAccessStream m_contentStream;
        Windows::Storage::Streams::IOutputStream m_outputStream;
        Windows::Storage::Streams::DataWriter m_dataWriter{ nullptr };
        unique_cotaskmem_ptr<WAVEFORMATEX> m_mixFormat;
        com_ptr<IAudioClient3> m_audioClient;
        uint32_t m_defaultPeriodInFrames;
        uint32_t m_fundamentalPeriodInFrames;
        uint32_t m_maxPeriodInFrames;
        uint32_t m_minPeriodInFrames;
        com_ptr<IAudioCaptureClient> m_audioCaptureClient;
        com_ptr<IMFAsyncResult> m_sampleReadyAsyncResult;

        Windows::Storage::Streams::IBuffer m_plotDataBuffer;
        uint32_t m_plotDataMaxPoints;
        uint32_t m_plotDataPointsFilled;

        DeviceState m_deviceState = DeviceState::Uninitialized;
        void SetState(enum DeviceState state, hresult error = S_OK)
        {
            if (m_deviceState != state)
            {
                m_deviceState = state;
            }
        }
        void SetStateNoNotify(enum DeviceState state)
        {
            m_deviceState = state;
        }

        wil::unique_event_nothrow m_hActivateCompleted;
        wil::unique_event_nothrow m_hCaptureStopped;
    };
}