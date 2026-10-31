// SPDX-License-Identifier: GPL-2.0-or-later
// User-mode setup of the August VGB100 (CX23102), following the Linux cx231xx
// driver with the AUGUST_VGB100 board profile: probe (cx231xx_init_dev),
// v4l2 open, raw VBI and raw video streaming of the analogue inputs.
namespace TeletextRecoveReese.Core.Vgb100;

public enum Vgb100Input
{
    Composite = 0,
    SVideo = 1,
}

public enum Vgb100Standard
{
    Pal,
    Secam,
    NtscM,
    NtscJ,
    PalM,
    PalN,
}

public sealed class Vgb100Device
{
    public const ushort VendorId = 0x1f4d;
    public const ushort ProductId = 0x0102;
    public const ushort SupportedRevision = 0x4002;

    public const byte VideoEndpoint = 0x84;
    public const byte VbiEndpoint = 0x85;
    public const int VideoAlternateSetting = 3; // 720-wide isochronous video
    public const int VideoInterface = 3;
    public const int VbiInterface = 4;
    public const int SlicedCaptionInterface = 5;

    public const int VbiSamplesPerLine = 1440;       // dev->width * 2
    public const uint VbiSamplingRate = 6750000 * 4; // vidioc_g_fmt_vbi_cap

    // cx231xx_itype
    private const int VmuxComposite1 = 1;
    private const int VmuxSvideo = 2;
    private const int VmuxTelevision = 3;

    // cx231xx_amux
    private const int AmuxLineIn = 1;

    // AV_MODE
    private const int AvModeExternalAv = 0x30;

    // Bridge power control (cx231xx-reg.h)
    private const uint PwrModeMask = 0x17f;
    private const uint PwrAvEn = 0x08;
    private const uint PwrIsoEn = 0x40;
    private const int PwrSleepInterval = 10;

    // Endpoint enable bits and TS_MODE_REG values for raw video and VANC/raw VBI
    private const uint EnableEp4 = 0x08;
    private const uint EnableEp5 = 0x10;
    private const uint TsModeRawVideo = 0x100;
    private const uint TsModeVbi = 0x300;

    // AFE registers (cx231xx-reg.h)
    private const ushort SupBlockTune1 = 0x00;
    private const ushort SupBlockTune2 = 0x01;
    private const ushort SupBlockTune3 = 0x02;
    private const ushort SupBlockPll2 = 0x05;
    private const ushort SupBlockPowerDown = 0x08;
    private const ushort AdcInputCh1 = 0x28, AdcInputCh2 = 0x48, AdcInputCh3 = 0x68;
    private const ushort AdcPowerDownClampCh1 = 0x23, AdcPowerDownClampCh2 = 0x43, AdcPowerDownClampCh3 = 0x63;
    private const ushort AdcComQuant = 0x0b;
    private const ushort AdcFbForceResetCh1 = 0x27, AdcFbForceResetCh2 = 0x47, AdcFbForceResetCh3 = 0x67;
    private const ushort AdcCalAtestCh1 = 0x22, AdcCalAtestCh2 = 0x42, AdcCalAtestCh3 = 0x62;
    private const ushort AdcNtfPreclampEnCh1 = 0x29, AdcNtfPreclampEnCh2 = 0x49, AdcNtfPreclampEnCh3 = 0x69;
    private const ushort AdcQgainResTrimCh1 = 0x2a, AdcQgainResTrimCh2 = 0x4a, AdcQgainResTrimCh3 = 0x6a;
    private const ushort AdcDcservoDemCh1 = 0x26, AdcDcservoDemCh2 = 0x46, AdcDcservoDemCh3 = 0x66;
    private const byte InputSelectMask = 0x30;
    private const byte PowerDownTuningBias = 0x10;
    private const byte PowerDownEnablePll = 0x08;

    // Video block registers (cx231xx-conf-reg.h)
    private const ushort AfeCtrl = 0x104;
    private const ushort PinCtrl = 0x120;
    private const ushort AudIoCtrl = 0x124;
    private const ushort DifMiscCtrl = 0x33c;
    private const ushort DifSrcPhaseInc = 0x340;
    private const ushort ModeCtrl = 0x400;
    private const ushort OutCtrl1 = 0x404;
    private const ushort HorizTimCtrl = 0x470;
    private const ushort VertTimCtrl = 0x474;
    private const ushort DfeCtrl1 = 0x488;
    private const ushort DfeCtrl3 = 0x490;
    private const ushort DlCtl = 0x800;
    private const ushort GeneralCtl = 0x810;
    private const ushort Path1Ctl1 = 0x8d0;
    private const ushort Path1VolCtl = 0x8d4;
    private const ushort Path1ScCtl = 0x8dc;
    private const ushort BandOutSel = 0x910;
    private const ushort Ac97Ctl = 0x91c;

    private const uint FldOutMode = 0x00000003;
    private const uint FldVbiHactRawEn = 0x00000008;
    private const uint FldVbiGateEn = 0x08000000;
    private const uint FldVgaAutoEn = 0x10000000;
    private const uint FldAcfgDis = 0x00000020;
    private const uint FldInputMode = 0x00000600;
    private const uint FldChromaInSel = 0x00001000;
    private const uint FldVgaSelCh2 = 0x00000080;
    private const uint FldVgaSelCh3 = 0x00000100;
    private const uint FldDifBypass = 0x80000000;
    private const uint FldDifAudSrcSel = 0x00080000;
    private const uint FldVblankCnt = 0x000003ff;
    private const uint FldVactiveCnt = 0x003ff000;
    private const uint FldV656BlankCnt = 0xff000000;
    private const uint FldHblankCnt = 0x000003ff;
    private const uint FldAudChan1Src = 0x00000003;
    private const uint FldAc97Up2xBypass = 0x01000000;
    private const uint FldPath1AvcThreshold = 0x7fff0000;
    private const uint FldPath1ScThreshold = 0x7fff0000;

    // I2S block (cx231xx-reg.h)
    private const ushort ChPowerCtrl1 = 0x0e;
    private const ushort ChPowerCtrl2 = 0x0f;

    // AUGUST_VGB100 board profile (cx231xx-cards.c)
    private const uint CtlPinStatusMask = 0xffffffc4;
    private const uint OutputModeVip11 = 2;
    private static readonly (int Type, uint Vmux, int Amux)[] Inputs =
    [
        (VmuxComposite1, 2, AmuxLineIn),                         // CX231XX_VIN_2_1
        (VmuxSvideo, 1u | (3u << 8) | 0x80000100u, AmuxLineIn),  // VIN_1_1 | VIN_3_2 << 8 | SVIDEO_ON
    ];

    private readonly Cx231xxBridge _bridge;
    private readonly Cx25840Decoder _decoder;
    private readonly bool _highSpeed;
    private bool _busPowered;
    private int _powerMode;
    private int _videoInput;
    private uint _afeRefCount;
    private ulong _norm = V4l2Std.Pal;

    public Vgb100Device(ICx231xxTransport usb, bool highSpeed = true)
    {
        _bridge = new Cx231xxBridge(usb);
        _decoder = new Cx25840Decoder(_bridge);
        _highSpeed = highSpeed;
    }

    public ushort DecoderId => _decoder.DeviceId;

    public Vgb100Input CurrentInput => (Vgb100Input)_videoInput;

    public static bool Is625Lines(Vgb100Standard standard) =>
        (StandardId(standard) & V4l2Std.Std625_50) != 0;

    private static ulong StandardId(Vgb100Standard standard) => standard switch
    {
        Vgb100Standard.Secam => V4l2Std.Secam,
        Vgb100Standard.NtscM => V4l2Std.NtscM,
        Vgb100Standard.NtscJ => V4l2Std.NtscMJp,
        Vgb100Standard.PalM => V4l2Std.PalM,
        Vgb100Standard.PalN => V4l2Std.PalN,
        _ => V4l2Std.Pal,
    };

    /// <summary>VIDIOC_S_STD: reselecting the current standard is a no-op.</summary>
    public void SetStandard(Vgb100Standard standard)
    {
        ulong norm = StandardId(standard);
        if (norm == _norm) return;
        _norm = norm;
        _decoder.SetStandard(_norm);
        DoModeControlOverrides();
    }

    // ----- probe: cx231xx_usb_probe() -> cx231xx_init_dev() -----

    public void Probe()
    {
        // initialize_cx231xx(): only the power type of the PCB scenario is used here.
        uint boardConfig = _bridge.ReadRegister(Cx231xxBridge.BoardCfgStat);
        _busPowered = (boardConfig & 0x40) != 0;

        // cx231xx_pre_card_setup(): the board leaves tuner_sif_gpio at 0.
        _bridge.SetGpioDirection(0, true);

        DevInit();

        // cx231xx_card_setup(): cx25840 subdevice and its load_fw core op.
        _decoder.Probe();
        _decoder.LoadFirmware();

        // cx231xx_register_analog_devices()
        _norm = V4l2Std.Pal;
        VideoMux(0);
        _decoder.SetStandard(_norm);
        DoModeControlOverrides();
    }

    private void DevInit()
    {
        Check(SetPowerMode(AvModeExternalAv), "set power mode");
        Check(AfeInitSuperBlock(0x23c), "AFE super block init");
        Check(AfeInitChannels(), "AFE channel init");
        Check(DifSetBaseband(), "DIF baseband");
        Check(I2sBlockInitialize(), "I2S block init");
        Check(InitControlPinStatus(), "control pins");
        TrySetAltSetting(VideoInterface, 0);
        TrySetAltSetting(SlicedCaptionInterface, 0);
    }

    // ----- v4l2 open of the VBI node -----

    public void OpenVbi()
    {
        Check(SetPowerMode(AvModeExternalAv), "set power mode");
        // cx231xx_set_video_alternate(): 720-wide isochronous video uses alt 3.
        TrySetAltSetting(VideoInterface, VideoAlternateSetting);
    }

    public void CloseVbi() => TrySetAltSetting(VideoInterface, 0);

    // ----- raw VBI streaming: cx231xx_init_vbi_isoc() / cx231xx_uninit_vbi_isoc() -----

    public void PrepareVbiStreaming()
    {
        CaptureStartVbi(false);
        _bridge.Usb.ClearHalt(VbiEndpoint);
    }

    public void StartVbiStreaming() => Check(CaptureStartVbi(true), "enable VBI endpoint");

    public void StopVbiStreaming() => CaptureStartVbi(false);

    // ----- raw video streaming: cx231xx_init_isoc() / cx231xx_uninit_isoc() -----

    public void StartVideoStreaming()
    {
        int status = _highSpeed
            ? _bridge.ModeRegister(Cx231xxBridge.TsModeReg, TsModeRawVideo)
            : _bridge.ModeRegister(Cx231xxBridge.TsModeReg, 0x101);
        Check(status < 0 ? status : StartStream(EnableEp4), "enable video endpoint");
    }

    public void StopVideoStreaming() => StopStream(EnableEp4);

    /// <summary>VIDIOC_S_INPUT for the VGB100: reselecting the current input is a no-op.</summary>
    public void SelectInput(Vgb100Input input)
    {
        int index = (int)input;
        if (index == _videoInput) return;
        VideoMux(index);
        _decoder.SetStandard(_norm);
        DoModeControlOverrides();
    }

    private int CaptureStartVbi(bool start)
    {
        if (!start) return StopStream(EnableEp5);
        int status = _highSpeed
            ? _bridge.ModeRegister(Cx231xxBridge.TsModeReg, TsModeVbi)
            : _bridge.ModeRegister(Cx231xxBridge.TsModeReg, 0x101);
        if (status < 0) return status;
        return StartStream(EnableEp5);
    }

    private int StartStream(uint endpointMask)
    {
        Span<byte> value = stackalloc byte[4];
        int status = _bridge.ReadCtrlReg(Cx231xxBridge.VrtGetRegister, Cx231xxBridge.EpModeSet, value);
        if (status < 0) return status;
        BitConverter.TryWriteBytes(value, BitConverter.ToUInt32(value) | endpointMask);
        return _bridge.WriteCtrlReg(Cx231xxBridge.VrtSetRegister, Cx231xxBridge.EpModeSet, value);
    }

    private int StopStream(uint endpointMask)
    {
        Span<byte> value = stackalloc byte[4];
        int status = _bridge.ReadCtrlReg(Cx231xxBridge.VrtGetRegister, Cx231xxBridge.EpModeSet, value);
        if (status < 0) return status;
        BitConverter.TryWriteBytes(value, BitConverter.ToUInt32(value) & ~endpointMask);
        return _bridge.WriteCtrlReg(Cx231xxBridge.VrtSetRegister, Cx231xxBridge.EpModeSet, value);
    }

    private void TrySetAltSetting(int interfaceNumber, int alternateSetting)
    {
        try { _bridge.Usb.SetInterfaceAltSetting(interfaceNumber, alternateSetting); }
        catch (IOException) { }
    }

    // ----- power: cx231xx_set_power_mode() -----

    private int SetPowerMode(int mode)
    {
        if (_powerMode == mode) return 0;
        _powerMode = mode;

        Span<byte> value = stackalloc byte[4];
        int status = _bridge.ReadCtrlReg(Cx231xxBridge.VrtGetRegister, Cx231xxBridge.PwrCtlEn, value);
        if (status < 0) return status;
        uint power = BitConverter.ToUInt32(value);

        // Only external A/V is used by this board.
        power &= ~PwrModeMask;
        power |= PwrAvEn;
        _bridge.WriteRegister(Cx231xxBridge.PwrCtlEn, power);
        _bridge.Delay(PwrSleepInterval);
        power |= PwrIsoEn;
        _bridge.WriteRegister(Cx231xxBridge.PwrCtlEn, power);
        _bridge.Delay(PwrSleepInterval);
        power |= (uint)AvModeExternalAv;
        _bridge.WriteRegister(Cx231xxBridge.PwrCtlEn, power);

        _bridge.Delay(PwrSleepInterval);
        AfeUpdatePowerControl();
        I2sBlockUpdatePowerControl();
        return _bridge.ReadCtrlReg(Cx231xxBridge.VrtGetRegister, Cx231xxBridge.PwrCtlEn, value);
    }

    private void AfeUpdatePowerControl()
    {
        // OTG102/VGB100 branch for POLARIS_AVMODE_ENXTERNAL_AV. The kernel loops
        // until the PLL reports enabled; bound it so a dead device cannot hang.
        byte powerStatus = 0;
        for (int attempt = 0; attempt < 100 && powerStatus != (PowerDownTuningBias | PowerDownEnablePll); attempt++)
        {
            int status = _bridge.AfeWriteByte(SupBlockPowerDown, PowerDownTuningBias | PowerDownEnablePll);
            status |= _bridge.AfeReadByte(SupBlockPowerDown, out powerStatus);
            if (status < 0) break;
        }
        _bridge.AfeWriteByte(AdcPowerDownClampCh1, 0x00);
        _bridge.AfeWriteByte(AdcPowerDownClampCh2, 0x00);
        _bridge.AfeWriteByte(AdcPowerDownClampCh3, 0x00);
    }

    private void I2sBlockUpdatePowerControl() =>
        _bridge.WriteI2cData(Cx231xxBridge.I2sBlockDeviceAddress, ChPowerCtrl2, 1, 0x00, 1);

    // ----- analogue front end -----

    private int AfeInitSuperBlock(uint refCount)
    {
        int status = _bridge.AfeWriteByte(SupBlockTune2, (byte)(refCount & 0xff));
        if (status < 0) return status;
        status = _bridge.AfeReadByte(SupBlockTune2, out byte powerStatus);
        if (status < 0) return status;
        status = _bridge.AfeWriteByte(SupBlockTune1, (byte)(((refCount & 0x300) >> 8) | 0x40));
        if (status < 0) return status;
        status = _bridge.AfeWriteByte(SupBlockPll2, 0x0f);
        if (status < 0) return status;

        int attempt = 0;
        while (powerStatus != 0x18)
        {
            status = _bridge.AfeWriteByte(SupBlockPowerDown, 0x18);
            if (status < 0) break;
            status = _bridge.AfeReadByte(SupBlockPowerDown, out powerStatus);
            if (status < 0) break;
            if (++attempt == 10)
            {
                status = -1;
                break;
            }
        }
        if (status < 0) return status;

        status = _bridge.AfeWriteByte(SupBlockTune3, 0x40);  // start tuning filter
        if (status < 0) return status;
        _bridge.Delay(5);
        return _bridge.AfeWriteByte(SupBlockTune3, 0x00);    // exit tuning
    }

    private int AfeInitChannels()
    {
        _bridge.AfeWriteByte(AdcPowerDownClampCh1, 0x00);
        _bridge.AfeWriteByte(AdcPowerDownClampCh2, 0x00);
        _bridge.AfeWriteByte(AdcPowerDownClampCh3, 0x00);
        _bridge.AfeWriteByte(AdcComQuant, 0x02);
        _bridge.AfeWriteByte(AdcFbForceResetCh1, 0x17);
        _bridge.AfeWriteByte(AdcFbForceResetCh2, 0x17);
        _bridge.AfeWriteByte(AdcFbForceResetCh3, 0x17);
        _bridge.AfeWriteByte(AdcCalAtestCh1, 0x10);
        _bridge.AfeWriteByte(AdcCalAtestCh2, 0x10);
        _bridge.AfeWriteByte(AdcCalAtestCh3, 0x10);
        _bridge.Delay(5);
        _bridge.AfeWriteByte(AdcFbForceResetCh1, 0x07);
        _bridge.AfeWriteByte(AdcFbForceResetCh2, 0x07);
        _bridge.AfeWriteByte(AdcFbForceResetCh3, 0x07);
        _bridge.AfeWriteByte(AdcNtfPreclampEnCh1, 0xf0);
        _bridge.AfeWriteByte(AdcNtfPreclampEnCh2, 0xf0);
        _bridge.AfeWriteByte(AdcNtfPreclampEnCh3, 0xf0);
        _bridge.RegisterMaskWrite(Cx231xxBridge.AfeDeviceAddress, 8, AdcQgainResTrimCh1, 3, 7, 0x00);
        _bridge.RegisterMaskWrite(Cx231xxBridge.AfeDeviceAddress, 8, AdcQgainResTrimCh2, 3, 7, 0x00);
        _bridge.RegisterMaskWrite(Cx231xxBridge.AfeDeviceAddress, 8, AdcQgainResTrimCh3, 3, 7, 0x00);
        _bridge.AfeWriteByte(AdcDcservoDemCh1, 0x03);
        _bridge.AfeWriteByte(AdcDcservoDemCh2, 0x03);
        return _bridge.AfeWriteByte(AdcDcservoDemCh3, 0x03);
    }

    private int AfeSetupForBaseband()
    {
        _bridge.AfeReadByte(AdcPowerDownClampCh2, out byte value);
        value &= unchecked((byte)~0x50);
        return _bridge.AfeWriteByte(AdcPowerDownClampCh2, value);
    }

    private int AfeSetInputMux(uint inputMux)
    {
        int status = 0;
        foreach ((ushort register, byte setting) in new[]
                 {
                     (AdcInputCh1, (byte)inputMux),
                     (AdcInputCh2, (byte)(inputMux >> 8)),
                     (AdcInputCh3, (byte)(inputMux >> 16)),
                 })
        {
            if (setting == 0) continue;
            _bridge.AfeReadByte(register, out byte value);
            value &= unchecked((byte)~InputSelectMask);
            value |= (byte)((setting - 1) << 4);
            status = _bridge.AfeWriteByte(register, value);
        }
        return status;
    }

    private int AfeAdjustRefCount(int videoInput)
    {
        _videoInput = videoInput;
        byte inputMode, ntfMode;
        if (videoInput == VmuxTelevision)
        {
            _bridge.AfeReadByte(AdcInputCh3, out inputMode);
            _bridge.AfeReadByte(AdcNtfPreclampEnCh3, out ntfMode);
        }
        else
        {
            _bridge.AfeReadByte(AdcInputCh1, out inputMode);
            _bridge.AfeReadByte(AdcNtfPreclampEnCh1, out ntfMode);
        }

        switch ((ntfMode & 0x3) | ((inputMode & 0x6) << 1))
        {
            case 0x0: _afeRefCount = 0x23c; break; // single ended
            case 0x4: _afeRefCount = 0x24c; break; // low IF
            case 0x9: _afeRefCount = 0x258; break; // EU IF
            case 0xa: _afeRefCount = 0x260; break; // US IF
        }
        return AfeInitSuperBlock(_afeRefCount);
    }

    // ----- DIF / I2S / control pins -----

    private int DifSetBaseband()
    {
        _bridge.VideoBlockReadWord(DifMiscCtrl, out uint misc);
        // cx231xx_dif_configure_C2HH_for_low_IF() does nothing for baseband.
        _bridge.VideoBlockWriteWord(DifSrcPhaseInc, 0xdf7df83);
        _bridge.VideoBlockReadWord(DifMiscCtrl, out misc);
        misc |= FldDifBypass;
        _bridge.VideoBlockWriteWord(DifMiscCtrl, misc);
        misc &= ~FldDifAudSrcSel;
        return _bridge.VideoBlockWriteWord(DifMiscCtrl, misc);
    }

    private int I2sBlockInitialize()
    {
        _bridge.ReadI2cData(Cx231xxBridge.I2sBlockDeviceAddress, ChPowerCtrl1, 1, out uint value, 1);
        value |= 0x80;  // clock to delta-sigma and decimation filter
        _bridge.WriteI2cData(Cx231xxBridge.I2sBlockDeviceAddress, ChPowerCtrl1, 1, value, 1);
        return _bridge.WriteI2cData(Cx231xxBridge.I2sBlockDeviceAddress, ChPowerCtrl2, 1, 0x00, 1);
    }

    private int InitControlPinStatus()
    {
        _bridge.VideoBlockReadWord(PinCtrl, out uint value);
        value |= ~CtlPinStatusMask;
        return _bridge.VideoBlockWriteWord(PinCtrl, value);
    }

    // ----- input routing: video_mux() -----

    private void VideoMux(int index)
    {
        _videoInput = index;
        int audioInput = Inputs[index].Amux;

        Check(SetVideoInputMux(index), "select video input");
        _decoder.SetVideoRouting(Inputs[index].Vmux);
        SetAudioInput(audioInput);
        DoModeControlOverrides();
    }

    private int SetVideoInputMux(int index)
    {
        (int type, uint vmux, _) = Inputs[index];
        if (_busPowered && _powerMode != AvModeExternalAv)
        {
            int powerStatus = SetPowerMode(AvModeExternalAv);
            if (powerStatus < 0) return powerStatus;
        }
        int status = SetDecoderVideoInput(type, vmux);
        _videoInput = index;
        return status;
    }

    private int SetDecoderVideoInput(int pinType, uint input)
    {
        int status;
        if (pinType != _videoInput)
        {
            status = AfeAdjustRefCount(pinType);
            if (status < 0) return status;
        }

        status = AfeSetInputMux(input);
        if (status < 0) return status;

        uint value;
        if (pinType == VmuxComposite1)
        {
            _bridge.VideoBlockReadWord(AfeCtrl, out value);
            value |= 1u << 4;
            value &= ~(1u << 5);
            value &= ~0x1ff8000u;
            value |= 0x1000000;
            _bridge.VideoBlockWriteWord(AfeCtrl, value);

            _bridge.VideoBlockReadWord(OutCtrl1, out value);
            value |= 1u << 7;
            _bridge.VideoBlockWriteWord(OutCtrl1, value);

            _bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, OutCtrl1, FldOutMode, OutputModeVip11);

            status = DifSetBaseband();
            if (status < 0) return status;

            _bridge.VideoBlockReadWord(DfeCtrl1, out value);
            value |= FldVbiGateEn | FldVgaAutoEn;
            _bridge.VideoBlockWriteWord(DfeCtrl1, value);

            _bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, ModeCtrl, FldAcfgDis,
                Cx231xxBridge.SetField(FldAcfgDis, 1));
            _bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, ModeCtrl, FldInputMode,
                Cx231xxBridge.SetField(FldInputMode, 0));  // CVBS
        }
        else
        {
            _bridge.VideoBlockReadWord(AfeCtrl, out value);
            value &= ~0x1ff8000u;
            value |= 0x1000010;
            _bridge.VideoBlockWriteWord(AfeCtrl, value);

            status = DifSetBaseband();
            if (status < 0) return status;

            _bridge.VideoBlockReadWord(DfeCtrl1, out value);
            value |= FldVbiGateEn | FldVgaAutoEn;
            _bridge.VideoBlockWriteWord(DfeCtrl1, value);

            _bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, ModeCtrl, FldAcfgDis,
                Cx231xxBridge.SetField(FldAcfgDis, 1));
            _bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, ModeCtrl, FldInputMode,
                Cx231xxBridge.SetField(FldInputMode, 1));  // Y/C

            _bridge.VideoBlockReadWord(AfeCtrl, out value);
            value |= FldChromaInSel;
            value &= ~(FldVgaSelCh2 | FldVgaSelCh3);
            _bridge.VideoBlockWriteWord(AfeCtrl, value);

            AfeSetupForBaseband();  // cx231xx_afe_set_mode(AFE_MODE_BASEBAND)
        }

        // Raw VBI mode
        status = _bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, OutCtrl1, FldVbiHactRawEn,
            Cx231xxBridge.SetField(FldVbiHactRawEn, 1));
        _bridge.VideoBlockReadWord(OutCtrl1, out value);
        if ((value & 0x02) != 0)
        {
            value |= 1u << 19;
            status = _bridge.VideoBlockWriteWord(OutCtrl1, value);
        }

        // Revision 40.02 needs this AFE tuning or the decoder never locks horizontally.
        status = _bridge.AfeWriteByte(SupBlockTune1, 0xbe);
        return status;
    }

    // ----- audio routing: cx231xx_set_audio_input() -----

    private void SetAudioInput(int input)
    {
        // video_mux() passes the amux value where an input index is expected;
        // keep that quirk so the register sequence matches the kernel driver.
        if (Inputs[input].Amux == AmuxLineIn)
        {
            _bridge.WriteI2cData(Cx231xxBridge.I2sBlockDeviceAddress, ChPowerCtrl2, 1, 0x00, 1);
            _bridge.WriteI2cData(Cx231xxBridge.I2sBlockDeviceAddress, ChPowerCtrl1, 1, 0x80, 1);
        }
        SetAudioDecoderLineInput();
    }

    private void SetAudioDecoderLineInput()
    {
        _bridge.VideoBlockReadByte(GeneralCtl, out byte general);
        general |= 1;
        _bridge.VideoBlockWriteByte(GeneralCtl, general);

        _bridge.VideoBlockWriteWord(AudIoCtrl, Cx231xxBridge.SetField(FldAudChan1Src, 0));
        _bridge.VideoBlockReadWord(Ac97Ctl, out uint value);
        _bridge.VideoBlockWriteWord(Ac97Ctl, value | FldAc97Up2xBypass);
        _bridge.VideoBlockWriteWord(BandOutSel, 0);
        _bridge.VideoBlockWriteWord(DlCtl, 0x3000001);
        _bridge.VideoBlockWriteWord(Path1Ctl1, 0x00063073);
        _bridge.VideoBlockReadWord(Path1VolCtl, out value);
        _bridge.VideoBlockWriteWord(Path1VolCtl, value | FldPath1AvcThreshold);
        _bridge.VideoBlockReadWord(Path1ScCtl, out value);
        _bridge.VideoBlockWriteWord(Path1ScCtl, value | FldPath1ScThreshold);

        _bridge.VideoBlockReadByte(GeneralCtl, out general);
        general &= unchecked((byte)~1);
        _bridge.VideoBlockWriteByte(GeneralCtl, general);
    }

    // ----- cx231xx_do_mode_ctrl_overrides() for the VGB100 -----

    private void DoModeControlOverrides()
    {
        _bridge.VideoBlockWriteWord(DfeCtrl3, 0xcd3f0280);
        if ((_norm & (V4l2Std.Ntsc | V4l2Std.PalM)) != 0)
        {
            // Move the closed caption lines out of active video and adjust
            // the active video start point.
            Check(_bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, VertTimCtrl,
                FldVblankCnt, 0x18), "NTSC vertical blanking");
            Check(_bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, VertTimCtrl,
                FldVactiveCnt, 0x1e7000), "NTSC active lines");
            Check(_bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, VertTimCtrl,
                FldV656BlankCnt, 0x1c000000), "NTSC 656 blanking");
            Check(_bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, HorizTimCtrl,
                FldHblankCnt, Cx231xxBridge.SetField(FldHblankCnt, 0x79)), "NTSC horizontal timing");
            return;
        }
        if ((_norm & V4l2Std.Std625_50) == 0) return;

        // The AUGUST_VGB100 board uses one timing for every 625-line standard.
        // Keep the 36/40-line PAL blanking from cx25840_std_setup() so raw VBI
        // completes 18-line fields, and start capture 16 samples earlier
        // (HBLANK 125) to retain the Teletext clock run-in.
        Check(_bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, VertTimCtrl,
            FldVblankCnt | FldVactiveCnt | FldV656BlankCnt, 0x28244024), "PAL vertical timing");
        Check(_bridge.ReadModifyWriteI2cDword(Cx231xxBridge.VideoBlockI2cAddress, HorizTimCtrl,
            FldHblankCnt, 0x7d), "PAL horizontal timing");
    }

    private static void Check(int status, string operation) => Cx231xxBridge.Check(status, operation);
}
