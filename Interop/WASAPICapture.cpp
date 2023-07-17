// Copyright (c) Microsoft Corporation. All rights reserved.

#include "pch.h"
#include "WASAPICapture.h"

WasapiCapture::WasapiCapture()
{
    // Set the capture event work queue to use the MMCSS queue
    m_SampleReadyCallback.SetQueueId(m_queue.GetId());

    winrt::check_hresult(m_SampleReadyEvent.create(wil::EventOptions::None));
    winrt::check_hresult(m_hActivateCompleted.create(wil::EventOptions::None));
    winrt::check_hresult(m_hCaptureStarted.create(wil::EventOptions::None));
    winrt::check_hresult(m_hCaptureStopped.create(wil::EventOptions::None));
}

winrt::Windows::Foundation::IAsyncAction WasapiCapture::InitializeAsync()
{
    winrt::com_ptr<IActivateAudioInterfaceAsyncOperation> asyncOp;

    // This call must be made on the main UI thread.  Async operation will call back to 
    // IActivateAudioInterfaceCompletionHandler::ActivateCompleted, which must be an agile interface implementation
    winrt::check_hresult(ActivateAudioInterfaceAsync(
        winrt::Windows::Media::Devices::MediaDevice::GetDefaultAudioRenderId(winrt::Windows::Media::Devices::AudioDeviceRole::Default).c_str(),
        __uuidof(IAudioClient),
        nullptr,
        this,
        asyncOp.put()));

    using namespace std::literals;
    if (co_await winrt::resume_on_signal(m_hActivateCompleted.get(), winrt::Windows::Foundation::TimeSpan{3s}))
    {
        winrt::check_hresult(m_activateCompletedResult);
    }
    else
    {
        throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT), L"ActivateAudioInterfaceAsync timeout");
    }
}

HRESULT WasapiCapture::ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) try
{
    HRESULT status = S_OK;
    winrt::com_ptr<::IUnknown> punkAudioInterface;

    winrt::check_hresult(operation->GetActivateResult(&status, punkAudioInterface.put()));
    winrt::check_hresult(status);

    m_audioClient = punkAudioInterface.as<IAudioClient>();

    winrt::check_hresult(m_audioClient->GetMixFormat(wil::out_param(m_mixFormat)));

    switch (m_mixFormat->wFormatTag)
    {
    case WAVE_FORMAT_PCM:
        m_audioEncodingProperties = winrt::Windows::Media::MediaProperties::AudioEncodingProperties::CreatePcm(
            m_mixFormat->nSamplesPerSec,
            m_mixFormat->nChannels,
            m_mixFormat->wBitsPerSample);
        break;
    case WAVE_FORMAT_IEEE_FLOAT:
        m_audioEncodingProperties = winrt::Windows::Media::MediaProperties::AudioEncodingProperties::CreatePcm(
            m_mixFormat->nSamplesPerSec,
            m_mixFormat->nChannels,
            m_mixFormat->wBitsPerSample);
        m_audioEncodingProperties.Subtype(L"Float");
        break;

    case WAVE_FORMAT_EXTENSIBLE:
    {
        auto* pWaveFormatExtensible = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(m_mixFormat.get());
        if (pWaveFormatExtensible->SubFormat == KSDATAFORMAT_SUBTYPE_PCM)
        {
            m_audioEncodingProperties = winrt::Windows::Media::MediaProperties::AudioEncodingProperties::CreatePcm(
                pWaveFormatExtensible->Format.nSamplesPerSec,
                pWaveFormatExtensible->Format.nChannels,
                pWaveFormatExtensible->Format.wBitsPerSample);
        }
        else if (pWaveFormatExtensible->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
        {
            m_audioEncodingProperties = winrt::Windows::Media::MediaProperties::AudioEncodingProperties::CreatePcm(
                pWaveFormatExtensible->Format.nSamplesPerSec,
                pWaveFormatExtensible->Format.nChannels,
                pWaveFormatExtensible->Format.wBitsPerSample);
            m_audioEncodingProperties.Subtype(L"Float");
        }
        else
        {
            // we can only handle float or PCM
            throw winrt::hresult_error(AUDCLNT_E_UNSUPPORTED_FORMAT, winrt::to_hstring(pWaveFormatExtensible->SubFormat));
        }
        break;
    }

    default:
        // we can only handle float or PCM
        throw winrt::hresult_error(AUDCLNT_E_UNSUPPORTED_FORMAT, winrt::to_hstring(m_mixFormat->wFormatTag));
    }

    using namespace std::literals;
    winrt::check_hresult(m_audioClient->Initialize(
        AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMFLAGS_NOPERSIST | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_LOOPBACK,
        winrt::Windows::Foundation::TimeSpan{ 20ms }.count(),
        0,
        m_mixFormat.get(),
        nullptr));

    m_audioCaptureClient.capture(m_audioClient, &IAudioClient::GetService);

    winrt::check_hresult(MFCreateAsyncResult(nullptr, &m_SampleReadyCallback, nullptr, m_sampleReadyAsyncResult.put()));

    // Provides the event handle for the system to signal when an audio buffer is ready to be processed by the client
    winrt::check_hresult(m_audioClient->SetEventHandle(m_SampleReadyEvent.get()));

    m_state = CaptureState::Initialized;

    m_activateCompletedResult = S_OK;
    m_hActivateCompleted.SetEvent();
    return S_OK;
}
catch (...)
{
    m_audioClient = nullptr;
    m_audioCaptureClient = nullptr;
    m_sampleReadyAsyncResult = nullptr;

    m_activateCompletedResult = winrt::to_hresult();
    m_hActivateCompleted.SetEvent();

    // Must return S_OK even on failure.
    return S_OK;
}

winrt::Windows::Foundation::IAsyncAction WasapiCapture::StartCaptureAsync()
{
    if (m_state == CaptureState::Initialized)
    {
        m_state = CaptureState::Starting;

        // Starts asynchronous capture on a separate thread via MF Work Item
        winrt::check_hresult(MFPutWorkItem2(MFASYNC_CALLBACK_QUEUE_MULTITHREADED, 0, &m_StartCaptureCallback, nullptr));

        using namespace std::literals;
        if (co_await winrt::resume_on_signal(m_hCaptureStarted.get(), winrt::Windows::Foundation::TimeSpan{ 3s }))
        {
            winrt::check_hresult(m_captureStartedResult);
        }
        else
        {
            throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT), L"StartCaptureAsync timeout");
        }
    }
}

HRESULT WasapiCapture::OnStartCapture(IMFAsyncResult*) try
{
    winrt::check_hresult(m_audioClient->Start());
    winrt::check_hresult(MFPutWaitingWorkItem(m_SampleReadyEvent.get(), 0, m_sampleReadyAsyncResult.get(), &m_sampleReadyKey));

    m_state = CaptureState::Capturing;
    m_captureStartedResult = S_OK;
    m_hCaptureStarted.SetEvent();
    return S_OK;
}
catch (...)
{
    const auto hr = winrt::to_hresult();
    m_captureStartedResult = hr;
    m_hCaptureStarted.SetEvent();

    // Must return S_OK even on failure.
    return S_OK;
}

winrt::Windows::Foundation::IAsyncAction WasapiCapture::StopCaptureAsync()
{
    if (m_state == CaptureState::Capturing)
    {
        m_state = CaptureState::Stopping;

        // Stops asynchronous capture on a separate thread via MF Work Item
        winrt::check_hresult(MFPutWorkItem2(MFASYNC_CALLBACK_QUEUE_MULTITHREADED, 0, &m_StopCaptureCallback, nullptr));

        using namespace std::literals;
        if (co_await winrt::resume_on_signal(m_hCaptureStopped.get(), winrt::Windows::Foundation::TimeSpan{ 3s }))
        {
            winrt::check_hresult(m_captureStoppedResult);
        }
        else
        {
            throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT), L"StopCaptureAsync timeout");
        }
    }
}

HRESULT WasapiCapture::OnStopCapture(IMFAsyncResult*) try
{
    // Cancel the queued work item (if any)
    if (0 != m_sampleReadyKey)
    {
        winrt::check_hresult(MFCancelWorkItem(std::exchange(m_sampleReadyKey, 0)));
    }

    m_sampleReadyAsyncResult = nullptr;
    if (m_audioClient)
    {
        winrt::check_hresult(m_audioClient->Stop());
    }

    m_state = CaptureState::Stopped;
    m_captureStoppedResult = S_OK;
    m_hCaptureStopped.SetEvent();
    return S_OK;
}
catch (...)
{
    const auto hr = winrt::to_hresult();
    m_captureStoppedResult = hr;
    m_hCaptureStopped.SetEvent();

    // Must return S_OK even on failure.
    return S_OK;
}

HRESULT WasapiCapture::OnSampleReady(IMFAsyncResult*) try
{
    OnAudioSampleRequested();

    // Re-queue work item for next sample
    if (m_state == CaptureState::Capturing)
    {
        winrt::check_hresult(MFPutWaitingWorkItem(m_SampleReadyEvent.get(), 0, m_sampleReadyAsyncResult.get(), &m_sampleReadyKey));
    }

    return S_OK;
}
catch (...)
{
    // TODO 45371899: Do we need to stop capture and log the error?
    return winrt::to_hresult();
}

void WasapiCapture::OnAudioSampleRequested()
{
    if (m_state != CaptureState::Capturing)
    {
        return;
    }

    // A word on why we have a loop here:
    // Suppose it has been 10 milliseconds or so since the last time
    // this routine was invoked, and that we're capturing 48000 samples per second.
    //
    // The audio engine can be reasonably expected to have accumulated about that much
    // audio data - that is, about 480 samples.
    //
    // However, the audio engine is free to accumulate this in various ways:
    // a. as a single packet of 480 samples, OR
    // b. as a packet of 80 samples plus a packet of 400 samples, OR
    // c. as 48 packets of 10 samples each.
    //
    // In particular, there is no guarantee that this routine will be
    // run once for each packet.
    //
    // So every time this routine runs, we need to read ALL the packets
    // that are now available;
    //
    // We do this by calling IAudioCaptureClient::GetNextPacketSize
    // over and over again until it indicates there are no more packets remaining.
    uint32_t framesAvailable = 0;
    while (SUCCEEDED(m_audioCaptureClient->GetNextPacketSize(&framesAvailable)) && framesAvailable > 0)
    {
        DWORD bytesToCapture = framesAvailable * m_mixFormat->nBlockAlign;

        {
            uint8_t* data = nullptr;
            DWORD dwCaptureFlags;
            uint64_t devicePosition = 0;
            uint64_t qpcPosition = 0;

            winrt::check_hresult(m_audioCaptureClient->GetBuffer(&data, &framesAvailable, &dwCaptureFlags, &devicePosition, &qpcPosition));

            // Ensure that the buffer is released at scope exit, even if an exception occurs.
            auto release = wil::scope_exit([&] { m_audioCaptureClient->ReleaseBuffer(framesAvailable); });

            // Zero out sample if silence
            if (dwCaptureFlags & AUDCLNT_BUFFERFLAGS_SILENT)
            {
                memset(data, 0, framesAvailable * m_mixFormat->nBlockAlign);
            }

            {
                std::scoped_lock lk{m_lock};
                m_audioData.insert(m_audioData.end(), data, data + bytesToCapture);
            }
        }
    }
}

bool WasapiCapture::GetNextAudioBytes(uint8_t* data, const uint32_t size)
{
    std::scoped_lock lk{m_lock};
    if (m_audioData.size() < size)
    {
        return false;
    }
    memcpy(data, m_audioData.data(), size);
    m_audioData.erase(m_audioData.begin(), m_audioData.begin() + size);
    return true;
}
