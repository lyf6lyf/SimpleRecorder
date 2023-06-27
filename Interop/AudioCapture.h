#pragma once

#include "AudioCapture.g.h"
#include "WASAPICapture.h"

namespace winrt::Interop::implementation
{
    struct AudioCapture : AudioCaptureT<AudioCapture>, MediaFoundationInitializer
    {
        AudioCapture(bool isMic);

        void StartCapture();
        void StopCapture();

        winrt::Windows::Foundation::Collections::IVector<winrt::Interop::AudioFrame> AudioFrames();

        winrt::com_ptr<internal::WASAPICapture> m_wasapiCapture;
        winrt::Windows::Foundation::Collections::IVector<winrt::Interop::AudioFrame> m_audioFrames{ nullptr };
    };
}

namespace winrt::Interop::factory_implementation
{
    struct AudioCapture : AudioCaptureT<AudioCapture, implementation::AudioCapture>
    {
    };
}
