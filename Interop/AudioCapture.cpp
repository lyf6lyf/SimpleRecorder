#include "pch.h"
#include "AudioCapture.h"
#include "AudioCapture.g.cpp"

namespace winrt::Interop::implementation
{
    AudioCapture::AudioCapture(bool isMic)
    {
        m_audioFrames = winrt::multi_threaded_vector<winrt::Interop::AudioFrame>();
        m_wasapiCapture = winrt::make_self<internal::WASAPICapture>(isMic, m_audioFrames);
        m_wasapiCapture->AsyncInitializeAudioDevice();
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

    winrt::Windows::Foundation::Collections::IVector<winrt::Interop::AudioFrame> AudioCapture::AudioFrames()
    {
        return m_audioFrames;
    }
}
