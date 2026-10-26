using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PdcHomeConnect.Agent.Native;

/// <summary>Raises an event (debounced) when a registry key or its subtree changes.</summary>
public sealed class RegistryWatcher : IDisposable
{
    private const int NotifyChangeName = 0x1;
    private const int NotifyChangeLastSet = 0x4;

    private readonly RegistryHive _hive;
    private readonly string _path;
    private readonly TimeSpan _debounce;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public RegistryWatcher(RegistryHive hive, string path, TimeSpan debounce)
    {
        _hive = hive;
        _path = path;
        _debounce = debounce;
        _thread = new Thread(Run) { IsBackground = true, Name = $"RegistryWatcher {path}" };
    }

    public event Action? Changed;

    public RegistryWatcher Start()
    {
        _thread.Start();
        return this;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, int notifyFilter, SafeWaitHandle eventHandle, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            RegistryKey? key = null;
            try
            {
                key = RegistryKey.OpenBaseKey(_hive, RegistryView.Default).OpenSubKey(_path);
                if (key is null)
                {
                    // Key does not exist (yet): check again later.
                    _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                    continue;
                }

                using var signal = new AutoResetEvent(false);
                while (!_cts.IsCancellationRequested)
                {
                    var result = RegNotifyChangeKeyValue(key.Handle, true, NotifyChangeName | NotifyChangeLastSet, signal.SafeWaitHandle, true);
                    if (result != 0)
                    {
                        break;
                    }

                    var index = WaitHandle.WaitAny([signal, _cts.Token.WaitHandle]);
                    if (index != 0)
                    {
                        return;
                    }

                    // Debounce bursts of changes.
                    if (_cts.Token.WaitHandle.WaitOne(_debounce))
                    {
                        return;
                    }

                    try
                    {
                        Changed?.Invoke();
                    }
                    catch
                    {
                        // Listener errors must not stop the watcher.
                    }
                }
            }
            catch
            {
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            }
            finally
            {
                key?.Dispose();
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
