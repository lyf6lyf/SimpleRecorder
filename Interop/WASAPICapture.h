// Copyright (c) Microsoft Corporation. All rights reserved.

#pragma once

namespace Details
{
    // RAII class to initialize Media Foundation.
    struct MediaFoundationBase
    {
        MediaFoundationBase()
        {
            winrt::check_hresult(MFStartup(MF_VERSION, MFSTARTUP_LITE));
        }

        ~MediaFoundationBase()
        {
            winrt::check_hresult(MFShutdown());
        }
    };

    // RAII class to lock/unlock a shared work queue.
    struct UniqueSharedWorkQueue
    {
        explicit UniqueSharedWorkQueue(std::wstring_view className)
        {
            DWORD taskId = 0; // 0 means "create a new task group"
            winrt::check_hresult(MFLockSharedWorkQueue(className.data(), 0, &taskId, &m_queueId));
        }

        ~UniqueSharedWorkQueue()
        {
            winrt::check_hresult(MFUnlockWorkQueue(m_queueId));
        }

        UniqueSharedWorkQueue(UniqueSharedWorkQueue const&) = delete;
        void operator=(UniqueSharedWorkQueue const&) = delete;

        DWORD GetId() const noexcept { return m_queueId; }

    private:
        DWORD m_queueId = 0;
    };

    struct RelayMFAsyncCallback final : IMFAsyncCallback
    {
        RelayMFAsyncCallback(std::function<HRESULT(IMFAsyncResult*)> callback, IUnknown* parent) : m_callback(std::move(callback)), m_parent(parent) {}

        STDMETHOD(QueryInterface)(REFIID riid, void** ppvObject) override
        {
            if (winrt::is_guid_of<::IMFAsyncCallback, ::IUnknown>(riid))
            {
                (*ppvObject) = this;
                AddRef();
                return S_OK;
            }
            *ppvObject = nullptr;
            return E_NOINTERFACE;
        }

        STDMETHOD_(ULONG, AddRef)() override { return m_parent->AddRef(); }
        STDMETHOD_(ULONG, Release)() override { return m_parent->Release(); }

        STDMETHOD(GetParameters)(DWORD* flags, DWORD* queueId) override
        {
            *flags = 0;
            *queueId = m_queueId;
            return S_OK;
        }

        STDMETHOD(Invoke)(IMFAsyncResult* result) override
        {
            return m_callback(result);
        }

        void SetQueueId(const DWORD queueId) { m_queueId = queueId; }

    private:
        std::function<HRESULT(IMFAsyncResult*)> m_callback;
        ::IUnknown* m_parent;
        DWORD m_queueId = 0;
    };
}

enum class CaptureState
{
    Uninitialized,
    Initialized,
    Starting,
    Capturing,
    Stopping,
    Stopped,
};

class WasapiCapture : public winrt::implements<WasapiCapture, IActivateAudioInterfaceCompletionHandler>, Details::MediaFoundationBase
{
public:
    WasapiCapture();

    winrt::Windows::Foundation::IAsyncAction InitializeAsync();
    winrt::Windows::Foundation::IAsyncAction StartCaptureAsync();
    winrt::Windows::Foundation::IAsyncAction StopCaptureAsync();

    winrt::Windows::Media::MediaProperties::AudioEncodingProperties GetAudioEncodingProperties() const noexcept
    {
        assert(m_state != CaptureState::Uninitialized);
        return m_audioEncodingProperties;
    }

    /**
     * \brief Get the next audio bytes from the capture buffer.
     * \param data The bytes array to copy the audio bytes to.
     * \param size The size to copy.
     * \return true if the copy succeeded, false if there is no enough data to copy.
     */
    bool GetNextAudioBytes(uint8_t* data, uint32_t size);

    // IActivateAudioInterfaceCompletionHandler
    STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation) final;

private:
    HRESULT OnStartCapture(IMFAsyncResult* pResult);
    HRESULT OnStopCapture(IMFAsyncResult* pResult);
    HRESULT OnSampleReady(IMFAsyncResult* pResult);
    void OnAudioSampleRequested();

    Details::RelayMFAsyncCallback m_StartCaptureCallback{ [this](IMFAsyncResult* res) { return OnStartCapture(res); }, this };
    Details::RelayMFAsyncCallback m_StopCaptureCallback{ [this](IMFAsyncResult* res) { return OnStopCapture(res); }, this };
    Details::RelayMFAsyncCallback m_SampleReadyCallback{ [this](IMFAsyncResult* res) { return OnSampleReady(res); }, this };

    Details::UniqueSharedWorkQueue m_queue{ L"Capture" };
    wil::unique_event_nothrow m_SampleReadyEvent;
    MFWORKITEM_KEY m_sampleReadyKey = 0;
    winrt::com_ptr<IMFAsyncResult> m_sampleReadyAsyncResult;
    std::mutex m_lock;

    wil::unique_event_nothrow m_hActivateCompleted;
    wil::unique_event_nothrow m_hCaptureStarted;
    wil::unique_event_nothrow m_hCaptureStopped;
    winrt::hresult m_activateCompletedResult;
    winrt::hresult m_captureStartedResult;
    winrt::hresult m_captureStoppedResult;

    winrt::com_ptr<IAudioClient> m_audioClient;
    winrt::com_ptr<IAudioCaptureClient> m_audioCaptureClient;
    wil::unique_cotaskmem_ptr<WAVEFORMATEX> m_mixFormat{};
    winrt::Windows::Media::MediaProperties::AudioEncodingProperties m_audioEncodingProperties { nullptr };

    CaptureState m_state = CaptureState::Uninitialized;
    std::vector<uint8_t> m_audioData{};
};
