#pragma once
#include "AudioFrame.g.h"

namespace winrt::Interop::implementation
{
    struct AudioFrame : AudioFrameT<AudioFrame>
    {
        AudioFrame() = default;

        uint64_t timestamp();
        void timestamp(uint64_t value);
        com_array<uint8_t> data();
        void data(array_view<uint8_t const> value);

    private:
    	uint64_t m_timestamp;
        com_array<uint8_t> m_data;
    };
}
namespace winrt::Interop::factory_implementation
{
    struct AudioFrame : AudioFrameT<AudioFrame, implementation::AudioFrame>
    {
    };
}
