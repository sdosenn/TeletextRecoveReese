#nullable enable
using TeletextRecoveReese.Core;
using TeletextRecoveReese.Core.Vgb100;

internal static class Vgb100Regressions
{
    public static void Run()
    {
        ParserAssemblesFragmentedFrames(Vgb100VbiParser.PalLinesPerField);
        ParserAssemblesFragmentedFrames(Vgb100VbiParser.NtscLinesPerField);
        ParserWaitsForFirstField();
        VideoParserInterleavesFieldsAndSwapsBytes(Vgb100VideoParser.PalHeight);
        VideoParserInterleavesFieldsAndSwapsBytes(Vgb100VideoParser.NtscHeight);
        ProbeAndStreamingProgramTheDevice();
        NtscProgramsTheDecoderTiming();
        LibUsbAndOpenClLoadTogether();
    }

    private static byte[] Line(byte code, byte fill)
    {
        var line = new byte[4 + Vgb100VbiParser.SamplesPerLine];
        line[0] = 0xff;
        line[1] = 0x00;
        line[2] = 0x00;
        line[3] = code;
        Array.Fill(line, fill, 4, Vgb100VbiParser.SamplesPerLine);
        return line;
    }

    private static byte[] Field(byte code, byte firstFill, int lines)
    {
        var data = new List<byte>();
        for (int line = 0; line < lines; line++)
        {
            data.AddRange(new byte[] { 0x10, 0x80, 0xff, 0x01 }); // inter-line noise and a false marker
            data.AddRange(Line(code, (byte)(firstFill + line)));
        }
        return data.ToArray();
    }

    private static void ParserAssemblesFragmentedFrames(int linesPerField)
    {
        var frames = new List<byte[]>();
        var parser = new Vgb100VbiParser(frames.Add, linesPerField);
        // Each field carries one surplus VBI line that must not shift the next frame.
        int lines = linesPerField + 1;
        byte[] stream = [.. Field(0x20, 1, lines), .. Field(0x60, 101, lines), .. Field(0x20, 1, lines), .. Field(0x60, 101, lines)];
        for (int offset = 0; offset < stream.Length; offset += 7)
            parser.Feed(stream.AsSpan(offset, Math.Min(7, stream.Length - offset)));

        if (frames.Count != 2) throw new Exception($"VGB100 parser produced {frames.Count} frames instead of 2.");
        foreach (byte[] frame in frames)
        {
            if (frame.Length != parser.FrameSize) throw new Exception("VGB100 frame has the wrong size.");
            for (int line = 0; line < 2 * parser.LinesPerField; line++)
            {
                byte expected = line < parser.LinesPerField
                    ? (byte)(1 + line)
                    : (byte)(101 + line - parser.LinesPerField);
                ReadOnlySpan<byte> samples = frame.AsSpan(line * Vgb100VbiParser.SamplesPerLine, Vgb100VbiParser.SamplesPerLine);
                if (samples.ContainsAnyExcept(expected))
                    throw new Exception($"VGB100 frame line {line} holds the wrong samples.");
            }
        }
        Console.WriteLine($"PASS: VGB100 parser assembles fragmented {linesPerField}-line fields and drops surplus lines");
    }

    private static void ParserWaitsForFirstField()
    {
        int frames = 0;
        var parser = new Vgb100VbiParser(_ => frames++);
        parser.Feed(Field(0x60, 1, 18)); // capture starts in the second field
        if (frames != 0) throw new Exception("VGB100 parser emitted a frame without its first field.");
        parser.Feed(Field(0x20, 1, 18));
        parser.Feed(Field(0x60, 1, 18));
        if (frames != 1) throw new Exception("VGB100 parser did not resynchronise on the first field.");
        Console.WriteLine("PASS: VGB100 parser resynchronises on the first field");
    }

    private static void VideoParserInterleavesFieldsAndSwapsBytes(int height)
    {
        var frames = new List<byte[]>();
        var parser = new Vgb100VideoParser(frame => frames.Add(frame.ToArray()), height);
        var stream = new List<byte>();
        void AddField(byte code, byte fieldTag)
        {
            stream.AddRange(new byte[] { 0xff, 0x00, 0x00, 0xb6 }); // blanking code ends the previous field
            for (int line = 0; line < parser.LinesPerField; line++)
            {
                stream.AddRange(new byte[] { 0xff, 0x00, 0x00, code });
                for (int pair = 0; pair < Vgb100VideoParser.BytesPerLine / 2; pair++)
                    stream.AddRange(new[] { fieldTag, (byte)line }); // UYVY order: chroma, luma
            }
        }
        AddField(0xc0, 9); // trailing second field before the first complete frame
        AddField(0x80, 1);
        AddField(0xc0, 2);
        byte[] data = stream.ToArray();
        for (int offset = 0; offset < data.Length; offset += 2891) // odd chunks split byte pairs
            parser.Feed(data.AsSpan(offset, Math.Min(2891, data.Length - offset)));

        if (frames.Count != 1) throw new Exception($"VGB100 video parser produced {frames.Count} frames instead of 1.");
        byte[] frame = frames[0];
        for (int row = 0; row < parser.Height; row++)
        {
            byte luma = (byte)(row / 2);
            byte chroma = (byte)(row % 2 == 0 ? 1 : 2);
            int start = row * Vgb100VideoParser.BytesPerLine;
            if (frame[start] != luma || frame[start + 1] != chroma
                || frame[start + Vgb100VideoParser.BytesPerLine - 2] != luma)
                throw new Exception($"VGB100 video row {row} is not interleaved YUYV.");
        }
        Console.WriteLine($"PASS: VGB100 video parser interleaves {height}-line fields and swaps UYVY to YUYV");
    }

    private static void ProbeAndStreamingProgramTheDevice()
    {
        var usb = new SimulatedCx23102();
        var device = new Vgb100Device(usb);
        device.Probe();
        device.OpenVbi();
        device.PrepareVbiStreaming();
        device.StartVbiStreaming();

        // Spot checks of the AUGUST_VGB100 profile after probe and stream start.
        if (usb.I2c(0x30, 0x00) != 0xbe) throw new Exception("VGB100 AFE TUNE1 is not 0xbe.");
        if ((usb.I2cWord(0x44, 0x474) & 0xff3ff3ff) != 0x28244024) throw new Exception("VGB100 PAL vertical timing is wrong.");
        if ((usb.I2cWord(0x44, 0x470) & 0x3ff) != 0x7d) throw new Exception("VGB100 PAL HBLANK is not 125.");
        if ((usb.I2cWord(0x44, 0x404) & 0x8) == 0) throw new Exception("VGB100 raw VBI output is not enabled.");
        if ((usb.I2cWord(0x44, 0x400) & 0xf) != 4) throw new Exception("VGB100 decoder is not set to PAL.");
        if ((usb.Bridge[0x18] & 0x10) == 0) throw new Exception("VGB100 VBI endpoint is not enabled.");
        if ((usb.Bridge[0x04] & 0x300) != 0x300) throw new Exception("VGB100 TS mode is not VANC.");
        if ((usb.Bridge[0x74] & 0x7f) != 0x78) throw new Exception("VGB100 bridge power is not external A/V.");
        if (!usb.ClearedHalts.Contains(Vgb100Device.VbiEndpoint)) throw new Exception("VGB100 VBI endpoint halt was not cleared.");

        // The first transfers follow cx231xx_init_dev(): board config, then the
        // tuner_sif_gpio direction, then bridge power.
        (bool In, byte Request, ushort Value, ushort Index)[] expectedStart =
        [
            (true, 0x0d, 0x000f, 0x0000),
            (false, 0x08, 0x0000, 0x0001),
            (true, 0x0d, 0x000f, 0x0074),
        ];
        for (int i = 0; i < expectedStart.Length; i++)
        {
            var actual = usb.Log[i];
            if ((actual.In, actual.Request, actual.Value, actual.Index) != expectedStart[i])
                throw new Exception($"VGB100 transfer {i} is {actual}, expected {expectedStart[i]}.");
        }

        // AFE byte write encoding: I2C master 0, 7-bit address 0x30, 2-byte subaddress.
        if (!usb.Log.Any(t => !t.In && t.Request == 0x00 && t.Value == 0x6029 && t.Index == 0x0000 && t.Data.SequenceEqual(new byte[] { 0xbe })))
            throw new Exception("VGB100 AFE write is not encoded like cx231xx_send_usb_command().");

        device.StopVbiStreaming();
        if ((usb.Bridge[0x18] & 0x10) != 0) throw new Exception("VGB100 VBI endpoint was not disabled.");
        Console.WriteLine($"PASS: VGB100 probe and VBI start program the CX23102 ({usb.Log.Count} control transfers)");
    }

    private static void LibUsbAndOpenClLoadTogether()
    {
        // The capture dialog looks for VGB100 cards before deconvolution starts
        // OpenCL; both bindings resolve native libraries in the Core assembly.
        _ = Vgb100VbiCaptureStream.IsLibUsbAvailable;
        string device = VbiDeconvolutionEngine.ValidateOpenClBackend();
        Console.WriteLine($"PASS: libusb and OpenCL bindings load in one process ({device})");
    }

    private static void NtscProgramsTheDecoderTiming()
    {
        var usb = new SimulatedCx23102();
        var device = new Vgb100Device(usb);
        device.Probe();
        device.SetStandard(Vgb100Standard.NtscM);
        // cx231xx_do_mode_ctrl_overrides() for NTSC
        uint vertical = usb.I2cWord(0x44, 0x474);
        if ((vertical & 0x3ff) != 0x18 || (vertical & 0x3ff000) != 0x1e7000 || (vertical & 0xff000000) != 0x1c000000)
            throw new Exception($"VGB100 NTSC vertical timing is 0x{vertical:x8}.");
        if ((usb.I2cWord(0x44, 0x470) & 0x3ff) != 0x79) throw new Exception("VGB100 NTSC HBLANK is not 0x79.");
        if ((usb.I2cWord(0x44, 0x400) & 0xf) != 1) throw new Exception("VGB100 decoder is not set to NTSC-M.");
        device.SetStandard(Vgb100Standard.Pal);
        if ((usb.I2cWord(0x44, 0x474) & 0xff3ff3ff) != 0x28244024) throw new Exception("VGB100 PAL timing was not restored.");
        Console.WriteLine("PASS: VGB100 NTSC and PAL program the decoder timing");
    }

    /// <summary>Register memory model of the CX23102 bridge and its I2C devices.</summary>
    private sealed class SimulatedCx23102 : ICx231xxTransport
    {
        public readonly Dictionary<ushort, uint> Bridge = new() { [0x00] = 0x41, [0x74] = 0x00 };
        private readonly Dictionary<(int Address, int Register), byte> _i2c = new()
        {
            [(0x44, 0x100)] = 0x3e,
            [(0x44, 0x101)] = 0x5a,
        };
        public readonly List<(bool In, byte Request, ushort Value, ushort Index, byte[] Data)> Log = new();
        public readonly List<byte> ClearedHalts = new();

        public byte I2c(int address, int register) => _i2c.GetValueOrDefault((address, register));

        public uint I2cWord(int address, int register) =>
            (uint)(I2c(address, register) | I2c(address, register + 1) << 8
                   | I2c(address, register + 2) << 16 | I2c(address, register + 3) << 24);

        public int ControlIn(byte request, ushort value, ushort index, Span<byte> data)
        {
            if (request == 0x0d)
            {
                BitConverter.TryWriteBytes(data, Bridge.GetValueOrDefault(index));
            }
            else if (request is >= 4 and <= 6)
            {
                int address = value >> 9;
                for (int i = 0; i < data.Length; i++)
                    data[i] = I2c(address, index + i);
            }
            Log.Add((true, request, value, index, data.ToArray()));
            return data.Length;
        }

        public int ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data)
        {
            if (request == 0x0c)
            {
                var bytes = new byte[4];
                data.CopyTo(bytes);
                Bridge[index] = BitConverter.ToUInt32(bytes);
            }
            else if (request <= 2)
            {
                int address = value >> 9;
                int subAddressLength = (value >> 2) & 3;
                int register = index;
                ReadOnlySpan<byte> payload = data;
                if (subAddressLength == 0)
                {
                    register = data[0] << 8 | data[1];
                    payload = data[2..];
                }
                for (int i = 0; i < payload.Length; i++)
                    _i2c[(address, register + i)] = payload[i];
                // The AFE PLL reports enabled once its power-down register is written.
                if (address == 0x30 && register == 0x08) _i2c[(0x30, 0x08)] = 0x18;
            }
            Log.Add((false, request, value, index, data.ToArray()));
            return data.Length;
        }

        public void SetInterfaceAltSetting(int interfaceNumber, int alternateSetting) { }

        public void ClearHalt(byte endpoint) => ClearedHalts.Add(endpoint);

        public void Delay(int milliseconds) { }
    }
}
