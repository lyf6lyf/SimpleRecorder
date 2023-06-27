#include "pch.h"
#include "AudioFrame.h"
#include "AudioFrame.g.cpp"

namespace winrt::Interop::implementation
{
    uint64_t AudioFrame::timestamp()
    {
        return m_timestamp;
    }
    void AudioFrame::timestamp(uint64_t value)
    {
        m_timestamp = value;
    }
    com_array<uint8_t> AudioFrame::data()
    {
        return winrt::com_array<uint8_t>{ m_data.begin(), m_data.end() };
    }
    void AudioFrame::data(array_view<uint8_t const> value)
    {
        m_data = { value.begin(), value.end() };
    }
}
