#include "algorithm.hpp"

NativeOutput Algorithm::Calculate(const NativeInput& input)
{
    return NativeOutput{
        static_cast<std::int32_t>(input.patient_age * input.scale)
    };
}
