﻿#pragma once

// RAII class to initialize Media Foundation.
struct MediaFoundationInitializer
{
    MediaFoundationInitializer()
    {
        // Initialize MF
        winrt::check_hresult(MFStartup(MF_VERSION, MFSTARTUP_LITE));
    }

    ~MediaFoundationInitializer()
    {
        MFShutdown();
    }
};

// RAII class to lock/unlock a shared work queue.
struct unique_shared_work_queue
{
    unique_shared_work_queue(PCWSTR className)
    {
        DWORD taskId = 0; // 0 means "create a new task group"
        winrt::check_hresult(MFLockSharedWorkQueue(className, 0, &taskId, &m_queueId));
    }

    ~unique_shared_work_queue()
    {
        MFUnlockWorkQueue(m_queueId);
    }

    unique_shared_work_queue(unique_shared_work_queue const&) = delete;
    void operator=(unique_shared_work_queue const&) = delete;

    DWORD get() { return m_queueId; }
private:
    DWORD m_queueId = 0;
};

// Helper class for allowing a class to implement multiple versions of
// IMFAsyncCallback.
template<auto Callback>
struct EmbeddedMFAsyncCallback : ::IMFAsyncCallback
{
    template<typename Parent> static Parent* parent_finder(HRESULT(Parent::*)(IMFAsyncResult*)) { return nullptr; }
    using ParentPtr = decltype(parent_finder(Callback));

    ParentPtr m_parent;
    DWORD m_queueId = 0;

    EmbeddedMFAsyncCallback(ParentPtr parent) : m_parent(parent) {}

    STDMETHOD(QueryInterface)(REFIID riid, void** ppvObject) final
    {
        if (winrt::is_guid_of<::IMFAsyncCallback, ::IUnknown>(riid))
        {
            (*ppvObject) = this;
            AddRef();
            return S_OK;
        }
        *ppvObject = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHOD_(ULONG, AddRef)() final { return m_parent->AddRef(); }
    STDMETHOD_(ULONG, Release)() final { return m_parent->Release(); }

    STDMETHOD(GetParameters)(DWORD* flags, DWORD* queueId) final
    {
        *flags = 0;
        *queueId = m_queueId;
        return S_OK;
    }

    STDMETHOD(Invoke)(IMFAsyncResult* result) final
    {
        return (m_parent->*Callback)(result);
    }

    void SetQueueId(DWORD queueId) { m_queueId = queueId; }
};
