extern "C" __declspec(dllexport) int __cdecl risk_parameter(
    long value,
    const wchar_t* text)
{
    return static_cast<int>(value + (text == nullptr ? 0 : 1));
}
