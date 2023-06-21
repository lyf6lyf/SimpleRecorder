#pragma once

#include "AudioCapture.g.h"
#include "WASAPICapture.h"

namespace winrt::Interop::implementation
{
    struct AudioCapture : AudioCaptureT<AudioCapture>, MediaFoundationInitializer
    {
        AudioCapture();

        int32_t MyProperty();
        void MyProperty(int32_t value);

        void StartCapture();
        void StopCapture();

        winrt::com_ptr<internal::WASAPICapture> m_wasapiCapture;
    };
}

namespace winrt::Interop::factory_implementation
{
    struct AudioCapture : AudioCaptureT<AudioCapture, implementation::AudioCapture>
    {
    };
}
