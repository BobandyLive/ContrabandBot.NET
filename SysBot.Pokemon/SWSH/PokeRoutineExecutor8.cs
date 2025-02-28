﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Base;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsets;

namespace SysBot.Pokemon
{
    /// <summary>
    /// Executor for SW/SH games.
    /// </summary>
    public abstract class PokeRoutineExecutor8 : PokeRoutineExecutor<PK8>
    {
        protected PokeRoutineExecutor8(PokeBotState cfg) : base(cfg) { }

        private static uint GetBoxSlotOffset(int box, int slot) => BoxStartOffset + (uint)(BoxFormatSlotSize * ((30 * box) + slot));

        public override async Task<PK8> ReadPokemon(ulong offset, CancellationToken token) => await ReadPokemon(offset, BoxFormatSlotSize, token).ConfigureAwait(false);

        public override async Task<PK8> ReadPokemon(ulong offset, int size, CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync((uint)offset, size, token).ConfigureAwait(false);
            return new PK8(data);
        }

        public override async Task<PK8> ReadPokemonPointer(IEnumerable<long> jumps, int size, CancellationToken token)
        {
            var (valid, offset) = await ValidatePointerAll(jumps, token).ConfigureAwait(false);
            if (!valid)
                return new PK8();
            return await ReadPokemon(offset, token).ConfigureAwait(false);
        }

        public async Task<PK8> ReadSurpriseTradePokemon(CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(SurpriseTradePartnerPokemonOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
            return new PK8(data);
        }

        public async Task SetBoxPokemon(PK8 pkm, int box, int slot, CancellationToken token, ITrainerInfo? sav = null)
        {
            if (sav != null)
            {
                // Update PKM to the current save's handler data
                DateTime Date = DateTime.Now;
                pkm.Trade(sav, Date.Day, Date.Month, Date.Year);
                pkm.RefreshChecksum();
            }
            var ofs = GetBoxSlotOffset(box, slot);
            pkm.ResetPartyStats();
            await Connection.WriteBytesAsync(pkm.EncryptedPartyData, ofs, token).ConfigureAwait(false);
        }

        public override async Task<PK8> ReadBoxPokemon(int box, int slot, CancellationToken token)
        {
            var ofs = GetBoxSlotOffset(box, slot);
            return await ReadPokemon(ofs, BoxFormatSlotSize, token).ConfigureAwait(false);
        }

        public async Task SetCurrentBox(int box, CancellationToken token)
        {
            await Connection.WriteBytesAsync(BitConverter.GetBytes(box), CurrentBoxOffset, token).ConfigureAwait(false);
        }

        public async Task<int> GetCurrentBox(CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(CurrentBoxOffset, 1, token).ConfigureAwait(false);
            return data[0];
        }

        public async Task<bool> ReadIsChanged(uint offset, byte[] original, CancellationToken token)
        {
            var result = await Connection.ReadBytesAsync(offset, original.Length, token).ConfigureAwait(false);
            return !result.SequenceEqual(original);
        }

        public async Task<SAV8SWSH> IdentifyTrainer(CancellationToken token)
        {
            // Check title so we can warn if mode is incorrect.
            string title = await SwitchConnection.GetTitleID(token).ConfigureAwait(false);
            if (title is not (SwordID or ShieldID))
                throw new Exception($"{title} is not a valid SWSH title. Is your mode correct?");

            Log("Grabbing trainer data of host console...");
            var sav = await GetFakeTrainerSAV(token).ConfigureAwait(false);
            InitSaveData(sav);

            if (!IsValidTrainerData())
                throw new Exception("Trainer data is not valid. Refer to the SysBot.NET wiki for bad or no trainer data.");
            if (await GetTextSpeed(token).ConfigureAwait(false) < TextSpeedOption.Fast)
                throw new Exception("Text speed should be set to FAST. Fix this for correct operation.");

            return sav;
        }

        public async Task InitializeHardware(IBotStateSettings settings, CancellationToken token)
        {
            Log("Detaching on startup.");
            await DetachController(token).ConfigureAwait(false);
            if (settings.ScreenOff)
            {
                Log("Turning off screen.");
                await SetScreen(ScreenState.Off, token).ConfigureAwait(false);
            }
        }

        public async Task CleanExit(IBotStateSettings settings, CancellationToken token)
        {
            if (settings.ScreenOff)
            {
                Log("Turning on screen.");
                await SetScreen(ScreenState.On, token).ConfigureAwait(false);
            }
            Log("Detaching controllers on routine exit.");
            await DetachController(token).ConfigureAwait(false);
        }

        /// <summary>
        /// Identifies the trainer information and loads the current runtime language.
        /// </summary>
        public async Task<SAV8SWSH> GetFakeTrainerSAV(CancellationToken token)
        {
            var sav = new SAV8SWSH();
            var info = sav.MyStatus;
            var read = await Connection.ReadBytesAsync(TrainerDataOffset, TrainerDataLength, token).ConfigureAwait(false);
            read.CopyTo(info.Data, 0);
            return sav;
        }

        protected virtual async Task EnterLinkCode(int code, PokeTradeHubConfig config, CancellationToken token)
        {
            // Default implementation to just press directional arrows. Can do via Hid keys, but users are slower than bots at even the default code entry.
            var keys = TradeUtil.GetPresses(code);
            foreach (var key in keys)
            {
                int delay = config.Timings.KeypressTime;
                await Click(key, delay, token).ConfigureAwait(false);
            }
            // Confirm Code outside of this method (allow synchronization)
        }

        public async Task EnsureConnectedToYComm(PokeTradeHubConfig config, CancellationToken token)
        {
            if (!await IsGameConnectedToYComm(token).ConfigureAwait(false))
            {
                Log("Reconnecting to Y-Comm...");
                await ReconnectToYComm(config, token).ConfigureAwait(false);
            }
        }

        public async Task<bool> IsGameConnectedToYComm(CancellationToken token)
        {
            // Reads the Y-Comm Flag is the Game is connected Online
            var data = await Connection.ReadBytesAsync(IsConnectedOffset, 1, token).ConfigureAwait(false);
            return data[0] == 1;
        }

        public async Task ReconnectToYComm(PokeTradeHubConfig config, CancellationToken token)
        {
            // Press B in case a Error Message is Present
            await Click(B, 2000, token).ConfigureAwait(false);

            // Return to Overworld
            if (!await IsOnOverworld(config, token).ConfigureAwait(false))
            {
                for (int i = 0; i < 5; i++)
                {
                    await Click(B, 500, token).ConfigureAwait(false);
                }
            }

            await Click(Y, 1000, token).ConfigureAwait(false);

            // Press it twice for safety -- sometimes misses it the first time.
            await Click(PLUS, 2_000, token).ConfigureAwait(false);
            await Click(PLUS, 5_000 + config.Timings.ExtraTimeReconnectYComm, token).ConfigureAwait(false);

            for (int i = 0; i < 5; i++)
            {
                await Click(B, 500, token).ConfigureAwait(false);
            }
        }

        public async Task ReOpenGame(PokeTradeHubConfig config, CancellationToken token)
        {
            // Reopen the game if we get soft-banned
            Log("Potential soft ban detected, reopening game just in case!");
            await CloseGame(config, token).ConfigureAwait(false);
            await StartGame(config, token).ConfigureAwait(false);

            // In case we are soft banned, reset the timestamp
            await UnSoftBan(token).ConfigureAwait(false);
        }

        public async Task UnSoftBan(CancellationToken token)
        {
            // Like previous generations, the game uses a Unix timestamp for 
            // how long we are soft banned and once the soft ban is lifted
            // the game sets the value back to 0 (1970/01/01 12:00 AM (UTC))
            Log("Soft ban detected, unbanning.");
            var data = BitConverter.GetBytes(0);
            await Connection.WriteBytesAsync(data, SoftBanUnixTimespanOffset, token).ConfigureAwait(false);
        }

        public async Task<bool> CheckIfSoftBanned(CancellationToken token)
        {
            // Check if the Unix Timestamp isn't zero, if so we are soft banned.
            var data = await Connection.ReadBytesAsync(SoftBanUnixTimespanOffset, 1, token).ConfigureAwait(false);
            return data[0] > 1;
        }

        public async Task CloseGame(PokeTradeHubConfig config, CancellationToken token)
        {
            var timing = config.Timings;
            // Close out of the game
            await Click(HOME, 2_000 + timing.ExtraTimeReturnHome, token).ConfigureAwait(false);
            await Click(X, 1_000, token).ConfigureAwait(false);
            await Click(A, 5_000 + timing.ExtraTimeCloseGame, token).ConfigureAwait(false);
            Log("Closed out of the game!");
        }

        public async Task StartGame(PokeTradeHubConfig config, CancellationToken token)
        {
            var timing = config.Timings;
            // Open game.
            await Click(A, 1_000 + timing.ExtraTimeLoadProfile, token).ConfigureAwait(false);

            // Menus here can go in the order: Update Prompt -> Profile -> DLC check -> Unable to use DLC.
            //  The user can optionally turn on the setting if they know of a breaking system update incoming.
            if (timing.AvoidSystemUpdate)
            {
                await Click(DUP, 0_600, token).ConfigureAwait(false);
                await Click(A, 1_000 + timing.ExtraTimeLoadProfile, token).ConfigureAwait(false);
            }

            await Click(A, 1_000 + timing.ExtraTimeCheckDLC, token).ConfigureAwait(false);
            // If they have DLC on the system and can't use it, requires an UP + A to start the game.
            // Should be harmless otherwise since they'll be in loading screen.
            await Click(DUP, 0_600, token).ConfigureAwait(false);
            await Click(A, 0_600, token).ConfigureAwait(false);

            Log("Restarting the game!");

            // Switch Logo lag, skip cutscene, game load screen
            await Task.Delay(10_000 + timing.ExtraTimeLoadGame, token).ConfigureAwait(false);

            for (int i = 0; i < 4; i++)
                await Click(A, 1_000, token).ConfigureAwait(false);

            var timer = 60_000;
            while (!await IsOnOverworld(config, token).ConfigureAwait(false) && !await IsInBattle(token).ConfigureAwait(false))
            {
                await Task.Delay(0_200, token).ConfigureAwait(false);
                timer -= 0_250;
                // We haven't made it back to overworld after a minute, so press A every 6 seconds hoping to restart the game.
                // Don't risk it if hub is set to avoid updates.
                if (timer <= 0 && !timing.AvoidSystemUpdate)
                {
                    Log("Still not in the game, initiating rescue protocol!");
                    while (!await IsOnOverworld(config, token).ConfigureAwait(false) && !await IsInBattle(token).ConfigureAwait(false))
                        await Click(A, 6_000, token).ConfigureAwait(false);
                    break;
                }
            }

            Log("Back in the overworld!");
        }

        public async Task<bool> IsCorrectScreen(uint expectedScreen, CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(CurrentScreenOffset, 4, token).ConfigureAwait(false);
            return BitConverter.ToUInt32(data, 0) == expectedScreen;
        }

        public async Task<uint> GetCurrentScreen(CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(CurrentScreenOffset, 4, token).ConfigureAwait(false);
            return BitConverter.ToUInt32(data, 0);
        }

        public async Task<bool> IsInBattle(CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(Version == GameVersion.SH ? InBattleRaidOffsetSH : InBattleRaidOffsetSW, 1, token).ConfigureAwait(false);
            return data[0] == (Version == GameVersion.SH ? 0x40 : 0x41);
        }

        public async Task<bool> IsInBox(CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(CurrentScreenOffset, 4, token).ConfigureAwait(false);
            var dataint = BitConverter.ToUInt32(data, 0);
            return dataint is CurrentScreen_Box1 or CurrentScreen_Box2;
        }

        public async Task<bool> IsOnOverworld(PokeTradeHubConfig config, CancellationToken token)
        {
            // Uses CurrentScreenOffset and compares the value to CurrentScreen_Overworld.
            if (config.ScreenDetection == ScreenDetectionMode.Original)
            {
                var data = await Connection.ReadBytesAsync(CurrentScreenOffset, 4, token).ConfigureAwait(false);
                var dataint = BitConverter.ToUInt32(data, 0);
                return dataint is CurrentScreen_Overworld1 or CurrentScreen_Overworld2;
            }
            // Uses an appropriate OverworldOffset for the console language.
            if (config.ScreenDetection == ScreenDetectionMode.ConsoleLanguageSpecific)
            {
                var data = await Connection.ReadBytesAsync(GetOverworldOffset(config.ConsoleLanguage), 1, token).ConfigureAwait(false);
                return data[0] == 1;
            }
            return false;
        }

        public async Task<TextSpeedOption> GetTextSpeed(CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(TextSpeedOffset, 1, token).ConfigureAwait(false);
            return (TextSpeedOption)(data[0] & 3);
        }

        public async Task SetTextSpeed(TextSpeedOption speed, CancellationToken token)
        {
            var textSpeedByte = await Connection.ReadBytesAsync(TextSpeedOffset, 1, token).ConfigureAwait(false);
            var data = new[] { (byte)((textSpeedByte[0] & 0xFC) | (int)speed) };
            await Connection.WriteBytesAsync(data, TextSpeedOffset, token).ConfigureAwait(false);
        }

        public async Task ToggleAirplane(int delay, CancellationToken token)
        {
            await PressAndHold(HOME, 2_000, 1_000, token).ConfigureAwait(false);
            for (int i = 0; i < 4; i++)
                await Click(DDOWN, i == 3 ? delay : 0_150, token).ConfigureAwait(false);
            await Click(A, 2_000, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false);
        }

        public async Task<bool> SpinTrade(uint offset, byte[] comparison, int waitms, int waitInterval, bool match, CancellationToken token)
        {
            // Revival of Red's SpinTrade
            if (!await GetCoordinatesForSpin(token).ConfigureAwait(false))
                return await ReadUntilChanged(offset, comparison, waitms, waitInterval, match, token).ConfigureAwait(false);

            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();

            do
            {
                var result = await Connection.ReadBytesAsync(offset, comparison.Length, token).ConfigureAwait(false);
                if (match == result.SequenceEqual(comparison))
                {
                    await SetStick(SwitchStick.LEFT, 0, 0, 0_100, token).ConfigureAwait(false);
                    await Task.Delay(waitInterval, token).ConfigureAwait(false);
                    return true;
                }

                if (sw.ElapsedMilliseconds < waitms - 4_000) // Give it ample time to finish the pirouette end animation before correcting position
                {
                    await SetStick(SwitchStick.LEFT, -3_500, 0, 0, token).ConfigureAwait(false); // ←
                    await SetStick(SwitchStick.LEFT, 0, -3_500, 0, token).ConfigureAwait(false); // ↓
                    await SetStick(SwitchStick.LEFT, 3_500, 0, 0, token).ConfigureAwait(false); // →
                    await SetStick(SwitchStick.LEFT, 0, 3_500, 0, token).ConfigureAwait(false); // ↑
                }
                else await SetStick(SwitchStick.LEFT, 0, 0, 0_100, token).ConfigureAwait(false);
            } while (sw.ElapsedMilliseconds < waitms);

            await Task.Delay(waitInterval, token).ConfigureAwait(false);
            await SpinCorrection(token).ConfigureAwait(false);
            return false;
        }

        public async Task SpinCorrection(CancellationToken token)
        {
            await SwitchConnection.WriteBytesAbsoluteAsync(TradeExtensions<PK8>.XCoords, TradeExtensions<PK8>.CoordinatesOffset, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(TradeExtensions<PK8>.YCoords, TradeExtensions<PK8>.CoordinatesOffset + 0x4, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(TradeExtensions<PK8>.ZCoords, TradeExtensions<PK8>.CoordinatesOffset + 0x8, token).ConfigureAwait(false);
        }

        private async Task<bool> GetCoordinatesForSpin(CancellationToken token)
        {
            if (TradeExtensions<PK8>.CoordinatesSet)
                return true;
            else if (!TradeExtensions<PK8>.CoordinatesSet && TradeExtensions<PK8>.CoordinatesOffset != 0)
                return false;

            TradeExtensions<PK8>.CoordinatesOffset = await ParsePointer("[[[[[[main+26365B8]+88]+1F8]+E0]+10]+E0]+60", token).ConfigureAwait(false); // Thank you for the pointer, Zyro <3
            TradeExtensions<PK8>.XCoords = await SwitchConnection.ReadBytesAbsoluteAsync(TradeExtensions<PK8>.CoordinatesOffset, 4, token).ConfigureAwait(false);
            TradeExtensions<PK8>.YCoords = await SwitchConnection.ReadBytesAbsoluteAsync(TradeExtensions<PK8>.CoordinatesOffset + 0x4, 4, token).ConfigureAwait(false);
            TradeExtensions<PK8>.ZCoords = await SwitchConnection.ReadBytesAbsoluteAsync(TradeExtensions<PK8>.CoordinatesOffset + 0x8, 4, token).ConfigureAwait(false);
            if (TradeExtensions<PK8>.XCoords.Length == 1 || TradeExtensions<PK8>.YCoords.Length == 1 || TradeExtensions<PK8>.ZCoords.Length == 1)
                return false;

            TradeExtensions<PK8>.CoordinatesSet = true;
            return true;
        }

        public async Task SaveGame(PokeTradeHubConfig config, CancellationToken token)
        {
            await Click(B, 0_200, token).ConfigureAwait(false);
            Log("Saving the game...");
            await Click(X, 2_000, token).ConfigureAwait(false);
            await Click(R, 0_250, token).ConfigureAwait(false);
            while (!await IsOnOverworld(config, token).ConfigureAwait(false))
                await Click(A, 0_500, token).ConfigureAwait(false);
            Log("Game saved!");
        }

        public async Task<bool> LairStatusCheck(ushort val, uint ofs, CancellationToken token) => BitConverter.GetBytes(val).SequenceEqual(await Connection.ReadBytesAsync(ofs, 2, token).ConfigureAwait(false));
        public async Task<bool> LairStatusCheck(uint val, uint ofs, CancellationToken token) => BitConverter.GetBytes(val).SequenceEqual(await Connection.ReadBytesAsync(ofs, 4, token).ConfigureAwait(false));
        public async Task<bool> LairStatusCheckMain(ushort val, ulong ofs, CancellationToken token) => BitConverter.GetBytes(val).SequenceEqual(await SwitchConnection.ReadBytesAbsoluteAsync(ofs, 2, token).ConfigureAwait(false));

        public async Task<ulong> ParsePointer(string pointer, CancellationToken token) //Code from LiveHex
        {
            var ptr = pointer;
            uint finadd = 0;
            if (!ptr.EndsWith("]"))
                finadd = Util.GetHexValue(ptr.Split('+').Last());
            var jumps = ptr.Replace("main", "").Replace("[", "").Replace("]", "").Split(new[] { "+" }, StringSplitOptions.RemoveEmptyEntries);
            if (jumps.Length == 0)
            {
                Log("Invalid Pointer");
                return 0;
            }

            var initaddress = Util.GetHexValue(jumps[0].Trim());
            ulong address = BitConverter.ToUInt64(await SwitchConnection.ReadBytesMainAsync(initaddress, 0x8, token).ConfigureAwait(false), 0);
            foreach (var j in jumps)
            {
                var val = Util.GetHexValue(j.Trim());
                if (val == initaddress)
                    continue;
                if (val == finadd)
                {
                    address += val;
                    break;
                }
                address = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(address + val, 0x8, token).ConfigureAwait(false), 0);
            }
            return address;
        }

        public async Task<PK8?> ReadUntilPresentAbsolute(ulong offset, int waitms, int waitInterval, CancellationToken token, int size = BoxFormatSlotSize) // Need to eliminate duplicate code, currently a hack
        {
            int msWaited = 0;
            while (msWaited < waitms)
            {
                var data = await SwitchConnection.ReadBytesAbsoluteAsync(offset, size, token).ConfigureAwait(false);
                var pk = new PK8(data);
                if (pk.Species != 0 && pk.ChecksumValid)
                    return pk;

                await Task.Delay(waitInterval, token).ConfigureAwait(false);
                msWaited += waitInterval;
            }
            return null;
        }
    }
}
