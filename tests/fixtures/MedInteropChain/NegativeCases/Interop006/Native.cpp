#include <cstdlib>

extern "C" __declspec(dllexport) void* __cdecl risk_allocate()
{
    return std::malloc(64);
}
