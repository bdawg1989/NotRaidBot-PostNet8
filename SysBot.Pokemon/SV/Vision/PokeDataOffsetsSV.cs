using System.Collections.Generic;

namespace SysBot.Pokemon
{
    /// <summary>
    /// Pokémon Scarlet/Violet RAM offsets
    /// </summary>
    public class PokeDataOffsetsSV
    {
        public const string SVGameVersion = "2.0.2";
        public const string ScarletID = "0100A3D008C5C000";
        public const string VioletID = "01008F6008C5E000";
        public IReadOnlyList<long> BoxStartPokemonPointer           { get; } = new long[] { 0x4617648, 0xD8, 0x8, 0xB8, 0x30, 0x9D0, 0x0 };
        public IReadOnlyList<long> MyStatusPointer                  { get; } = new long[] { 0x4617648, 0xD8, 0x8, 0xB8, 0x0, 0x40 }; // KMyStatus - TeraFinder or Official
        public IReadOnlyList<long> ConfigPointer                    { get; } = new long[] { 0x4617648, 0xD8, 0x8, 0xB8, 0xD0, 0x40 };
        public IReadOnlyList<long> CurrentBoxPointer                { get; } = new long[] { 0x4617648, 0xD8, 0x8, 0xB8, 0x28, 0x570 };
        public IReadOnlyList<long> LinkTradePartnerNIDPointer       { get; } = new long[] { 0x46404B8, 0xF8, 0x8 };
        public IReadOnlyList<long> Trader2MyStatusPointer           { get; } = new long[] { 0x461BE58, 0x48, 0xE0, 0x0 };
        public IReadOnlyList<long> IsConnectedPointer               { get; } = new long[] { 0x461B3D8, 0x30 };
        public IReadOnlyList<long> OverworldPointer                 { get; } = new long[] { 0x461CB18, 0x160, 0xE8, 0x28 };
        // RaidCrawler Offsets
        public IReadOnlyList<long> BlockKeyPointer                  { get; } = new long[] { 0x4617648, 0xD8, 0x0, 0x0, 0x30, 0x0 }; // RaidCrawler Offsets.cs
        public IReadOnlyList<long> RaidBlockPointerP                { get; } = new long[] { 0x4623A30, 0x198, 0x88, 0x40 }; // RaidBlockPointerBase RaidCrawler (Can use same as ConfigPointer)
        public IReadOnlyList<long> RaidBlockPointerK                { get; } = new long[] { 0x4623A30, 0x198, 0x88, 0xCD8 }; // RaidBlockPointerK RaidCrawler (Can use same as ConfigPointer)
        // TeraFinder Offsets
        public static IReadOnlyList<long> SaveBlockKeyPointer       { get; } = new long[] { 0x4617648, 0xD8, 0x0, 0x0, 0x30, 0x08 }; //TeraFinder
        public IReadOnlyList<long> TeraRaidCodePointer              { get; } = new long[] { 0x46404B8, 0x10, 0x78, 0x10, 0x1A9 }; // Zyro
        public ulong TeraLobbyIsConnected                           { get; } = 0x042CB430; // Zyro
        public ulong LoadedIntoDesiredState                         { get; } = 0x046B4020; // Zyro

        public const int BoxFormatSlotSize = 0x158;
        public const ulong LibAppletWeID = 0x010000000000100a; // One of the process IDs for the news.
    }
}
