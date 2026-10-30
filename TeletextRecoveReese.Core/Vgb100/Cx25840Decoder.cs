// SPDX-License-Identifier: GPL-2.0-or-later
// The CX23102 integrated A/V decoder as driven by the Linux cx25840 module
// (cx25840-core.c, cx25840-audio.c), reduced to the cx231xx code paths.
namespace TeletextRecoveReese.Core.Vgb100;

internal static class V4l2Std
{
    public const ulong PalB = 0x00000001, PalB1 = 0x00000002, PalG = 0x00000004, PalH = 0x00000008;
    public const ulong PalI = 0x00000010, PalD = 0x00000020, PalD1 = 0x00000040, PalK = 0x00000080;
    public const ulong PalM = 0x00000100, PalN = 0x00000200, PalNc = 0x00000400, Pal60 = 0x00000800;
    public const ulong NtscM = 0x00001000, NtscMJp = 0x00002000, Ntsc443 = 0x00004000, NtscMKr = 0x00008000;
    public const ulong Pal = PalB | PalB1 | PalG | PalH | PalI | PalD | PalD1 | PalK;
    public const ulong Ntsc = NtscM | NtscMJp | NtscMKr;
    public const ulong Secam = 0x00ff0000;
    public const ulong SecamDk = 0x00020000 | 0x00100000 | 0x00200000; // D, K, K1
    public const ulong SecamL = 0x00400000, SecamLc = 0x00800000;
    public const ulong Std525_60 = PalM | Pal60 | Ntsc | Ntsc443;
    public const ulong Std625_50 = Pal | PalN | PalNc | Secam;
}

internal sealed class Cx25840Decoder
{
    // include/media/drv-intf/cx25840.h
    public const uint Composite7 = 7;
    public const uint Audio8 = 8;
    private const uint AudioSerial = 0;
    private const uint Vin1Ch1 = 0x80000000;
    private const uint SvideoOn = 0x80000100;
    private const uint ComponentOn = 0x80000200;
    private const uint SvideoChroma7 = 0x700;

    private const byte DeviceAddress7Bit = 0x88 >> 1;

    private readonly Cx231xxBridge _bridge;
    private ulong _std = V4l2Std.NtscM;
    private uint _videoInput = Composite7;
    private uint _audioInput = Audio8;
    private const int AudioClockFrequency = 48000;
    private bool _isInitialized;

    public Cx25840Decoder(Cx231xxBridge bridge) => _bridge = bridge;

    public ushort DeviceId { get; private set; }

    // ----- register access, cx25840_write/read with the cx231xx I2C mapping -----

    private void Write(ushort address, byte value)
    {
        Span<byte> buffer = [(byte)(address >> 8), (byte)address, value];
        _bridge.SendUsbCommand(0, DeviceAddress7Bit, false, 0, 0, buffer);
    }

    private void Write4(ushort address, uint value)
    {
        Span<byte> buffer =
        [
            (byte)(address >> 8), (byte)address,
            (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
        ];
        _bridge.SendUsbCommand(0, DeviceAddress7Bit, false, 0, 0, buffer);
    }

    private byte Read(ushort address)
    {
        Span<byte> buffer = stackalloc byte[1];
        return _bridge.SendUsbCommand(0, DeviceAddress7Bit, true, 2, address, buffer) < 0 ? (byte)0 : buffer[0];
    }

    private void AndOr(ushort address, uint andMask, byte orValue) =>
        Write(address, (byte)((Read(address) & andMask) | orValue));

    // ----- probe -----

    /// <summary>cx25840_probe() for a CX2310x A/V core, including the control handler setup.</summary>
    public void Probe()
    {
        ushort deviceId = (ushort)(Read(0x101) << 8);
        deviceId |= Read(0x100);
        if ((deviceId & 0xfff0) != 0x5a30)
            throw new IOException($"VGB100: unexpected A/V decoder id 0x{deviceId:x4}.");
        DeviceId = deviceId;

        _videoInput = Composite7;
        _audioInput = Audio8;
        _std = V4l2Std.NtscM;

        int defaultVolume = Read(0x8d4);
        if (defaultVolume > 228)
        {
            defaultVolume = 228;
            Write(0x8d4, 228);
        }
        else if (defaultVolume < 20)
        {
            defaultVolume = 20;
            Write(0x8d4, 20);
        }
        int volume = (((228 - defaultVolume) >> 1) + 23) << 9;

        // v4l2_ctrl_handler_setup() in creation order.
        Write(0x414, unchecked((byte)(128 - 128)));  // brightness
        Write(0x415, 64 << 1);                       // contrast
        Write(0x420, 64 << 1);                       // saturation
        Write(0x421, 64 << 1);
        Write(0x422, 0);                             // hue
        SetVolume(volume);                           // volume/mute cluster
        SetBalance(32768);
        AndOr(0x8d9, unchecked((uint)~0x3f), (byte)(48 - 32768 * 48 / 0xffff)); // bass
        AndOr(0x8db, unchecked((uint)~0x3f), (byte)(48 - 32768 * 48 / 0xffff)); // treble
    }

    private void SetVolume(int volume)
    {
        int value = volume >> 9;
        value = value <= 23 ? 0 : value - 23;
        Write(0x8d4, (byte)(228 - value * 2));
    }

    private void SetBalance(int balance)
    {
        int value = balance >> 8;
        if (value > 0x80)
        {
            AndOr(0x8d5, 0x7f, 0x80);
            AndOr(0x8d5, unchecked((uint)~0x7f), (byte)(value & 0x7f));
        }
        else
        {
            AndOr(0x8d5, 0x7f, 0x00);
            AndOr(0x8d5, unchecked((uint)~0x7f), (byte)(0x80 - value));
        }
    }

    // ----- core ops -----

    /// <summary>
    /// load_fw: runs cx231xx_initialize() once. The audio microcontroller
    /// firmware is not uploaded; it only serves TV audio standard detection.
    /// </summary>
    public void LoadFirmware()
    {
        if (_isInitialized) return;
        Initialize();
        _isInitialized = true;
    }

    private void Initialize()
    {
        AndOr(0x102, unchecked((uint)~0x01), 0x01);   // internal reset
        AndOr(0x102, unchecked((uint)~0x01), 0x00);
        AndOr(0x803, unchecked((uint)~0x10), 0x00);   // stop microcontroller
        Write(0x398, 0);                               // DIF in reset
        Write(0x2, 0x76);                              // default xtal, no division
        Write(0x1, 0x40);                              // regulator for AUX clk
        Write4(0x33c, 0x00000001);                     // disable DIF bypass
        Write4(0x340, 0x0df7df83);                     // DIF src phase inc
        Write4(0x414, 0x00107d12);                     // luma
        Write4(0x420, 0x3d008282);                     // chroma
        Write(0x102, 0x10);                            // ADC2 input select
        Write(0x103, 0x11);                            // VIN1 & VIN5
        Write(0x400, 0);                               // format auto detect
        Write(0x401, 0xe8);                            // fast subchroma lock, crush/AGC/killer

        StandardSetup();
        SetInput(_videoInput, _audioInput);

        AndOr(0x803, unchecked((uint)~0x10), 0x10);   // start microcontroller
        Write(0x404, 0x0b);                            // CC raw enable
        Write(0x42f, 0x66);                            // CC on
        Write4(0x474, 0x1e1e601a);
    }

    /// <summary>s_routing(video).</summary>
    public void SetVideoRouting(uint input) => SetInput(input, _audioInput);

    /// <summary>s_std().</summary>
    public void SetStandard(ulong std)
    {
        if (_std == std) return;
        _std = std;
        SetV4lStandard();
    }

    private void StandardSetup()
    {
        ulong std = _std;
        int hblank, hactive, burst, vblank, vactive, sc, vblank656;
        int lumaLpf, uvLpf, comb;
        const int srcDecimation = 0x21f;

        Write(0x49f, (std & ~V4l2Std.Ntsc) != 0 ? (byte)0x11 : (byte)0x14);

        if ((std & V4l2Std.Std625_50) != 0)
        {
            hblank = 132;
            hactive = 720;
            burst = 93;
            vblank = 36;
            vactive = 580;
            vblank656 = 40;
            lumaLpf = 2;
            if ((std & V4l2Std.Secam) != 0)
            {
                uvLpf = 0;
                comb = 0;
                sc = 0x0a425f;
            }
            else if (std == V4l2Std.PalNc)
            {
                uvLpf = 1;
                comb = 0x20;
                sc = 556453;
            }
            else
            {
                uvLpf = 1;
                comb = 0x20;
                sc = 688739;
            }
        }
        else
        {
            hactive = 720;
            hblank = 122;
            vactive = 487;
            lumaLpf = 1;
            uvLpf = 1;
            if (std == V4l2Std.Pal60)
            {
                vblank = 26;
                vblank656 = 26;
                burst = 0x5b;
                lumaLpf = 2;
                comb = 0x20;
                sc = 688739;
            }
            else if (std == V4l2Std.PalM)
            {
                vblank = 20;
                vblank656 = 24;
                burst = 0x61;
                comb = 0x20;
                sc = 555452;
            }
            else
            {
                vblank = 26;
                vblank656 = 26;
                burst = 0x5b;
                comb = 0x66;
                sc = 556063;
            }
        }

        Write(0x470, (byte)hblank);
        Write(0x471, (byte)((((hblank >> 8) & 0x3) | (hactive << 4)) & 0xff));
        Write(0x472, (byte)(hactive >> 4));
        Write(0x473, (byte)burst);
        Write(0x474, (byte)vblank);
        Write(0x475, (byte)((((vblank >> 8) & 0x3) | (vactive << 4)) & 0xff));
        Write(0x476, (byte)(vactive >> 4));
        Write(0x477, (byte)vblank656);
        Write(0x478, srcDecimation & 0xff);
        Write(0x479, (srcDecimation >> 8) & 0xff);
        Write(0x47a, (byte)(lumaLpf << 6 | ((uvLpf << 4) & 0x30)));
        Write(0x47b, (byte)comb);
        Write(0x47c, (byte)sc);
        Write(0x47d, (byte)((sc >> 8) & 0xff));
        Write(0x47e, (byte)((sc >> 16) & 0xff));
        Write(0x47f, (std & V4l2Std.Std625_50) != 0 ? (byte)0x01 : (byte)0x00);
    }

    private void InputChange()
    {
        ulong std = _std;
        if ((std & V4l2Std.Secam) != 0)
        {
            Write(0x402, 0);
        }
        else
        {
            Write(0x402, 0x04);
            Write(0x49f, (std & V4l2Std.Ntsc) != 0 ? (byte)0x14 : (byte)0x11);
        }
        AndOr(0x401, unchecked((uint)~0x60), 0);
        AndOr(0x401, unchecked((uint)~0x60), 0x60);

        AndOr(0x810, unchecked((uint)~0x01), 1);
        if ((std & V4l2Std.Std525_60) != 0)
        {
            if (std == V4l2Std.NtscMJp) Write(0x808, 0xf7);
            else if (std == V4l2Std.NtscMKr) Write(0x808, 0xf8);
            else Write(0x808, 0xf6);
            Write(0x80b, 0x00);
        }
        else if ((std & V4l2Std.Pal) != 0)
        {
            Write(0x808, 0xff);
            Write(0x80b, 0x00);
        }
        else if ((std & V4l2Std.Secam) != 0)
        {
            Write(0x808, 0xff);
            bool dk = (std & V4l2Std.SecamDk) != 0;
            bool l = (std & (V4l2Std.SecamL | V4l2Std.SecamLc)) != 0;
            Write(0x80b, dk && !l ? (byte)0x00 : !dk && l ? (byte)0x08 : (byte)0x10);
        }
        AndOr(0x810, unchecked((uint)~0x01), 0);
    }

    private void SetInput(uint videoInput, uint audioInput)
    {
        bool isComponent = (videoInput & ComponentOn) == ComponentOn;
        bool isComposite = videoInput is >= 1 and <= 8;
        uint chroma = videoInput & 0xf00;
        byte register;

        if (videoInput >= Vin1Ch1)
        {
            register = (byte)(videoInput & 0xff);
            isComposite = !isComponent && (videoInput & SvideoOn) != SvideoOn;
        }
        else if (isComposite)
        {
            register = (byte)(0xf0 + (videoInput - 1));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(videoInput),
                $"0x{videoInput:x4} is not a supported video input.");
        }

        Write(0x103, register);
        if (isComponent) AndOr(0x401, unchecked((uint)~0x6), 0x6);
        else AndOr(0x401, unchecked((uint)~0x6), isComposite ? (byte)0 : (byte)0x02);

        AndOr(0x102, unchecked((uint)~0x4), isComponent ? (byte)0x4 : (byte)0x0);
        if (isComposite)
            AndOr(0x102, unchecked((uint)~0x2), 0);
        else if (!isComponent)
            AndOr(0x102, unchecked((uint)~0x2), chroma >= SvideoChroma7 ? (byte)2 : (byte)0);
        AndOr(0x102, unchecked((uint)~0x2), 0);

        _videoInput = videoInput;
        _audioInput = audioInput;
        AudioSetPath();
        InputChange();

        Write(0x124, 0x03);  // audio channel 1 src: parallel 1
        Write(0x914, 0xa0);  // I2S_IN_CTL
        Write(0x918, 0xa0);  // I2S_OUT_CTL
        Write(0x919, 0x01);
    }

    private void AudioSetPath()
    {
        AndOr(0x810, unchecked((uint)~0x1), 0x01);  // assert soft reset
        AndOr(0x803, unchecked((uint)~0x10), 0);    // stop microcontroller
        Write(0x8d3, 0x1f);                          // mute
        Write4(0x8d0, _audioInput == AudioSerial ? 0x01011012u : 0x1f063870u);

        SetAudioClockFrequency(AudioClockFrequency);

        if (_audioInput != AudioSerial)
            AndOr(0x803, unchecked((uint)~0x10), 0x10);
        AndOr(0x810, unchecked((uint)~0x1), 0x00);  // deassert soft reset
        AndOr(0x803, unchecked((uint)~0x10), 0x10); // keep the controller running
    }

    private void SetAudioClockFrequency(int frequency)
    {
        if (_audioInput != AudioSerial)
        {
            uint ratio = frequency switch
            {
                32000 => 0x0801f77f,
                44100 => 0x08016d59,
                _ => 0x08014faa,
            };
            Write4(0x900, ratio);
            Write4(0x904, ratio);
            Write4(0x90c, ratio);
        }
        else
        {
            (uint src1, uint src3) = frequency switch
            {
                32000 => (0x08010000u, 0x08020000u),
                44100 => (0x080160cdu, 0x08017385u),
                _ => (0x0801867cu, 0x08014faau),
            };
            Write4(0x8f8, src1);
            Write4(0x900, src3);
            Write4(0x904, src3);
            Write4(0x90c, src3);
        }
    }

    private void SetV4lStandard()
    {
        byte format = 0;
        byte palM = 0, palN = 0, ntscJ = 0;

        if (_std == V4l2Std.NtscMJp) { format = 0x2; ntscJ = 0x80; }
        else if (_std == V4l2Std.Ntsc443) format = 0x3;
        else if (_std == V4l2Std.PalM) { palM = 1; format = 0x5; }
        else if (_std == V4l2Std.PalN) { format = 0x6; palN = 0x40; }
        else if (_std == V4l2Std.PalNc) format = 0x7;
        else if (_std == V4l2Std.Pal60) format = 0x8;
        else if ((_std & V4l2Std.Ntsc) != 0) format = 0x1;
        else if ((_std & V4l2Std.Pal) != 0) format = 0x4;
        else if ((_std & V4l2Std.Secam) != 0) format = 0xc;

        if (format is >= 4 and < 8)
        {
            AndOr(0x400, unchecked((uint)~0xf), 1);    // NTSC-M first (datasheet step 9)
            AndOr(0x47b, unchecked((uint)~0x6), 0);    // LCOMB off
        }
        else if (format == 0xc)
        {
            byte current = Read(0x401);
            AndOr(0x401, unchecked((uint)~0x20), (current & 0x20) != 0 ? (byte)0x00 : (byte)0x20);
            AndOr(0x401, unchecked((uint)~0x20), (current & 0x20) != 0 ? (byte)0x20 : (byte)0x00);
        }

        AndOr(0x400, unchecked((uint)~0xf), format);

        if (format is >= 4 and < 8)
        {
            byte current = Read(0x401);
            AndOr(0x401, unchecked((uint)~0x40), (current & 0x40) != 0 ? (byte)0x00 : (byte)0x40); // CAGCEN
            AndOr(0x401, unchecked((uint)~0x40), (current & 0x40) != 0 ? (byte)0x40 : (byte)0x00);
            AndOr(0x401, unchecked((uint)~0x20), (current & 0x20) != 0 ? (byte)0x00 : (byte)0x20); // CKILLEN
            AndOr(0x401, unchecked((uint)~0x20), (current & 0x20) != 0 ? (byte)0x20 : (byte)0x00);
        }

        if (palM != 0) AndOr(0x403, unchecked((uint)~0x3), palM);
        else if (palN != 0) AndOr(0x403, unchecked((uint)~0x40), palN);
        else if (ntscJ != 0) AndOr(0x403, unchecked((uint)~0x80), ntscJ);

        StandardSetup();
        InputChange();
    }
}
