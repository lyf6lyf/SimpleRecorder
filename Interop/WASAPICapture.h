// Copyright (c) Microsoft Corporation. All rights reserved.

#pragma once

namespace details
{
    // RAII class to initialize Media Foundation.
    class MediaFoundationBase
    {
    public:
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
    class UniqueSharedWorkQueue
    {
    public:
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
        DWORD m_queueId;
    };
} // namespace details

namespace
{
    struct WasapiCaptureHelper;
} // namespace


class WasapiCapture : public winrt::implements<WasapiCapture, IUnknown>, public details::MediaFoundationBase
{
    friend struct WasapiCaptureHelper;

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

private:
    enum class CaptureState
    {
        Uninitialized,
        Initialized,
        Starting,
        Capturing,
        Stopping,
        Stopped,
    };

    details::UniqueSharedWorkQueue m_queue{ L"Capture" };
    wil::unique_event_nothrow m_SampleReadyEvent;
    MFWORKITEM_KEY m_sampleReadyKey = 0;
    winrt::com_ptr<IMFAsyncResult> m_sampleReadyAsyncResult;
    std::mutex m_lock;

    winrt::com_ptr<IAudioClient> m_audioClient;
    winrt::com_ptr<IAudioCaptureClient> m_audioCaptureClient;
    wil::unique_cotaskmem_ptr<WAVEFORMATEX> m_mixFormat;
    winrt::Windows::Media::MediaProperties::AudioEncodingProperties m_audioEncodingProperties = nullptr;

    CaptureState m_state = CaptureState::Uninitialized;
    std::deque<uint8_t> m_audioData;
};
