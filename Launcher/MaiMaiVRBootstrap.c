// MaiMaiVR portable bootstrap - MIT License
// Copyright (c) 2026 MaiMaiVR contributors
//
// Functionally equivalent source for MaiMaiVR.exe.
// The bootstrap starts the compatibility launcher path from the portable application folder.
// V0.6.6 keeps this filename because the already validated native bootstrap binary points to it;
// that script now dispatches the current V0.6.6 runtime.

#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shellapi.h>
#include <wchar.h>

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE hPrev, PWSTR cmd, int show)
{
    wchar_t exePath[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, exePath, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        MessageBoxW(NULL, L"Could not resolve MaiMaiVR path.", L"MaiMaiVR - Error", MB_ICONERROR);
        return 1;
    }

    wchar_t *slash = wcsrchr(exePath, L'\\');
    if (!slash) return 1;
    *slash = 0;

    if (!SetCurrentDirectoryW(exePath)) {
        MessageBoxW(NULL, L"Could not set MaiMaiVR working folder.", L"MaiMaiVR - Error", MB_ICONERROR);
        return 1;
    }

    const wchar_t *args =
        L"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \".\\Scripts\\MaiMaiVRLauncher_V0.6.5.ps1\"";

    HINSTANCE r = ShellExecuteW(NULL, L"open", L"powershell.exe", args, exePath, SW_HIDE);
    if ((INT_PTR)r <= 32) {
        MessageBoxW(NULL, L"Could not start MaiMaiVR launcher.", L"MaiMaiVR - Error", MB_ICONERROR);
        return 2;
    }
    return 0;
}
