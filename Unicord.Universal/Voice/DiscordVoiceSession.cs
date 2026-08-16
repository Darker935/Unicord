using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Structs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unicord.Universal.Voice.Transport;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.System.Threading;

namespace Unicord.Universal.Voice
{
    internal sealed class DiscordVoiceSession : IDisposable
    {
        public const string EncryptionMode = "aead_aes256_gcm_rtpsize";
        private const int RtpHeaderSize = 12;
        private const int OpusPayloadType = 120;
        private const int MaxQueuedCaptureFrames = 4;
        private const int MaxQueuedReceivePackets = 64;
        private const int CaptureBacklogFrames = 2;
        private const int SilenceTailFrames = 5;
        private const int VoiceHangoverFrames = 25;
        private const int MaxConcealedFrames = 2;
        private const int FrameDurationMs = 20;
        private const int MaxClockCatchUpFrames = 2;
        private const int SpeakingHangoverMs = 350;
        private const int StereoLockBitrate = 64000;
        private const int StereoReleaseBitrate = 48000;
        private const double VoiceAttackRms = 300;
        private const double VoiceReleaseRms = 120;
        private const uint TransitionExpiry = 10;
        private const uint DowngradeTransitionExpiry = 24;

        private static readonly byte[] SilenceFrame = { 0xF8, 0xFF, 0xFE };

        private readonly string _endpoint;
        private readonly string _token;
        private readonly string _sessionId;
        private readonly ulong _userId;
        private readonly ulong _guildId;
        private readonly ulong _channelId;
        private readonly string _inputDeviceId;
        private readonly string _outputDeviceId;
        private readonly VoiceQualityOptions _options;

        private MessageWebSocket _webSocket;
        private DatagramSocket _udpSocket;
        private DataWriter _udpWriter;
        private ThreadPoolTimer _heartbeatTimer;
        private ThreadPoolTimer _keepaliveTimer;
        private VoiceAesGcm _aes;
        private VoiceAudioEngine _audio;
        private OpusEncoder _encoder;
        private readonly ConcurrentDictionary<uint, OpusDecoder> _decoders = new ConcurrentDictionary<uint, OpusDecoder>();
        private readonly ConcurrentDictionary<uint, ulong> _ssrcToUser = new ConcurrentDictionary<uint, ulong>();
        private readonly ConcurrentDictionary<uint, int> _lastSequence = new ConcurrentDictionary<uint, int>();
        private readonly HashSet<ulong> _connectedClients = new HashSet<ulong>();
        private DaveNative _dave;
        private byte[] _pendingExternalSender;
        private ushort _daveProtocolVersion;
        private bool _daveDowngraded;
        private const int DaveDecryptFailureTolerance = 36;

        /// <summary>
        /// Consecutive encrypt failures tolerated before the session is rebuilt. Lower than
        /// the decrypt tolerance because every one of these is a frame of our own microphone
        /// that never left, where a decrypt failure costs one speaker one frame.
        /// </summary>
        private const int DaveEncryptFailureTolerance = 10;

        /// <summary>
        /// Consecutive decrypt failures per speaker. davey holds a decryptor per user, so a
        /// broken one is a per-user condition and has to be counted that way.
        /// </summary>
        private readonly ConcurrentDictionary<ulong, int> _daveFailuresByUser = new ConcurrentDictionary<ulong, int>();
        private int _daveEncryptFailures;

        /// <summary>
        /// Consecutive decoder exceptions, and how often to report them. Logging every one
        /// was itself part of the problem: the failure arrives at packet rate, so the trace
        /// filled with identical lines and the logging cost piled onto the fault.
        /// </summary>
        private int _decodeFailures;
        private const int DecodeFailureLogInterval = 250;
        private volatile bool _daveReinitializing;
        private int _daveLastTransitionId;
        private readonly Dictionary<int, ushort> _pendingTransitions = new Dictionary<int, ushort>();

        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _wsSendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _udpSendLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<short[]> _captureQueue = new ConcurrentQueue<short[]>();
        private readonly SemaphoreSlim _captureSignal = new SemaphoreSlim(0);
        private readonly ConcurrentQueue<byte[]> _receiveQueue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _receiveSignal = new SemaphoreSlim(0);
        private CancellationTokenSource _senderCts;
        private readonly Random _random = new Random();

        private uint _ssrc;
        private uint _nonce;
        private ushort _sequence;
        private uint _timestampBase;
        private long _frameIndex;
        private int _silenceTail;
        private int _voiceHangover;
        private readonly System.Diagnostics.Stopwatch _rateWatch = System.Diagnostics.Stopwatch.StartNew();
        private long _rateFrames;
        private long _backlogDropped;
        private readonly System.Diagnostics.Stopwatch _qualityWatch = System.Diagnostics.Stopwatch.StartNew();
        private long _packetsReceived;
        private long _packetsLost;
        private long _packetsDropped;
        private long _droppedNoMapping;
        private long _droppedDaveFailed;
        private readonly System.Diagnostics.Stopwatch _receiveWatch = System.Diagnostics.Stopwatch.StartNew();
        private long _receivePackets;
        private long _receiveBatches;
        private int _receivePeakBatch;
        private long _receiveBusyMs;
        private int _targetBitrate;
        private int _defaultForceChannels;
        private bool _stereoLocked;
        private readonly System.Diagnostics.Stopwatch _streamWatch = new System.Diagnostics.Stopwatch();
        private long _clockBaseFrame;
        private long _sentPackets;
        private long _clockRepeats;
        private byte[] _lastSentOpus;
        private readonly byte[] _encodeScratch = new byte[4000];
        private bool _voiceActive;
        private int _wsSequence = -1;
        private ulong _keepalive;
        private readonly ConcurrentDictionary<ulong, long> _keepaliveTimestamps = new ConcurrentDictionary<ulong, long>();
        private bool _speaking;
        private readonly ConcurrentDictionary<ulong, long> _speakingUntil = new ConcurrentDictionary<ulong, long>();
        private readonly HashSet<ulong> _speakingUsers = new HashSet<ulong>();
        private ThreadPoolTimer _speakingTimer;
        private bool _muted;
        private bool _deafened;
        private bool _disposed;
        private int _faulted;
        private bool _udpReady;
        private string _selectedMode;

        public uint WebSocketPing { get; private set; }
        public uint UdpPing { get; private set; }

        /// <summary>
        /// The voice websocket close code, once it has closed. 4014 means the channel is gone,
        /// we were moved or kicked, or the main gateway session dropped, and per Discord's
        /// guidance the client must not reconnect.
        /// </summary>
        public ushort CloseCode { get; private set; }

        public event EventHandler<uint> WebSocketPingUpdated;
        public event EventHandler<uint> UdpPingUpdated;
        public event EventHandler<VoiceSpeakingEventArgs> SpeakingChanged;
        public event EventHandler Disconnected;

        public DiscordVoiceSession(
            string endpoint,
            string token,
            string sessionId,
            ulong userId,
            ulong guildId,
            ulong channelId,
            string inputDeviceId,
            string outputDeviceId,
            VoiceQualityOptions options)
        {
            _options = options ?? new VoiceQualityOptions();
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _token = token ?? throw new ArgumentNullException(nameof(token));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _userId = userId;
            _guildId = guildId;
            _channelId = channelId;
            _inputDeviceId = inputDeviceId;
            _outputDeviceId = outputDeviceId;
            _sequence = (ushort)_random.Next(0, ushort.MaxValue);
            _timestampBase = (uint)_random.Next();
        }

        public bool Muted
        {
            get => _muted;
            set
            {
                _muted = value;
                if (_audio != null)
                    _audio.CaptureEnabled = !value;
                if (value)
                    _ = SendSpeakingAsync(false);
            }
        }

        public bool Deafened
        {
            get => _deafened;
            set
            {
                _deafened = value;
                if (_audio != null)
                    _audio.PlaybackEnabled = !value;
            }
        }

        public async Task ConnectAsync()
        {
            // Discord voice endpoints include a port that is often not 443.
            // Stripping it connects to the wrong gateway and Identify fails with 4006.
            var host = _endpoint;
            if (host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                host = host.Substring(6);
            else if (host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                host = host.Substring(5);

            _webSocket = new MessageWebSocket();
            _webSocket.Control.MessageType = SocketMessageType.Utf8;
            _webSocket.MessageReceived += OnWsMessage;
            _webSocket.Closed += OnWsClosed;

            var uri = new Uri("wss://" + host + "/?v=8");
            Logger.Log("Voice WS connecting " + uri);
            await _webSocket.ConnectAsync(uri);

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            using (timeout.Token.Register(() => _ready.TrySetException(new TimeoutException("Timed out completing the Discord voice handshake."))))
            {
                await _ready.Task.ConfigureAwait(false);
            }
        }

        public async Task DisconnectAsync()
        {
            Dispose();
            await Task.CompletedTask;
        }

        private async void OnWsMessage(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                var reader = args.GetDataReader();
                if (args.MessageType == SocketMessageType.Binary)
                {
                    var length = reader.UnconsumedBufferLength;
                    var binary = new byte[length];
                    reader.ReadBytes(binary);
                    await HandleBinaryAsync(binary);
                    return;
                }

                reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                var json = reader.ReadString(reader.UnconsumedBufferLength);
                Logger.Log("Voice WS << " + json);

                var packet = JObject.Parse(json);
                if (packet["seq"] != null)
                    _wsSequence = packet.Value<int>("seq");

                var op = packet.Value<int>("op");
                var data = packet["d"];

                switch (op)
                {
                    case 8: // Hello
                        var interval = data.Value<int>("heartbeat_interval");
                        _heartbeatTimer?.Cancel();
                        _heartbeatTimer = ThreadPoolTimer.CreatePeriodicTimer(async _ => await SendHeartbeatAsync(), TimeSpan.FromMilliseconds(Math.Max(interval, 1000)));
                        await SendIdentifyAsync();
                        break;
                    case 2: // Ready
                        await HandleReadyAsync((JObject)data);
                        break;
                    case 4: // Session Description
                        await HandleSessionDescriptionAsync((JObject)data);
                        break;
                    case 6: // Heartbeat ACK
                        HandleHeartbeatAck(data);
                        break;
                    case 5: // Speaking
                        HandleSpeaking((JObject)data);
                        break;
                    case 11: // Clients Connect
                        HandleClientsConnect((JObject)data);
                        break;
                    case 13: // Client Disconnect
                        if (data?["user_id"] != null && ulong.TryParse((string)data["user_id"], out var left))
                            lock (_connectedClients) { _connectedClients.Remove(left); }
                        break;
                    case 21: // DAVE prepare transition
                        await HandleDavePrepareTransitionAsync((JObject)data);
                        break;
                    case 22: // DAVE execute transition
                        ExecuteDaveTransition(data.Value<int>("transition_id"));
                        break;
                    case 24: // DAVE prepare epoch
                        HandleDavePrepareEpoch((JObject)data);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                _ready.TrySetException(ex);
            }
        }

        private async Task SendIdentifyAsync()
        {
            var payload = new VoiceDispatch
            {
                OpCode = 0,
                Payload = new
                {
                    server_id = _guildId.ToString(),
                    user_id = _userId.ToString(),
                    session_id = _sessionId,
                    token = _token,
                    max_dave_protocol_version = 1
                }
            };

            await SendJsonAsync(payload);
        }

        private async Task HandleReadyAsync(JObject data)
        {
            _ssrc = data.Value<uint>("ssrc");
            var ip = data.Value<string>("ip");
            var port = data.Value<ushort>("port");
            var modes = data["modes"]?.Values<string>().ToArray() ?? Array.Empty<string>();

            if (!modes.Contains(EncryptionMode, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Discord did not offer " + EncryptionMode + ". Available: " + string.Join(", ", modes));

            _udpSocket = new DatagramSocket();
            _udpSocket.Control.QualityOfService = SocketQualityOfService.LowLatency;
            _udpSocket.MessageReceived += OnUdpMessage;

            await _udpSocket.ConnectAsync(new HostName(ip), port.ToString());
            _udpWriter = new DataWriter(_udpSocket.OutputStream);

            var discovery = new byte[74];
            WriteUInt16BigEndian(discovery, 0, 1);
            WriteUInt16BigEndian(discovery, 2, 70);
            WriteUInt32BigEndian(discovery, 4, _ssrc);

            await SendUdpAsync(discovery);
        }

        private async void OnUdpMessage(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                var reader = args.GetDataReader();
                var length = reader.UnconsumedBufferLength;
                var packet = new byte[length];
                reader.ReadBytes(packet);

                if (!_udpReady)
                {
                    if (packet.Length < 74)
                        return;

                    var discoveredIp = Encoding.ASCII.GetString(packet, 8, 64).TrimEnd('\0');
                    var discoveredPort = ReadUInt16BigEndian(packet, 72);
                    _udpReady = true;

                    var select = new VoiceDispatch
                    {
                        OpCode = 1,
                        Payload = new
                        {
                            protocol = "udp",
                            data = new
                            {
                                address = discoveredIp,
                                port = discoveredPort,
                                mode = EncryptionMode
                            }
                        }
                    };
                    await SendJsonAsync(select);
                    return;
                }

                if (packet.Length == 8)
                {
                    var count = ReadUInt64LittleEndian(packet, 0);
                    if (_keepaliveTimestamps.TryRemove(count, out var sent))
                    {
                        UdpPing = (uint)Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sent);
                        UdpPingUpdated?.Invoke(this, UdpPing);
                    }
                    return;
                }

                // Hand off immediately. AES-GCM, DAVE and Opus decode on the socket
                // callback make UWP drop inbound datagrams whenever this stalls, and
                // because the callback is shared it drops every speaker at once.
                _receiveQueue.Enqueue(packet);
                while (_receiveQueue.Count > MaxQueuedReceivePackets && _receiveQueue.TryDequeue(out _))
                {
                }

                try { _receiveSignal.Release(); }
                catch (ObjectDisposedException) { }
                catch (SemaphoreFullException) { }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                if (!_udpReady)
                    _ready.TrySetException(ex);
            }
        }

        private async Task HandleSessionDescriptionAsync(JObject data)
        {
            _selectedMode = data.Value<string>("mode");
            if (!string.Equals(_selectedMode, EncryptionMode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Discord selected unsupported voice mode " + _selectedMode);

            var keyToken = data["secret_key"];
            var key = keyToken.Select(t => (byte)t.Value<int>()).ToArray();
            _aes = new VoiceAesGcm(key);

            var daveVersion = data.Value<int?>("dave_protocol_version") ?? 0;
            _daveProtocolVersion = (ushort)daveVersion;
            if (daveVersion > 0)
                InitializeDave((ushort)daveVersion);

            ConfigureEncoder();

            // AudioGraph creation was measured at roughly 900ms, and it used to run here,
            // inside the handshake, so the join sat waiting on Windows audio device setup
            // that has nothing to do with Discord.
            var audioWatch = System.Diagnostics.Stopwatch.StartNew();
            _audio = new VoiceAudioEngine();
            _audio.CaptureEnabled = !_muted;
            _audio.PlaybackEnabled = !_deafened;
            _audio.PcmCaptured += OnPcmCaptured;
            await _audio.StartAsync(_inputDeviceId, _outputDeviceId);
            Logger.Log("Voice audio graph ready in " + audioWatch.ElapsedMilliseconds + "ms");

            _senderCts = new CancellationTokenSource();
            var token = _senderCts.Token;

            // Dedicated threads with blocking waits, not Task.Run plus WaitAsync. A
            // SemaphoreSlim waiter registered by WaitAsync can be resumed on the thread that
            // calls Release, so the queue-and-signal handoff out of the AudioGraph capture
            // callback was not a handoff at all: Opus encode at 128 kbps stereo, DAVE
            // encrypt, AES-GCM and the UDP write ran inside the callback. The graph cannot
            // advance a quantum until the callback returns, so its clock slipped by whatever
            // that cost - measured at 12 to 35 ms against a 10 ms budget, which is the whole
            // 95 quanta a second against 100. Wait blocks this thread instead, and Release
            // from the audio thread only pulses it.
            _ = Windows.System.Threading.ThreadPool.RunAsync(
                _ => SenderLoop(token), WorkItemPriority.High, WorkItemOptions.TimeSliced);
            _ = Windows.System.Threading.ThreadPool.RunAsync(
                _ => ReceiveLoop(token), WorkItemPriority.High, WorkItemOptions.TimeSliced);

            _keepaliveTimer = ThreadPoolTimer.CreatePeriodicTimer(async _ => await SendKeepaliveAsync(), TimeSpan.FromSeconds(5));
            _speakingTimer = ThreadPoolTimer.CreatePeriodicTimer(_ => ExpireSpeaking(), TimeSpan.FromMilliseconds(200));

            await SendSpeakingAsync(true);
            // Discord does not deliver other users' audio until this client has sent RTP.
            for (var i = 0; i < 5; i++)
                await SendRtpPacketAsync(SilenceFrame, NextTimestamp());
            await SendSpeakingAsync(false);

            // The RTP clock is paced against wall time rather than the capture callback, but
            // it is not started here. Starting it at session-ready began counting before the
            // audio graph was producing anything - the graph takes around 100 ms to come up -
            // so by the first captured frame the clock already expected several that had
            // never existed. FillClockGapAsync saw the deficit and resynced, which is a
            // discontinuity in the very first audio the far end receives, and matches the
            // brief robotic stretch heard on joining. It starts on the first real frame
            // instead, in SendCapturedFrameAsync.

            _ready.TrySetResult(true);
            Logger.Log("Voice session ready (" + _selectedMode + ")");
        }

        private void OnPcmCaptured(short[] pcm)
        {
            if (pcm == null)
                return;

            if (_disposed || _aes == null)
            {
                VoicePcmPool.Return(pcm);
                return;
            }

            // QuantumStarted runs on the AudioGraph realtime thread. Opus encoding,
            // DAVE encryption and UDP writes there starve the playback quantum, which
            // is heard as periodic chopping on every incoming stream.
            _captureQueue.Enqueue(pcm);
            while (_captureQueue.Count > MaxQueuedCaptureFrames && _captureQueue.TryDequeue(out var stale))
                VoicePcmPool.Return(stale);

            try { _captureSignal.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }

        private void ReceiveLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                try
                {
                    _receiveSignal.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                // Batch size is the diagnostic that matters: packets arrive at 50/s per
                // speaker, so anything but ones and twos here means this loop stalled and
                // then flushed, which the jitter buffer sees as a burst followed by a gap.
                var batch = 0;
                var batchStart = _receiveWatch.ElapsedMilliseconds;

                while (_receiveQueue.TryDequeue(out var packet))
                {
                    batch++;
                    try
                    {
                        HandleIncomingVoice(packet);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex);
                    }
                }

                if (batch > _receivePeakBatch)
                    _receivePeakBatch = batch;

                _receiveBatches++;
                _receivePackets += batch;
                _receiveBusyMs += _receiveWatch.ElapsedMilliseconds - batchStart;

                if (_receiveWatch.ElapsedMilliseconds >= 5000)
                {
                    if (DiagnosticLog.Voice.IsEnabled)
                    {
                        DiagnosticLog.Voice.Log("receive packets=" + _receivePackets +
                                   " batches=" + _receiveBatches +
                                   " peakBatch=" + _receivePeakBatch +
                                   " busy=" + _receiveBusyMs + "ms/" + _receiveWatch.ElapsedMilliseconds +
                                   "ms daveWaitMax=" + DaveNative.TakeMaxWaitMs() + "ms");
                    }
                    else
                    {
                        DaveNative.TakeMaxWaitMs();
                    }

                    _receivePackets = 0;
                    _receiveBatches = 0;
                    _receivePeakBatch = 0;
                    _receiveBusyMs = 0;
                    _receiveWatch.Restart();
                }
            }
        }

        private void SenderLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                try
                {
                    _captureSignal.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                // Drain fully. If a stall (GC, UI work, a gateway round-trip) left a backlog,
                // the oldest frames are skipped rather than sent late, otherwise the added
                // latency ratchets up and never comes back down.
                while (_captureQueue.TryDequeue(out var pcm))
                {
                    try
                    {
                        // Counted here, not in SendCapturedFrameAsync, so "capture=" reports
                        // frames the engine actually produced. Counting it after the backlog
                        // guard below meant every frame this loop threw away was invisible,
                        // and the deficit it measured was partly its own doing.
                        _rateFrames++;
                        LogPacing();

                        if (_captureQueue.Count >= CaptureBacklogFrames)
                        {
                            // Still advance the RTP clock so the far end sees a real gap
                            // instead of spliced audio.
                            _backlogDropped++;
                            NextTimestamp();
                            continue;
                        }

                        // Blocking on this dedicated thread is the point: it keeps the whole
                        // send pipeline off every thread that matters.
                        SendCapturedFrameAsync(pcm).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex);
                    }
                    finally
                    {
                        // The encoder copies out of this frame, so it is free the moment the
                        // send returns, on every path including the drops above.
                        VoicePcmPool.Return(pcm);
                    }
                }
            }
        }

        private async Task SendCapturedFrameAsync(short[] pcm)
        {
            if (_disposed || _aes == null || _encoder == null)
                return;

            // The wall clock starts with the audio, not with the handshake, so it can never
            // owe frames the microphone was not yet producing.
            if (!_streamWatch.IsRunning)
            {
                _clockBaseFrame = _frameIndex;
                _streamWatch.Restart();
            }

            UpdateAdaptiveQuality();

            // Music mode transmits continuously; gating it would chop sustained content.
            var voice = !_muted && (_options.IsMusic || HasVoiceActivity(pcm));
            if (voice)
                _voiceHangover = VoiceHangoverFrames;
            else if (_voiceHangover > 0)
                _voiceHangover--;

            if (voice || _voiceHangover > 0)
            {
                // Reused: the sender loop is the only thread here and the result is copied out
                // immediately, so a fresh 4 KB buffer 50 times a second was pure garbage.
                var opus = _encodeScratch;
                var encoded = _encoder.Encode(pcm, 0, VoiceAudioEngine.FrameSamplesPerChannel, opus, 0, opus.Length);
                if (encoded <= 0)
                    return;

                var payload = new byte[encoded];
                System.Buffer.BlockCopy(opus, 0, payload, 0, encoded);

                _lastSentOpus = payload;
                _silenceTail = SilenceTailFrames;

                // our own RTP never comes back to us and Discord does not echo our speaking
                // opcode, so the local user is ringed from the frames we are about to send.
                // Marked here rather than in SendSpeakingAsync so the silence frames used to
                // prime the session at connect do not flash the ring.
                MarkSpeaking(_userId, true);

                await SendSpeakingAsync(true).ConfigureAwait(false);
                await TransmitAsync(payload).ConfigureAwait(false);
                await FillClockGapAsync().ConfigureAwait(false);
                return;
            }

            if (_silenceTail > 0)
            {
                _silenceTail--;
                _lastSentOpus = null;
                await TransmitAsync(SilenceFrame).ConfigureAwait(false);
                if (_silenceTail == 0)
                    await SendSpeakingAsync(false).ConfigureAwait(false);
            }
            else
            {
                // Idle. Realign the free-running RTP clock so the next talk spurt starts
                // on time instead of carrying the accumulated capture deficit into it.
                var expected = ExpectedFrames;
                if (expected > _frameIndex)
                    _frameIndex = expected;
            }
        }

        private async Task TransmitAsync(byte[] opus)
        {
            var payload = opus;

            // DAVE never encrypts the Opus silence frame.
            if (_daveProtocolVersion > 0 && !IsOpusSilence(opus))
            {
                var dave = _dave;
                payload = dave?.EncryptOpus(opus);
                if (payload == null)
                {
                    // The frame existed and its 20 ms slot has passed, so the clock has to
                    // move whether or not the bytes went out. Returning without advancing it
                    // left _frameIndex frozen while ExpectedFrames kept running, so
                    // FillClockGapAsync saw a growing deficit, tried to repeat, failed here
                    // again for the same reason, and resynced - measured at 92 resyncs in one
                    // session against sent=7/s with repeats=205. The far end heard almost
                    // nothing, which is the robotic outgoing audio.
                    NextTimestamp();
                    HandleDaveEncryptFailure();
                    return;
                }

                _daveEncryptFailures = 0;
            }

            _sentPackets++;
            await SendRtpPacketAsync(payload, NextTimestamp()).ConfigureAwait(false);
        }

        /// <summary>
        /// Counterpart to <see cref="HandleDaveDecryptFailure"/>, which had no equivalent on
        /// the sending side: an encryptor that stopped working stayed broken for the rest of
        /// the call, because nothing counted the failures and nothing rebuilt the session.
        /// That is why the fault survived until the channel was left and rejoined.
        /// </summary>
        private void HandleDaveEncryptFailure()
        {
            if (_daveReinitializing)
                return;

            lock (_pendingTransitions)
            {
                if (_pendingTransitions.Count > 0)
                    return;
            }

            if (++_daveEncryptFailures <= DaveEncryptFailureTolerance)
                return;

            var transitionId = _daveLastTransitionId;
            if (transitionId == 0)
                return;

            _daveReinitializing = true;
            _daveEncryptFailures = 0;
            Logger.Log("DAVE encrypt failing persistently, rebuilding from transition " + transitionId);
            _ = RecoverDaveSessionAsync(transitionId);
        }

        /// <summary>
        /// The AudioGraph capture callback is not a 50 Hz clock. Measured on real hardware it
        /// delivers about 48 frames/s, dropping to under 40 when the UI thread stalls, because
        /// quanta that arrive while the handler is busy are lost. Pacing RTP off that callback
        /// sends 48 packets/s into a stream the far end plays at 50, so its jitter buffer
        /// drains and gaps out every couple of seconds regardless of how much we buffer.
        ///
        /// Wall clock is the authority. When the capture stream falls behind, the previous
        /// Opus frame is repeated to keep the far end fed: a repeated 20 ms is far less
        /// audible than a dropout, and it stops the deficit accumulating.
        /// </summary>
        private async Task FillClockGapAsync()
        {
            var behind = ExpectedFrames - _frameIndex;
            if (behind <= 0)
                return;

            // A large deficit is not something to transmit. It means capture stalled - the
            // log caught capture dropping to 6 frames a second - and that audio never
            // existed, so replaying the previous frame to cover it floods the far end.
            // Measured at sent=147/s against a target of 50 with repeats=500 a window:
            // three times real time, which the listener hears as speech running seconds
            // late and robotic before it slowly drains. Skip the clock forward instead and
            // accept the gap, which is what actually happened.
            if (behind > MaxClockCatchUpFrames)
            {
                Logger.Log("Voice clock behind by " + behind + " frames, resyncing instead of replaying");
                _frameIndex = ExpectedFrames;
                return;
            }

            var repeat = _lastSentOpus;
            if (repeat == null)
                return;

            for (var i = 0; i < behind; i++)
            {
                _clockRepeats++;
                await TransmitAsync(repeat).ConfigureAwait(false);
            }
        }

        private long ExpectedFrames
            => _clockBaseFrame + (_streamWatch.ElapsedMilliseconds / FrameDurationMs);

        private void LogPacing()
        {
            if (_rateFrames < 250)
                return;

            var elapsed = Math.Max(1, _rateWatch.ElapsedMilliseconds);
            if (DiagnosticLog.Voice.IsEnabled)
            {
                DiagnosticLog.Voice.Log("pacing capture=" + (_rateFrames * 1000 / elapsed) +
                           "/s sent=" + (_sentPackets * 1000 / elapsed) +
                           "/s repeats=" + _clockRepeats +
                           " backlogDrops=" + _backlogDropped + " (target 50/s)");
            }
            _rateFrames = 0;
            _sentPackets = 0;
            _clockRepeats = 0;
            _backlogDropped = 0;
            _rateWatch.Restart();
        }

        private void ConfigureEncoder()
        {
            var music = _options.IsMusic;
            _encoder = new OpusEncoder(
                VoiceAudioEngine.SampleRate,
                VoiceAudioEngine.Channels,
                music ? OpusApplication.OPUS_APPLICATION_AUDIO : OpusApplication.OPUS_APPLICATION_VOIP);

            // Whatever Opus uses for "decide per frame". Captured rather than hardcoded so
            // the stereo lock can be handed back exactly as it was found.
            _defaultForceChannels = _encoder.ForceChannels;
            _stereoLocked = false;

            _targetBitrate = _options.ResolveInitialBitrate();
            _encoder.Bitrate = _targetBitrate;
            _encoder.UseVBR = true;

            if (music)
            {
                _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
                _encoder.Complexity = 10;
                _encoder.UseDTX = false;
                // Inband FEC only exists in SILK; music mode runs CELT, so asking for it
                // would be a no-op that costs bitrate.
                _encoder.UseInbandFEC = false;
                ApplyStereoLock(true);
            }
            else
            {
                _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
                _encoder.UseDTX = true;
                _encoder.UseInbandFEC = _options.ForwardErrorCorrection;
                UpdateStereoLock();
            }

            Logger.Log("Voice encoder " + _options + " starting at " + _targetBitrate + "bps");
        }

        /// <summary>
        /// Applies the user's stereo preference against the current bitrate. Auto never pins
        /// the channel count; Always pins it unconditionally; Prefer pins it only while the
        /// bitrate can carry two channels, because forced stereo on a starved link sounds
        /// worse than the mono Opus would have picked. Hysteresis stops it flapping.
        /// </summary>
        private void UpdateStereoLock()
        {
            if (_options.IsMusic)
                return;

            switch (_options.Stereo)
            {
                case StereoPreference.Always:
                    ApplyStereoLock(true);
                    break;
                case StereoPreference.Prefer:
                    ApplyStereoLock(_stereoLocked
                        ? _targetBitrate >= StereoReleaseBitrate
                        : _targetBitrate >= StereoLockBitrate);
                    break;
            }
        }

        private void ApplyStereoLock(bool locked)
        {
            if (_encoder == null || locked == _stereoLocked)
                return;

            _stereoLocked = locked;
            _encoder.ForceChannels = locked ? VoiceAudioEngine.Channels : _defaultForceChannels;
            Logger.Log("Voice stereo " + (locked ? "locked" : "released") + " at " + _targetBitrate + "bps");
        }

        /// <summary>
        /// Re-targets the encoder from measured loss. The voice gateway sends no RTCP
        /// receiver reports, so inbound loss is used as a proxy for the outbound path.
        /// Runs on the sender loop only; <see cref="_encoder"/> has no other writer.
        /// </summary>
        private void UpdateAdaptiveQuality()
        {
            if (_encoder == null || _qualityWatch.ElapsedMilliseconds < 2000)
                return;

            var received = Interlocked.Exchange(ref _packetsReceived, 0);
            var lost = Interlocked.Exchange(ref _packetsLost, 0);
            var dropped = Interlocked.Exchange(ref _packetsDropped, 0);
            _qualityWatch.Restart();

            var total = received + lost;
            if (total <= 0)
                return;

            var lossPercent = (int)(lost * 100 / total);
            var noMapping = Interlocked.Exchange(ref _droppedNoMapping, 0);
            var daveFailed = Interlocked.Exchange(ref _droppedDaveFailed, 0);
            if (dropped > 0)
                Logger.Log("Voice discarded " + dropped + " packets (no ssrc mapping " + noMapping +
                           ", dave failed " + daveFailed + ", other " + (dropped - noMapping - daveFailed) + ")");

            if (_encoder.UseInbandFEC)
                _encoder.PacketLossPercent = Math.Min(lossPercent, 30);

            if (!_options.AdaptiveBitrate)
                return;

            var bitrate = _targetBitrate;
            if (lossPercent >= 5)
                bitrate -= bitrate / 4;
            else if (lossPercent <= 1)
                bitrate += bitrate / 8;

            bitrate = VoiceQualityOptions.Clamp(bitrate, _options.MinBitrate, _options.MaxBitrate);
            if (bitrate == _targetBitrate)
                return;

            _targetBitrate = bitrate;
            _encoder.Bitrate = bitrate;
            Logger.Log("Voice bitrate " + bitrate + "bps (inbound loss " + lossPercent + "%)");
            UpdateStereoLock();
        }

        private uint NextTimestamp()
        {
            var index = _frameIndex++;
            return unchecked(_timestampBase + (uint)(index * VoiceAudioEngine.FrameSamplesPerChannel));
        }

        private async Task SendRtpPacketAsync(byte[] opus, uint timestamp)
        {
            if (_aes == null)
                return;

            _sequence++;
            _nonce++;

            var header = new byte[RtpHeaderSize];
            header[0] = 0x80;
            header[1] = OpusPayloadType;
            WriteUInt16BigEndian(header, 2, _sequence);
            WriteUInt32BigEndian(header, 4, timestamp);
            WriteUInt32BigEndian(header, 8, _ssrc);

            var nonce = new byte[VoiceAesGcm.NonceSize];
            WriteUInt32BigEndian(nonce, 0, _nonce);

            var encrypted = _aes.Encrypt(opus, nonce, header);
            var packet = new byte[header.Length + encrypted.Length + VoiceAesGcm.SuffixSize];
            System.Buffer.BlockCopy(header, 0, packet, 0, header.Length);
            System.Buffer.BlockCopy(encrypted, 0, packet, header.Length, encrypted.Length);
            System.Buffer.BlockCopy(nonce, 0, packet, packet.Length - VoiceAesGcm.SuffixSize, VoiceAesGcm.SuffixSize);

            await SendUdpAsync(packet).ConfigureAwait(false);
        }

        private void HandleIncomingVoice(byte[] packet)
        {
            if (_aes == null || _deafened || packet.Length < RtpHeaderSize + VoiceAesGcm.TagSize + VoiceAesGcm.SuffixSize)
                return;

            if ((packet[0] >> 6) != 2)
                return;

            if ((packet[1] & 0x7F) != OpusPayloadType)
                return;

            var hasExtension = ((packet[0] >> 4) & 1) == 1;
            var headerSize = RtpHeaderSize + (hasExtension ? 4 : 0);

            if (packet.Length < headerSize + VoiceAesGcm.TagSize + VoiceAesGcm.SuffixSize)
                return;

            var ssrc = ReadUInt32BigEndian(packet, 8);
            if (ssrc == _ssrc)
                return;

            // Sequence accounting happens here, before any path that can discard the
            // packet. Doing it after the decrypt/DAVE drops left _lastSequence stale, so
            // the next good packet reported a huge gap: that inflated "inbound loss" to
            // 89%, drove the adaptive bitrate up and down for no reason, and injected
            // packet-loss concealment for audio that was never lost - which is itself
            // audible. Gaps measured here are real wire loss.
            var sequence = ReadUInt16BigEndian(packet, 2);
            var conceal = 0;
            if (_lastSequence.TryGetValue(ssrc, out var previous))
            {
                // 16-bit wrapping comparison; a non-positive delta is a duplicate or a
                // packet that arrived after we already played past it.
                var delta = (short)(sequence - (ushort)previous);
                if (delta <= 0)
                    return;

                // Opus packet-loss concealment extrapolates from the previous frame. It is
                // convincing for one or two frames and turns into a robotic warble beyond
                // that, so a larger gap is treated as a discontinuity and left silent
                // rather than filled with 100ms of synthetic audio. A big gap is usually a
                // talk-spurt boundary anyway, not loss worth hiding.
                var missing = delta - 1;
                conceal = missing <= MaxConcealedFrames ? missing : 0;
                Interlocked.Add(ref _packetsLost, missing);
            }

            Interlocked.Increment(ref _packetsReceived);
            _lastSequence[ssrc] = sequence;

            var nonce = new byte[VoiceAesGcm.NonceSize];
            System.Buffer.BlockCopy(packet, packet.Length - VoiceAesGcm.SuffixSize, nonce, 0, VoiceAesGcm.SuffixSize);

            var aad = new byte[headerSize];
            System.Buffer.BlockCopy(packet, 0, aad, 0, headerSize);
            var cipher = new byte[packet.Length - headerSize - VoiceAesGcm.SuffixSize];
            System.Buffer.BlockCopy(packet, headerSize, cipher, 0, cipher.Length);

            byte[] opus;
            try
            {
                opus = _aes.Decrypt(cipher, nonce, aad);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                Interlocked.Increment(ref _packetsDropped);
                return;
            }

            opus = StripRtpPadding(packet, opus);
            opus = StripHeaderExtension(packet, hasExtension, opus);
            if (opus == null || opus.Length == 0)
            {
                Interlocked.Increment(ref _packetsDropped);
                return;
            }

            opus = DecryptDave(ssrc, opus);
            if (opus == null || opus.Length == 0)
            {
                Interlocked.Increment(ref _packetsDropped);
                return;
            }

            // audio arriving is the reliable "is talking" signal; the silence frame a client sends
            // when it stops is not audio
            if (_ssrcToUser.TryGetValue(ssrc, out var speakerId))
                MarkSpeaking(speakerId, !IsSilenceFrame(opus));

            try
            {
                var decoder = _decoders.GetOrAdd(ssrc, _ => new OpusDecoder(VoiceAudioEngine.SampleRate, VoiceAudioEngine.Channels));

                // Opus packet-loss concealment. Without it a dropped packet leaves a hole
                // that the mixer plays as silence, which is the audible "cut".
                // Pooled. A decoded frame lives in the jitter queue for 200 to 500 ms, which
                // is long enough to survive gen0 and gen1 and be promoted into gen2; at this
                // packet rate that promoted roughly a megabyte a second and drove the gen2
                // collections that suspend the AudioGraph thread. EnqueuePlayback takes
                // ownership and returns the buffer to the pool once it has been played.
                var engine = _audio;
                for (var i = 0; i < conceal; i++)
                    DecodeInto(decoder, ssrc, engine, null);

                DecodeInto(decoder, ssrc, engine, opus);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        /// <summary>
        /// Decodes one packet, or one concealment frame when <paramref name="opus"/> is null,
        /// and hands the result to playback. The buffer is returned to the pool on every
        /// path.
        /// </summary>
        /// <remarks>
        /// The rent and the return used to sit either side of the decode with nothing between
        /// them but the happy path, so a decoder exception leaked the buffer. Opus throws on a
        /// payload that is not a valid frame, which happens for as long as the DAVE session is
        /// not ready and undecryptable audio is passed through, and it happens at packet rate:
        /// one trace leaked the pool empty inside a single window, which then fell back to
        /// allocating - poolMiss=316 against gc=311/153/138 in the same window, an allocation
        /// storm feeding the collections that stall the audio clock.
        /// </remarks>
        private void DecodeInto(OpusDecoder decoder, uint ssrc, VoiceAudioEngine engine, byte[] opus)
        {
            var pcm = VoicePcmPool.Rent();
            var handedOver = false;
            try
            {
                var decoded = opus == null
                    ? decoder.Decode(null, 0, 0, pcm, 0, VoiceAudioEngine.FrameSamplesPerChannel, false)
                    : decoder.Decode(opus, 0, opus.Length, pcm, 0, VoiceAudioEngine.FrameSamplesPerChannel, false);

                if (decoded > 0 && engine != null)
                {
                    engine.EnqueuePlayback(ssrc, pcm, decoded * VoiceAudioEngine.Channels);
                    handedOver = true;
                }

                _decodeFailures = 0;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _packetsDropped);

                // LogError records only the member, file and line, so a decoder fault was
                // indistinguishable from any other exception in this method. The type and
                // message are what say whether the payload was undecryptable or malformed.
                if (++_decodeFailures == 1 || _decodeFailures % DecodeFailureLogInterval == 0)
                    Logger.Log("Voice decode failed (" + _decodeFailures + " consecutive): " +
                               ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (!handedOver)
                    VoicePcmPool.Return(pcm);
            }
        }

        private static byte[] StripRtpPadding(byte[] packet, byte[] plaintext)
        {
            if ((packet[0] & 0x20) == 0 || plaintext == null || plaintext.Length == 0)
                return plaintext;

            var padding = plaintext[plaintext.Length - 1];
            if (padding == 0 || padding >= plaintext.Length)
                return plaintext;

            var trimmed = new byte[plaintext.Length - padding];
            System.Buffer.BlockCopy(plaintext, 0, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        /// <summary>
        /// The <c>_rtpsize</c> AEAD modes only authenticate the fixed RTP header plus the
        /// 0xBEDE profile word. The extension body itself stays inside the ciphertext and
        /// is not part of the Opus payload, so it has to be removed after decryption.
        /// Discord clients set this extension; bots generally do not, which is why only
        /// bot audio decoded before this was handled.
        /// </summary>
        private static byte[] StripHeaderExtension(byte[] packet, bool hasExtension, byte[] plaintext)
        {
            if (!hasExtension || plaintext == null || packet.Length < RtpHeaderSize + 4)
                return plaintext;

            if (packet[12] != 0xBE || packet[13] != 0xDE)
                return plaintext;

            var words = (packet[14] << 8) | packet[15];
            var skip = words * 4;
            if (skip <= 0 || skip > plaintext.Length)
                return plaintext;

            return Slice(plaintext, skip);
        }

        private byte[] DecryptDave(uint ssrc, byte[] opus)
        {
            var dave = _dave;
            if (dave == null || IsOpusSilence(opus))
                return opus;

            if (!_ssrcToUser.TryGetValue(ssrc, out var speakerId))
            {
                if (_daveProtocolVersion == 0)
                    return opus;

                // No SSRC to user mapping yet. Only opcode 5 Speaking carries it, so every
                // packet from a member who has not spoken since we joined lands here.
                Interlocked.Increment(ref _droppedNoMapping);
                return null;
            }

            // While MLS is still converging the voice server keeps forwarding plaintext
            // from clients outside the group. Passing those through matches the reference
            // client instead of dropping every packet until the session is ready.
            if (!dave.IsReady)
                return opus;

            var decrypted = dave.Decrypt(speakerId, opus);
            if (decrypted != null && decrypted.Length > 0)
            {
                _daveFailuresByUser[speakerId] = 0;
                return decrypted;
            }

            if (_daveProtocolVersion == 0)
                return opus;

            Interlocked.Increment(ref _droppedDaveFailed);
            HandleDaveDecryptFailure(speakerId);
            return null;
        }

        /// <summary>
        /// A few decrypt failures around an epoch change are normal. Sustained failure means
        /// our MLS state is out of sync with the group, and nothing recovers it on its own:
        /// davey returns NoDecryptorForUser for a speaker who is not in the group we think we
        /// are in, so that person stays silent for the rest of the call. One log showed 66
        /// consecutive failures in two seconds, well over a second of speech dropped.
        ///
        /// The reference client counts consecutive failures and, past its tolerance,
        /// invalidates the last transition and rebuilds the session. This mirrors that.
        /// Guarded by the reinitialising flag and the pending-transition set so it cannot
        /// loop, and it only ever runs in response to a real protocol failure.
        /// </summary>
        private void HandleDaveDecryptFailure(ulong speakerId)
        {
            if (_daveReinitializing)
                return;

            lock (_pendingTransitions)
            {
                if (_pendingTransitions.Count > 0)
                    return;
            }

            // Counted per speaker. davey keeps a decryptor per user and fails with
            // NoDecryptorForUser, so one participant's key material can be broken while
            // everyone else decrypts normally. A single shared counter was reset by any
            // success from any user, so the healthy streams kept clearing the broken one's
            // failures and recovery never fired for them - that person stayed silent for the
            // whole call, which is indistinguishable from their microphone being off.
            var failures = _daveFailuresByUser.AddOrUpdate(speakerId, 1, (_, count) => count + 1);
            if (failures <= DaveDecryptFailureTolerance)
                return;

            var transitionId = _daveLastTransitionId;
            if (transitionId == 0)
                return;

            _daveReinitializing = true;
            _daveFailuresByUser.Clear();
            Logger.Log("DAVE decrypt failing persistently for user " + speakerId +
                       ", rebuilding from transition " + transitionId);
            _ = RecoverDaveSessionAsync(transitionId);
        }

        private async Task RecoverDaveSessionAsync(int transitionId)
        {
            try
            {
                await SendJsonAsync(new VoiceDispatch { OpCode = 31, Payload = new { transition_id = transitionId } });
                if (_daveProtocolVersion > 0)
                    InitializeDave(_daveProtocolVersion);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        private static bool IsOpusSilence(byte[] opus)
            => opus != null && opus.Length == 3 && opus[0] == 0xF8 && opus[1] == 0xFF && opus[2] == 0xFE;

        private void InitializeDave(ushort protocolVersion)
        {
            _dave?.Dispose();
            _dave = new DaveNative(protocolVersion, _userId, _channelId);
            if (_pendingExternalSender != null)
            {
                _dave.SetExternalSender(_pendingExternalSender);
                _pendingExternalSender = null;
            }

            var keyPackage = _dave.CreateKeyPackage();
            _ = SendBinaryAsync(26, keyPackage);
            Logger.Log("DAVE session started v" + protocolVersion + ", sent key package (" + keyPackage.Length + " bytes)");
        }

        private void HandleSpeaking(JObject data)
        {
            if (data?["ssrc"] == null)
                return;

            var ssrc = data.Value<uint>("ssrc");
            if (data["user_id"] != null && ulong.TryParse((string)data["user_id"], out var userId) && userId != 0)
            {
                _ssrcToUser[ssrc] = userId;

                // op 5 carries the ssrc mapping and is honoured for stopping a ring, never for
                // starting one. Joining a channel replays this opcode for every member already
                // present, purely to hand over their mappings, and each one carries a speaking
                // flag - so taking that flag at face value lit the ring for almost everybody
                // the moment the channel was opened.
                //
                // Arriving audio is the reliable signal and already refreshes this state, with
                // a hangover to expire it. A speaker whose audio cannot be decoded no longer
                // shows a ring, which is the honest outcome: they cannot be heard either.
                var speaking = data["speaking"]?.Value<uint>() ?? 0;
                if (speaking == 0)
                    MarkSpeaking(userId, false);
            }
        }

        /// <summary>
        /// Tracks who is talking. Purely local bookkeeping off packets that already arrive; nothing
        /// is sent or requested for it.
        /// </summary>
        private void MarkSpeaking(ulong userId, bool speaking)
        {
            if (userId == 0)
                return;

            if (speaking)
            {
                _speakingUntil[userId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + SpeakingHangoverMs;

                bool started;
                lock (_speakingUsers)
                    started = _speakingUsers.Add(userId);

                if (started)
                    SpeakingChanged?.Invoke(this, new VoiceSpeakingEventArgs(userId, true));

                return;
            }

            _speakingUntil.TryRemove(userId, out _);

            bool stopped;
            lock (_speakingUsers)
                stopped = _speakingUsers.Remove(userId);

            if (stopped)
                SpeakingChanged?.Invoke(this, new VoiceSpeakingEventArgs(userId, false));
        }

        private void ExpireSpeaking()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var pair in _speakingUntil)
            {
                if (pair.Value <= now)
                    MarkSpeaking(pair.Key, false);
            }
        }

        private static bool IsSilenceFrame(byte[] opus)
            => opus.Length == SilenceFrame.Length &&
               opus[0] == SilenceFrame[0] &&
               opus[1] == SilenceFrame[1] &&
               opus[2] == SilenceFrame[2];

        private void HandleClientsConnect(JObject data)
        {
            var ids = data?["user_ids"] as JArray;
            if (ids == null)
                return;
            lock (_connectedClients)
            {
                foreach (var id in ids)
                {
                    if (ulong.TryParse((string)id, out var userId))
                        _connectedClients.Add(userId);
                }
            }
        }

        private async Task HandleDavePrepareTransitionAsync(JObject data)
        {
            var transitionId = data.Value<int>("transition_id");
            var version = (ushort)(data.Value<int?>("protocol_version") ?? _daveProtocolVersion);
            Logger.Log("DAVE prepare transition " + transitionId + " v" + version);

            lock (_pendingTransitions) { _pendingTransitions[transitionId] = version; }

            if (transitionId == 0)
            {
                ExecuteDaveTransition(transitionId);
                return;
            }

            // A pending downgrade still has to accept plaintext from clients that
            // already left the MLS group.
            if (version == 0)
                _dave?.SetPassthrough(true, DowngradeTransitionExpiry);

            await SendJsonAsync(new VoiceDispatch { OpCode = 23, Payload = new { transition_id = transitionId } });
        }

        private void ExecuteDaveTransition(int transitionId)
        {
            ushort version;
            lock (_pendingTransitions)
            {
                if (!_pendingTransitions.TryGetValue(transitionId, out version))
                {
                    Logger.Log("DAVE execute transition " + transitionId + " with no pending transition");
                    return;
                }

                _pendingTransitions.Remove(transitionId);
            }

            var previous = _daveProtocolVersion;
            _daveProtocolVersion = version;

            if (previous != version && version == 0)
            {
                _daveDowngraded = true;
            }
            else if (transitionId > 0 && _daveDowngraded)
            {
                _daveDowngraded = false;
                _dave?.SetPassthrough(true, TransitionExpiry);
            }

            // Back in sync: the group moved to a state we agreed to.
            _daveReinitializing = false;
            _daveFailuresByUser.Clear();
            _daveLastTransitionId = transitionId;

            Logger.Log("DAVE transition " + transitionId + " executed (v" + previous + " -> v" + version + ")");
        }

        private void HandleDavePrepareEpoch(JObject data)
        {
            var epoch = data.Value<int>("epoch");
            var version = (ushort)(data.Value<int?>("protocol_version") ?? _daveProtocolVersion);
            Logger.Log("DAVE prepare epoch " + epoch + " v" + version);

            if (epoch != 1)
                return;

            _daveProtocolVersion = version;
            if (version > 0)
                InitializeDave(version);
        }

        private async Task HandleBinaryAsync(byte[] data)
        {
            if (data == null || data.Length < 3)
                return;

            _wsSequence = (data[0] << 8) | data[1];
            var op = data[2];
            var payload = new byte[data.Length - 3];
            if (payload.Length > 0)
                System.Buffer.BlockCopy(data, 3, payload, 0, payload.Length);

            Logger.Log("Voice WS binary op=" + op + " len=" + payload.Length);
            switch (op)
            {
                case 25:
                    try
                    {
                        if (_dave != null)
                            _dave.SetExternalSender(payload);
                        else
                            _pendingExternalSender = payload;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex);
                    }
                    break;
                case 27:
                    if (_dave == null || payload.Length < 1)
                        break;
                    try
                    {
                        ulong[] users;
                        lock (_connectedClients) { users = new List<ulong>(_connectedClients).ToArray(); }
                        var commitWelcome = _dave.ProcessProposals(payload[0], Slice(payload, 1), users);
                        if (commitWelcome != null && commitWelcome.Length > 0)
                            await SendBinaryAsync(28, commitWelcome);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex);
                    }
                    break;
                case 29:
                    await HandleDaveCommitOrWelcomeAsync(payload, commit: true);
                    break;
                case 30:
                    await HandleDaveCommitOrWelcomeAsync(payload, commit: false);
                    break;
            }
        }

        private async Task HandleDaveCommitOrWelcomeAsync(byte[] payload, bool commit)
        {
            var dave = _dave;
            if (dave == null || payload.Length < 2)
                return;

            var transitionId = (payload[0] << 8) | payload[1];
            var body = Slice(payload, 2);
            var ok = commit ? dave.ProcessCommit(body) : dave.ProcessWelcome(body);

            if (!ok)
            {
                await SendJsonAsync(new VoiceDispatch { OpCode = 31, Payload = new { transition_id = transitionId } });
                if (_daveProtocolVersion > 0)
                    InitializeDave(_daveProtocolVersion);
                return;
            }

            Logger.Log("DAVE " + (commit ? "commit" : "welcome") + " processed (transition " + transitionId + ")");
            if (transitionId == 0)
            {
                _daveReinitializing = false;
                _daveFailuresByUser.Clear();
                _daveLastTransitionId = 0;
                return;
            }

            // The new keys only become live once the matching execute transition
            // arrives, so remember which version this id will move us to.
            lock (_pendingTransitions) { _pendingTransitions[transitionId] = _daveProtocolVersion; }
            await SendJsonAsync(new VoiceDispatch { OpCode = 23, Payload = new { transition_id = transitionId } });
        }

        private static byte[] Slice(byte[] source, int offset)
        {
            if (source == null || offset >= source.Length)
                return Array.Empty<byte>();
            var result = new byte[source.Length - offset];
            System.Buffer.BlockCopy(source, offset, result, 0, result.Length);
            return result;
        }

        private async Task SendHeartbeatAsync()
        {
            try
            {
                await SendJsonAsync(new VoiceDispatch
                {
                    OpCode = 3,
                    Payload = new
                    {
                        t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        seq_ack = _wsSequence
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        /// <summary>
        /// Voice WS v8 echoes the heartbeat's own <c>t</c> back in the ACK, so the round trip is
        /// the difference against the clock now. Older payloads send the nonce bare rather than
        /// wrapped in an object, so both shapes are accepted.
        /// </summary>
        private void HandleHeartbeatAck(JToken data)
        {
            var sent = data is JObject obj ? obj["t"]?.Value<long>() : data?.Value<long>();
            if (sent == null || sent <= 0)
                return;

            WebSocketPing = (uint)Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sent.Value);
            WebSocketPingUpdated?.Invoke(this, WebSocketPing);
        }

        private async Task SendKeepaliveAsync()
        {
            try
            {
                var count = _keepalive++;
                _keepaliveTimestamps[count] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var packet = new byte[8];
                WriteUInt64LittleEndian(packet, 0, count);
                await SendUdpAsync(packet);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        private async Task SendSpeakingAsync(bool speaking)
        {
            if (_speaking == speaking)
                return;

            _speaking = speaking;
            await SendJsonAsync(new VoiceDispatch
            {
                OpCode = 5,
                Payload = new
                {
                    speaking = speaking ? 1 : 0,
                    delay = 0,
                    ssrc = _ssrc
                }
            });
        }

        private async Task SendUdpAsync(byte[] packet)
        {
            if (_udpWriter == null || _disposed)
                return;

            await _udpSendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var writer = _udpWriter;
                if (writer == null || _disposed)
                    return;

                writer.WriteBytes(packet);
                await writer.StoreAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
            finally
            {
                _udpSendLock.Release();
            }
        }

        private Task SendJsonAsync(VoiceDispatch payload)
        {
            var json = JsonConvert.SerializeObject(payload);
            Logger.Log("Voice WS >> " + json);
            return SendWebSocketAsync(SocketMessageType.Utf8, null, json);
        }

        private Task SendBinaryAsync(byte opcode, byte[] payload)
        {
            var packet = new byte[1 + (payload?.Length ?? 0)];
            packet[0] = opcode;
            if (payload != null && payload.Length > 0)
                System.Buffer.BlockCopy(payload, 0, packet, 1, payload.Length);

            Logger.Log("Voice WS >> binary op=" + opcode + " len=" + (payload?.Length ?? 0));
            return SendWebSocketAsync(SocketMessageType.Binary, packet, null);
        }

        /// <summary>
        /// MessageWebSocket carries a single MessageType for the whole socket, so text and
        /// binary sends must be fully serialised. Flipping the type while another store is
        /// in flight sends MLS key packages as text frames, which Discord discards, and the
        /// DAVE group then never completes.
        /// </summary>
        private async Task SendWebSocketAsync(SocketMessageType type, byte[] binary, string text)
        {
            if (_webSocket == null || _disposed)
                return;

            await _wsSendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var socket = _webSocket;
                if (socket == null || _disposed)
                    return;

                socket.Control.MessageType = type;
                var writer = new DataWriter(socket.OutputStream);
                try
                {
                    if (type == SocketMessageType.Binary)
                        writer.WriteBytes(binary);
                    else
                        writer.WriteString(text);

                    await writer.StoreAsync();
                }
                finally
                {
                    writer.DetachStream();
                    writer.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                FaultSession("websocket send failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _wsSendLock.Release();
            }
        }

        /// <summary>
        /// Ends the session after a failure that leaves the websocket unusable.
        /// </summary>
        /// <remarks>
        /// A send failure used to be logged and swallowed, so a socket that died without a
        /// close frame left the session believing it was still connected. Heartbeats stopped
        /// reaching Discord, Discord stopped relaying audio, and the client went on capturing
        /// into a socket that was gone - silent, with nothing in the UI to say so, and
        /// recoverable only by leaving the channel and rejoining. One trace ends exactly
        /// there: a heartbeat send throws and the session simply continues.
        ///
        /// Signalled once. Disconnected is what the connection model already listens to for
        /// an external teardown, and it holds a single connect gate, so this cannot turn into
        /// repeated reconnect attempts.
        /// </remarks>
        private void FaultSession(string reason)
        {
            if (_disposed || Interlocked.Exchange(ref _faulted, 1) == 1)
                return;

            Logger.Log("Voice session faulted, ending: " + reason);
            _ready.TrySetException(new InvalidOperationException("Voice session faulted: " + reason));
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        private static void WriteUInt16BigEndian(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteUInt64LittleEndian(byte[] buffer, int offset, ulong value)
        {
            for (var i = 0; i < 8; i++)
                buffer[offset + i] = (byte)(value >> (8 * i));
        }

        private static ushort ReadUInt16BigEndian(byte[] buffer, int offset)
            => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

        private static uint ReadUInt32BigEndian(byte[] buffer, int offset)
            => ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

        private static ulong ReadUInt64LittleEndian(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++)
                value |= (ulong)buffer[offset + i] << (8 * i);
            return value;
        }

        private bool HasVoiceActivity(short[] pcm)
        {
            long sum = 0;
            for (var i = 0; i < pcm.Length; i++)
            {
                var sample = pcm[i];
                sum += sample * sample;
            }

            var rms = Math.Sqrt(sum / (double)pcm.Length);

            // A single hard threshold clips the quiet tail of every word, which the
            // other side hears as chopped, robotic speech. Latch on loud, release quiet.
            _voiceActive = _voiceActive ? rms > VoiceReleaseRms : rms > VoiceAttackRms;
            return _voiceActive;
        }

        private void OnWsClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            CloseCode = (ushort)args.Code;
            Logger.Log("Voice WS closed " + args.Code + " " + args.Reason);
            _ready.TrySetException(new InvalidOperationException("Voice websocket closed: " + args.Code + " " + args.Reason));
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                _senderCts?.Cancel();
                _heartbeatTimer?.Cancel();
                _keepaliveTimer?.Cancel();
                _speakingTimer?.Cancel();
                if (_audio != null)
                    _audio.PcmCaptured -= OnPcmCaptured;
                _audio?.Dispose();
                _dave?.Dispose();
                _aes?.Dispose();
                _udpWriter?.DetachStream();
                _udpWriter?.Dispose();
                _udpSocket?.Dispose();
                _webSocket?.Dispose();
                // The semaphores and the CTS are deliberately not disposed: the sender
                // loop may still be parked on them, and disposing here turns a clean
                // shutdown into an ObjectDisposedException storm.
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }
    }
}
