#include "pch.h"
#include "AudioCapture.h"
#include "AudioCapture.g.cpp"

namespace winrt::Interop::implementation
{
    AudioCapture::AudioCapture()
    {
        m_wasapiCapture = winrt::make_self<internal::WASAPICapture>();
        m_wasapiCapture->AsyncInitializeAudioDevice();
    }

    int32_t AudioCapture::MyProperty()
    {
        throw hresult_not_implemented();
    }

    void AudioCapture::MyProperty(int32_t /* value */)
    {
        throw hresult_not_implemented();
    }

    void AudioCapture::StartCapture()
    {
        //m_loopbackCapture.StartCaptureAsync(L"recording.wav");
        m_wasapiCapture->AsyncStartCapture();
    }

    void AudioCapture::StopCapture()
    {
        //m_loopbackCapture.StopCaptureAsync();
        m_wasapiCapture->AsyncStopCapture();
    }
}
