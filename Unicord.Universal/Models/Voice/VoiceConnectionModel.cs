using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Net.Abstractions;
using Microsoft.AppCenter.Analytics;
using Unicord.Universal.Services;
using Unicord.Universal.Voice;
using Unicord.Universal.Voice.Transport;
using Windows.ApplicationModel.Calls;
using Windows.ApplicationModel.Resources;

namespace Unicord.Universal.Models.Voice
{
    public class VoiceConnectionModel : ViewModelBase
    {
        private DiscordVoiceSession _session;
        private VoipCallCoordinator _voipCallCoordinator;
        private VoipPhoneCall _voipCall;
        private ResourceLoader _strings;
        private VoiceState _state = VoiceState.None;
        private TaskCompletionSource<bool> _credentialsReady;
        private TaskCompletionSource<bool> _leftChannel;
        private string _sessionId;
        private string _endpoint;
        private string _token;
        private int _credentialVersion;
        private bool _gatewaySubscribed;
        private bool _handshakeComplete;
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private int _connectState;
        private bool _doNotReconnect;
        private bool _disposed;

        private const ushort VoiceCloseCodeDisconnected = 4014;
        private const ushort VoiceCloseCodeCallTerminated = 4022;

        /// <summary>
        /// The voice server ended this session for good: kicked, moved, the channel went away,
        /// or another client disconnected us. The model cannot be reused, and navigation must
        /// build a fresh one rather than treating a click as "already in this channel".
        /// </summary>
        public bool IsTerminated { get; private set; }
        private uint _webSocketPing;
        private uint _udpPing;
        private string _connectionStatus;

        public string ConnectionStatus { get => _connectionStatus; set => OnPropertySet(ref _connectionStatus, value); }
        public DiscordChannel Channel { get; }

        /// <summary>
        /// Where the call is, shown under the connection status. Group and direct calls have no
        /// guild, so they show the channel alone.
        /// </summary>
        public string ConnectionLocation
            => Channel?.Guild != null ? $"{Channel.Name} / {Channel.Guild.Name}" : Channel?.Name;

        public bool Muted
        {
            get => _state.HasFlag(VoiceState.Muted);
            set
            {
                var next = value ? _state | VoiceState.Muted : _state & ~VoiceState.Muted;
                OnPropertySet(ref _state, next);
            }
        }

        public bool Deafened
        {
            get => _state.HasFlag(VoiceState.Deafened);
            set
            {
                var next = value ? _state | VoiceState.Deafened : _state & ~VoiceState.Deafened;
                OnPropertySet(ref _state, next, nameof(Deafened), nameof(Muted));
            }
        }

        public uint WebSocketPing { get => _webSocketPing; set => OnPropertySet(ref _webSocketPing, value, nameof(WebSocketPing), nameof(Ping)); }
        public uint UdpPing { get => _udpPing; set => OnPropertySet(ref _udpPing, value, nameof(UdpPing), nameof(Ping)); }

        /// <summary>
        /// What the signal indicator shows. The UDP round trip is the honest number, but the voice
        /// server only echoes a keepalive every few seconds, so the gateway heartbeat round trip
        /// stands in until the first echo lands.
        /// </summary>
        public uint Ping
            => _udpPing > 0 ? _udpPing : _webSocketPing;
        public event EventHandler<EventArgs> Disconnected;

        /// <summary>
        /// If voice is connected before Unicord launched, returns a VoiceConnectionModel for 
        /// that connection, otherwise null.
        /// </summary>
        public static Task<VoiceConnectionModel> FindExistingConnectionAsync()
        {
            return Task.FromResult<VoiceConnectionModel>(null);
        }

        public VoiceConnectionModel(DiscordChannel channel)
        {
            _credentialsReady = CreateCompletion();
            _voipCallCoordinator = VoipCallCoordinator.GetDefault();
            _strings = ResourceLoader.GetForViewIndependentUse("Voice");

            Channel = channel;
            ConnectionStatus = _strings.GetString("InitialConnectionState");
            PropertyChanged += OnPropertyChanged;
        }

        private async void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Muted))
            {
                await ToggleMuteAsync();
            }

            if (e.PropertyName == nameof(Deafened))
            {
                await ToggleDeafenAsync();
            }
        }

        public Task UpdatePreferredAudioDevicesAsync(string audioRender, string audioCapture)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// True once a join is under way or established. A second click must not start a
        /// second voice session: two identifies against one voice session is exactly what
        /// Discord answers with 4006, and duplicate signalling is what we must never send.
        /// </summary>
        public bool IsConnecting => Volatile.Read(ref _connectState) != 0;

        public async Task ConnectAsync()
        {
            if (Interlocked.CompareExchange(ref _connectState, 1, 0) != 0)
            {
                Logger.Log("Voice connect ignored: already connecting or connected");
                return;
            }

            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await ConnectCoreAsync().ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref _connectState, 0);
                throw;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private async Task ConnectCoreAsync()
        {
            Analytics.TrackEvent("VoiceConnection_Connect");

            ConnectionStatus = _strings.GetString("ConnectionState1");

            // Every stage is timed. The join was measured taking over six seconds before
            // Discord's Voice Server Update even arrived, and none of this was instrumented,
            // so the cost could only be guessed at.
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Windows call registration is unrelated to the Discord voice session and was
            // measured at 5.8 seconds on a cold start against 326ms for Discord's entire
            // part of the join, so it does not gate the connection at all. It registers in
            // the background and reports itself active if it lands after we are up. The
            // second join in a session is fast because reservation then returns
            // ERROR_ALREADY_EXISTS immediately.
            _ = RegisterSystemCallAsync(watch);

            ConnectionStatus = _strings.GetString("ConnectionState3");

            SubscribeGateway();
            await LeaveExistingVoiceAsync().ConfigureAwait(false);
            Logger.Log("Voice join: leave check " + watch.ElapsedMilliseconds + "ms");

            _sessionId = null;
            _endpoint = null;
            _token = null;
            _credentialsReady = CreateCompletion();

            var signalled = watch.ElapsedMilliseconds;
            await SendVoiceStateUpdateAsync(_state, Channel.Id);
            Logger.Log("Voice join: signalled at " + watch.ElapsedMilliseconds + "ms");

            if (await Task.WhenAny(_credentialsReady.Task, Task.Delay(15000)).ConfigureAwait(false) != _credentialsReady.Task)
            {
                await ForceLeaveAsync().ConfigureAwait(false);
                throw new TimeoutException("Timed out waiting for Discord voice server credentials.");
            }

            Logger.Log("Voice join: credentials after " + (watch.ElapsedMilliseconds - signalled) +
                       "ms, total " + watch.ElapsedMilliseconds + "ms");


            ConnectionStatus = string.Format(_strings.GetString("ConnectionState4Format"), Channel.Name);

            Exception lastError = null;
            var connectedVersion = 0;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var version = Volatile.Read(ref _credentialVersion);
                connectedVersion = version;
                DisposeSession();
                try
                {
                    _session = CreateSession();
                    await _session.ConnectAsync().ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Logger.LogError(ex);
                    DisposeSession();
                    if (Volatile.Read(ref _credentialVersion) != version && attempt < 2)
                        continue;
                    break;
                }
            }

            if (lastError != null)
            {
                await ForceLeaveAsync().ConfigureAwait(false);
                throw lastError;
            }

            _handshakeComplete = true;
            _voipCall?.NotifyCallActive();

            // member rows dim our own row when the account is in the call from somewhere else, so
            // they need to know which channel this client is actually in
            VoiceSessionTracker.Set(Channel.Id);

            ConnectionStatus = string.Format(_strings.GetString("ConnectedStateFormat"), Channel.Name);

            // A Voice Server Update that landed while this attempt was in flight was skipped
            // so two sessions could not race. Skipping it outright left us identified against
            // a token Discord had already rotated, which closes as 4006 and looks exactly
            // like the rejoin loop in the log. Apply the newest credentials once, here, where
            // nothing else is connecting.
            var latest = Volatile.Read(ref _credentialVersion);
            if (latest != connectedVersion && !_doNotReconnect && !_disposed)
            {
                Logger.Log("Voice credentials rotated during connect, reconfiguring once");
                _ = ReconfigureNetworkingAsync(latest);
            }
        }

        private async Task RegisterSystemCallAsync(System.Diagnostics.Stopwatch watch)
        {
            try
            {
                var status = await ReserveCallResourcesAsync().ConfigureAwait(false);
                if (status != VoipPhoneCallResourceReservationStatus.Success)
                {
                    Logger.Log("Voice call resources unavailable: " + status);
                    return;
                }

                _voipCall = _voipCallCoordinator.RequestNewOutgoingCall(
                    "",
                    Channel.Guild != null ? $"{Channel.Name} - {Channel.Guild.Name}" : Channel.Name,
                    "Unicord",
                    VoipPhoneCallMedia.Audio);

                Logger.Log("Voice system call registered at " + watch.ElapsedMilliseconds + "ms");

                if (_handshakeComplete && !_disposed)
                    _voipCall.NotifyCallActive();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        private async Task<VoipPhoneCallResourceReservationStatus> ReserveCallResourcesAsync()
        {
            try
            {
                return await _voipCallCoordinator.ReserveCallResourcesAsync("Unicord.Universal.Voice.Background.VoiceBackgroundTask");
            }
            catch (Exception ex)
            {
                // ERROR_ALREADY_EXISTS
                if (ex.HResult == -2147024713)
                    return VoipPhoneCallResourceReservationStatus.Success;
                else
                    throw;
            }
        }

        private Task ToggleMuteAsync()
        {
            if (_session == null)
                return Task.CompletedTask;

            // Apply locally first and let the gateway update run on its own. Awaiting the
            // round-trip here stalls the caller and shows up as delayed audio.
            _session.Muted = Muted;
            _ = SendVoiceStateUpdateAsync(_state, Channel.Id);
            return Task.CompletedTask;
        }

        private Task ToggleDeafenAsync()
        {
            if (_session == null)
                return Task.CompletedTask;

            _session.Deafened = Deafened;
            _ = SendVoiceStateUpdateAsync(_state, Channel.Id);
            return Task.CompletedTask;
        }

        public async Task DisconnectAsync()
        {
            _disposed = true;
            _doNotReconnect = true;
            _handshakeComplete = false;
            Volatile.Write(ref _connectState, 0);
            DisposeSession();

            try
            {
                _voipCall?.NotifyCallEnded();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

            _voipCall = null;

            // Always signal the leave. This used to call LeaveExistingVoiceAsync, which skips
            // when the current voice state is already this channel - true for every deliberate
            // disconnect - so the leave payload was never sent. Discord kept us in the channel:
            // the disconnect button did nothing, the speaking ring stayed lit, and rejoining
            // hit 4006 because the old voice state was still there.
            await ForceLeaveAsync().ConfigureAwait(false);
            UnsubscribeGateway();
            ConnectionStatus = _strings.GetString("DisconnectedState");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        private async Task SendVoiceStateUpdateAsync(VoiceState state, ulong? channel_id)
        {
            try
            {
                var payload = new VoiceStateUpdatePayload
                {
                    GuildId = Channel.Guild.Id,
                    ChannelId = channel_id,
                    Deafened = channel_id != null ? (bool?)state.HasFlag(VoiceState.Deafened) : null,
                    Muted = channel_id != null ? (bool?)state.HasFlag(VoiceState.Muted) : null
                };

#pragma warning disable CS0618 // Type or member is obsolete
                await DiscordManager.Discord.SendPayloadAsync(GatewayOpCode.VoiceStateUpdate, payload);
#pragma warning restore CS0618 // Type or member is obsolete
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        private void SubscribeGateway()
        {
            if (_gatewaySubscribed)
                return;

            discord.VoiceStateUpdated += OnVoiceStateUpdated;
            discord.VoiceServerUpdated += OnVoiceServerUpdated;
            _gatewaySubscribed = true;
        }

        private void UnsubscribeGateway()
        {
            if (!_gatewaySubscribed)
                return;

            discord.VoiceStateUpdated -= OnVoiceStateUpdated;
            discord.VoiceServerUpdated -= OnVoiceServerUpdated;
            _gatewaySubscribed = false;
        }

        /// <summary>
        /// Clears a voice state left over from a previous channel before joining this one.
        /// Join path only: disconnecting must always signal the leave, so
        /// <see cref="DisconnectAsync"/> calls <see cref="ForceLeaveAsync"/> directly.
        /// </summary>
        private async Task LeaveExistingVoiceAsync()
        {
            if (Channel.Guild == null || discord.CurrentUser == null)
                return;

            if (!Channel.Guild.VoiceStates.TryGetValue(discord.CurrentUser.Id, out var state) ||
                state.ChannelId == null ||
                state.ChannelId == 0)
                return;

            // Already in the channel we are joining. Leaving and rejoining costs a Discord
            // round trip plus the two second wait below, and produces an extra Voice Server
            // Update for no gain.
            if (state.ChannelId == Channel.Id)
                return;

            await ForceLeaveAsync().ConfigureAwait(false);
        }

        private async Task ForceLeaveAsync()
        {
            _leftChannel = CreateCompletion();
            await SendVoiceStateUpdateAsync(VoiceState.None, null);
            await Task.WhenAny(_leftChannel.Task, Task.Delay(2000)).ConfigureAwait(false);
            _leftChannel = null;
        }

        private DiscordVoiceSession CreateSession()
        {
            if (string.IsNullOrWhiteSpace(_sessionId) ||
                string.IsNullOrWhiteSpace(_endpoint) ||
                string.IsNullOrWhiteSpace(_token))
                throw new InvalidOperationException("Voice credentials are incomplete.");

            Logger.Log("Voice credentials endpoint=" + _endpoint + " session_len=" + _sessionId.Length);

            var session = new DiscordVoiceSession(
                _endpoint,
                _token,
                _sessionId,
                discord.CurrentUser.Id,
                Channel.Guild.Id,
                Channel.Id,
                App.LocalSettings.Read<string>("InputDevice", null),
                App.LocalSettings.Read<string>("OutputDevice", null),
                VoiceQualityOptions.FromSettings(Channel.Bitrate))
            {
                Muted = Muted,
                Deafened = Deafened
            };
            session.UdpPingUpdated += (_, ping) => UdpPing = ping;
            session.WebSocketPingUpdated += (_, ping) => WebSocketPing = ping;
            session.SpeakingChanged += OnSessionSpeakingChanged;
            session.Disconnected += OnSessionDisconnected;
            return session;
        }

        private void DisposeSession()
        {
            if (_session == null)
                return;

            _session.Disconnected -= OnSessionDisconnected;
            _session.SpeakingChanged -= OnSessionSpeakingChanged;
            try { _session.Dispose(); } catch { }
            _session = null;

            // nobody should be left ringed once the call is gone
            VoiceSpeakingTracker.Clear();
            VoiceSessionTracker.Set(null);
        }

        private void OnSessionSpeakingChanged(object sender, VoiceSpeakingEventArgs e)
            => VoiceSpeakingTracker.Set(e.UserId, e.Speaking);

        private void OnSessionDisconnected(object sender, EventArgs e)
        {
            var code = (sender as DiscordVoiceSession)?.CloseCode ?? 0;

            // 4014 means the channel is gone, we were moved or kicked, or the main gateway
            // session dropped. 4022 is the call being terminated, which is what another
            // client disconnecting us looks like. Both are terminal: the reference client
            // does not reconnect on 4014, and reconnecting only re-signals a join nobody
            // asked for.
            if (code == VoiceCloseCodeDisconnected || code == VoiceCloseCodeCallTerminated)
            {
                _doNotReconnect = true;
                IsTerminated = true;
                Volatile.Write(ref _connectState, 0);
                Logger.Log("Voice closed " + code + ", session is terminated");

                ConnectionStatus = _strings.GetString("DisconnectedState");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!_handshakeComplete)
                return;

            ConnectionStatus = _strings.GetString("DisconnectedState");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        private void TryCompleteCredentials()
        {
            if (!string.IsNullOrWhiteSpace(_sessionId) &&
                !string.IsNullOrWhiteSpace(_endpoint) &&
                !string.IsNullOrWhiteSpace(_token))
            {
                _credentialsReady?.TrySetResult(true);
            }
        }

        private Task OnVoiceStateUpdated(DiscordClient client, VoiceStateUpdateEventArgs e)
        {
            if (e.User?.Id != discord.CurrentUser.Id || e.Guild?.Id != Channel.Guild.Id)
                return Task.CompletedTask;

            if (e.After?.ChannelId == Channel.Id && !string.IsNullOrWhiteSpace(e.SessionId))
            {
                if (!string.Equals(_sessionId, e.SessionId, StringComparison.Ordinal))
                    Interlocked.Increment(ref _credentialVersion);
                _sessionId = e.SessionId;
                TryCompleteCredentials();
            }
            else
            {
                // Our voice state is no longer this channel: either we asked to leave, or
                // something else moved us - another client taking the call, a moderator
                // disconnecting or moving us, or the channel going away.
                _leftChannel?.TrySetResult(true);

                // _leftChannel is non-null only while ForceLeaveAsync is waiting, so this
                // is the "somebody else did it" case. Nothing used to react to it, so the
                // session stayed up, the speaking ring stayed lit and the card never faded
                // while Discord had already removed the account from the channel.
                if (_leftChannel == null && _handshakeComplete && !_disposed)
                    HandleExternalDisconnect(e.After?.ChannelId);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// The account left this channel without us asking. Tears the local session down so
        /// the app stops behaving as though it is still in the call. No Discord traffic: the
        /// server already knows, and re-signalling anything here would be a duplicate join.
        /// </summary>
        private void HandleExternalDisconnect(ulong? movedTo)
        {
            Logger.Log("Voice state changed elsewhere (now " +
                       (movedTo?.ToString() ?? "no channel") + "), ending local session");

            _doNotReconnect = true;
            IsTerminated = true;
            _handshakeComplete = false;
            Volatile.Write(ref _connectState, 0);

            DisposeSession();

            try
            {
                _voipCall?.NotifyCallEnded();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

            _voipCall = null;
            UnsubscribeGateway();

            ConnectionStatus = _strings.GetString("DisconnectedState");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        private Task OnVoiceServerUpdated(DiscordClient client, VoiceServerUpdateEventArgs e)
        {
            if (e.Guild?.Id != Channel.Guild.Id ||
                string.IsNullOrEmpty(e.Endpoint) ||
                string.IsNullOrEmpty(e.VoiceToken))
                return Task.CompletedTask;

            var changed = !string.Equals(_endpoint, e.Endpoint, StringComparison.Ordinal) ||
                          !string.Equals(_token, e.VoiceToken, StringComparison.Ordinal);
            _endpoint = e.Endpoint;
            _token = e.VoiceToken;
            if (changed)
                Interlocked.Increment(ref _credentialVersion);

            Logger.Log("Voice server update endpoint=" + _endpoint);
            TryCompleteCredentials();

            if (_handshakeComplete && changed && !_doNotReconnect)
                _ = ReconfigureNetworkingAsync(Volatile.Read(ref _credentialVersion));

            return Task.CompletedTask;
        }

        /// <summary>
        /// Discord can send several Voice Server Updates in a row, and the reference client
        /// reconfigures on each one. It gets away with that because it is single threaded and
        /// cannot race itself; this was fire-and-forget, so two updates opened two voice
        /// websockets against one session and Discord killed one with 4006. That failure made
        /// us leave and rejoin, which produced another Voice Server Update, which reconnected
        /// again: one call in the log did this nine times.
        ///
        /// Only one attempt runs at a time now. If one is already in flight it is skipped
        /// rather than queued, because that attempt reads the newest endpoint and token when
        /// it builds its session, so the newer update is already covered.
        /// </summary>
        private async Task ReconfigureNetworkingAsync(int version)
        {
            if (!await _connectLock.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                if (_doNotReconnect || !_handshakeComplete || _disposed)
                    return;

                // Superseded by a newer credential generation; that one will reconnect.
                if (Volatile.Read(ref _credentialVersion) != version)
                    return;

                DisposeSession();
                _session = CreateSession();
                await _session.ConnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private static TaskCompletionSource<bool> CreateCompletion()
            => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Flags]
    public enum VoiceState
    {
        None = 0,
        Deafened = 1,
        Muted = 2
    }
}