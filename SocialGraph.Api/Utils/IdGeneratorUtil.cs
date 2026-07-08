namespace SocialGraph.Api.Utils;

public class IdGeneratorUtil
{
    private const long EpochMilliseconds = 1704067200000L; // 2024-01-01T00:00:00Z
    private const int WorkerIdBits = 10;
    private const int SequenceBits = 12;
    private const long MaxWorkerId = (1L << WorkerIdBits) - 1;
    private const long SequenceMask = (1L << SequenceBits) - 1;
    private const int WorkerIdShift = SequenceBits;
    private const int TimestampShift = WorkerIdBits + SequenceBits;

    private static readonly object SyncRoot = new();
    private static readonly long WorkerId = ResolveWorkerId();
    private static long _lastTimestamp = -1L;
    private static long _sequence;

    public static long GenerateId()
    {
        lock (SyncRoot)
        {
            var timestamp = CurrentMilliseconds();

            if (timestamp < _lastTimestamp)
            {
                timestamp = WaitNextMilliseconds(_lastTimestamp);
            }

            if (timestamp == _lastTimestamp)
            {
                _sequence = (_sequence + 1) & SequenceMask;
                if (_sequence == 0)
                {
                    timestamp = WaitNextMilliseconds(_lastTimestamp);
                }
            }
            else
            {
                _sequence = 0;
            }

            _lastTimestamp = timestamp;

            return ((timestamp - EpochMilliseconds) << TimestampShift)
                | (WorkerId << WorkerIdShift)
                | _sequence;
        }
    }

    private static long ResolveWorkerId()
    {
        var raw = Environment.GetEnvironmentVariable("SOCIAL_GRAPH_WORKER_ID");
        return long.TryParse(raw, out var workerId) && workerId >= 0 && workerId <= MaxWorkerId
            ? workerId
            : 1;
    }

    private static long WaitNextMilliseconds(long lastTimestamp)
    {
        var timestamp = CurrentMilliseconds();
        while (timestamp <= lastTimestamp)
        {
            Thread.Sleep(1);
            timestamp = CurrentMilliseconds();
        }

        return timestamp;
    }

    private static long CurrentMilliseconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
