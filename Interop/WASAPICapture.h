// Copyright © Microsoft. All rights reserved.

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
    enum class CaptureState
    {
        Uninitialized,
        Initialized,
        Starting,
        Capturing,
        Stopping,
        Stopped,
    };

    struct WasapiCapture : winrt::implements<WasapiCapture, IActivateAudioInterfaceCompletionHandler>, MediaFoundationInitializer
    {
    public:
        WasapiCapture();

        winrt::Windows::Foundation::IAsyncAction InitializeAsync();
        winrt::Windows::Foundation::IAsyncAction StartCaptureAsync();
        winrt::Windows::Foundation::IAsyncAction StopCaptureAsync();

        winrt::Windows::Media::MediaProperties::AudioEncodingProperties GetAudioEncodingProperties()
        {
            assert(m_state != CaptureState::Uninitialized);
            return m_audioEncodingProperties;
        }

        bool GetNextAudioBytes(uint8_t data[], uint32_t size);

        // IActivateAudioInterfaceCompletionHandler
        STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation);

    private:
        HRESULT OnStartCapture(IMFAsyncResult* pResult);
        HRESULT OnStopCapture(IMFAsyncResult* pResult);
        HRESULT OnSampleReady(IMFAsyncResult* pResult);

        EmbeddedMFAsyncCallback<&WasapiCapture::OnStartCapture> m_StartCaptureCallback{ this };
        EmbeddedMFAsyncCallback<&WasapiCapture::OnStopCapture> m_StopCaptureCallback{ this };
        EmbeddedMFAsyncCallback<&WasapiCapture::OnSampleReady> m_SampleReadyCallback{ this };

        void OnAudioSampleRequested();

        wil::unique_event_nothrow m_SampleReadyEvent;
        wil::unique_event_nothrow m_hActivateCompleted;
        wil::unique_event_nothrow m_hCaptureStarted;
        wil::unique_event_nothrow m_hCaptureStopped;

        winrt::hresult m_activateCompletedResult;
        winrt::hresult m_captureStartedResult;
        winrt::hresult m_captureStoppedResult;

        MFWORKITEM_KEY m_sampleReadyKey = 0;
        wil::srwlock m_lock;
        unique_shared_work_queue m_queueId{ L"Capture" };

        wil::unique_cotaskmem_ptr<WAVEFORMATEX> m_mixFormat{};
        winrt::com_ptr<IAudioClient> m_audioClient;
        winrt::com_ptr<IAudioCaptureClient> m_audioCaptureClient;
        winrt::com_ptr<IMFAsyncResult> m_sampleReadyAsyncResult;
        winrt::Windows::Media::MediaProperties::AudioEncodingProperties m_audioEncodingProperties { nullptr };

        CaptureState m_state = CaptureState::Uninitialized;
        std::vector<uint8_t> m_audioData{};
    };
}