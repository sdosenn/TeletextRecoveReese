using System.Buffers;

namespace TeletextRecoveReese.Core;

public static class VbiDeconvolutionEngine
{
    private const double TeletextBitRate = 6_937_500.0;
    private const int TeletextPacketBits = 16 + 8 + 42 * 8;
    private const int DecoderInputBits = 368;
    public static readonly bool UseLegacyFixedDetectionForTest = false;

    public static string ValidateOpenClBackend()
    {
        using var matcher = new OpenClVhsPatternMatcher();
        return matcher.DeviceDescription;
    }

    public static int?[] FindClockRunInOffsets(
        byte[] rawFrame,
        VbiCaptureOptions options,
        int lineCount,
        int maximumOffsetSamples = 300,
        int lineMask = -1)
    {
        int sampleBytes = options.IsUInt16 ? 2 : 1;
        int lineBytes = checked(options.LineLength * sampleBytes);
        int availableLines = Math.Min(lineCount, rawFrame.Length / lineBytes);
        var offsets = new int?[lineCount];
        if (availableLines <= 0) return offsets;

        VbiResamplePlan plan = CreateResamplePlan(options);
        // The offset belongs to the actual CRI start marker, not to the middle of
        // the movable search window. Keeping those coordinate systems separate
        // lets the marker drift a little before or after the preset window.
        int targetPosition = options.LineStart;
        for (int line = 0; line < availableLines; line++)
        {
            if ((lineMask & (1 << line)) == 0) continue;
            byte[] rawLine = rawFrame.AsSpan(line * lineBytes, lineBytes).ToArray();
            foreach (int searchOffset in EnumerateSearchOffsets(maximumOffsetSamples))
            {
                PreparedLine? prepared = PrepareLine(
                    rawLine, options, plan, searchOffset,
                    out int detectedStartSample,
                    allowClippedClockRunIn: true,
                    detectStartOnly: true);
                if (prepared is null) continue;
                offsets[line] = Math.Clamp(
                    detectedStartSample - targetPosition,
                    -maximumOffsetSamples,
                    maximumOffsetSamples);
                break;
            }
        }
        return offsets;

        static IEnumerable<int> EnumerateSearchOffsets(int maximum)
        {
            yield return 0;
            for (int offset = 10; offset <= maximum; offset += 10)
            {
                yield return offset;
                yield return -offset;
            }
        }
    }

    public static VbiLineTiming?[] FindLineTimings(
        byte[] rawFrame,
        VbiCaptureOptions options,
        IReadOnlyList<int> searchOffsets,
        IReadOnlyList<double>? manualPacketSpanSamples,
        int lineCount,
        int lineMask = -1)
    {
        int sampleBytes = options.IsUInt16 ? 2 : 1;
        int lineBytes = checked(options.LineLength * sampleBytes);
        int availableLines = Math.Min(lineCount, rawFrame.Length / lineBytes);
        var timings = new VbiLineTiming?[lineCount];
        if (availableLines <= 0) return timings;

        VbiResamplePlan plan = CreateResamplePlan(options);
        for (int line = 0; line < availableLines; line++)
        {
            if ((lineMask & (1 << line)) == 0) continue;
            byte[] rawLine = rawFrame.AsSpan(line * lineBytes, lineBytes).ToArray();
            int searchOffset = line < searchOffsets.Count ? searchOffsets[line] : 0;
            double manualPacketSpanSample = manualPacketSpanSamples is not null
                                         && line < manualPacketSpanSamples.Count
                ? manualPacketSpanSamples[line]
                : -1;
            PreparedLine? prepared = PrepareLine(
                rawLine, options, plan, searchOffset,
                out int detectedStartSample,
                out int detectedEndSample,
                manualPacketSpanSamples: manualPacketSpanSample);
            if (prepared is null || detectedEndSample < 0) continue;
            timings[line] = new VbiLineTiming(
                detectedStartSample,
                detectedEndSample,
                prepared.Pll is not null);
        }
        return timings;
    }

    public static async Task<VbiDeconvolutionResult> DeconvolveAsync(
        Stream input,
        Stream output,
        VbiCaptureOptions options,
        IProgress<VbiDeconvolutionProgress>? progress,
        IVbiDecodedPacketProgress? decodedPackets,
        CancellationToken cancellationToken,
        IVbiDeconvolutionControl? deconvolutionControl = null)
    {
        bool liveInput = !input.CanSeek;
        int sampleBytes = options.IsUInt16 ? 2 : 1;
        int lineBytes = checked(options.LineLength * sampleBytes);
        long totalPhysicalLines = liveInput ? 0 : input.Length / lineBytes;
        long totalFields = liveInput ? 0 : totalPhysicalLines / options.FieldLines;
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
        if (!liveInput) input.Position = 0;
        var captureTimer = System.Diagnostics.Stopwatch.StartNew();

        async Task<List<CapturedLine>> ReadBatchAsync()
        {
            var lines = new List<CapturedLine>(batchSize);
            while (lines.Count < batchSize && !endOfInput)
            {
                var raw = new byte[lineBytes];
                if (!await ReadExactlyOrEndAsync(input, raw, cancellationToken).ConfigureAwait(false))
                {
                    endOfInput = true;
                    break;
                }
                int capturedFieldLine = fieldLine;
                bool selected = capturedFieldLine >= options.FieldRangeStart
                    && capturedFieldLine < options.FieldRangeEnd;
                fieldLine = (fieldLine + 1) % options.FieldLines;
                if (selected) lines.Add(new CapturedLine(raw, capturedFieldLine));
            }
            return lines;
        }

        Task<PreparedLine?[]> PrepareBatchAsync(IReadOnlyList<CapturedLine> lines) => Task.Run(() =>
        {
            var prepared = new PreparedLine?[lines.Count];
            if (deconvolutionControl?.Enabled == false)
                return prepared;
            Parallel.For(
                0,
                lines.Count,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                },
                index =>
                {
                    CapturedLine line = lines[index];
                    if (deconvolutionControl is not null
                        && !deconvolutionControl.GetLineDecodingEnabled(line.FieldLine))
                        return;
                    int searchOffset = 0;
                    double manualPacketSpanSamples = -1;
                    if (deconvolutionControl is not null)
                    {
                        searchOffset = deconvolutionControl.GetClockSearchOffset(line.FieldLine);
                        manualPacketSpanSamples = deconvolutionControl.GetManualPacketSpanSamples(
                            line.FieldLine);
                    }
                    prepared[index] = PrepareLine(
                        line.Raw, options, resamplePlan, searchOffset,
                        manualPacketSpanSamples: manualPacketSpanSamples);
                });
            return prepared;
        }, cancellationToken);

        List<CapturedLine> currentLines = await ReadBatchAsync().ConfigureAwait(false);
        Task<PreparedLine?[]>? currentPreparation = currentLines.Count > 0
            ? PrepareBatchAsync(currentLines)
            : null;
        while (currentPreparation is not null)
        {
            PreparedLine?[] preparedLines = await currentPreparation.ConfigureAwait(false);
            // Start CPU preparation of the next batch before matching this batch on
            // OpenCL. This keeps the CPU and GPU busy at the same time.
            List<CapturedLine> nextLines = await ReadBatchAsync().ConfigureAwait(false);
            Task<PreparedLine?[]>? nextPreparation = nextLines.Count > 0
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
                    PreparedLine? prepared = preparedLines[index];
                    if (prepared is null) continue;
                    float[] bits = prepared.Pll ?? prepared.Nominal;
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
                    progress?.Report(new VbiDeconvolutionProgress(
                        processed, totalLines, teletext, written,
                        liveInput && captureTimer.Elapsed.TotalSeconds > 0
                            ? processed / (double)options.FieldLines / captureTimer.Elapsed.TotalSeconds
                            : 0));
            }

            currentLines = nextLines;
            currentPreparation = nextPreparation;
        }
        if (previewBatch.Count > 0 && decodedPackets?.IsEnabled == true)
            decodedPackets?.Report(previewBatch.ToArray());
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new VbiDeconvolutionProgress(
            processed, totalLines, teletext, written,
            liveInput && captureTimer.Elapsed.TotalSeconds > 0
                ? processed / (double)options.FieldLines / captureTimer.Elapsed.TotalSeconds
                : 0));
        return new VbiDeconvolutionResult(processed, teletext, written,
            $"{matchers[0].DeviceDescription} ({workerCount} parallel OpenCL workers)");
        }
        finally
        {
            foreach (OpenClVhsPatternMatcher? matcher in matchers)
                matcher?.Dispose();
        }
    }

    private static byte[] DecodePacket(
        OpenClVhsPatternMatcher matcher,
        float[] bits)
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
    private sealed record CapturedLine(byte[] Raw, int FieldLine);
    private sealed record PreparedLine(float[] Nominal, float[]? Pll);

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

    private static PreparedLine? PrepareLine(
        byte[] raw,
        VbiCaptureOptions options,
        VbiResamplePlan plan,
        int searchOffsetSamples = 0,
        double manualPacketSpanSamples = -1,
        bool allowClippedClockRunIn = false,
        bool detectStartOnly = false)
        => PrepareLine(
            raw, options, plan, searchOffsetSamples, out _, out _,
            manualPacketSpanSamples, allowClippedClockRunIn, detectStartOnly);

    private static PreparedLine? PrepareLine(
        byte[] raw,
        VbiCaptureOptions options,
        VbiResamplePlan plan,
        int searchOffsetSamples,
        out int detectedStartSample,
        double manualPacketSpanSamples = -1,
        bool allowClippedClockRunIn = false,
        bool detectStartOnly = false)
        => PrepareLine(
            raw, options, plan, searchOffsetSamples,
            out detectedStartSample, out _, manualPacketSpanSamples,
            allowClippedClockRunIn, detectStartOnly);

    private static PreparedLine? PrepareLine(
        byte[] raw,
        VbiCaptureOptions options,
        VbiResamplePlan plan,
        int searchOffsetSamples,
        out int detectedStartSample,
        out int detectedEndSample,
        double manualPacketSpanSamples = -1,
        bool allowClippedClockRunIn = false,
        bool detectStartOnly = false)
    {
        detectedStartSample = -1;
        detectedEndSample = -1;
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
        int effectiveSearchOffset = UseLegacyFixedDetectionForTest
            ? 0
            : searchOffsetSamples;
        int shiftedStart = options.LineStart + effectiveSearchOffset;
        int shiftedEnd = options.LineStartEnd + effectiveSearchOffset;
        if ((allowClippedClockRunIn || shiftedStart < 0)
            && TryFindClippedClockRunIn(
                resampled, plan.Length, options,
                out int clippedStart,
                out float clippedLow,
                out float clippedHigh))
        {
            if (detectStartOnly)
            {
                detectedStartSample = (int)Math.Round(
                    clippedStart * bitWidth / 8.0);
                return new PreparedLine(Array.Empty<float>(), null);
            }
            PreparedLine? clipped = BuildPreparedLine(
                resampled, plan.Length, clippedStart, bitWidth,
                manualPacketSpanSamples, clippedLow, clippedHigh,
                out detectedEndSample);
            if (clipped is not null)
            {
                detectedStartSample = (int)Math.Round(
                    clippedStart * bitWidth / 8.0);
                return clipped;
            }
        }
        int searchStart = Math.Max(0, (int)Math.Floor(shiftedStart * 8 / bitWidth));
        int searchEnd = Math.Min(plan.Length - 24 * 8 - 1, (int)Math.Ceiling(shiftedEnd * 8 / bitWidth));
        if (searchEnd <= searchStart) return null;

        if (!UseLegacyFixedDetectionForTest)
        {
            int varianceEnd = Math.Min(plan.Length, searchEnd + DecoderInputBits * 8);
            double sampleSum = 0;
            double squareSum = 0;
            int varianceCount = varianceEnd - searchStart;
            for (int sample = searchStart; sample < varianceEnd; sample++)
            {
                double value = resampled[sample];
                sampleSum += value;
                squareSum += value * value;
            }
            double mean = sampleSum / Math.Max(varianceCount, 1);
            double standardDeviation = Math.Sqrt(Math.Max(
                0, squareSum / Math.Max(varianceCount, 1) - mean * mean));
            if (standardDeviation < options.StandardDeviationThreshold)
                return null;
        }

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
        float signalLevelThreshold = UseLegacyFixedDetectionForTest
            ? 64
            : options.SignalLevelThreshold;
        if (signalMax < signalLevelThreshold) return null;

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
        if (bestStart < 0 || bestStart + 24 * 8 > plan.Length) return null;

        // Do not launch the very expensive 8K/32K/64K-pattern OpenCL matcher for
        // arbitrary VBI noise. vhs-teletext performs the same kind of early
        // is_teletext rejection before deconvolution. Correlating the known 16-bit
        // alternating clock run-in plus framing byte is cheap and highly selective.
        Span<float> criFcBits = stackalloc float[24];
        float criFcMinimum = float.MaxValue;
        float criFcMaximum = float.MinValue;
        for (int bit = 0; bit < criFcBits.Length; bit++)
        {
            float average = AverageNominalBit(resampled, bestStart, bit);
            criFcBits[bit] = average;
            criFcMinimum = Math.Min(criFcMinimum, average);
            criFcMaximum = Math.Max(criFcMaximum, average);
        }
        float criFcRange = criFcMaximum - criFcMinimum;
        float criFcRangeThreshold = UseLegacyFixedDetectionForTest
            ? 28
            : options.CriFcRangeThreshold;
        if (criFcRange < criFcRangeThreshold) return null;
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
        double criFcConfidenceThreshold = UseLegacyFixedDetectionForTest
            ? 0.35
            : options.CriFcConfidenceThreshold;
        if (criFcConfidence < criFcConfidenceThreshold) return null;

        if (detectStartOnly)
        {
            detectedStartSample = (int)Math.Round(bestStart * bitWidth / 8.0);
            return new PreparedLine(Array.Empty<float>(), null);
        }

        PreparedLine? preparedLine = BuildPreparedLine(
            resampled, plan.Length, bestStart, bitWidth,
            manualPacketSpanSamples, null, null,
            out detectedEndSample);
        if (preparedLine is null) return null;
        detectedStartSample = (int)Math.Round(bestStart * bitWidth / 8.0);
        return preparedLine;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(resampled);
        }
    }

    private static bool TryFindClippedClockRunIn(
        float[] samples,
        int sampleCount,
        VbiCaptureOptions options,
        out int packetStart,
        out float signalLow,
        out float signalHigh)
    {
        packetStart = 0;
        signalLow = 0;
        signalHigh = 0;
        ReadOnlySpan<sbyte> criAndFraming = VbiPatternResources.IdealCriFc;
        const int samplesPerBit = VbiPatternResources.ObservedCriFcSamplesPerBit;
        const int clockRunInSamples = 16 * samplesPerBit;
        double minimumConfidence = options.CriFcConfidenceThreshold;
        double bestConfidence = double.MinValue;
        float bestRange = 0;
        Span<float> bitLevels = stackalloc float[VbiPatternResources.ObservedCriFcBits];

        // A clipped candidate must begin before the captured line. Correlate every
        // complete CRI/FC bit that is still visible, not only the framing byte.
        // The alternating visible CRI tail makes this much less likely to lock to
        // an unrelated byte while still allowing the leading run-in to be absent.
        for (int candidateStart = -clockRunInSamples + 1;
             candidateStart < 0;
             candidateStart++)
        {
            int firstVisibleBit = Math.Max(
                0, (int)Math.Ceiling(-candidateStart / (double)samplesPerBit));
            int lastVisibleBitExclusive = Math.Min(
                criAndFraming.Length,
                (sampleCount - candidateStart) / samplesPerBit);
            int visibleBits = lastVisibleBitExclusive - firstVisibleBit;
            if (visibleBits < 8 || lastVisibleBitExclusive < criAndFraming.Length)
                continue;

            float minimum = float.MaxValue;
            float maximum = float.MinValue;
            for (int bit = firstVisibleBit; bit < lastVisibleBitExclusive; bit++)
            {
                float sum = 0;
                int offset = candidateStart + bit * samplesPerBit;
                for (int sample = 0; sample < samplesPerBit; sample++)
                    sum += samples[offset + sample];
                float average = sum / samplesPerBit;
                bitLevels[bit] = average;
                minimum = Math.Min(minimum, average);
                maximum = Math.Max(maximum, average);
            }

            float range = maximum - minimum;
            if (range < options.CriFcRangeThreshold) continue;
            float midpoint = (minimum + maximum) * 0.5f;
            double correlation = 0;
            double energy = 0;
            for (int bit = firstVisibleBit; bit < lastVisibleBitExclusive; bit++)
            {
                double centered = bitLevels[bit] - midpoint;
                correlation += centered * criAndFraming[bit];
                energy += Math.Abs(centered);
            }
            double confidence = energy > 0 ? correlation / energy : 0;
            if (confidence < minimumConfidence
                || confidence < bestConfidence
                || confidence == bestConfidence && range <= bestRange)
                continue;

            bestConfidence = confidence;
            bestRange = range;
            packetStart = candidateStart;
            signalLow = minimum;
            signalHigh = maximum;
        }
        return bestConfidence >= minimumConfidence;
    }

    private static PreparedLine? BuildPreparedLine(
        float[] samples,
        int sampleCount,
        int start,
        double bitWidth,
        double manualPacketSpanSamples,
        float? clippedSignalLow,
        float? clippedSignalHigh,
        out int detectedEndSample)
    {
        detectedEndSample = -1;
        double manualEndPosition = manualPacketSpanSamples >= 0
            ? start + manualPacketSpanSamples * 8.0 / bitWidth
            : -1;
        double[]? adjustedBoundaries = manualEndPosition > start
            ? BuildManualEndBoundaries(
                start, manualEndPosition, DecoderInputBits, sampleCount)
            : null;
        if (adjustedBoundaries is null && start + DecoderInputBits * 8 > sampleCount)
            return null;

        var nominalBits = new float[DecoderInputBits];
        for (int bit = 0; bit < nominalBits.Length; bit++)
        {
            int bitStart = start + bit * 8;
            if (bitStart >= 0)
            {
                nominalBits[bit] = AverageNominalBit(samples, start, bit);
                continue;
            }

            // Only the leading clock run-in can be outside the captured line.
            // Recreate it at the levels measured from the visible framing byte;
            // useful packet bytes remain sampled from the real signal.
            if (bit >= 16
                || clippedSignalLow is not float low
                || clippedSignalHigh is not float high)
                return null;
            nominalBits[bit] = VbiPatternResources.IdealCriFc[bit] > 0
                ? high
                : low;
        }
        NormalizeBits(nominalBits);

        float[]? adjustedBits = null;
        if (adjustedBoundaries is not null)
        {
            adjustedBits = new float[DecoderInputBits];
            for (int bit = 0; bit < adjustedBits.Length; bit++)
            {
                if (adjustedBoundaries[bit] < 0)
                {
                    if (bit >= 16
                        || clippedSignalLow is not float low
                        || clippedSignalHigh is not float high)
                        return null;
                    adjustedBits[bit] = VbiPatternResources.IdealCriFc[bit] > 0
                        ? high
                        : low;
                    continue;
                }
                adjustedBits[bit] = AveragePllBit(
                    samples, adjustedBoundaries[bit], adjustedBoundaries[bit + 1],
                    sampleCount);
            }
            NormalizeBits(adjustedBits);
        }

        // The matcher keeps eight extra working bits for pattern context, but the
        // physical line packet itself ends after CRI + framing code + 42 bytes.
        double endPosition = adjustedBoundaries is not null
            ? adjustedBoundaries[TeletextPacketBits]
            : start + TeletextPacketBits * 8.0;
        detectedEndSample = (int)Math.Round(endPosition * bitWidth / 8.0);
        return new PreparedLine(nominalBits, adjustedBits);
    }

    private static double[]? BuildManualEndBoundaries(
        int start,
        double packetEnd,
        int bitCount,
        int sampleCount)
    {
        double period = (packetEnd - start) / TeletextPacketBits;
        // Reject an accidental marker close to the start, but leave enough range
        // for severely time-compressed or expanded VHS lines.
        if (period is < 4.0 or > 12.0) return null;

        var boundaries = new double[bitCount + 1];
        for (int bit = 0; bit <= bitCount; bit++)
        {
            double position = start + bit * period;
            if (position < 0 || position >= sampleCount - 1) return null;
            boundaries[bit] = position;
        }
        return boundaries;
    }

    private static float AverageNominalBit(
        float[] samples,
        int start,
        int bit)
    {
        float sum = 0;
        int offset = start + bit * 8;
        for (int point = 0; point < 8; point++)
            sum += samples[offset + point];
        return sum / 8;
    }

    private static void NormalizeBits(float[] bits)
    {
        float minimum = float.MaxValue;
        float maximum = float.MinValue;
        foreach (float bit in bits)
        {
            minimum = Math.Min(minimum, bit);
            maximum = Math.Max(maximum, bit);
        }
        float range = Math.Max(maximum - minimum, 1);
        for (int index = 0; index < bits.Length; index++)
            bits[index] = Math.Clamp((bits[index] - minimum) * 255f / range, 0, 255);
    }

    private static float AveragePllBit(
        float[] samples,
        double start,
        double end,
        int sampleCount)
    {
        float sum = 0;
        double width = end - start;
        for (int point = 0; point < 8; point++)
        {
            double position = start + point / 8.0 * width;
            int left = Math.Clamp((int)Math.Floor(position), 0, sampleCount - 1);
            int right = Math.Min(left + 1, sampleCount - 1);
            float fraction = (float)(position - left);
            sum += samples[left] + (samples[right] - samples[left]) * fraction;
        }
        return sum / 8;
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
