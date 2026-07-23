#include <stdexcept>

extern "C" __declspec(dllexport) int __cdecl risk_throws()
{
    throw std::runtime_error("crosses C ABI");
}
