using System.Buffers;

namespace TeletextRecoveReese.Core;

public static class VbiDeconvolutionEngine
{
    private const double TeletextBitRate = 6_937_500.0;

    public static string ValidateOpenClBackend()
    {
        using var matcher = new OpenClVhsPatternMatcher();
        return matcher.DeviceDescription;
    }

    public static async Task<VbiDeconvolutionResult> DeconvolveAsync(
        Stream input,
        Stream output,
        VbiCaptureOptions options,
        IProgress<VbiDeconvolutionProgress>? progress,
        IVbiDecodedPacketProgress? decodedPackets,
        CancellationToken cancellationToken)
    {
        if (!input.CanSeek) throw new ArgumentException("The VBI input stream must be seekable.", nameof(input));
        int sampleBytes = options.IsUInt16 ? 2 : 1;
        int lineBytes = checked(options.LineLength * sampleBytes);
        long totalPhysicalLines = input.Length / lineBytes;
        long totalFields = totalPhysicalLines / options.FieldLines;
        int selectedPerField = options.FieldRangeEnd - options.FieldRangeStart;
        long totalLines = totalFields * selectedPerField;
        long remainder = totalPhysicalLines % options.FieldLines;
        totalLines += Math.Clamp(remainder - options.FieldRangeStart, 0, selectedPerField);
        VbiResamplePlan resamplePlan = CreateResamplePlan(options);

        // vhs-teletext feeds OpenCL from one process per CPU by default. A single
        // blocking queue leaves a large part of the GPU idle between the small
        // per-line uploads and result reads, so keep several independent queues
        // in flight here as well.
        // These are mostly GPU command queues waiting on blocking transfers, not
        // CPU-bound workers. Keep more queues than logical CPU cores in flight so
        // uploads, kernels and result reads from another line can cover that wait.
        int workerCount = Math.Clamp(Environment.ProcessorCount * 2, 4, 24);
        var matchers = new OpenClVhsPatternMatcher[workerCount];
        try
        {
            for (int i = 0; i < matchers.Length; i++)
                matchers[i] = new OpenClVhsPatternMatcher();

        var previewBatch = new List<byte[]>(16);
        long processed = 0, teletext = 0, written = 0;
        int fieldLine = 0;
        bool endOfInput = false;
        int batchSize = Math.Max(32, workerCount * 4);
        input.Position = 0;

        async Task<List<byte[]>> ReadBatchAsync()
        {
            var lines = new List<byte[]>(batchSize);
            while (lines.Count < batchSize && !endOfInput)
            {
                var raw = new byte[lineBytes];
                if (!await ReadExactlyOrEndAsync(input, raw, cancellationToken).ConfigureAwait(false))
                {
                    endOfInput = true;
                    break;
                }
                bool selected = fieldLine >= options.FieldRangeStart && fieldLine < options.FieldRangeEnd;
                fieldLine = (fieldLine + 1) % options.FieldLines;
                if (selected) lines.Add(raw);
            }
            return lines;
        }

        Task<float[]?[]> PrepareBatchAsync(IReadOnlyList<byte[]> lines) => Task.Run(() =>
        {
            var prepared = new float[]?[lines.Count];
            Parallel.For(
                0,
                lines.Count,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                },
                index => prepared[index] = PrepareLine(lines[index], options, resamplePlan));
            return prepared;
        }, cancellationToken);

        List<byte[]> currentLines = await ReadBatchAsync().ConfigureAwait(false);
        Task<float[]?[]>? currentPreparation = currentLines.Count > 0
            ? PrepareBatchAsync(currentLines)
            : null;
        while (currentPreparation is not null)
        {
            float[]?[] preparedLines = await currentPreparation.ConfigureAwait(false);
            // Start CPU preparation of the next batch before matching this batch on
            // OpenCL. This keeps the CPU and GPU busy at the same time.
            List<byte[]> nextLines = await ReadBatchAsync().ConfigureAwait(false);
            Task<float[]?[]>? nextPreparation = nextLines.Count > 0
                ? PrepareBatchAsync(nextLines)
                : null;

            var decoded = new byte[]?[preparedLines.Length];
            int nextDecodeIndex = -1;
            await Task.WhenAll(Enumerable.Range(0, workerCount).Select(worker => Task.Run(() =>
            {
                OpenClVhsPatternMatcher matcher = matchers[worker];
                while (true)
                {
                    int index = Interlocked.Increment(ref nextDecodeIndex);
                    if (index >= preparedLines.Length) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    float[]? bits = preparedLines[index];
                    if (bits is null) continue;
                    matcher.UploadLine(bits);
                    byte[] packet = DecodePacket(matcher, bits);
                    if (packet.Length == 42) decoded[index] = packet;
                }
            }, cancellationToken))).ConfigureAwait(false);

            for (int index = 0; index < preparedLines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                if (preparedLines[index] is not null)
                {
                    teletext++;
                    byte[]? packet = decoded[index];
                    if (packet is not null)
                    {
                        await output.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                        written++;
                        if (decodedPackets?.IsEnabled == true)
                        {
                            previewBatch.Add((byte[])packet.Clone());
                            if (previewBatch.Count >= 16)
                            {
                                decodedPackets.Report(previewBatch.ToArray());
                                previewBatch.Clear();
                            }
                        }
                        else if (previewBatch.Count > 0)
                        {
                            previewBatch.Clear();
                        }
                    }
                }
                if ((processed & 31) == 0 || processed == totalLines)
                    progress?.Report(new VbiDeconvolutionProgress(processed, totalLines, teletext, written));
            }

            currentLines = nextLines;
            currentPreparation = nextPreparation;
        }
        if (previewBatch.Count > 0 && decodedPackets?.IsEnabled == true)
            decodedPackets?.Report(previewBatch.ToArray());
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new VbiDeconvolutionProgress(processed, totalLines, teletext, written));
        return new VbiDeconvolutionResult(processed, teletext, written,
            $"{matchers[0].DeviceDescription} ({workerCount} parallel OpenCL workers)");
        }
        finally
        {
            foreach (OpenClVhsPatternMatcher? matcher in matchers)
                matcher?.Dispose();
        }
    }

    private static byte[] DecodePacket(OpenClVhsPatternMatcher matcher, float[] bits)
    {
        byte[] first = matcher.MatchHamming(16, 3);
        int lo = Hamming.Decode84(first[0]).Value;
        int hi = Hamming.Decode84(first[1]).Value;
        int address = lo | (hi << 4);
        int magazine = address & 7;
        int row = address >> 3;
        if (magazine > 7 || row > 31) return Array.Empty<byte>();

        var packet = new byte[42];
        packet[0] = first[0]; packet[1] = first[1]; packet[2] = first[2];
        if (row == 0)
        {
            Copy(matcher.MatchHamming(40, 7), packet, 3);
            Copy(matcher.MatchParity(96, 32), packet, 10);
        }
        else if (row < 26)
        {
            Copy(matcher.MatchParity(32, 40), packet, 2);
        }
        else if (row == 27)
        {
            int designation = Hamming.Decode84(packet[2]).Value;
            if (designation < 4)
            {
                Copy(matcher.MatchHamming(40, 37), packet, 3);
                Copy(matcher.MatchFull(336, 2), packet, 40);
            }
            else Copy(matcher.MatchFull(40, 39), packet, 3);
        }
        else if (row < 30)
        {
            Copy(matcher.MatchFull(40, 39), packet, 3);
        }
        else if (row == 30 && magazine == 0)
        {
            int designation = Hamming.Decode84(packet[2]).Value;
            Copy(matcher.MatchHamming(40, 6), packet, 3);
            Copy(designation is 2 or 3 ? matcher.MatchHamming(88, 13) : matcher.MatchFull(88, 13), packet, 9);
            Copy(matcher.MatchParity(192, 20), packet, 22);
        }
        else Copy(matcher.MatchFull(40, 39), packet, 3);
        return packet;
    }

    private static void Copy(byte[] source, byte[] destination, int offset) =>
        Array.Copy(source, 0, destination, offset, Math.Min(source.Length, destination.Length - offset));

    private sealed record VbiResamplePlan(int Length, int[] Left, float[] Fraction);

    private static VbiResamplePlan CreateResamplePlan(VbiCaptureOptions options)
    {
        double targetRate = TeletextBitRate * 8.0;
        int length = (int)Math.Ceiling(options.LineLength * targetRate / options.SampleRate) + 64;
        var left = new int[length];
        var fraction = new float[length];
        double sourceStep = options.SampleRate / targetRate;
        for (int i = 0; i < length; i++)
        {
            double source = i * sourceStep;
            left[i] = Math.Min((int)source, options.LineLength - 1);
            fraction[i] = (float)(source - left[i]);
        }
        return new VbiResamplePlan(length, left, fraction);
    }

    private static float[]? PrepareLine(
        byte[] raw,
        VbiCaptureOptions options,
        VbiResamplePlan plan)
    {
        float[] resampled = ArrayPool<float>.Shared.Rent(plan.Length);
        try
        {
            for (int i = 0; i < plan.Length; i++)
            {
                int left = plan.Left[i];
                int right = Math.Min(left + 1, options.LineLength - 1);
                float leftValue;
                float rightValue;
                if (options.IsUInt16)
                {
                    int leftOffset = left * 2;
                    int rightOffset = right * 2;
                    leftValue = (raw[leftOffset] | (raw[leftOffset + 1] << 8)) / 256f;
                    rightValue = (raw[rightOffset] | (raw[rightOffset + 1] << 8)) / 256f;
                }
                else
                {
                    leftValue = raw[left];
                    rightValue = raw[right];
                }
                resampled[i] = leftValue + (rightValue - leftValue) * plan.Fraction[i];
            }

        double bitWidth = options.SampleRate / TeletextBitRate;
        int searchStart = Math.Max(0, (int)Math.Floor(options.LineStart * 8 / bitWidth));
        int searchEnd = Math.Min(plan.Length - 24 * 8 - 1, (int)Math.Ceiling(options.LineStartEnd * 8 / bitWidth));
        if (searchEnd <= searchStart) return null;
        ReadOnlySpan<byte> reference = VbiPatternResources.ObservedCriFc;

        // Match vhs-teletext's staged lock instead of exhaustively comparing the
        // entire observed CRI/FC at every possible start. First locate the strongest
        // rising edge in a lightly smoothed start window.
        int searchLength = searchEnd - searchStart + 1;
        Span<float> smoothed = searchLength <= 256
            ? stackalloc float[searchLength]
            : new float[searchLength];
        float signalMax = float.MinValue;
        const int smoothRadius = 4;
        for (int i = 0; i < searchLength; i++)
        {
            int from = Math.Max(0, searchStart + i - smoothRadius);
            int to = Math.Min(plan.Length - 1, searchStart + i + smoothRadius);
            float sum = 0;
            for (int sample = from; sample <= to; sample++) sum += resampled[sample];
            float value = sum / (to - from + 1);
            smoothed[i] = value;
            signalMax = Math.Max(signalMax, value);
        }
        if (signalMax < 64) return null;

        float accumulatedMax = smoothed[0];
        float previousAccumulated = accumulatedMax;
        float strongestRise = float.MinValue;
        int roughStart = searchStart;
        for (int i = 1; i < smoothed.Length; i++)
        {
            accumulatedMax = Math.Max(accumulatedMax, smoothed[i]);
            float rise = accumulatedMax - previousAccumulated;
            if (rise > strongestRise)
            {
                strongestRise = rise;
                roughStart = searchStart + i;
            }
            previousAccumulated = accumulatedMax;
        }

        // Lock to the distinctive 01110 transition at the CRI/framing boundary.
        double bestClockConfidence = double.MinValue;
        int clockRoll = 0;
        int minimumRoll = Math.Max(-30, 8 - roughStart);
        Span<float> clockBits = stackalloc float[6];
        for (int roll = minimumRoll; roll < 20; roll++)
        {
            int start = roughStart + roll;
            if (start + 21 * 8 > plan.Length) break;
            for (int bit = 0; bit < 6; bit++)
            {
                float sum = 0;
                int offset = start + (15 + bit) * 8;
                for (int sample = 0; sample < 8; sample++) sum += resampled[offset + sample];
                clockBits[bit] = sum / 8;
            }
            double confidence = clockBits[1] + clockBits[2] + clockBits[3]
                                - clockBits[0] - clockBits[4] - clockBits[5];
            if (confidence > bestClockConfidence) { bestClockConfidence = confidence; clockRoll = roll; }
        }
        int clockStart = roughStart + clockRoll;

        // Only eight final sample positions are compared with observed_crifc.
        double bestCriFcScore = double.MaxValue;
        int bestStart = -1;
        const int gradientLength = 16 * 8;
        for (int roll = -4; roll < 4; roll++)
        {
            int sliceStart = clockStart + roll + 8 * 8;
            if (sliceStart < 0 || sliceStart + gradientLength > plan.Length) continue;
            double score = 0;
            for (int i = 0; i < gradientLength; i++)
            {
                double observedGradient = i switch
                {
                    0 => resampled[sliceStart + 1] - resampled[sliceStart],
                    gradientLength - 1 => resampled[sliceStart + i] - resampled[sliceStart + i - 1],
                    _ => (resampled[sliceStart + i + 1] - resampled[sliceStart + i - 1]) * 0.5,
                };
                int referenceIndex = 8 * 8 + i;
                double referenceGradient = i switch
                {
                    0 => reference[referenceIndex + 1] - reference[referenceIndex],
                    gradientLength - 1 => reference[referenceIndex] - reference[referenceIndex - 1],
                    _ => (reference[referenceIndex + 1] - reference[referenceIndex - 1]) * 0.5,
                };
                double difference = observedGradient - referenceGradient;
                score += difference * difference;
            }
            if (score < bestCriFcScore) { bestCriFcScore = score; bestStart = clockStart + roll; }
        }
        if (bestStart < 0 || bestStart + 368 * 8 > plan.Length) return null;

        // Do not launch the very expensive 8K/32K/64K-pattern OpenCL matcher for
        // arbitrary VBI noise. vhs-teletext performs the same kind of early
        // is_teletext rejection before deconvolution. Correlating the known 16-bit
        // alternating clock run-in plus framing byte is cheap and highly selective.
        Span<float> criFcBits = stackalloc float[24];
        float criFcMinimum = float.MaxValue;
        float criFcMaximum = float.MinValue;
        for (int bit = 0; bit < criFcBits.Length; bit++)
        {
            float sum = 0;
            int offset = bestStart + bit * 8;
            for (int sample = 0; sample < 8; sample++) sum += resampled[offset + sample];
            float average = sum / 8;
            criFcBits[bit] = average;
            criFcMinimum = Math.Min(criFcMinimum, average);
            criFcMaximum = Math.Max(criFcMaximum, average);
        }
        float criFcRange = criFcMaximum - criFcMinimum;
        if (criFcRange < 28) return null;
        float criFcMidpoint = (criFcMinimum + criFcMaximum) * 0.5f;
        double signedCorrelation = 0;
        double absoluteEnergy = 0;
        ReadOnlySpan<sbyte> idealCriFc = VbiPatternResources.IdealCriFc;
        for (int bit = 0; bit < criFcBits.Length; bit++)
        {
            double centered = criFcBits[bit] - criFcMidpoint;
            signedCorrelation += centered * idealCriFc[bit];
            absoluteEnergy += Math.Abs(centered);
        }
        double criFcConfidence = absoluteEnergy > 0
            ? signedCorrelation / absoluteEnergy
            : 0;
        if (criFcConfidence < 0.35) return null;

        var bits = new float[368];
        float bitsMin = float.MaxValue, bitsMax = float.MinValue;
        for (int bit = 0; bit < bits.Length; bit++)
        {
            float sum = 0;
            for (int sample = 0; sample < 8; sample++) sum += resampled[bestStart + bit * 8 + sample];
            bits[bit] = sum / 8;
            bitsMin = Math.Min(bitsMin, bits[bit]); bitsMax = Math.Max(bitsMax, bits[bit]);
        }
        float range = Math.Max(bitsMax - bitsMin, 1);
        for (int i = 0; i < bits.Length; i++) bits[i] = Math.Clamp((bits[i] - bitsMin) * 255f / range, 0, 255);
        return bits;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(resampled);
        }
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
