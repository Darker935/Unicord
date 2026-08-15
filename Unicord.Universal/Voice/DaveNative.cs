using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Unicord.Universal.Voice
{
    /// <summary>
    /// P/Invoke wrapper over unicord_dave.dll (davey / OpenMLS).
    ///
    /// Every davey method takes <c>&amp;mut self</c>: the Rust side requires exclusive
    /// access, and the FFI turns the raw handle into that exclusive reference on whatever
    /// thread calls in. Unicord calls encrypt from the sender loop, decrypt from the
    /// receive loop, and the MLS handshake ops from the websocket thread, so every entry
    /// point here serialises on one lock. Unsynchronised concurrent calls are a data race
    /// in Rust: they corrupted the ratchet state (bursts of decrypt failures whenever the
    /// local user was transmitting) and faulted outright when a rebuild destroyed the
    /// handle mid-call.
    /// </summary>
    internal sealed class DaveNative : IDisposable
    {
        private const string Dll = "unicord_dave.dll";
        private readonly object _sync = new object();
        private IntPtr _handle;
        private bool _disposed;

        // How long a caller waited for the session lock, worst case since last read. The
        // sender loop encrypts while the receive loop decrypts, so if this is more than a
        // millisecond or two the two directions are fighting and inbound audio is arriving
        // in bursts.
        private static long _maxWaitMs;

        public static long TakeMaxWaitMs() => System.Threading.Interlocked.Exchange(ref _maxWaitMs, 0);

        private static void RecordWait(long waited)
        {
            while (true)
            {
                var current = System.Threading.Interlocked.Read(ref _maxWaitMs);
                if (waited <= current ||
                    System.Threading.Interlocked.CompareExchange(ref _maxWaitMs, waited, current) == current)
                    return;
            }
        }

        private void Enter()
        {
            if (System.Threading.Monitor.TryEnter(_sync))
                return;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.Monitor.Enter(_sync);
            RecordWait(watch.ElapsedMilliseconds);
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dave_create(ushort protocolVersion, ulong userId, ulong channelId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void dave_destroy(IntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void dave_free_buf(IntPtr ptr, int len);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dave_set_external_sender(IntPtr handle, byte[] data, int len);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dave_create_key_package(IntPtr handle, out IntPtr outPtr, out int outLen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dave_process_proposals(IntPtr handle, byte opType, byte[] data, int len, ulong[] userIds, int userCount, out IntPtr outPtr, out int outLen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dave_process_commit(IntPtr handle, byte[] data, int len);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dave_process_welcome(IntPtr handle, byte[] data, int len);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dave_encrypt_opus(IntPtr handle, byte[] data, int len, out IntPtr outPtr, out int outLen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dave_decrypt(IntPtr handle, ulong userId, byte[] data, int len, out IntPtr outPtr, out int outLen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dave_is_ready(IntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void dave_set_passthrough(IntPtr handle, int enabled, uint expiry);

        public DaveNative(ushort protocolVersion, ulong userId, ulong channelId)
        {
            _handle = dave_create(protocolVersion, userId, channelId);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create DAVE session.");
        }

        public bool IsReady
        {
            get
            {
                lock (_sync)
                {
                    return !_disposed && dave_is_ready(_handle) != 0;
                }
            }
        }

        public void SetExternalSender(byte[] data)
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DaveNative));
                if (dave_set_external_sender(_handle, data, data.Length) != 0)
                    throw new InvalidOperationException("DAVE set_external_sender failed.");
            }
        }

        public byte[] CreateKeyPackage()
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DaveNative));
                if (dave_create_key_package(_handle, out var ptr, out var len) != 0)
                    throw new InvalidOperationException("DAVE create_key_package failed.");
                return TakeBuffer(ptr, len);
            }
        }

        public byte[] ProcessProposals(byte opType, byte[] data, ICollection<ulong> userIds)
        {
            ulong[] ids = null;
            var count = 0;
            if (userIds != null && userIds.Count > 0)
            {
                ids = new ulong[userIds.Count];
                userIds.CopyTo(ids, 0);
                count = ids.Length;
            }

            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DaveNative));
                if (dave_process_proposals(_handle, opType, data, data.Length, ids, count, out var ptr, out var len) != 0)
                    throw new InvalidOperationException("DAVE process_proposals failed.");
                if (ptr == IntPtr.Zero || len <= 0)
                    return null;
                return TakeBuffer(ptr, len);
            }
        }

        public bool ProcessCommit(byte[] data)
        {
            lock (_sync)
            {
                return !_disposed && dave_process_commit(_handle, data, data.Length) == 0;
            }
        }

        public bool ProcessWelcome(byte[] data)
        {
            lock (_sync)
            {
                return !_disposed && dave_process_welcome(_handle, data, data.Length) == 0;
            }
        }

        public byte[] EncryptOpus(byte[] data)
        {
            if (data == null)
                return null;

            Enter();
            try
            {
                if (_disposed || dave_is_ready(_handle) == 0)
                    return null;
                if (dave_encrypt_opus(_handle, data, data.Length, out var ptr, out var len) != 0)
                    return null;
                return TakeBuffer(ptr, len);
            }
            finally
            {
                System.Threading.Monitor.Exit(_sync);
            }
        }

        public byte[] Decrypt(ulong userId, byte[] data)
        {
            if (data == null)
                return null;

            Enter();
            try
            {
                if (_disposed)
                    return null;
                if (dave_decrypt(_handle, userId, data, data.Length, out var ptr, out var len) != 0)
                    return null;
                return TakeBuffer(ptr, len);
            }
            finally
            {
                System.Threading.Monitor.Exit(_sync);
            }
        }

        public void SetPassthrough(bool enabled, uint expirySeconds = 10)
        {
            lock (_sync)
            {
                if (!_disposed)
                    dave_set_passthrough(_handle, enabled ? 1 : 0, expirySeconds);
            }
        }

        private static byte[] TakeBuffer(IntPtr ptr, int len)
        {
            if (ptr == IntPtr.Zero || len <= 0)
                return Array.Empty<byte>();
            var buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            dave_free_buf(ptr, len);
            return buffer;
        }

        public void Dispose()
        {
            // Taking the lock means no other thread can be inside a native call on this
            // handle when it is destroyed; late callers see _disposed and back out.
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (_handle != IntPtr.Zero)
                {
                    dave_destroy(_handle);
                    _handle = IntPtr.Zero;
                }
            }
        }
    }
}
