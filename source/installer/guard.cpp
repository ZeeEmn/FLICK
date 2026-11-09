// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 ZeeEmn
#include <windows.h>
#include <msi.h>
#include <msiquery.h>

extern "C" __declspec(dllexport) UINT __stdcall CheckFlickClosed(MSIHANDLE install) {
    // Keep the existing application mutex name for compatibility with portable builds.
    const wchar_t* names[] = {L"Local\\REWIND.Development", L"Local\\FLICK.Application"};
    for (const auto* name : names) {
        HANDLE mutex = OpenMutexW(SYNCHRONIZE, FALSE, name);
        DWORD status = GetLastError();
        if (mutex) CloseHandle(mutex);
        if (mutex || status == ERROR_ACCESS_DENIED) {
            MSIHANDLE message = MsiCreateRecord(1);
            MsiRecordSetStringW(message, 0,
                L"FLICK is running. Save any replay you want to keep, then exit "
                L"FLICK from its tray menu before installing, repairing or uninstalling. "
                L"No running recorder has been closed.");
            MsiProcessMessage(install, INSTALLMESSAGE_ERROR, message);
            MsiCloseHandle(message);
            return ERROR_INSTALL_FAILURE;
        }
    }
    return ERROR_SUCCESS;
}
