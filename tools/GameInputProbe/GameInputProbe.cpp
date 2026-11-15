#include <GameInput.h>

#include <Windows.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

using Microsoft::WRL::ComPtr;
using namespace GameInput::v4;

namespace
{
std::mutex g_outputMutex;

std::string HResult(HRESULT value)
{
    std::ostringstream text;
    text << "0x" << std::hex << std::uppercase << std::setw(8) << std::setfill('0')
         << static_cast<unsigned long>(value);
    return text.str();
}

std::string EventName(GameInputDeviceEvent value)
{
    switch (value)
    {
    case GameInputDeviceEventConnected: return "connected";
    case GameInputDeviceEventDisconnected: return "disconnected";
    case GameInputDeviceEventXInputIndexUpdated: return "xinput-index-updated";
    case GameInputDeviceEventKeyboardLayoutUpdated: return "keyboard-layout-updated";
    case GameInputDeviceEventHapticInfoUpdated: return "haptic-info-updated";
    default: return "unknown";
    }
}

std::string SystemButtonNames(GameInputSystemButtons buttons)
{
    if (buttons == GameInputSystemButtonNone)
    {
        return "none";
    }

    std::string result;
    if ((buttons & GameInputSystemButtonGuide) != GameInputSystemButtonNone)
    {
        result = "Guide";
    }
    if ((buttons & GameInputSystemButtonShare) != GameInputSystemButtonNone)
    {
        result += result.empty() ? "Share" : "|Share";
    }
    return result;
}

std::string DisplayName(IGameInputDevice* device)
{
    char buffer[512]{};
    const auto count = device->GetDisplayName(static_cast<uint32_t>(sizeof(buffer)), buffer);
    if (count == 0 || buffer[0] == '\0')
    {
        return "<unnamed>";
    }
    buffer[sizeof(buffer) - 1] = '\0';
    return buffer;
}

std::string PnpPath(IGameInputDevice* device)
{
    char buffer[1024]{};
    const auto count = device->GetPnpPath(static_cast<uint32_t>(sizeof(buffer)), buffer);
    if (count == 0 || buffer[0] == '\0')
    {
        return "<unavailable>";
    }
    buffer[sizeof(buffer) - 1] = '\0';
    return buffer;
}

std::string ContainerId(IGameInputDevice* device)
{
    GUID id{};
    device->GetContainerId(&id);
    std::ostringstream text;
    text << std::hex << std::uppercase << std::setfill('0')
         << std::setw(8) << id.Data1 << '-' << std::setw(4) << id.Data2 << '-'
         << std::setw(4) << id.Data3 << '-';
    for (size_t index = 0; index < sizeof(id.Data4); ++index)
    {
        text << std::setw(2) << static_cast<unsigned int>(id.Data4[index]);
        if (index == 1)
        {
            text << '-';
        }
    }
    return text.str();
}

void PrintDevice(const char* prefix, IGameInputDevice* device)
{
    uint32_t xinputIndex = 0;
    const bool hasXInputIndex = device->GetXInputDeviceIndex(&xinputIndex);
    const auto supported = device->GetSupportedInput();
    const auto systemButtons = device->GetSupportedSystemButtons();

    std::lock_guard lock(g_outputMutex);
    std::cout << prefix << " name=\"" << DisplayName(device) << "\""
              << " VID=0x" << std::hex << std::uppercase << std::setw(4) << std::setfill('0')
              << device->GetVendorId() << " PID=0x" << std::setw(4) << device->GetProductId()
              << std::dec << " supportedInput=0x" << std::hex << static_cast<uint32_t>(supported)
              << " systemButtons=" << SystemButtonNames(systemButtons) << std::dec;
    if (hasXInputIndex)
    {
        std::cout << " xinputIndex=" << xinputIndex;
    }
    else
    {
        std::cout << " xinputIndex=<not-associated>";
    }
    std::cout << "\n  containerId=" << ContainerId(device)
              << "\n  pnpPath=" << PnpPath(device) << std::endl;
}

struct ProbeContext
{
    std::mutex devicesMutex;
    std::vector<ComPtr<IGameInputDevice>> devices;
    std::unordered_map<IGameInputDevice*, GameInputSystemButtons> previousButtons;
};

void CALLBACK OnDeviceEvent(
    GameInputCallbackToken,
    void* context,
    GameInputDeviceEvent deviceEvent,
    IGameInputDevice* device)
{
    auto& probe = *static_cast<ProbeContext*>(context);
    {
        std::lock_guard lock(probe.devicesMutex);
        const auto found = std::find_if(
            probe.devices.begin(), probe.devices.end(),
            [device](const auto& candidate) { return candidate.Get() == device; });

        if (deviceEvent == GameInputDeviceEventConnected && found == probe.devices.end())
        {
            probe.devices.emplace_back(device);
        }
        else if (deviceEvent == GameInputDeviceEventDisconnected)
        {
            if (found != probe.devices.end())
            {
                probe.devices.erase(found);
            }
            probe.previousButtons.erase(device);
        }
    }

    {
        std::lock_guard lock(g_outputMutex);
        std::cout << "[event] " << EventName(deviceEvent) << std::endl;
    }

    if (deviceEvent == GameInputDeviceEventConnected
        || deviceEvent == GameInputDeviceEventXInputIndexUpdated)
    {
        PrintDevice("[device]", device);
    }
}

std::wstring ModulePath(HMODULE module)
{
    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    path.resize(length);
    return path;
}

void PrintLoadedRuntime()
{
    const HMODULE redist = GetModuleHandleW(L"GameInputRedist.dll");
    const HMODULE inbox = GetModuleHandleW(L"GameInput.dll");
    const HMODULE runtime = redist != nullptr ? redist : inbox;

    std::lock_guard lock(g_outputMutex);
    if (runtime == nullptr)
    {
        std::cout << "[runtime] loaded DLL path unavailable\n";
        return;
    }

    const auto runtimePath = ModulePath(runtime);
    std::wcout << L"[runtime] " << runtimePath << L'\n';

    wchar_t executablePath[32768]{};
    GetModuleFileNameW(nullptr, executablePath, static_cast<DWORD>(std::size(executablePath)));
    std::wstring applicationDirectory(executablePath);
    applicationDirectory.resize(applicationDirectory.find_last_of(L"\\/") + 1);

    const bool appLocal = runtimePath.size() >= applicationDirectory.size()
        && _wcsnicmp(runtimePath.c_str(), applicationDirectory.c_str(), applicationDirectory.size()) == 0;
    std::cout << "[runtime] deployment=" << (appLocal ? "app-local/side-by-side" : "system/inbox") << '\n';
    if (appLocal)
    {
        std::cout << "[runtime] app-local PC runtime: EnableBackgroundInput(true) has no effect; "
                     "focus is not managed by the GameInput service. System-button readings remain available.\n";
    }
}

int ParseSeconds(int argc, wchar_t** argv)
{
    constexpr int defaultSeconds = 60;
    if (argc == 1)
    {
        return defaultSeconds;
    }

    const std::wstring argument = argv[1];
    if (argument == L"--help" || argument == L"-h")
    {
        return 0;
    }

    const wchar_t* value = argument.starts_with(L"--seconds=") ? argv[1] + 10 : nullptr;
    if (value == nullptr && argument == L"--seconds" && argc > 2)
    {
        value = argv[2];
    }
    if (value == nullptr)
    {
        return -1;
    }

    wchar_t* end = nullptr;
    const auto seconds = std::wcstoul(value, &end, 10);
    if (end == value || *end != L'\0' || seconds == 0 || seconds > 86400)
    {
        return -1;
    }
    return static_cast<int>(seconds);
}

void PrintButtonEdges(
    ProbeContext& probe,
    IGameInputDevice* device,
    GameInputSystemButtons current)
{
    std::lock_guard lock(probe.devicesMutex);
    const auto previous = probe.previousButtons[device];
    probe.previousButtons[device] = current;
    const auto changed = static_cast<GameInputSystemButtons>(previous ^ current);
    if (changed == GameInputSystemButtonNone)
    {
        return;
    }

    std::lock_guard outputLock(g_outputMutex);
    for (const auto button : { GameInputSystemButtonGuide, GameInputSystemButtonShare })
    {
        if ((changed & button) == GameInputSystemButtonNone)
        {
            continue;
        }
        std::cout << "[system-button] " << SystemButtonNames(button) << ' '
                  << ((current & button) == GameInputSystemButtonNone ? "UP" : "DOWN")
                  << " name=\"" << DisplayName(device) << "\"\n";
    }
    std::cout.flush();
}
}

int wmain(int argc, wchar_t** argv)
{
    std::cout << "GameInput v4 system-button probe (compile-time API "
              << GAMEINPUT_API_VERSION << ")\n";
    const int seconds = ParseSeconds(argc, argv);
    if (seconds == 0)
    {
        std::cout << "Usage: GameInputProbe.exe [--seconds N]\n"
                     "Polls Guide/Share for N seconds (default 60, maximum 86400).\n";
        return 0;
    }
    if (seconds < 0)
    {
        std::cerr << "Invalid arguments. Use --help for usage.\n";
        return 2;
    }

    ComPtr<IGameInput> gameInput;
    const HRESULT createResult = GameInputCreate(&gameInput);
    PrintLoadedRuntime();
    if (FAILED(createResult))
    {
        std::cerr << "GameInputCreate failed: " << HResult(createResult)
                  << " (the selected runtime may not implement the v4 interface)\n";
        return 1;
    }

    gameInput->EnableBackgroundInput(true);
    std::cout << "[background] EnableBackgroundInput(true) called; switch away from this console "
                 "and press Guide/Share to test delivery.\n";

    ProbeContext probe;
    GameInputCallbackToken deviceCallbackToken = 0;
    const HRESULT callbackResult = gameInput->RegisterDeviceCallback(
        nullptr,
        static_cast<GameInputKind>(GameInputKindGamepad | GameInputKindSystemButton),
        &probe,
        &OnDeviceEvent,
        &deviceCallbackToken);
    if (FAILED(callbackResult))
    {
        std::cerr << "RegisterDeviceCallback failed: " << HResult(callbackResult) << '\n';
        gameInput->EnableBackgroundInput(false);
        return 1;
    }

    std::cout << "[probe] polling for " << seconds
              << " seconds; connect/disconnect devices during this run.\n";
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
    while (std::chrono::steady_clock::now() < deadline)
    {
        std::vector<ComPtr<IGameInputDevice>> devices;
        {
            std::lock_guard lock(probe.devicesMutex);
            devices = probe.devices;
        }

        for (const auto& device : devices)
        {
            ComPtr<IGameInputReading> reading;
            const HRESULT readingResult = gameInput->GetCurrentReading(
                GameInputKindSystemButton, device.Get(), &reading);
            if (FAILED(readingResult))
            {
                continue;
            }

            GameInputSystemButtons buttons = GameInputSystemButtonNone;
            if (reading->GetSystemButtonState(&buttons))
            {
                PrintButtonEdges(probe, device.Get(), buttons);
            }
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    gameInput->UnregisterCallback(deviceCallbackToken);
    gameInput->EnableBackgroundInput(false);
    std::cout << "[probe] finished; no hardware result is inferred from compilation or this run alone.\n";
    return 0;
}
