// エクスプローラーの右クリックメニュー（Windows 11 の新しいメニュー）に
// 「GitReview で開く」を出すためのハンドラー。
//
// エクスプローラーのプロセスに読み込まれるので、例外・RTTI・動的な C++ ランタイムを
// 使わず、Win32 API だけで書いている。やることは、選ばれたフォルダのパスを添えて
// 同じフォルダにある GitReview.exe（ローカルの入口 EXE）を起動することだけ。
//
// ビルド: shell\build-shell.ps1（llvm-mingw を使う。Visual Studio は不要）

#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <new>

// パッケージのマニフェスト（AppxManifest.xml）の CLSID と一致させること
static const CLSID CLSID_GitReviewCommand =
    {0xFA52F213, 0x62E8, 0x4BBA, {0xA5, 0x21, 0x1C, 0xAE, 0xE2, 0x5C, 0xAE, 0x95}};

static HMODULE g_module = nullptr;
static LONG g_objects = 0;   // 生きているオブジェクト数（DllCanUnloadNow の判断に使う）
static LONG g_locks = 0;     // IClassFactory::LockServer によるロック数

// この DLL と同じフォルダにあるファイルのフルパスを作る
static bool PathBesideModule(const wchar_t* fileName, wchar_t* out, DWORD size)
{
    DWORD len = GetModuleFileNameW(g_module, out, size);
    if (len == 0 || len >= size) return false;

    if (!PathRemoveFileSpecW(out)) return false;
    return PathAppendW(out, fileName) != FALSE;
}

class GitReviewCommand final : public IExplorerCommand
{
public:
    GitReviewCommand() { InterlockedIncrement(&g_objects); }

    // ---- IUnknown ----
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;

        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IExplorerCommand))
        {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }

        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG refs = InterlockedDecrement(&_refs);
        if (refs == 0) delete this;
        return refs;
    }

    // ---- IExplorerCommand ----
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override
    {
        return SHStrDupW(L"GitReview で開く", name);
    }

    // アイコンは GitReview.exe に埋め込まれているものを使う
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        wchar_t path[MAX_PATH + 4];
        if (!PathBesideModule(L"GitReview.exe", path, MAX_PATH)) return E_FAIL;

        lstrcatW(path, L",0");
        return SHStrDupW(path, icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override
    {
        *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* name) override
    {
        *name = CLSID_GitReviewCommand;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        *state = ECS_ENABLED;
        return S_OK;
    }

    // 選ばれたフォルダ（空白部分の右クリックでは、そのフォルダ自身）を渡して起動する
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (!items) return E_INVALIDARG;

        IShellItem* item = nullptr;
        HRESULT hr = items->GetItemAt(0, &item);
        if (FAILED(hr)) return hr;

        LPWSTR folder = nullptr;
        hr = item->GetDisplayName(SIGDN_FILESYSPATH, &folder);
        item->Release();
        if (FAILED(hr)) return hr;   // 仮想フォルダなどファイルシステム上に無いものは対象外

        hr = Launch(folder);
        CoTaskMemFree(folder);
        return hr;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }

private:
    ~GitReviewCommand() { InterlockedDecrement(&g_objects); }

    static HRESULT Launch(const wchar_t* folder)
    {
        wchar_t exe[MAX_PATH];
        if (!PathBesideModule(L"GitReview.exe", exe, MAX_PATH)) return E_FAIL;

        // "exe" "folder" の形にする。CreateProcess はコマンドラインを書き換えることがあるので
        // 書き込み可能なバッファで渡す
        const size_t size = lstrlenW(exe) + lstrlenW(folder) + 8;
        wchar_t* commandLine = new (std::nothrow) wchar_t[size];
        if (!commandLine) return E_OUTOFMEMORY;

        wnsprintfW(commandLine, static_cast<int>(size), L"\"%s\" \"%s\"", exe, folder);

        wchar_t workDir[MAX_PATH];
        lstrcpynW(workDir, exe, MAX_PATH);
        PathRemoveFileSpecW(workDir);

        STARTUPINFOW si = {};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi = {};

        BOOL ok = CreateProcessW(exe, commandLine, nullptr, nullptr, FALSE, 0,
                                 nullptr, workDir, &si, &pi);
        delete[] commandLine;

        if (!ok) return HRESULT_FROM_WIN32(GetLastError());

        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return S_OK;
    }

    LONG _refs = 1;
};

class ClassFactory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;

        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    // 工場は DLL と同じ寿命の静的オブジェクトなので、参照数は数えない
    IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }
    IFACEMETHODIMP_(ULONG) Release() override { return 1; }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;

        GitReviewCommand* command = new (std::nothrow) GitReviewCommand();
        if (!command) return E_OUTOFMEMORY;

        HRESULT hr = command->QueryInterface(riid, ppv);
        command->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_locks);
        else InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

static ClassFactory g_factory;

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    if (!IsEqualCLSID(clsid, CLSID_GitReviewCommand)) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}
