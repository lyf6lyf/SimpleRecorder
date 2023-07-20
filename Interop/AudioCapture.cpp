#include "pch.h"
#include "AudioCapture.h"
#include "AudioCapture.g.cpp"

namespace winrt::Interop::implementation
{
    AudioCapture::AudioCapture()
    {
        m_wasapiCapture = winrt::make_self<WasapiCapture>();
    }

    winrt::Windows::Media::MediaProperties::AudioEncodingProperties AudioCapture::AudioEncodingProperties()
    {
        return m_wasapiCapture->GetAudioEncodingProperties();
    }
    winrt::Windows::Foundation::IAsyncAction AudioCapture::InitializeAsync()
    {
        co_await m_wasapiCapture->InitializeAsync();
    }
    winrt::Windows::Foundation::IAsyncAction AudioCapture::StartCaptureAsync()
    {
        co_await m_wasapiCapture->StartCaptureAsync();
    }
    winrt::Windows::Foundation::IAsyncAction AudioCapture::StopCaptureAsync()
    {
        co_await m_wasapiCapture->StopCaptureAsync();
    }
    com_array<uint8_t> AudioCapture::GetNextAudioBytes(const uint32_t size)
    {
        std::vector<uint8_t> data(size);
        if(m_wasapiCapture->GetNextAudioBytes(data.data(), size))
        {
            return winrt::com_array<uint8_t>{data};
		}
		return winrt::com_array<uint8_t>{};
    }
}
