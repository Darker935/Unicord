using System.Collections.Concurrent;
using System.Threading;

namespace Unicord.Universal.Voice
{
    /// <summary>
    /// Pool of 20 ms PCM frames shared by the capture and playback paths.
    /// </summary>
    /// <remarks>
    /// These buffers are the reason the audio clock slips. Every captured frame and every
    /// decoded packet allocated a fresh 3.8 KB array, and each one then sat in a queue for
    /// 200 to 500 ms waiting to be sent or played. That is long enough to survive a gen0
    /// collection and a gen1 collection, so the whole stream was promoted into gen2 at
    /// roughly a megabyte a second. Gen2 filled and collected 88 times in a five second
    /// window, and every one of those suspends the AudioGraph callback thread.
    ///
    /// The graph cannot advance a quantum while it is suspended, so the suspensions come
    /// straight out of the audio clock - measured at 500 quanta taking 5263 ms rather than
    /// 5000, with only 150 ms of that inside our own callbacks. A pooled buffer is allocated
    /// once and lives forever, so it is never promoted and never collected.
    ///
    /// Rent falls back to a plain allocation when the pool is empty, so a missed Return
    /// costs efficiency and nothing else. Returning a buffer that is still referenced
    /// elsewhere would corrupt audio, so ownership passes with the buffer: whoever holds it
    /// last returns it.
    /// </remarks>
    internal static class VoicePcmPool
    {
        /// <summary>
        /// Enough for the capture queue, every source jitter queue at its cap and the frames
        /// in flight between them, with room to spare. Beyond this a returned buffer is
        /// dropped rather than hoarded.
        /// </summary>
        private const int Capacity = 256;

        private static readonly ConcurrentQueue<short[]> Free = new ConcurrentQueue<short[]>();
        private static int _pooled;
        private static int _allocations;

        public static short[] Rent()
        {
            if (Free.TryDequeue(out var buffer))
            {
                Interlocked.Decrement(ref _pooled);
                return buffer;
            }

            Interlocked.Increment(ref _allocations);
            return new short[VoiceAudioEngine.FrameShorts];
        }

        public static void Return(short[] buffer)
        {
            if (buffer == null || buffer.Length != VoiceAudioEngine.FrameShorts)
                return;

            if (Interlocked.Increment(ref _pooled) > Capacity)
            {
                Interlocked.Decrement(ref _pooled);
                return;
            }

            Free.Enqueue(buffer);
        }

        /// <summary>
        /// Fallback allocations since the last read. Zero means the audio path allocated
        /// nothing this window, which is what has to be true for gen2 to settle.
        /// </summary>
        public static int TakeAllocations()
            => Interlocked.Exchange(ref _allocations, 0);
    }
}
