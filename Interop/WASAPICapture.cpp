// Copyright (c) Microsoft Corporation. All rights reserved.

#include "pch.h"
#include "WASAPICapture.h"

namespace
{
    struct WasapiCaptureHelper
    {
        class RelayMFAsyncCallback : public winrt::implements<RelayMFAsyncCallback, IMFAsyncCallback>
        {
        public:
            RelayMFAsyncCallback(std::function<void(WasapiCapture*, IMFAsyncResult*)> callback, WasapiCapture* capture, const DWORD queueId = 0) : m_callback(std::move(callback)), m_queueId(queueId)
            {
                m_capture.copy_from(capture);
                winrt::check_hresult(EventHandle.create());
            }

            STDMETHOD(GetParameters)(DWORD* flags, DWORD* queueId) noexcept override
            {
                *flags = 0;
                *queueId = m_queueId;
                return S_OK;
            }

            STDMETHOD(Invoke)(IMFAsyncResult* result) noexcept override
            {
                try
                {
                    m_callback(m_capture.get(), result);
                    Result = S_OK;
                }
                catch (...)
                {
                    Result = winrt::to_hresult();
                }

                EventHandle.SetEvent();

                // Must return S_OK even on failure.
                return S_OK;
            }

            wil::unique_event_nothrow EventHandle;
            winrt::hresult Result;

        private:
            std::function<void(WasapiCapture*, IMFAsyncResult*)> m_callback;
            winrt::com_ptr<WasapiCapture> m_capture;
            DWORD m_queueId = 0;
        };

        class ActivateAudioInterfaceCompletionHandler : public winrt::implements<ActivateAudioInterfaceCompletionHandler, IActivateAudioInterfaceCompletionHandler>
        {
        public:
            explicit ActivateAudioInterfaceCompletionHandler(WasapiCapture* parent)
            {
                m_parent.copy_from(parent);
                winrt::check_hresult(EventHandle.create(wil::EventOptions::None));
            }

            STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation) final try
            {
                HRESULT status = S_OK;
                winrt::com_ptr<::IUnknown> punkAudioInterface;

                winrt::check_hresult(operation->GetActivateResult(&status, punkAudioInterface.put()));
                winrt::check_hresult(status);

                m_parent->m_audioClient = punkAudioInterface.as<IAudioClient>();

                winrt::check_hresult(m_parent->m_audioClient->GetMixFormat(wil::out_param(m_parent->m_mixFormat)));

                switch (m_parent->m_mixFormat->wFormatTag)
                {
                case WAVE_FORMAT_PCM:
                    m_parent->m_audioEncodingProperties = winrt::Windows::Media::MediaProperties::AudioEncodingProperties::CreatePcm(
                        m_parent->m_mixFormat->nSamplesPerSec,
                        m_parent->m_mixFormat->nChannels,
                        m_parent->m_mixFormat->wBitsPerSample);
                    break;
                case WAVE_FORMAT_IEEE_FLOAT:
                    m_parent->m_audioEncodingProperties = winrt::Windows::Media::MediaProperties::AudioEncodingProperties::CreatePcm(
                        m_parent->m_mixFormat->nSamplesPerSec,
                        m_parent->m_mixFormat->nChannels,
                        m_parent->m_mixFormat->wBitsPerSample);
                    m_parent->m_audioEncodingProperties.Subtype(L"Float");
                    break;

                case WAVE_FORMAT_EXTENSIBLE:
                {
                    auto* pWaveFormatExtensible = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(m_parent->m_mixFormat.get());
                    if (pWaveFormatExtensible->SubFormat == KSDATAFORMAT_SUBTYPE_PCM)
                    {
                        m_parent->m_audioEncodingProperties = winrt::Windows::Media::MediaProperties::AudioEncodingProperties::CreatePcm(
                            pWaveFormatExtensible->Format.nSamplesPerSec,
                            pWaveFormatExtensible->Format.nChannels,
                            pWaveFormatExtensible->Format.wBitsPerSample);
                    }
                    else if (pWaveFormatExtensible->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                    {
                        m_parent->m_audioEncodingProperties = winrt::Windows::Media::MediaProperties::AudioEncodingProperties::CreatePcm(
                            pWaveFormatExtensible->Format.nSamplesPerSec,
                            pWaveFormatExtensible->Format.nChannels,
                            pWaveFormatExtensible->Format.wBitsPerSample);
                        m_parent->m_audioEncodingProperties.Subtype(L"Float");
                    }
                    else
                    {
                        // we can only handle float or PCM
                        throw winrt::hresult_error{AUDCLNT_E_UNSUPPORTED_FORMAT, winrt::to_hstring(pWaveFormatExtensible->SubFormat)};
                    }
                    break;
                }

                default:
                    // we can only handle float or PCM
                    throw winrt::hresult_error{AUDCLNT_E_UNSUPPORTED_FORMAT, winrt::to_hstring(m_parent->m_mixFormat->wFormatTag)};
                }

                using namespace std::literals;
                winrt::check_hresult(m_parent->m_audioClient->Initialize(
                    AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_NOPERSIST | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_LOOPBACK,
                    winrt::Windows::Foundation::TimeSpan{ 20ms }.count(),
                    0,
                    m_parent->m_mixFormat.get(),
                    nullptr));

                m_parent->m_audioCaptureClient.capture(m_parent->m_audioClient, &IAudioClient::GetService);

                // Set the capture event work queue to use the MMCSS queue
                auto sampleReadyCallback = winrt::make_self<RelayMFAsyncCallback>([](WasapiCapture* capture, IMFAsyncResult*) { return OnSampleReady(capture); }, m_parent.get(), m_parent->m_queue.GetId());

                winrt::check_hresult(MFCreateAsyncResult(nullptr, sampleReadyCallback.get(), nullptr, m_parent->m_sampleReadyAsyncResult.put()));

                // Provides the event handle for the system to signal when an audio buffer is ready to be processed by the client
                winrt::check_hresult(m_parent->m_audioClient->SetEventHandle(m_parent->m_SampleReadyEvent.get()));

                m_parent->m_state = WasapiCapture::CaptureState::Initialized;

                Result = S_OK;
                EventHandle.SetEvent();
                return S_OK;
            }
            catch (...)
            {
                m_parent->m_audioClient = nullptr;
                m_parent->m_audioCaptureClient = nullptr;
                m_parent->m_sampleReadyAsyncResult = nullptr;

                Result = winrt::to_hresult();
                EventHandle.SetEvent();

                // Must return S_OK even on failure.
                return S_OK;
            }

            wil::unique_event_nothrow EventHandle;
            winrt::hresult Result;

        private:
            winrt::com_ptr<WasapiCapture> m_parent;
        };

        static void OnStartCapture(WasapiCapture* self)
        {
            winrt::check_hresult(self->m_audioClient->Start());

            self->m_state = WasapiCapture::CaptureState::Capturing;
            winrt::check_hresult(MFPutWaitingWorkItem(self->m_SampleReadyEvent.get(), 0, self->m_sampleReadyAsyncResult.get(), &self->m_sampleReadyKey));
        }

        static void OnStopCapture(WasapiCapture* self)
        {
            self->m_sampleReadyAsyncResult = nullptr;

            // Cancel the queued work item (if any)
            if (0 != self->m_sampleReadyKey)
            {
                // Ignore the error code because canceling a work item may fail.
                MFCancelWorkItem(std::exchange(self->m_sampleReadyKey, 0));
            }

            if (self->m_audioClient)
            {
                winrt::check_hresult(self->m_audioClient->Stop());
            }

            self->m_state = WasapiCapture::CaptureState::Stopped;
        }

        static void OnSampleReady(WasapiCapture* self) try
        {
            OnAudioSampleRequested(self);

            // Re-queue work item for next sample
            if (self->m_state == WasapiCapture::CaptureState::Capturing)
            {
                winrt::check_hresult(MFPutWaitingWorkItem(self->m_SampleReadyEvent.get(), 0, self->m_sampleReadyAsyncResult.get(), &self->m_sampleReadyKey));
            }
        }
        catch (...)
        {
            // TODO 45371899: Do we need to stop capture and log the error?
        }

        static void OnAudioSampleRequested(WasapiCapture* self)
        {
            if (self->m_state != WasapiCapture::CaptureState::Capturing)
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
            while (SUCCEEDED(self->m_audioCaptureClient->GetNextPacketSize(&framesAvailable)) && framesAvailable > 0)
            {
                DWORD bytesToCapture = framesAvailable * self->m_mixFormat->nBlockAlign;

                {
                    uint8_t* data = nullptr;
                    DWORD dwCaptureFlags;
                    uint64_t devicePosition = 0;
                    uint64_t qpcPosition = 0;

                    winrt::check_hresult(self->m_audioCaptureClient->GetBuffer(&data, &framesAvailable, &dwCaptureFlags, &devicePosition, &qpcPosition));

                    // Ensure that the buffer is released at scope exit, even if an exception occurs.
                    auto raiiGuard = wil::scope_exit([&] { self->m_audioCaptureClient->ReleaseBuffer(framesAvailable); });

                    // Zero out sample if silence
                    if (dwCaptureFlags & AUDCLNT_BUFFERFLAGS_SILENT)
                    {
                        memset(data, 0, framesAvailable * self->m_mixFormat->nBlockAlign);
                    }

                    {
                        std::scoped_lock lk{self->m_lock};
                        self->m_audioData.insert(self->m_audioData.end(), data, data + bytesToCapture);
                    }
                }
            }
        }

    };

    using Helper = WasapiCaptureHelper;
}

WasapiCapture::WasapiCapture()
{
    winrt::check_hresult(m_SampleReadyEvent.create());
}

winrt::Windows::Foundation::IAsyncAction WasapiCapture::InitializeAsync()
{
    winrt::com_ptr<IActivateAudioInterfaceAsyncOperation> asyncOp;

    auto completeHandler = winrt::make_self<Helper::ActivateAudioInterfaceCompletionHandler>(this);

    // This call must be made on the main UI thread.  Async operation will call back to 
    // IActivateAudioInterfaceCompletionHandler::ActivateCompleted, which must be an agile interface implementation
    winrt::check_hresult(ActivateAudioInterfaceAsync(
        winrt::Windows::Media::Devices::MediaDevice::GetDefaultAudioRenderId(winrt::Windows::Media::Devices::AudioDeviceRole::Default).c_str(),
        __uuidof(IAudioClient),
        nullptr,
        completeHandler.get(),
        asyncOp.put()));

    using namespace std::literals;
    if (co_await winrt::resume_on_signal(completeHandler->EventHandle.get(), 3s))
    {
        winrt::check_hresult(completeHandler->Result);
    }
    else
    {
        throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT), L"ActivateAudioInterfaceAsync timeout");
    }
}

winrt::Windows::Foundation::IAsyncAction WasapiCapture::StartCaptureAsync()
{
    if (m_state == CaptureState::Initialized)
    {
        m_state = CaptureState::Starting;

        auto startCaptureCallback = winrt::make_self<Helper::RelayMFAsyncCallback>([](WasapiCapture* capture, IMFAsyncResult*) { return Helper::OnStartCapture(capture); }, this);

        // Starts asynchronous capture on a separate thread via MF Work Item
        winrt::check_hresult(MFPutWorkItem2(MFASYNC_CALLBACK_QUEUE_MULTITHREADED, 0, startCaptureCallback.get(), nullptr));

        using namespace std::literals;
        if (co_await winrt::resume_on_signal(startCaptureCallback->EventHandle.get(), 3s))
        {
            winrt::check_hresult(startCaptureCallback->Result);
        }
        else
        {
            throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT), L"StartCaptureAsync timeout");
        }
    }
}

winrt::Windows::Foundation::IAsyncAction WasapiCapture::StopCaptureAsync()
{
    if (m_state == CaptureState::Capturing)
    {
        m_state = CaptureState::Stopping;

        auto stopCaptureCallback = winrt::make_self<Helper::RelayMFAsyncCallback>([](WasapiCapture* capture, IMFAsyncResult*) { return Helper::OnStopCapture(capture); }, this);

        // Stops asynchronous capture on a separate thread via MF Work Item
        winrt::check_hresult(MFPutWorkItem2(MFASYNC_CALLBACK_QUEUE_MULTITHREADED, 0, stopCaptureCallback.get(), nullptr));

        using namespace std::literals;
        if (co_await winrt::resume_on_signal(stopCaptureCallback->EventHandle.get(), 3s))
        {
            winrt::check_hresult(stopCaptureCallback->Result);
        }
        else
        {
            throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT), L"StopCaptureAsync timeout");
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
    for (auto i = 0u; i < size; i++)
    {
        data[i] = m_audioData.front();
        m_audioData.pop_front();
    }
    return true;
}
