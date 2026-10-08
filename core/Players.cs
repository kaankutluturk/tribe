using System;
using System.Diagnostics;
using System.Globalization;
using Steamworks;
using UnityEngine;

namespace Tribe
{
    // Ping and kick for the players tab.
    //
    // Ping. Green Hell never measures latency, so tribe does: every client sends the host a tiny packet every two
    // seconds over the Steam P2P session the game already has open, on a channel the game doesn't read (it polls
    // channels 0..1; this is 77), and the host's tribe echoes it. The round trip is each player's ping to the host,
    // which is the number that matters in a hosted session. Each client publishes its own figure as lobby member data
    // ("tribe_ping"), so everyone's list shows everyone. Nothing is sent unless the session is already active, so
    // this never opens a connection of its own.
    //
    // Kick. The game has one for the host (its pause-menu player list): TransportLayerSteam.KickLobbyMember sends a
    // lobby message every client obeys, patched or not. This calls exactly that.
    static class Players
    {
        const int Channel = 77;
        const string PingKey = "tribe_ping";
        const byte Ask = 1, Answer = 2;

        static readonly byte[] packet = new byte[9];
        static float nextAsk, nextPublish;
        static int ping = -1, published = -2;

        /// <summary>Every frame: answer pings, take answers.</summary>
        public static void Pump()
        {
            if (!Patch.Applied || !SteamManager.Initialized || Patch.LobbyId == CSteamID.Nil) { ping = -1; return; }
            uint size;
            int guard = 0;
            while (guard++ < 32 && SteamNetworking.IsP2PPacketAvailable(out size, Channel))
            {
                uint read; CSteamID from;
                if (!SteamNetworking.ReadP2PPacket(packet, (uint)packet.Length, out read, out from, Channel) || read != packet.Length) continue;
                if (packet[0] == Ask)
                {
                    if (!SteamFriends.IsUserInSource(from, Patch.LobbyId)) continue;   // only people in our lobby get an answer
                    packet[0] = Answer;
                    SteamNetworking.SendP2PPacket(from, packet, (uint)packet.Length, EP2PSend.k_EP2PSendUnreliableNoDelay, Channel);
                }
                else if (packet[0] == Answer && from == Patch.OwnerId)
                {
                    long sent = BitConverter.ToInt64(packet, 1);
                    int ms = (int)Math.Max(0, (Stopwatch.GetTimestamp() - sent) * 1000 / Stopwatch.Frequency);
                    if (ms < 5000) ping = ping < 0 ? ms : (int)Math.Round(ping * 0.6 + ms * 0.4);   // smoothed
                }
            }

            float now = Time.unscaledTime;
            CSteamID me = SteamUser.GetSteamID();
            if (Patch.OwnerId == me) { ping = -1; return; }   // the host is the reference point
            if (now >= nextAsk)
            {
                nextAsk = now + 2f;
                // Only once the game itself is connected, so this never opens a connection of its own. The game's
                // status is asked rather than Steam's session state: that struct carries the peer's address, and
                // tribe has no business holding one.
                if (P2PSession.Instance != null && P2PSession.Instance.Status == P2PSession.ESessionStatus.Connected)
                {
                    packet[0] = Ask;
                    Array.Copy(BitConverter.GetBytes(Stopwatch.GetTimestamp()), 0, packet, 1, 8);
                    SteamNetworking.SendP2PPacket(Patch.OwnerId, packet, (uint)packet.Length, EP2PSend.k_EP2PSendUnreliableNoDelay, Channel);
                }
            }
            // Lobby data isn't for chatter: publish when it moved noticeably, a few seconds apart at most.
            if (ping >= 0 && now >= nextPublish && Math.Abs(ping - published) >= 5)
            {
                nextPublish = now + 4f;
                published = ping;
                SteamMatchmaking.SetLobbyMemberData(Patch.LobbyId, PingKey, ping.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>A member's ping to the host in ms, -1 when unknown (no tribe, or not measured yet).</summary>
        public static int PingOf(CSteamID lobby, CSteamID member, bool self)
        {
            if (self) return ping;
            int ms;
            return int.TryParse(SteamMatchmaking.GetLobbyMemberData(lobby, member, PingKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) && ms >= 0 ? ms : -1;
        }

        /// <summary>Host only. Null on success, otherwise why not.</summary>
        public static string Kick(ulong id)
        {
            if (!SteamManager.Initialized || Patch.LobbyId == CSteamID.Nil) return "not in a lobby";
            CSteamID me = SteamUser.GetSteamID();
            if (Patch.OwnerId != me) return "only the host can kick";
            if (id == me.m_SteamID) return "that's you";
            var steam = P2PTransportLayer.Instance as TransportLayerSteam;
            if (steam == null) return "not a steam session";
            foreach (var info in steam.GetCurrentLobbyMembers())
            {
                var address = info != null ? info.m_Address as P2PAddressSteam : null;
                if (address == null || address.m_SteamID.m_SteamID != id) continue;
                UnityEngine.Debug.Log("[tribe] kicking " + info.m_Name);
                return steam.KickLobbyMember(info) ? null : "the game refused";
            }
            return "that player already left";
        }
    }
}
