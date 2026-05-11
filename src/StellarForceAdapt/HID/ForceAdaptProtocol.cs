namespace StellarForceAdapt.HID;

/// <summary>
/// FlyDigi ForceAdapt trigger protocol definitions.
/// Based on reverse engineering of FlyDigi Space Station + SDL hidapi_flydigi.c.
/// </summary>
public static class ForceAdaptProtocol
{
    public const int VendorId = 0x37D7;

    public static readonly int[] KnownProductIds =
    [
        0x2501, // Apex 5 / 八爪鱼5 (confirmed)
        0x2012, // Vader 4 Pro
        0x2021, // Apex 4
        0x2023, // Apex 4 (alt)
        0x2011, // Vader 3 Pro
        0x2010, // Vader 3
    ];

    public const int OutputReportLength = 65;
    public const int InputReportLength = 65;
    public const int Mi02OutputReportLength = 32;
    public const int Mi02InputReportLength = 32;

    public const byte ReportIdRumble = 0x05;
    public const byte SubCmdRumble = 0x0f;

    /// <summary>
    /// ForceAdapt effect modes — mapped to SpaceStation UI modes (V2 capture, 2026-05).
    /// Byte values correspond directly to A5 data[6] in the vendor protocol.
    /// </summary>
    public enum ForceAdaptMode : byte
    {
        Off = 0,
        Racing = 1,
        Machinegun = 2,
        Sniper = 3,
        TriggerLock = 4,
        Vibration = 5,
    }

    /// <summary>
    /// Identifies which physical trigger a ForceAdapt config targets.
    /// Value equals the channel-ID byte used in A4 BEGIN_CFG data[1].
    /// </summary>
    public enum TriggerSide : byte
    {
        LT = 0x09,
        RT = 0x0A,
    }

    /// <summary>
    /// SpaceStation vendor protocol command types (Report 0x03, magic 0x5AA5).
    /// </summary>
    public static class VendorProtocol
    {
        public const byte ReportId = 0x03;
        public static readonly byte[] Magic = [0x5A, 0xA5];

        public const byte CmdGetInfo     = 0x01;
        public const byte CmdHeartbeat   = 0x10;
        public const byte CmdQuery       = 0x11;
        public const byte CmdHaptic      = 0x12;
        public const byte CmdAcquire     = 0x1C;

        public const byte CmdConfig      = 0xA4;
        public const byte CmdSetEffect   = 0xA5;
        public const byte CmdEndConfig   = 0xA6;
        public const byte CmdSaveProfile = 0x51;

        public const byte SubBeginConfig    = 0x06;
        public const byte SubSetForceAdapt  = 0x17;
        public const byte SubEndConfig      = 0x04;
        public const byte SubSaveProfile    = 0x0A;

        private static byte[] BuildCmd(byte cmd, byte sub, byte[] data)
        {
            var buf = new byte[32];
            buf[0] = ReportId;
            buf[1] = Magic[0];
            buf[2] = Magic[1];
            buf[3] = cmd;
            buf[4] = sub;
            if (data != null && data.Length > 0)
                Array.Copy(data, 0, buf, 5, Math.Min(data.Length, 27));
            return buf;
        }

        /// <summary>
        /// SET_STATUS prefix that precedes every SpaceStation A4/A5/A6 triplet
        /// (cmd=0x11 sub=0x07 data=ff 00 ff ff ff 14 00).
        /// </summary>
        public static byte[] BuildSetStatusPrefix() =>
            BuildCmd(CmdQuery, 0x07, [0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0x14, 0x00]);

        /// <summary>
        /// Byte-exact replay templates from USBPcap capture v2 (spacestation_cmds_v2.txt, 2026-05).
        /// </summary>
        public static class CapturedReplay
        {
            public static readonly byte[] BeginLT = [0x01, 0x09, 0x01, 0x14, 0xC9, 0x00];
            public static readonly byte[] BeginRT = [0x01, 0x0A, 0x01, 0x14, 0xCA, 0x00];

            // --- LT templates (A5 data[2]=0x32) ---
            private static readonly byte[] LT_Set_Mode0 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x00, 0x00, 0x0A,0x32,0x01,0x01,0x01,0x80,0x00,0x00,
                 0x00,0x00,0x00,0x00,0x00, 0xAD, 0x00];
            private static readonly byte[] LT_End_Mode0  = [0xF7, 0x59, 0xFA, 0x00];

            private static readonly byte[] LT_Set_Mode1 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x01, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x0A,0x1E,0x00,0x00,0x00, 0xD5, 0x00];
            private static readonly byte[] LT_End_Mode1  = [0x5E, 0x14, 0x1C, 0x00];

            private static readonly byte[] LT_Set_Mode2 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x02, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x1E,0x32,0x32,0x11,0x01, 0x42, 0x00];
            private static readonly byte[] LT_End_Mode2  = [0xD6, 0x2E, 0xAE, 0x00];

            private static readonly byte[] LT_Set_Mode3 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x03, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x32,0x3C,0x3C,0x00,0x01, 0x5A, 0x00];
            private static readonly byte[] LT_End_Mode3  = [0x7F, 0xC3, 0xEC, 0x00];

            private static readonly byte[] LT_Set_Mode4 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x04, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x50,0xFF,0x01,0x00,0x00, 0x00, 0x00];
            private static readonly byte[] LT_End_Mode4  = [0xEA, 0xF9, 0x8D, 0x00];

            private static readonly byte[] LT_Set_Mode5 =
                [0x00,0x00,0x32,0x00,0x00,0x00, 0x05, 0x02, 0x0A,0x32,0x01,0x01,0x01,0x80,0x00,0x00,
                 0x01,0x80,0x01,0x5A,0x00, 0x90, 0x00];
            private static readonly byte[] LT_End_Mode5  = [0xA1, 0x84, 0xCF, 0x00];

            // --- RT templates (A5 data[2]=0x00) ---
            private static readonly byte[] RT_Set_Mode0 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x00, 0x00, 0x0A,0x32,0x01,0x01,0x01,0x80,0x00,0x00,
                 0x00,0x00,0x00,0x00,0x00, 0x7B, 0x00];
            private static readonly byte[] RT_End_Mode0  = [0x8B, 0xC4, 0xF9, 0x00];

            private static readonly byte[] RT_Set_Mode1 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x01, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x0A,0x1E,0x00,0x00,0x00, 0xA3, 0x00];
            private static readonly byte[] RT_End_Mode1  = [0xF0, 0x1F, 0xB9, 0x00];

            private static readonly byte[] RT_Set_Mode2 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x02, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x1E,0x32,0x32,0x11,0x01, 0x10, 0x00];
            private static readonly byte[] RT_End_Mode2  = [0xB4, 0x53, 0xB1, 0x00];

            private static readonly byte[] RT_Set_Mode3 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x03, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x32,0x3C,0x3C,0x00,0x01, 0x28, 0x00];
            private static readonly byte[] RT_End_Mode3  = [0xD2, 0xF5, 0x71, 0x00];

            private static readonly byte[] RT_Set_Mode4 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x04, 0x00, 0x0A,0x0A,0x64,0x01,0xFF,0x46,0x00,0x00,
                 0x50,0xFF,0x01,0x00,0x00, 0xCE, 0x00];
            private static readonly byte[] RT_End_Mode4  = [0x64, 0xF2, 0x00, 0x00];

            private static readonly byte[] RT_Set_Mode5 =
                [0x00,0x00,0x00,0x00,0x00,0x00, 0x05, 0x02, 0x0A,0x32,0x01,0x01,0x01,0x80,0x00,0x00,
                 0x01,0x80,0x01,0x5A,0x00, 0x5E, 0x00];
            private static readonly byte[] RT_End_Mode5  = [0x43, 0xCD, 0xBA, 0x00];

            public static (byte[] set23, byte[] end4) GetTemplate(TriggerSide side, ForceAdaptMode mode)
            {
                return (side, mode) switch
                {
                    (TriggerSide.LT, ForceAdaptMode.Off)         => (LT_Set_Mode0, LT_End_Mode0),
                    (TriggerSide.LT, ForceAdaptMode.Racing)      => (LT_Set_Mode1, LT_End_Mode1),
                    (TriggerSide.LT, ForceAdaptMode.Machinegun)  => (LT_Set_Mode2, LT_End_Mode2),
                    (TriggerSide.LT, ForceAdaptMode.Sniper)      => (LT_Set_Mode3, LT_End_Mode3),
                    (TriggerSide.LT, ForceAdaptMode.TriggerLock) => (LT_Set_Mode4, LT_End_Mode4),
                    (TriggerSide.LT, ForceAdaptMode.Vibration)   => (LT_Set_Mode5, LT_End_Mode5),
                    (TriggerSide.RT, ForceAdaptMode.Off)         => (RT_Set_Mode0, RT_End_Mode0),
                    (TriggerSide.RT, ForceAdaptMode.Racing)      => (RT_Set_Mode1, RT_End_Mode1),
                    (TriggerSide.RT, ForceAdaptMode.Machinegun)  => (RT_Set_Mode2, RT_End_Mode2),
                    (TriggerSide.RT, ForceAdaptMode.Sniper)      => (RT_Set_Mode3, RT_End_Mode3),
                    (TriggerSide.RT, ForceAdaptMode.TriggerLock) => (RT_Set_Mode4, RT_End_Mode4),
                    (TriggerSide.RT, ForceAdaptMode.Vibration)   => (RT_Set_Mode5, RT_End_Mode5),
                    _ => (LT_Set_Mode0, LT_End_Mode0),
                };
            }

            public static byte[] GetBegin(TriggerSide side) =>
                side == TriggerSide.LT ? BeginLT : BeginRT;

            /// <summary>
            /// Build the V2 apply sequence for a single (side, mode) pair.
            /// Returns 6 packets for modes 0..4, or 7 packets for Vibration (mode 5).
            /// </summary>
            public static byte[][] BuildApplySequenceV2(TriggerSide side, ForceAdaptMode mode)
            {
                var (set23, end4) = GetTemplate(side, mode);
                var begin = GetBegin(side);

                byte modeByte = set23[6];
                byte p0 = set23[16], p1 = set23[17], p2 = set23[18], p3 = set23[19], p4 = set23[20];

                byte[] ltSlot;
                if (side == TriggerSide.LT && mode != ForceAdaptMode.Vibration)
                    ltSlot = [0x01, 0x01, modeByte, p0, p1, p2, p3, p4, 0x00, 0x00];
                else
                    ltSlot = [0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

                byte[] rtSlot;
                if (side == TriggerSide.RT && mode != ForceAdaptMode.Vibration)
                    rtSlot = [0x01, 0x02, modeByte, p0, p1, p2, p3, p4, 0x00, 0x00];
                else
                    rtSlot = [0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

                if (mode != ForceAdaptMode.Vibration)
                {
                    return [
                        BuildSetStatusPrefix(),
                        BuildCmd(CmdConfig, SubBeginConfig, begin),
                        BuildCmd(CmdSetEffect, SubSetForceAdapt, set23),
                        BuildCmd(CmdEndConfig, SubEndConfig, end4),
                        BuildCmd(CmdSaveProfile, SubSaveProfile, ltSlot),
                        BuildCmd(CmdSaveProfile, SubSaveProfile, rtSlot),
                    ];
                }

                byte sideByte = side == TriggerSide.LT ? (byte)0x01 : (byte)0x02;
                byte h0 = set23[8], h1 = set23[9], h2 = set23[10], h3 = set23[11];
                byte h4 = set23[12], h5 = set23[13], h6 = set23[14], h7 = set23[15];
                byte[] hapticData = [sideByte, 0x02, h0, h1, h2, h3, h4, h5, h6, h7, 0x00];

                return [
                    BuildSetStatusPrefix(),
                    BuildCmd(CmdConfig, SubBeginConfig, begin),
                    BuildCmd(CmdSetEffect, SubSetForceAdapt, set23),
                    BuildCmd(CmdEndConfig, SubEndConfig, end4),
                    BuildCmd(CmdSaveProfile, SubSaveProfile, ltSlot),
                    BuildCmd(CmdSaveProfile, SubSaveProfile, rtSlot),
                    BuildCmd(0x52, 0x0B, hapticData),
                ];
            }

            /// <summary>
            /// Build V2 apply sequence with explicit LT/RT slot state for both sides.
            /// Caller provides pre-built 10-byte ltSlot and rtSlot so per-side state
            /// (mode, p0..p4) is preserved independently.
            /// </summary>
            public static byte[][] BuildApplySequenceV2(
                byte[] a5Custom23, byte[] end4, byte[] begin,
                byte[] ltSlot, byte[] rtSlot,
                byte[]? hapticData)
            {
                var seq = new List<byte[]>
                {
                    BuildSetStatusPrefix(),
                    BuildCmd(CmdConfig, SubBeginConfig, begin),
                    BuildCmd(CmdSetEffect, SubSetForceAdapt, a5Custom23),
                    BuildCmd(CmdEndConfig, SubEndConfig, end4),
                    BuildCmd(CmdSaveProfile, SubSaveProfile, ltSlot),
                    BuildCmd(CmdSaveProfile, SubSaveProfile, rtSlot),
                };
                if (hapticData != null)
                    seq.Add(BuildCmd(0x52, 0x0B, hapticData));
                return [.. seq];
            }

            public static readonly string[] V2PacketNames =
                ["11 STATUS", "A4 BEGIN", "A5 SET", "A6 END", "51 LT", "51 RT", "52 HAPTIC"];
        }
    }

    /// <summary>
    /// Build a rumble command (Report 0x05, sub-command 0x0f).
    /// </summary>
    public static byte[] BuildRumbleCommand(
        byte leftMainRumble = 0,
        byte rightMainRumble = 0,
        byte leftTriggerRumble = 0,
        byte rightTriggerRumble = 0)
    {
        var buf = new byte[OutputReportLength];
        buf[0] = ReportIdRumble;
        buf[1] = SubCmdRumble;
        buf[2] = leftMainRumble;
        buf[3] = rightMainRumble;
        buf[4] = leftTriggerRumble;
        buf[5] = rightTriggerRumble;
        return buf;
    }
}
