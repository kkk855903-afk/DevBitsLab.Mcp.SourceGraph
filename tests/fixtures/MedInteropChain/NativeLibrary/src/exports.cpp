#include "algorithm.hpp"

extern "C" MEDALGO_API int __cdecl medalgo_calculate(
    const NativeInput* input,
    NativeOutput* output)
{
    if (input == nullptr || output == nullptr) return 1;
    try {
        *output = Algorithm::Calculate(*input);
        return 0;
    } catch (...) {
        return 2;
    }
}
