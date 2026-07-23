using ResultCallback = void (*)(int);
static ResultCallback stored_callback;

extern "C" __declspec(dllexport) void __cdecl risk_register_callback(
    ResultCallback callback)
{
    stored_callback = callback;
}
