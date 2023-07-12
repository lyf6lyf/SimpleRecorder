#pragma once

#include "AudioCapture.g.h"
#include "WASAPICapture.h"

namespace winrt::Interop::implementation
{
    struct AudioCapture : AudioCaptureT<AudioCapture>
    {
        AudioCapture();

        winrt::Windows::Media::MediaProperties::AudioEncodingProperties AudioEncodingProperties();
        winrt::Windows::Foundation::IAsyncAction InitializeAsync();
        winrt::Windows::Foundation::IAsyncAction StartCaptureAsync();
        winrt::Windows::Foundation::IAsyncAction StopCaptureAsync();
        com_array<uint8_t> GetNextAudioBytes(uint32_t size);

        winrt::com_ptr<internal::WasapiCapture> m_wasapiCapture;
    };
}

namespace winrt::Interop::factory_implementation
{
    struct AudioCapture : AudioCaptureT<AudioCapture, implementation::AudioCapture>
    {
    };
}
