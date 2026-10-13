using System.Runtime.InteropServices;

namespace Echo.Audio.Interop;

// ─────────────────────────────────────────────────────────────────────────────
//  Hand-written WASAPI process-loopback interop.
//
//  Port of teams-recorder/process_loopback.py. There is NO managed equivalent
//  for per-process loopback: it requires ActivateAudioInterfaceAsync (Mmdevapi)
//  with an AUDIOCLIENT_ACTIVATION_PARAMS blob carried inside a VT_BLOB
//  PROPVARIANT, an agile completion handler, and the raw IAudioClient /
//  IAudioCaptureClient COM interfaces. All GUIDs / flags are verbatim from the
//  Python source (see docs/contract/audio.md §1).
// ─────────────────────────────────────────────────────────────────────────────

internal static class WasapiConstants
{
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    public const ushort VT_BLOB = 65;
    public const ushort WAVE_FORMAT_PCM = 1;

    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    /// <summary>hnsBufferDuration = 2 s in 100-ns units (shared mode, matches Python).</summary>
    public const long BufferDuration100Ns = 20_000_000;

    public const int S_OK = 0;

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
}

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;

    public static WAVEFORMATEX MakePcm(int rate = 48000, int channels = 2, int bits = 16)
    {
        var wf = new WAVEFORMATEX
        {
            wFormatTag = WasapiConstants.WAVE_FORMAT_PCM,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)rate,
            wBitsPerSample = (ushort)bits,
            cbSize = 0,
        };
        wf.nBlockAlign = (ushort)(channels * bits / 8);
        wf.nAvgBytesPerSec = (uint)(rate * wf.nBlockAlign);
        return wf;
    }
}

// union flattened to the single process-loopback member (matches Python)
[StructLayout(LayoutKind.Sequential)]
internal struct AUDIOCLIENT_ACTIVATION_PARAMS
{
    public int ActivationType;
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

// x64 PROPVARIANT laid out for a VT_BLOB: 8-byte header, ULONG cbSize, then a
// pointer (naturally 8-aligned → 4 bytes padding after cbSize). sizeof == 24.
[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT_BLOB
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public uint cbSize;
    public IntPtr pBlobData;
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

// Marker interface — agility lets the callback fire on an arbitrary MTA thread.
[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration,
        long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr dataPtr, out uint numFramesToRead, out uint flags,
        out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

/// <summary>
/// Managed completion handler. Implements the completion-handler interface plus
/// IAgileObject (required so the OS can fire ActivateCompleted on a pool thread).
/// ActivateCompleted just signals the waiter and returns S_OK (mirrors Python).
/// </summary>
internal sealed class ActivateCompletionHandler
    : IActivateAudioInterfaceCompletionHandler, IAgileObject, IDisposable
{
    private readonly ManualResetEventSlim _done = new(false);

    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        _done.Set();
        return WasapiConstants.S_OK;
    }

    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

    // Dispose the wait handle after activation completes — one per (re)activation (audit F7).
    public void Dispose() => _done.Dispose();
}

internal static class NativeMethods
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    // --- CreateToolhelp32Snapshot family (parent-PID discovery, no BCL API) ---
    public const uint TH32CS_SNAPPROCESS = 0x2;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    // --- MMCSS "Pro Audio" glitch-resistant scheduling (Avrt.dll) ---
    [DllImport("Avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("Avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PROCESSENTRY32W
{
    public uint dwSize;
    public uint cntUsage;
    public uint th32ProcessID;
    public IntPtr th32DefaultHeapID;
    public uint th32ModuleID;
    public uint cntThreads;
    public uint th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szExeFile;
}
