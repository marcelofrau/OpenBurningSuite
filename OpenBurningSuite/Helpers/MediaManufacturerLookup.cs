// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace OpenBurningSuite.Helpers;

public static class MediaManufacturerLookup
{
    private static readonly Dictionary<string, string> AtipManufacturers = new()
    {
        // --- TDK ---
        { "97m15s03f", "TDK Corp." },
        { "97m15s05f", "TDK Corp." },
        { "97m15s06f", "TDK Corp." },
        { "97m15s08f", "TDK Corp." },
        { "97m15s10f", "TDK Corp." },
        { "97m15s15f", "TDK Corp." },
        { "97m15s20f", "TDK Corp." },
        { "97m15s25f", "TDK Corp." },
        { "97m15s30f", "TDK Corp." },

        // --- Taiyo Yuden ---
        { "97m17s01f", "Taiyo Yuden" },
        { "97m17s02f", "Taiyo Yuden" },
        { "97m17s03f", "Taiyo Yuden" },
        { "97m17s04f", "Taiyo Yuden" },
        { "97m17s05f", "Taiyo Yuden" },
        { "97m17s06f", "Taiyo Yuden" },
        { "97m17s07f", "Taiyo Yuden" },
        { "97m17s08f", "Taiyo Yuden" },
        { "97m17s09f", "Taiyo Yuden" },
        { "97m17s10f", "Taiyo Yuden" },
        { "97m17s12f", "Taiyo Yuden" },
        { "97m17s15f", "Taiyo Yuden" },
        { "97m17s20f", "Taiyo Yuden" },
        { "97m17s25f", "Taiyo Yuden" },
        { "97m17s30f", "Taiyo Yuden" },

        // --- Mitsubishi ---
        { "97m24s01f", "Mitsubishi" },
        { "97m24s02f", "Mitsubishi" },
        { "97m24s03f", "Mitsubishi" },
        { "97m24s04f", "Mitsubishi" },
        { "97m24s05f", "Mitsubishi" },
        { "97m24s06f", "Mitsubishi" },
        { "97m24s10f", "Mitsubishi" },
        { "97m24s15f", "Mitsubishi" },
        { "97m24s20f", "Mitsubishi" },
        { "97m41s02f", "Mitsubishi" },
        { "97m41s03f", "Mitsubishi" },

        // --- Ricoh ---
        { "97m25s05f", "Ricoh" },
        { "97m25s10f", "Ricoh" },

        // --- Sony ---
        { "97m26s05f", "Sony" },
        { "97m26s10f", "Sony" },
        { "97m26s15f", "Sony" },

        // --- CMC Magnetics ---
        { "97m26s66f", "CMC Magnetics" },
        { "97m26s67f", "CMC Magnetics" },
        { "97m26s68f", "CMC Magnetics" },
        { "97m26s70f", "CMC Magnetics" },
        { "97m31s03f", "CMC Magnetics" },
        { "97m32s03f", "CMC Magnetics" },
        { "97m13s00f", "CMC Magnetics" },

        // --- Philips ---
        { "97m27s03f", "Philips" },
        { "97m27s04f", "Philips" },
        { "97m27s05f", "Philips" },
        { "97m27s06f", "Philips" },
        { "97m27s07f", "Philips" },
        { "97m27s10f", "Philips" },
        { "97m27s15f", "Philips" },

        // --- Kodak ---
        { "97m28s01f", "Kodak" },
        { "97m28s02f", "Kodak" },
        { "97m28s03f", "Kodak" },
        { "97m28s04f", "Kodak" },
        { "97m28s05f", "Kodak" },
        { "97m28s06f", "Kodak" },
        { "97m28s10f", "Kodak" },
        { "97m46s01f", "Kodak" },
        { "97m46s02f", "Kodak" },

        // --- Maxell ---
        { "97m29s01f", "Maxell" },
        { "97m29s02f", "Maxell" },
        { "97m29s03f", "Maxell" },
        { "97m29s04f", "Maxell" },
        { "97m29s07f", "Maxell" },
        { "97m29s10f", "Maxell" },

        // --- Samsung ---
        { "97m31s02f", "Samsung" },
        { "97m33s05f", "Samsung" },

        // --- Prodisc ---
        { "97m32s05f", "Prodisc" },

        // --- LG ---
        { "97m33s02f", "LG" },

        // --- Fuji Photo Film ---
        { "97m22s01f", "Fuji Photo Film" },
        { "97m33s04f", "Fuji Photo Film" },
        { "97m44s01f", "Fuji Photo Film" },
        { "97m44s02f", "Fuji Photo Film" },
        { "97m44s03f", "Fuji Photo Film" },
        { "97m44s04f", "Fuji Photo Film" },
        { "97m44s05f", "Fuji Photo Film" },
        { "97m44s06f", "Fuji Photo Film" },
        { "97m44s07f", "Fuji Photo Film" },

        // --- BASF / EMTEC ---
        { "97m33s06f", "EMTEC" },
        { "97m33s07f", "EMTEC" },

        // --- Hitachi ---
        { "97m34s01f", "Hitachi Maxell" },
        { "97m34s03f", "Hitachi" },

        // --- Plasmon ---
        { "97m34s05f", "Plasmon" },

        // --- JVC / Victor ---
        { "97m35s01f", "JVC" },

        // --- TEAC ---
        { "97m35s05f", "TEAC" },

        // --- Ritek ---
        { "97m36s03f", "Ritek" },
        { "97m36s05f", "Ritek" },
        { "97m36s06f", "Ritek" },
        { "97m36s10f", "Ritek" },

        // --- Lead Data ---
        { "97m37s05f", "Lead Data" },

        // --- Gigastorage ---
        { "97m38s01f", "Gigastorage" },
        { "97m38s02f", "Gigastorage" },
        { "97m38s04f", "Gigastorage" },
        { "97m38s05f", "Gigastorage" },

        // --- Princo ---
        { "97m18s02f", "Princo" },

        // --- SKC ---
        { "97m39s01f", "SKC Co." },
        { "97m39s02f", "SKC Co." },
        { "97m39s03f", "SKC Co." },

        // --- Moser Baer ---
        { "97m40s01f", "Moser Baer" },
        { "97m40s02f", "Moser Baer" },
        { "97m40s03f", "Moser Baer" },
        { "97m40s04f", "Moser Baer" },
        { "97m40s05f", "Moser Baer" },

        // --- Memorex ---
        { "97m42s01f", "Memorex" },
        { "97m42s02f", "Memorex" },

        // --- Pioneer ---
        { "97m43s01f", "Pioneer" },

        // --- Panasonic ---
        { "97m45s01f", "Panasonic" },
        { "97m45s02f", "Panasonic" },
    };

    private static readonly Dictionary<string, string> DvdBdManufacturers = new()
    {
        // --- Mitsubishi / Verbatim ---
        { "MCC 001", "Mitsubishi" },
        { "MCC 002", "Mitsubishi" },
        { "MCC 003", "Mitsubishi" },
        { "MCC 004", "Mitsubishi" },
        { "MCC 005", "Mitsubishi" },
        { "MCC 006", "Mitsubishi" },
        { "MCC 007", "Mitsubishi" },
        { "MCC 008", "Mitsubishi" },
        { "MCC 009", "Mitsubishi" },
        { "MCC 010", "Mitsubishi" },
        { "MCC 01RG20", "Mitsubishi" },
        { "MCC 02RG20", "Mitsubishi" },
        { "MCC 03RG20", "Mitsubishi" },
        { "VERBAT-IM", "Verbatim" },
        { "VERBAT-M", "Verbatim" },
        { "MKM 001", "Mitsubishi" },
        { "MKM 002", "Mitsubishi" },
        { "MKM 003", "Mitsubishi" },
        { "MKM 004", "Mitsubishi" },
        { "MKM 005", "Mitsubishi" },

        // --- Taiyo Yuden ---
        { "TYG01", "Taiyo Yuden" },
        { "TYG02", "Taiyo Yuden" },
        { "TYG03", "Taiyo Yuden" },
        { "TYG11", "Taiyo Yuden" },
        { "YUDEN000 T01", "Taiyo Yuden" },
        { "YUDEN000 T02", "Taiyo Yuden" },
        { "YUDEN000 T03", "Taiyo Yuden" },

        // --- Ricoh ---
        { "RICOHJPN R00", "Ricoh" },
        { "RICOHJPN R01", "Ricoh" },
        { "RICOHJPN R02", "Ricoh" },
        { "RICOHJPN W11", "Ricoh" },
        { "RICOHJPN W12", "Ricoh" },
        { "RICOHJPN W13", "Ricoh" },

        // --- Sony ---
        { "SONY04D1", "Sony" },
        { "SONY08D1", "Sony" },
        { "SONY16D1", "Sony" },
        { "SONY R1", "Sony" },
        { "SONY R2", "Sony" },

        // --- TDK ---
        { "TDK 001", "TDK" },
        { "TDK 002", "TDK" },
        { "TDK 003", "TDK" },
        { "TDK 004", "TDK" },

        // --- CMC Magnetics ---
        { "CMC MAG AE1", "CMC Magnetics" },
        { "CMC MAG AE2", "CMC Magnetics" },
        { "CMC MAG M01", "CMC Magnetics" },
        { "CMC MAG AM1", "CMC Magnetics" },
        { "CMC MAG AF1", "CMC Magnetics" },
        { "CMC MAG AF2", "CMC Magnetics" },
        { "CMC MAG AC1", "CMC Magnetics" },
        { "CMC MAG AD1", "CMC Magnetics" },
        { "CMCMGR10", "CMC Magnetics" },
        { "CMCMGR20", "CMC Magnetics" },
        { "CMCMGRA5", "CMC Magnetics" },
        { "CMCMGRB5", "CMC Magnetics" },
        { "CMCMGRC5", "CMC Magnetics" },

        // --- Prodisc ---
        { "PRODISC R01", "Prodisc" },
        { "PRODISC R02", "Prodisc" },
        { "PRODISC R03", "Prodisc" },
        { "PRODISC S04", "Prodisc" },
        { "PRODISC F01", "Prodisc" },

        // --- Ritek ---
        { "RITEK R1", "Ritek" },
        { "RITEK R2", "Ritek" },
        { "RITEK R3", "Ritek" },
        { "RITEK R4", "Ritek" },
        { "RITEK S1", "Ritek" },
        { "RITEK S2", "Ritek" },
        { "RITEK S3", "Ritek" },
        { "RITEK S4", "Ritek" },
        { "RITEK F1", "Ritek" },
        { "RITEK G1", "Ritek" },
        { "RITEK G2", "Ritek" },
        { "RITEK G3", "Ritek" },

        // --- Moser Baer ---
        { "MBIP R00", "Moser Baer" },
        { "MBIP R10", "Moser Baer" },
        { "MBIP R20", "Moser Baer" },
        { "MBIP R30", "Moser Baer" },
        { "MBIP R40", "Moser Baer" },
        { "MBIP R50", "Moser Baer" },
        { "MBIP R60", "Moser Baer" },
        { "MBIP R70", "Moser Baer" },
        { "MBIP R80", "Moser Baer" },
        { "MBIP R90", "Moser Baer" },

        // --- Infomedia ---
        { "INFOME R10", "Infomedia" },
        { "INFOME R20", "Infomedia" },
        { "INFOME R30", "Infomedia" },

        // --- Gigastorage ---
        { "GSC001", "Gigastorage" },
        { "GSC002", "Gigastorage" },
        { "GSC003", "Gigastorage" },
        { "GSC004", "Gigastorage" },
        { "GSC005", "Gigastorage" },

        // --- Optodisc ---
        { "OPTODISC R01", "Optodisc" },
        { "OPTODISC R02", "Optodisc" },
        { "OPTODISC S01", "Optodisc" },
        { "OPTODISC S02", "Optodisc" },

        // --- BeAll / Daxten ---
        { "BEALL R01", "BeAll" },
        { "BEALL R02", "BeAll" },
        { "BEALL R03", "BeAll" },

        // --- DVD+R DL specific ---
        { "UMEDISC-DL1-64", "UME Disc" },
        { "RITEK D01", "Ritek" },
        { "RITEK D02", "Ritek" },
        { "RITEK D03", "Ritek" },
        { "MCC D01", "Mitsubishi" },
        { "MCC D02", "Mitsubishi" },
        { "MCC D03", "Mitsubishi" },
        { "SONY D11", "Sony" },

        // --- HD DVD ---
        { "HDDVD-R 1", "General" },
        { "HDDVD-R 2", "General" },
        { "HDDVD-RAM 1", "General" },
        { "HDDVD-RW 1", "General" },

        // --- BD-R / BD-RE ---
        { "TTH01", "Taiyo Yuden" },
        { "PANASI", "Panasonic" },
        { "SUNMY1", "Sony" },
        { "M-JP001", "Mitsubishi" },
        { "M-JP002", "Mitsubishi" },
        { "M-JP003", "Mitsubishi" },
        { "M-JP004", "Mitsubishi" },
        { "M-JP005", "Mitsubishi" },
        { "RE-JP001", "Mitsubishi" },
        { "RE-JP002", "Mitsubishi" },
        { "RE-JP003", "Mitsubishi" },
        { "RE-JP004", "Mitsubishi" },
        { "RE-JP005", "Mitsubishi" },
        { "CMCMAGB1", "CMC Magnetics" },
        { "CMCMAGB2", "CMC Magnetics" },
        { "CMCMAGB3", "CMC Magnetics" },
        { "CMCMAGB4", "CMC Magnetics" },
        { "CMCMAGB5", "CMC Magnetics" },
        { "RITEK B1", "Ritek" },
        { "RITEK B2", "Ritek" },
        { "RITEK B3", "Ritek" },
        { "RITEK BR1", "Ritek" },
        { "RITEK BR2", "Ritek" },
        { "RITEK BR3", "Ritek" },
        { "PRODISCB1", "Prodisc" },
        { "PRODISCB2", "Prodisc" },
        { "PRODISCB3", "Prodisc" },
        { "INFOMEB1", "Infomedia" },
    };

    public static string LookupAtipManufacturer(string discId)
    {
        if (string.IsNullOrWhiteSpace(discId))
            return discId ?? string.Empty;

        if (AtipManufacturers.TryGetValue(discId, out var name))
            return name;

        // Fallback: match on minute+second prefix (e.g. "97m26s")
        if (discId.Length >= 6)
        {
            var prefix = discId[..6]; // "97m26s"
            foreach (var kvp in AtipManufacturers)
            {
                if (kvp.Key.StartsWith(prefix))
                    return kvp.Value;
            }
        }

        return discId;
    }

    public static string LookupDvdBdManufacturer(string mid)
    {
        if (string.IsNullOrWhiteSpace(mid))
            return mid ?? string.Empty;

        if (DvdBdManufacturers.TryGetValue(mid, out var name))
            return name;

        // Partial match fallback: check if any MID starts with the lookup key or vice versa
        foreach (var kvp in DvdBdManufacturers)
        {
            if (mid.StartsWith(kvp.Key) || kvp.Key.StartsWith(mid))
                return kvp.Value;
        }

        return mid;
    }
}
