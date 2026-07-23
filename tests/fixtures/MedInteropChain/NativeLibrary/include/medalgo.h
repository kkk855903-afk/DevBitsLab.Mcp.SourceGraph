#pragma once

#include <cstdint>

#if defined(_WIN32)
#define MEDALGO_API __declspec(dllexport)
#else
#define MEDALGO_API
#endif

struct NativeInput {
    std::int32_t patient_age;
    double scale;
};

struct NativeOutput {
    std::int32_t value;
};

extern "C" MEDALGO_API int __cdecl medalgo_calculate(
    const NativeInput* input,
    NativeOutput* output);
