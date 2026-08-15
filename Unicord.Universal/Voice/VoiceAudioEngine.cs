using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Unicord.Universal.Interop;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace Unicord.Universal.Voice
{
    /// <summary>
    /// Capture and render for a voice session, split across two AudioGraphs.
    /// </summary>
    /// <remarks>
    /// The split is the load-bearing decision. Every audible stall was proven to be a full
    /// garbage collection - the stall count, the stalls-containing-a-collection count and the
    /// gen2 count came back as the same number in every window - and the graph engine only
    /// stalls with them because a managed QuantumStarted handler runs synchronously on its
    /// pump, so suspending managed code suspends the pump. The device buffer behind it is a
    /// single 10 ms quantum with LatencyInSamples reporting zero, and Windows refused a
    /// deeper one under both Communications and Media, so a 17 ms pause always drained it.
    ///
    /// The render graph therefore has no managed handlers at all. Its pump never enters
    /// managed code, so the collector has nothing to suspend, and it drains a queue of
    /// pre-mixed frames held natively on the frame input node. The mixer runs on ordinary
    /// managed threads - the decode thread on every arrival, the capture callback as a
    /// 10 ms metronome - and keeps that queue topped up to <see cref="RenderCushionMs"/>.
    /// A collection suspends the mixer, the node keeps playing from its queue, and the
    /// mixer refills when it resumes. The cushion is the entire audible latency cost.
    ///
    /// Capture cannot be freed the same way: AudioFrameOutputNode only ever exposes the
    /// current quantum, so reading it needs the callback cadence. Its graph still stalls
    /// with a collection and the lost quanta are reconstructed as ramped silence, which the
    /// sender covers with at most two repeated Opus frames - a far smaller artefact than an
    /// incoming break, and confined to the outgoing stream.
    /// </remarks>
    internal sealed class VoiceAudioEngine : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;
        public const int FrameSamplesPerChannel = 960;
        public const int FrameShorts = FrameSamplesPerChannel * Channels;

        /// <summary>
        /// Hard ceiling on a source's queue, in 20 ms frames. This is a safety valve against a
        /// stream that never drains, not a working limit: trimming throws away speech that has
        /// already been decoded, which is an audible skip.
        /// </summary>
        private const int MaxQueuedFramesPerSource = 25;

        /// <summary>
        /// Working depth for a source queue, in 20 ms frames. Above this the stream is being
        /// produced faster than the output device can play it, so one frame is dropped per
        /// arrival until it settles. 200 ms is well clear of the jitter cushion.
        /// </summary>
        private const int SourceQueueTargetFrames = 10;
        private const int MinPrebufferFrames = 3;

        /// <summary>
        /// Ceiling on the adaptive jitter cushion, in 20 ms frames. 160 ms is already past
        /// what a healthy connection needs; beyond that the latency costs more than the
        /// glitches it prevents.
        /// </summary>
        private const int MaxPrebufferFrames = 8;

        /// <summary>
        /// Audio kept queued on the render node, in milliseconds. This is the shock absorber
        /// that rides out a collection: the longest stall measured was 55 ms, so 60 ms of
        /// natively-held audio outlasts it. It is also the incoming latency cost of the
        /// design, which the owner accepted.
        /// </summary>
        private const int RenderCushionMs = 60;

        /// <summary>
        /// Node depth below which the mixer pushes unconditionally, holes and all. Above it
        /// the mixer waits for a flowing source that is momentarily dry instead of mixing
        /// past its late packet, because filling the cushion eagerly turned ordinary network
        /// jitter into a padded hole mid-word while the node still held plenty of audio.
        ///
        /// The gap between this and <see cref="RenderCushionMs"/> is the whole grace period,
        /// and it is deliberately one frame. At 20 ms this floor allowed four frames of
        /// drain, which was wrong twice over: the node was measured sitting at exactly the
        /// floor, leaving less headroom than the 29 ms worst collection pause so a stall
        /// there would still break the device; and the wait scales with the call, because
        /// with eight people someone is nearly always between packets, so the queue drained
        /// toward the floor and stayed there. One frame of grace covers the single late
        /// packet that is the common case and cannot compound.
        /// </summary>
        private const int RenderFloorMs = 40;

        /// <summary>Duration of one mixed frame, matching the Opus frame the decoders emit.</summary>
        private const int FrameDurationMs = 20;

        // Source bookkeeping is counted in mixed frames, each FrameDurationMs long.
        private const int StableFramesBeforeShrink = 5000 / FrameDurationMs;
        private const int IdleFramesBeforeDrop = 2500 / FrameDurationMs;

        /// <summary>
        /// How long a source must be completely dry before its cushion is rebuilt. Half a
        /// second is long past any jitter and firmly into "this person stopped talking"
        /// territory. Re-priming after a brief gap muted the speaker for the refill.
        /// </summary>
        private const int EmptyFramesBeforeReprime = 500 / FrameDurationMs;

        /// <summary>
        /// Zero-crossing ramp applied either side of a dropped capture quantum, in samples per
        /// channel. Splicing the audio before a hole straight onto the audio after it leaves a
        /// step in the waveform, which is a click.
        /// </summary>
        private const int GapRampSamples = 48;

        /// <summary>
        /// Longest capture gap one callback will reconstruct as silence. Past this the app was
        /// suspended rather than merely late, and the sender's clock resync is the right
        /// recovery.
        /// </summary>
        private const int MaxGapMs = 250;

        /// <summary>
        /// Slack past one quantum before a capture callback interval counts as a stall rather
        /// than merely running late.
        /// </summary>
        private const int StallSlackMs = 5;

        /// <summary>
        /// Depth of the reusable <see cref="AudioFrame"/> ring. The render node holds at most
        /// the cushion, three 20 ms frames, so cycling back around takes 1.28 s of playback.
        /// A WinRT projection is finalizable and therefore always promoted, so allocating one
        /// per push was feeding gen2 at 50 objects a second.
        /// </summary>
        private const int FramePoolSize = 64;

        /// <summary>
        /// A pooled frame and how much of it is valid. The length alone cannot say, because a
        /// pooled buffer is always <see cref="FrameShorts"/> long regardless of how many
        /// samples the decoder actually produced.
        /// </summary>
        private struct QueuedFrame
        {
            public short[] Buffer;
            public int Count;
        }

        private sealed class SourceBuffer
        {
            public readonly ConcurrentQueue<QueuedFrame> Queue = new ConcurrentQueue<QueuedFrame>();
            public QueuedFrame Remainder;
            public int RemainderOffset;
            public bool Primed;
            public int IdleFrames;

            /// <summary>
            /// Adaptive depth. Grows on every underrun and decays while the stream is clean,
            /// so a bad connection buys latency for stability and a good one gives it back.
            /// </summary>
            public int Prebuffer = MinPrebufferFrames;
            public int StableFrames;
            public int EmptyFrames;

            /// <summary>Per-source tallies, reset each time the playback line is logged.</summary>
            public int Underruns;
            public int Trimmed;
            public int Frames;
            public int PeakDepth;
        }

        private readonly ConcurrentDictionary<uint, SourceBuffer> _sources = new ConcurrentDictionary<uint, SourceBuffer>();
        private readonly object _pumpLock = new object();

        private AudioGraph _renderGraph;
        private AudioGraph _captureGraph;
        private AudioDeviceInputNode _inputNode;
        private AudioFrameOutputNode _captureNode;
        private AudioFrameInputNode _renderNode;
        private AudioDeviceOutputNode _outputNode;

        private readonly short[] _captureCarry = new short[FrameShorts];
        private int _captureCarryCount;
        private bool _disposed;
        private string _outputDeviceId;
        private int _renderRestarting;
        private int _renderDeadWindows;

        // Render graph wire format.
        private bool _graphIsFloat;
        private int _graphChannels = Channels;
        private int _graphBytesPerSample = 2;
        private int _renderCushionSamples = SampleRate * RenderCushionMs / 1000;
        private int _renderFloorSamples = SampleRate * RenderFloorMs / 1000;

        // Capture graph format.
        private int _captureChannels = Channels;
        private bool _captureIsFloat = true;
        private bool _captureFormatResolved;
        private int _captureQuantumMs = 10;
        private int _maxGapQuanta = 25;
        private int _logEveryQuanta = 500;

        private short[] _readScratch = Array.Empty<short>();
        private int[] _mixBuffer = Array.Empty<int>();
        private short[] _scratchBuffer = Array.Empty<short>();
        private short[] _stereoBuffer = Array.Empty<short>();
        private short[] _graphBuffer = Array.Empty<short>();
        private AudioFrame[] _framePool;
        private uint _framePoolBytes;
        private int _framePoolIndex;

        /// <summary>Reusable silence for a dropped capture quantum. Sized on first use.</summary>
        private short[] _captureGap = Array.Empty<short>();

        // Diagnostics, reset each logging window.
        private readonly System.Diagnostics.Stopwatch _logWatch = System.Diagnostics.Stopwatch.StartNew();
        private long _captureQuantaSeen;
        private long _renderQuantumBase;
        private long _lastCaptureQuantum = -1;
        private long _captureMissedQuanta;
        private long _captureBusyTicks;
        private long _pumpBusyTicks;
        private long _captureMaxCallbackMs;
        private long _pumpMaxMs;
        private long _captureOverruns;
        private long _lastCaptureStart;
        private int _lastGen2;
        private long _stalls;
        private long _stallsWithGc;
        private long _stallExcessTicks;
        private long _playbackUnderruns;
        private long _playbackTrimmed;
        private long _playbackSupplied;
        private int _gcBase0;
        private int _gcBase1;
        private int _gcBase2;

        public bool CaptureEnabled { get; set; } = true;
        public bool PlaybackEnabled { get; set; } = true;

        public event Action<short[]> PcmCaptured;

        public async Task StartAsync(string inputDeviceId, string outputDeviceId)
        {
            _outputDeviceId = outputDeviceId;
            await StartRenderAsync(outputDeviceId);
            await StartCaptureAsync(inputDeviceId);

            _renderGraph.Start();
            _renderNode.Start();
            _renderQuantumBase = (long)_renderGraph.CompletedQuantumCount;

            if (_captureGraph != null)
            {
                _captureGraph.Start();
                _captureNode?.Start();
            }

            Logger.Log("Voice graphs running render quantum=" + _renderGraph.SamplesPerQuantum +
                       " latency=" + _renderGraph.LatencyInSamples +
                       " capture quantum=" + (_captureGraph?.SamplesPerQuantum ?? 0));
        }

        /// <summary>
        /// Output only, and deliberately empty of managed callbacks: nothing here subscribes
        /// to QuantumStarted on the graph or the node, so this graph's pump never executes
        /// managed code and a garbage collection cannot suspend it.
        /// </summary>
        private async Task StartRenderAsync(string outputDeviceId)
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Communications)
            {
                EncodingProperties = AudioEncodingProperties.CreatePcm(SampleRate, Channels, 16),
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
                DesiredSamplesPerQuantum = FrameSamplesPerChannel
            };

            if (!string.IsNullOrEmpty(outputDeviceId))
            {
                try
                {
                    settings.PrimaryRenderDevice = await DeviceInformation.CreateFromIdAsync(outputDeviceId);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex);
                }
            }

            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException("Unable to create audio graph: " + graphResult.Status);

            _renderGraph = graphResult.Graph;

            var props = _renderGraph.EncodingProperties;
            _graphChannels = Math.Max(1, (int)props.ChannelCount);
            _graphIsFloat = string.Equals(props.Subtype, MediaEncodingSubtypes.Float, StringComparison.OrdinalIgnoreCase)
                || props.BitsPerSample == 32;
            _graphBytesPerSample = _graphIsFloat ? 4 : Math.Max(2, (int)props.BitsPerSample / 8);
            _renderCushionSamples = (int)Math.Max(1u, props.SampleRate) * RenderCushionMs / 1000;
            _renderFloorSamples = (int)Math.Max(1u, props.SampleRate) * RenderFloorMs / 1000;

            Logger.Log("Voice render graph " + props.SampleRate + "Hz " + _graphChannels + "ch " +
                       props.BitsPerSample + "bit " + props.Subtype +
                       " quantum=" + _renderGraph.SamplesPerQuantum);

            var outputResult = await _renderGraph.CreateDeviceOutputNodeAsync();
            if (outputResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new InvalidOperationException("Unable to create audio output: " + outputResult.Status);

            _outputNode = outputResult.DeviceOutputNode;
            _renderNode = _renderGraph.CreateFrameInputNode(_renderGraph.EncodingProperties);
            _renderNode.AddOutgoingConnection(_outputNode);

            // The one managed handler this graph carries, and it never runs on the pump. A
            // graph dies on its own for reasons that have nothing to do with the call - the
            // output device disappearing, a driver reset, another app taking the endpoint in
            // exclusive mode - and until this was handled nothing noticed. Splitting render
            // from capture made that worse rather than better: the render graph can now die
            // alone, so the network, the decoders and the speaking rings all keep working
            // while playback is silent for the rest of the call.
            _renderGraph.UnrecoverableErrorOccurred += OnRenderGraphFailed;
        }

        private void OnRenderGraphFailed(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
        {
            Logger.Log("Voice render graph failed: " + args.Error + ", rebuilding");
            BeginRenderRestart();
        }

        private void OnCaptureGraphFailed(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
        {
            // Capture is not rebuilt here. Losing it costs the microphone, which the sender
            // covers with silence, where losing render costs every incoming voice.
            Logger.Log("Voice capture graph failed: " + args.Error);
        }

        /// <summary>
        /// Rebuilds the render graph in place after it dies. Local audio only - no Discord
        /// traffic of any kind - so it cannot turn into reconnect pressure on the session.
        /// </summary>
        private void BeginRenderRestart()
        {
            if (_disposed || Interlocked.Exchange(ref _renderRestarting, 1) == 1)
                return;

            // Clearing the node first parks the pump: it reads the field once and leaves
            // when it is null, so nothing touches the graph while it is being replaced.
            _renderNode = null;
            _ = RestartRenderAsync();
        }

        private async Task RestartRenderAsync()
        {
            try
            {
                var old = _renderGraph;
                _renderGraph = null;

                if (old != null)
                {
                    old.UnrecoverableErrorOccurred -= OnRenderGraphFailed;
                    try { old.Stop(); } catch (Exception ex) { Logger.LogError(ex); }
                }

                _outputNode?.Dispose();
                _outputNode = null;

                // The ring holds frames belonging to the graph being torn down.
                _framePool = null;
                _framePoolBytes = 0;
                _framePoolIndex = 0;

                old?.Dispose();

                if (_disposed)
                    return;

                await StartRenderAsync(_outputDeviceId);
                if (_disposed)
                    return;

                _renderGraph.Start();
                _renderNode.Start();
                _renderQuantumBase = (long)_renderGraph.CompletedQuantumCount;
                _renderDeadWindows = 0;
                Logger.Log("Voice render graph rebuilt, quantum=" + _renderGraph.SamplesPerQuantum);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _renderRestarting, 0);
            }
        }

        private async Task StartCaptureAsync(string inputDeviceId)
        {
            try
            {
                var settings = new AudioGraphSettings(AudioRenderCategory.Communications)
                {
                    EncodingProperties = AudioEncodingProperties.CreatePcm(SampleRate, Channels, 16),
                    QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
                    DesiredSamplesPerQuantum = FrameSamplesPerChannel
                };

                var graphResult = await AudioGraph.CreateAsync(settings);
                if (graphResult.Status != AudioGraphCreationStatus.Success)
                {
                    Logger.Log("Voice capture graph unavailable: " + graphResult.Status);
                    return;
                }

                _captureGraph = graphResult.Graph;

                var rate = (int)Math.Max(1u, _captureGraph.EncodingProperties.SampleRate);
                _captureQuantumMs = Math.Max(1, (int)_captureGraph.SamplesPerQuantum * 1000 / rate);
                _maxGapQuanta = Math.Max(1, MaxGapMs / _captureQuantumMs);
                _logEveryQuanta = Math.Max(50, 5000 / _captureQuantumMs);

                DeviceInformation inputDevice = null;
                if (!string.IsNullOrEmpty(inputDeviceId))
                    inputDevice = await DeviceInformation.CreateFromIdAsync(inputDeviceId);

                var inputResult = inputDevice != null
                    ? await _captureGraph.CreateDeviceInputNodeAsync(MediaCategory.Communications, _captureGraph.EncodingProperties, inputDevice)
                    : await _captureGraph.CreateDeviceInputNodeAsync(MediaCategory.Communications, _captureGraph.EncodingProperties);

                if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
                {
                    Logger.Log("Voice capture unavailable: " + inputResult.Status);
                    _captureGraph.Dispose();
                    _captureGraph = null;
                    return;
                }

                _inputNode = inputResult.DeviceInputNode;

                // AudioFrameOutputNode hands out 32-bit float regardless of the properties
                // the graph reports, so ask for float explicitly instead of trusting
                // AudioGraph.EncodingProperties, which echoes back the 16-bit PCM requested
                // at creation.
                var captureFormat = AudioEncodingProperties.CreatePcm(
                    _captureGraph.EncodingProperties.SampleRate,
                    _captureGraph.EncodingProperties.ChannelCount,
                    32);
                captureFormat.Subtype = MediaEncodingSubtypes.Float;

                _captureChannels = Math.Max(1, (int)captureFormat.ChannelCount);
                try
                {
                    _captureNode = _captureGraph.CreateFrameOutputNode(captureFormat);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex);
                    captureFormat = _captureGraph.EncodingProperties;
                    _captureChannels = Math.Max(1, (int)captureFormat.ChannelCount);
                    _captureNode = _captureGraph.CreateFrameOutputNode(captureFormat);
                }

                _inputNode.AddOutgoingConnection(_captureNode);
                _captureGraph.QuantumStarted += OnCaptureQuantumStarted;
                _captureGraph.UnrecoverableErrorOccurred += OnCaptureGraphFailed;
                Logger.Log("Voice capture node " + captureFormat.SampleRate + "Hz " +
                           captureFormat.ChannelCount + "ch " + captureFormat.BitsPerSample +
                           "bit " + captureFormat.Subtype);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        /// <summary>
        /// Takes ownership of <paramref name="pcm"/>, which must come from
        /// <see cref="VoicePcmPool"/>. It is returned to the pool once played, trimmed or
        /// discarded; the caller must not touch it again.
        /// </summary>
        public void EnqueuePlayback(uint ssrc, short[] pcm, int count)
        {
            if (pcm == null)
                return;

            if (!PlaybackEnabled || count <= 0)
            {
                VoicePcmPool.Return(pcm);
                return;
            }

            if (count > pcm.Length)
                count = pcm.Length;

            var source = _sources.GetOrAdd(ssrc, _ => new SourceBuffer());
            source.Queue.Enqueue(new QueuedFrame { Buffer = pcm, Count = count });
            source.Frames++;
            if (source.Queue.Count > source.PeakDepth)
                source.PeakDepth = source.Queue.Count;

            // Start playing as soon as a small cushion exists, never the full adaptive
            // target. Waiting for Prebuffer frames meant a source that had grown a deep
            // cushion held the first quarter second of every talk spurt before emitting
            // anything. Prebuffer stays the jitter target for trimming, not the gate.
            if (!source.Primed && source.Queue.Count >= MinPrebufferFrames)
                source.Primed = true;

            // The render device consumes marginally slower than the 48kHz the stream is
            // produced at, so a few percent more audio arrives than can ever be played and
            // the surplus has to go somewhere. Correcting one frame at a time as soon as the
            // queue sits above its working target spreads that unavoidable loss into single
            // 20 ms corrections instead of quarter-second dumps.
            var limit = Math.Max(SourceQueueTargetFrames, source.Prebuffer + 2);
            if (source.Queue.Count > limit && source.Queue.TryDequeue(out var dropped))
            {
                VoicePcmPool.Return(dropped.Buffer);
                _playbackTrimmed++;
                source.Trimmed++;
            }

            // Hard cap remains as a backstop for a stream that never drains.
            while (source.Queue.Count > MaxQueuedFramesPerSource && source.Queue.TryDequeue(out var excess))
            {
                VoicePcmPool.Return(excess.Buffer);
                _playbackTrimmed++;
                source.Trimmed++;
            }

            PumpPlayback();
        }

        /// <summary>
        /// Tops the render node's native queue up to the cushion. Called from the decode
        /// thread on every arrival and from the capture callback as a 10 ms metronome, so it
        /// keeps ticking through silence when no packets arrive. Non-reentrant: a caller that
        /// finds the pump busy simply leaves, because the busy caller is already doing the
        /// same work.
        /// </summary>
        private void PumpPlayback()
        {
            var node = _renderNode;
            if (node == null || _disposed)
                return;

            if (!Monitor.TryEnter(_pumpLock))
                return;

            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                while (!_disposed)
                {
                    var queued = (int)Math.Min(int.MaxValue, node.QueuedSampleCount);
                    if (queued >= _renderCushionSamples)
                        break;

                    if (!MixOneFrame(node, queued < _renderFloorSamples))
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
            finally
            {
                Monitor.Exit(_pumpLock);
                var elapsed = TicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
                _pumpBusyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
                if (elapsed > _pumpMaxMs)
                    _pumpMaxMs = elapsed;
            }
        }

        /// <summary>
        /// Mixes one 20 ms frame from every primed source and queues it on the render node.
        /// Returns false when no source is primed, which lets the node drain to native
        /// silence instead of queueing silence behind which real audio would then wait -
        /// and, unless <paramref name="mustPush"/> is set, when a source that was flowing
        /// is momentarily dry, so its late packet gets the node's remaining depth as grace
        /// instead of a hole mixed over it.
        /// </summary>
        private bool MixOneFrame(AudioFrameInputNode node, bool mustPush)
        {
            if (!mustPush)
            {
                foreach (var pair in _sources)
                {
                    var s = pair.Value;
                    if (s.Primed && s.EmptyFrames == 0 && s.Queue.IsEmpty && s.Remainder.Buffer == null)
                        return false;
                }
            }

            const int mixLen = FrameShorts;
            var opusMix = EnsureInts(ref _mixBuffer, mixLen);
            var scratch = EnsureShorts(ref _scratchBuffer, mixLen);
            Array.Clear(opusMix, 0, mixLen);
            var anyPrimed = false;
            var have = false;

            foreach (var pair in _sources)
            {
                var source = pair.Value;
                if (!source.Primed)
                {
                    if (++source.IdleFrames > IdleFramesBeforeDrop && source.Queue.IsEmpty && source.Remainder.Buffer == null)
                        _sources.TryRemove(pair.Key, out _);
                    continue;
                }

                anyPrimed = true;
                Array.Clear(scratch, 0, mixLen);
                var filled = FillSource(source, scratch, mixLen);

                if (filled < mixLen)
                {
                    // Count gap *events*, not every empty frame. Counting each one measured
                    // silence rather than glitches whenever somebody stopped talking.
                    if (source.EmptyFrames == 0)
                    {
                        _playbackUnderruns++;
                        source.Underruns++;
                    }

                    source.StableFrames = 0;

                    if (filled > 0)
                    {
                        // A short fill means audio was arriving and ran out mid-frame: real
                        // jitter, so deepen the cushion. A completely dry source is almost
                        // always somebody who stopped talking.
                        if (source.Prebuffer < MaxPrebufferFrames)
                            source.Prebuffer++;

                        source.EmptyFrames = 0;
                    }
                    else if (++source.EmptyFrames >= EmptyFramesBeforeReprime)
                    {
                        // Re-prime only after a long dry spell, which is a talk-spurt
                        // boundary where rebuilding the cushion costs nothing audible.
                        source.Primed = false;
                        source.EmptyFrames = 0;
                    }
                }
                else
                {
                    source.EmptyFrames = 0;
                    if (++source.StableFrames >= StableFramesBeforeShrink)
                    {
                        source.StableFrames = 0;
                        if (source.Prebuffer > MinPrebufferFrames)
                            source.Prebuffer--;
                    }
                }

                if (filled == 0)
                    continue;

                source.IdleFrames = 0;
                have = true;
                for (var i = 0; i < mixLen; i++)
                    opusMix[i] += scratch[i];
            }

            if (!anyPrimed)
                return false;

            var stereo = EnsureShorts(ref _stereoBuffer, mixLen);
            if (have && PlaybackEnabled)
            {
                for (var i = 0; i < mixLen; i++)
                {
                    var sample = opusMix[i];
                    if (sample > short.MaxValue)
                        sample = short.MaxValue;
                    else if (sample < short.MinValue)
                        sample = short.MinValue;
                    stereo[i] = (short)sample;
                }
            }
            else
            {
                Array.Clear(stereo, 0, mixLen);
            }

            _playbackSupplied += FrameSamplesPerChannel;

            var graphSamples = FrameSamplesPerChannel * _graphChannels;
            var graphPcm = ToGraphChannels(stereo, mixLen, graphSamples);

            // Not disposed: AddFrame queues the frame for the native pump to read
            // asynchronously, so the frame is cycled through a ring instead.
            node.AddFrame(CreateFrame(graphPcm, graphSamples));
            return true;
        }

        private void OnCaptureQuantumStarted(AudioGraph sender, object args)
        {
            if (_captureNode == null)
                return;

            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

            // Attribution for stalls on this managed callback. The interval between
            // consecutive callbacks is the wall time a quantum really took; sampling the
            // gen2 count across the same interval says whether a collection sat inside it.
            // This graph is EXPECTED to stall with collections - the proof of the render
            // design is renderQuanta holding at rate while these counters stay non-zero.
            var gen2Now = GC.CollectionCount(2);
            if (_lastCaptureStart != 0)
            {
                var interval = startedAt - _lastCaptureStart;
                if (TicksToMs(interval) >= _captureQuantumMs + StallSlackMs)
                {
                    _stalls++;
                    _stallExcessTicks += interval - (System.Diagnostics.Stopwatch.Frequency * _captureQuantumMs / 1000);
                    if (gen2Now != _lastGen2)
                        _stallsWithGc++;
                }
            }

            _lastCaptureStart = startedAt;
            _lastGen2 = gen2Now;

            // The graph's own quantum counter is the clock; the number of times this handler
            // ran is not. If the counter moved by more than one since the last callback the
            // handler was suspended and the microphone audio for those quanta is gone for
            // good, because GetFrame only ever returns the current quantum. Emitting the
            // exact silence that was lost keeps the outgoing rate at 50 frames a second and
            // confines the damage to the gap.
            var completed = (long)sender.CompletedQuantumCount;
            var missed = _lastCaptureQuantum < 0 ? 0 : completed - _lastCaptureQuantum - 1;
            _lastCaptureQuantum = completed;

            // Disposed explicitly: at 100 quanta/s an undisposed AudioFrame per quantum
            // keeps native audio buffers alive until the GC notices.
            short[] samples;
            using (var frame = _captureNode.GetFrame())
            {
                if (frame == null)
                    return;

                if (!CaptureEnabled)
                {
                    // Drop the partial frame too, otherwise unmuting splices in audio
                    // captured before the mute.
                    _captureCarryCount = 0;
                    MaintainPlayback(startedAt);
                    return;
                }

                samples = ReadFrame(frame);
            }

            if (missed > 0)
            {
                _captureMissedQuanta += missed;
                EmitCaptureGap(missed, samples.Length);
                if (samples.Length > 0)
                    RampUp(samples, samples.Length);
            }

            if (samples.Length > 0)
                PushCaptureSamples(samples, samples.Length);

            MaintainPlayback(startedAt);
        }

        /// <summary>
        /// The render-side work that rides on the capture callback: the 10 ms metronome tick
        /// for the mixer, and the periodic diagnostic log. Runs on the capture graph's
        /// thread, which cannot stall the render graph.
        /// </summary>
        private void MaintainPlayback(long startedAt)
        {
            PumpPlayback();

            var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
            _captureBusyTicks += ticks;
            var elapsed = TicksToMs(ticks);
            if (elapsed >= _captureQuantumMs)
                _captureOverruns++;
            if (elapsed > _captureMaxCallbackMs)
                _captureMaxCallbackMs = elapsed;

            if (++_captureQuantaSeen < _logEveryQuanta)
                return;

            var windowMs = Math.Max(1, _logWatch.ElapsedMilliseconds);
            var diagnostics = DiagnosticLog.Voice.IsEnabled;

            // renderQuanta is the render graph's own count of quanta pumped over the window,
            // read from outside it. It is the pass/fail number for this design: if it holds
            // the graph's full rate while stallsWithGc is non-zero, collections kept
            // happening and the render pump no longer cared.
            var graph = _renderGraph;
            var renderCompleted = graph == null ? _renderQuantumBase : (long)graph.CompletedQuantumCount;
            var renderQuanta = renderCompleted - _renderQuantumBase;
            _renderQuantumBase = renderCompleted;

            // A dead render graph stops advancing its quantum counter, and that is visible
            // from here because this runs on the capture graph's thread. The failure event
            // above is the tidy signal but is not guaranteed to arrive for every way a graph
            // can stop, and the cost of missing it is the whole call silent while everything
            // else keeps working. Two consecutive empty windows is ten seconds - far longer
            // than any collection, and unambiguous.
            if (renderQuanta <= 0 && _renderRestarting == 0 && !_disposed)
            {
                if (++_renderDeadWindows >= 2)
                {
                    Logger.Log("Voice render graph has not advanced for " + _renderDeadWindows +
                               " windows, rebuilding");
                    _renderDeadWindows = 0;
                    BeginRenderRestart();
                }
            }
            else if (renderQuanta > 0)
            {
                _renderDeadWindows = 0;
            }

            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);

            if (diagnostics)
            {
                var expected = (int)(windowMs / FrameDurationMs);
                var detail = new System.Text.StringBuilder();
                foreach (var pair in _sources)
                {
                    var s = pair.Value;
                    detail.Append(" [").Append(pair.Key)
                          .Append(" rx=").Append(s.Frames).Append('/').Append(expected)
                          .Append(" depth=").Append(s.Queue.Count)
                          .Append(" peak=").Append(s.PeakDepth)
                          .Append(" pre=").Append(s.Prebuffer)
                          .Append(" under=").Append(s.Underruns)
                          .Append(" trim=").Append(s.Trimmed)
                          .Append(s.Primed ? "" : " unprimed")
                          .Append(']');
                }

                var heapMb = GC.GetTotalMemory(false) / (1024 * 1024);
                var usedMb = (long)(Windows.System.MemoryManager.AppMemoryUsage / (1024 * 1024));
                var limitMb = (long)(Windows.System.MemoryManager.AppMemoryUsageLimit / (1024 * 1024));

                DiagnosticLog.Voice.Log("playback capQuanta=" + (_captureQuantaSeen * 1000 / windowMs) +
                           "/s renderQuanta=" + (renderQuanta * 1000 / windowMs) +
                           "/s supplied=" + (_playbackSupplied * 1000 / windowMs) +
                           "/s missedCap=" + _captureMissedQuanta +
                           " capBusy=" + TicksToMs(_captureBusyTicks) + "ms" +
                           " pumpBusy=" + TicksToMs(_pumpBusyTicks) + "ms" +
                           " over=" + _captureOverruns +
                           " stalls=" + _stalls + " stallsWithGc=" + _stallsWithGc +
                           " stallLoss=" + TicksToMs(_stallExcessTicks) + "ms" +
                           " maxCapMs=" + _captureMaxCallbackMs +
                           " maxPumpMs=" + _pumpMaxMs +
                           " gc=" + (gc0 - _gcBase0) + "/" + (gc1 - _gcBase1) + "/" + (gc2 - _gcBase2) +
                           " poolMiss=" + VoicePcmPool.TakeAllocations() +
                           " heap=" + heapMb + "MB app=" + usedMb + "/" + limitMb + "MB " +
                           Windows.System.MemoryManager.AppMemoryUsageLevel +
                           " nodeQueue=" + (long)Math.Min(long.MaxValue, _renderNode?.QueuedSampleCount ?? 0) +
                           " underruns=" + _playbackUnderruns + " trimmed=" + _playbackTrimmed +
                           " sources=" + _sources.Count + detail);
            }

            foreach (var pair in _sources)
            {
                var s = pair.Value;
                s.Frames = 0;
                s.PeakDepth = 0;
                s.Underruns = 0;
                s.Trimmed = 0;
            }

            if (!diagnostics)
                VoicePcmPool.TakeAllocations();

            _captureQuantaSeen = 0;
            _captureMissedQuanta = 0;
            _captureBusyTicks = 0;
            _pumpBusyTicks = 0;
            _captureMaxCallbackMs = 0;
            _pumpMaxMs = 0;
            _captureOverruns = 0;
            _stalls = 0;
            _stallsWithGc = 0;
            _stallExcessTicks = 0;
            _playbackUnderruns = 0;
            _playbackTrimmed = 0;
            _playbackSupplied = 0;
            _gcBase0 = gc0;
            _gcBase1 = gc1;
            _gcBase2 = gc2;
            _logWatch.Restart();
        }

        /// <summary>
        /// Replaces microphone audio that was lost because the handler did not run, so the
        /// outgoing stream keeps its 50 frames a second and the sender's wall clock never
        /// falls behind. The carry tail is ramped to zero first: a hole that starts mid
        /// waveform is a click on top of the gap.
        /// </summary>
        private void EmitCaptureGap(long missed, int quantumShorts)
        {
            if (quantumShorts <= 0)
                quantumShorts = (int)(_captureGraph?.SamplesPerQuantum ?? 0) * Channels;
            if (quantumShorts <= 0)
                return;

            // A long suspend is not audio worth reconstructing. Beyond this the stream is
            // realigned by the sender's own clock resync rather than by emitting whole
            // seconds of silence out of one callback.
            if (missed > _maxGapQuanta)
                missed = _maxGapQuanta;

            if (_captureCarryCount > 0)
                RampDown(_captureCarry, _captureCarryCount);

            var needed = (int)missed * quantumShorts;
            if (_captureGap.Length < needed)
                _captureGap = new short[needed];

            PushCaptureSamples(_captureGap, needed);
        }

        /// <summary>
        /// Accumulates graph quanta into whole 20 ms Opus frames. Shared by real capture and
        /// by gap fill so both advance the frame timeline identically.
        /// </summary>
        private void PushCaptureSamples(short[] samples, int length)
        {
            var offset = 0;
            if (_captureCarryCount > 0)
            {
                var need = FrameShorts - _captureCarryCount;
                var take = Math.Min(need, length);
                Array.Copy(samples, 0, _captureCarry, _captureCarryCount, take);
                _captureCarryCount += take;
                offset = take;
                if (_captureCarryCount == FrameShorts)
                {
                    var carried = VoicePcmPool.Rent();
                    Array.Copy(_captureCarry, carried, FrameShorts);
                    PcmCaptured?.Invoke(carried);
                    _captureCarryCount = 0;
                }
            }

            while (offset + FrameShorts <= length)
            {
                var framePcm = VoicePcmPool.Rent();
                Array.Copy(samples, offset, framePcm, 0, FrameShorts);
                PcmCaptured?.Invoke(framePcm);
                offset += FrameShorts;
            }

            if (offset < length)
            {
                _captureCarryCount = length - offset;
                Array.Copy(samples, offset, _captureCarry, 0, _captureCarryCount);
            }
        }

        private static void RampDown(short[] buffer, int count)
        {
            var ramp = Math.Min(count, GapRampSamples * Channels);
            if (ramp <= 0)
                return;

            var start = count - ramp;
            for (var i = 0; i < ramp; i++)
                buffer[start + i] = (short)(buffer[start + i] * (ramp - 1 - i) / ramp);
        }

        private static void RampUp(short[] buffer, int count)
        {
            var ramp = Math.Min(count, GapRampSamples * Channels);
            for (var i = 0; i < ramp; i++)
                buffer[i] = (short)(buffer[i] * i / ramp);
        }

        private static long TicksToMs(long ticks)
            => ticks * 1000 / System.Diagnostics.Stopwatch.Frequency;

        private static int[] EnsureInts(ref int[] buffer, int length)
        {
            if (buffer.Length < length)
                buffer = new int[length];
            return buffer;
        }

        private static short[] EnsureShorts(ref short[] buffer, int length)
        {
            if (buffer.Length < length)
                buffer = new short[length];
            return buffer;
        }

        /// <summary>
        /// Copies out of the source queue, returning each frame to the pool as it is fully
        /// consumed. A frame is only released once nothing still points into it, so a
        /// partially played frame is parked in <see cref="SourceBuffer.Remainder"/> and
        /// returned on the mix that finishes it.
        /// </summary>
        private static int FillSource(SourceBuffer source, short[] dest, int count)
        {
            var destOffset = 0;
            if (source.Remainder.Buffer != null)
            {
                var available = source.Remainder.Count - source.RemainderOffset;
                var take = Math.Min(available, count);
                Array.Copy(source.Remainder.Buffer, source.RemainderOffset, dest, destOffset, take);
                destOffset += take;
                source.RemainderOffset += take;
                if (source.RemainderOffset >= source.Remainder.Count)
                {
                    VoicePcmPool.Return(source.Remainder.Buffer);
                    source.Remainder = default(QueuedFrame);
                    source.RemainderOffset = 0;
                }
            }

            while (destOffset < count && source.Queue.TryDequeue(out var next))
            {
                var take = Math.Min(next.Count, count - destOffset);
                Array.Copy(next.Buffer, 0, dest, destOffset, take);
                destOffset += take;
                if (take < next.Count)
                {
                    source.Remainder = next;
                    source.RemainderOffset = take;
                }
                else
                {
                    VoicePcmPool.Return(next.Buffer);
                }
            }

            return destOffset;
        }

        private short[] ReadFrame(AudioFrame frame)
        {
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Read))
            using (var reference = buffer.CreateReference())
            {
                unsafe
                {
                    ((IMemoryBufferByteAccess)reference).GetBuffer(out var data, out var capacity);
                    var byteCount = (int)Math.Min(buffer.Length, capacity);
                    if (byteCount <= 0)
                        return Array.Empty<short>();

                    if (ResolveCaptureFormat(byteCount))
                    {
                        var floats = byteCount / 4;
                        // Reused across quanta: allocating here put the capture callback on
                        // the GC path, and a stalled callback loses whole quanta.
                        var raw = RentScratch(floats);
                        for (var i = 0; i < floats; i++)
                        {
                            var f = *(float*)(data + i * 4);
                            if (f > 1f)
                                f = 1f;
                            else if (f < -1f)
                                f = -1f;
                            raw[i] = (short)(f * 32767f);
                        }
                        return ToOpusStereo(raw);
                    }

                    byteCount &= ~1;
                    var shorts = RentScratch(byteCount / 2);
                    for (var i = 0; i < shorts.Length; i++)
                        shorts[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
                    return ToOpusStereo(shorts);
                }
            }
        }

        /// <summary>
        /// Derives the real sample width from the frame the graph actually handed us rather
        /// than from <see cref="AudioGraph.EncodingProperties"/>, which reports the 16-bit
        /// PCM that was requested at creation even when the frames are 32-bit float.
        /// Reading float frames as int16 produces noise *and* twice as many samples, which
        /// makes the encoder emit Opus frames at double real time.
        /// </summary>
        private bool ResolveCaptureFormat(int byteCount)
        {
            if (_captureFormatResolved)
                return _captureIsFloat;

            var samples = (int)(_captureGraph?.SamplesPerQuantum ?? 0) * _captureChannels;
            if (samples > 0)
            {
                var bytesPerSample = byteCount / samples;
                if (bytesPerSample == 2 || bytesPerSample == 4)
                {
                    _captureIsFloat = bytesPerSample == 4;
                    _captureFormatResolved = true;
                    Logger.Log("Voice capture frame " + byteCount + " bytes over " + samples +
                               " samples = " + bytesPerSample + " bytes/sample, float=" + _captureIsFloat);
                }
            }

            return _captureIsFloat;
        }

        /// <summary>
        /// Quantum-sized scratch, reused. The graph quantum never changes size during a
        /// session, so this allocates once. Callers must copy out before the next quantum.
        /// </summary>
        private short[] RentScratch(int length)
        {
            if (_readScratch.Length != length)
                _readScratch = new short[length];
            return _readScratch;
        }

        private short[] ToOpusStereo(short[] graphPcm)
        {
            if (_captureChannels == Channels)
                return graphPcm;
            if (_captureChannels == 1)
            {
                var stereo = new short[graphPcm.Length * 2];
                for (var i = 0; i < graphPcm.Length; i++)
                {
                    stereo[i * 2] = graphPcm[i];
                    stereo[i * 2 + 1] = graphPcm[i];
                }
                return stereo;
            }

            var frames = graphPcm.Length / _captureChannels;
            var down = new short[frames * Channels];
            for (var i = 0; i < frames; i++)
            {
                down[i * 2] = graphPcm[i * _captureChannels];
                down[i * 2 + 1] = _captureChannels > 1 ? graphPcm[i * _captureChannels + 1] : graphPcm[i * _captureChannels];
            }
            return down;
        }

        private short[] ToGraphChannels(short[] stereo, int stereoCount, int graphSamples)
        {
            if (_graphChannels == Channels)
                return stereo;

            if (_graphChannels == 1)
            {
                var mono = EnsureShorts(ref _graphBuffer, graphSamples);
                var frames = Math.Min(graphSamples, stereoCount / 2);
                for (var i = 0; i < frames; i++)
                    mono[i] = (short)((stereo[i * 2] + stereo[i * 2 + 1]) / 2);
                for (var i = frames; i < graphSamples; i++)
                    mono[i] = 0;
                return mono;
            }

            var interleaved = EnsureShorts(ref _graphBuffer, graphSamples);
            Array.Clear(interleaved, 0, graphSamples);
            var outFrames = graphSamples / _graphChannels;
            var inFrames = stereoCount / Channels;
            var n = Math.Min(outFrames, inFrames);
            for (var i = 0; i < n; i++)
            {
                interleaved[i * _graphChannels] = stereo[i * 2];
                if (_graphChannels > 1)
                    interleaved[i * _graphChannels + 1] = stereo[i * 2 + 1];
            }
            return interleaved;
        }

        /// <summary>
        /// Hands out a reusable <see cref="AudioFrame"/> from a fixed ring. A WinRT
        /// projection is finalizable, so it always survives its first collection and is
        /// promoted; allocating one per push fed gen2 continuously. The frame cannot be
        /// disposed after AddFrame because the native pump reads it asynchronously, so it is
        /// cycled instead - the node holds at most the cushion, three frames, against a ring
        /// 64 deep.
        /// </summary>
        private AudioFrame RentFrame(uint bytes)
        {
            if (_framePool == null || _framePoolBytes != bytes)
            {
                _framePool = new AudioFrame[FramePoolSize];
                _framePoolBytes = bytes;
                _framePoolIndex = 0;
            }

            var index = _framePoolIndex;
            _framePoolIndex = index + 1 >= FramePoolSize ? 0 : index + 1;

            var frame = _framePool[index];
            if (frame == null)
            {
                frame = new AudioFrame(bytes);
                _framePool[index] = frame;
            }

            return frame;
        }

        private AudioFrame CreateFrame(short[] pcm, int sampleCount)
        {
            var bytes = (uint)(sampleCount * _graphBytesPerSample);
            var frame = RentFrame(bytes);
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            {
                unsafe
                {
                    ((IMemoryBufferByteAccess)reference).GetBuffer(out var data, out var capacity);
                    var slots = (int)capacity / _graphBytesPerSample;
                    var count = Math.Min(sampleCount, slots);
                    if (_graphIsFloat)
                    {
                        for (var i = 0; i < count; i++)
                            *(float*)(data + i * 4) = pcm[i] / 32768f;
                        // AudioFrame memory is not zeroed, so any slot we do not fill
                        // would be played back as static.
                        for (var i = count; i < slots; i++)
                            *(float*)(data + i * 4) = 0f;
                    }
                    else
                    {
                        // One memcpy rather than 1920 shift-and-mask pairs per frame. The
                        // layout is identical: little-endian 16-bit.
                        System.Runtime.InteropServices.Marshal.Copy(pcm, 0, (IntPtr)data, count);
                        for (var i = count; i < slots; i++)
                        {
                            data[i * 2] = 0;
                            data[i * 2 + 1] = 0;
                        }
                    }
                }
            }

            return frame;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Hand every queued frame back so the pool survives a reconnect intact.
            foreach (var pair in _sources)
            {
                while (pair.Value.Queue.TryDequeue(out var queued))
                    VoicePcmPool.Return(queued.Buffer);

                VoicePcmPool.Return(pair.Value.Remainder.Buffer);
                pair.Value.Remainder = default(QueuedFrame);
            }

            _sources.Clear();

            try
            {
                if (_captureGraph != null)
                {
                    _captureGraph.QuantumStarted -= OnCaptureQuantumStarted;
                    _captureGraph.UnrecoverableErrorOccurred -= OnCaptureGraphFailed;
                }

                if (_renderGraph != null)
                    _renderGraph.UnrecoverableErrorOccurred -= OnRenderGraphFailed;

                _captureGraph?.Stop();
                _renderGraph?.Stop();
                _captureNode?.Stop();
                _renderNode?.Stop();
                _inputNode?.Dispose();
                _captureNode?.Dispose();
                _renderNode?.Dispose();
                _outputNode?.Dispose();
                _captureGraph?.Dispose();
                _renderGraph?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }
    }
}
