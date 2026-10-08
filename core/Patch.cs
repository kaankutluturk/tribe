using System;
using System.Collections.Generic;
using System.Reflection;
using Steamworks;
using UnityEngine;

namespace Tribe
{
    // Lifts Green Hell's four-player co-op limit. The limit is P2PSession.MAX_PLAYERS (a plain static, 4), which the
    // host hands to Steam when it creates the lobby. Three containers are sized from it once and overflow with a
    // fifth player on EVERY machine in the session, which is why every player runs this, not just the host:
    //   AIManager.s_PlayerPositionsHolder        Vector3[4], refilled with every player's position each AI update
    //   ReplicationComponent.s_SendToPeers.peers P2PPeer[4], the per-send list of remote peers
    //   HUDCoopPlayers.m_Elements                [3] name/health tags for the other players; its Update loops to
    //                                            MAX_PLAYERS - 1, so the array must grow before the limit does
    // Everyone gets the same fixed capacity; how many may actually join is the host's Steam lobby limit, which the
    // host's copy keeps at the chosen size. The patch stays until the game exits: shrinking back with extra players
    // in the session would break it.
    static class Patch
    {
        public const int Capacity = 16, Vanilla = 4;
        const string Tag = "tribe";                    // lobby member data: "1" on every patched player

        public static int Wanted = 8;                  // lobby size when we host
        public static bool Applied;
        public static string Error = "";

        // What the last Maintain saw, for the exe.
        public static int Lobby, Members, Limit;   // Lobby: 0 none, 1 member, 2 owner
        public static readonly List<string> Unpatched = new List<string>();

        // One row per lobby member, for the players tab. Patch: 0 no tribe, 1 patched, 2 just joined (tag not seen yet).
        public struct Row { public ulong Id; public string Name; public bool Self, Host; public int Patch, Ping; }
        public static readonly List<Row> Rows = new List<Row>();
        public static CSteamID LobbyId = CSteamID.Nil, OwnerId = CSteamID.Nil;   // Nil outside a lobby

        static CSteamID taggedLobby = CSteamID.Nil;
        // A joining player's tag takes a moment to reach us; only call them unpatched once it has been missing this long.
        const float TagGrace = 10f;
        static readonly Dictionary<ulong, float> untaggedSince = new Dictionary<ulong, float>();
        static FieldInfo lobbyField;

        public static void Apply()
        {
            if (Applied) return;
            try
            {
                // Containers first, the limit last: nothing may loop to the new limit over an old-sized array.
                GrowPositions();
                GrowSendList();
                GrowHud();
                P2PSession.MAX_PLAYERS = Capacity;
                Applied = true;
                Error = "";
                Debug.Log("[tribe] player limit lifted: capacity " + Capacity);
            }
            catch (Exception e)
            {
                Error = e.Message;
                Debug.LogError("[tribe] patch failed, limit left at " + P2PSession.MAX_PLAYERS + ": " + e);
            }
        }

        static void GrowPositions()
        {
            var old = AIs.AIManager.s_PlayerPositionsHolder;
            if (old != null && old.Length >= Capacity) return;
            var big = new Vector3[Capacity];
            if (old != null) Array.Copy(old, big, old.Length);
            AIs.AIManager.s_PlayerPositionsHolder = big;
        }

        static void GrowSendList()
        {
            var holder = typeof(ReplicationComponent).GetField("s_SendToPeers", BindingFlags.NonPublic | BindingFlags.Static);
            if (holder == null) throw new MissingFieldException("ReplicationComponent.s_SendToPeers");
            object list = holder.GetValue(null);
            var peers = list.GetType().GetField("peers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (peers == null) throw new MissingFieldException("SendToPeers.peers");
            var old = (P2PPeer[])peers.GetValue(list);
            if (old.Length >= Capacity) return;
            var big = new P2PPeer[Capacity];
            Array.Copy(old, big, old.Length);
            peers.SetValue(list, big);
        }

        /// <summary>
        /// Gives every existing co-op hud the extra tags, the way its own Awake makes them. Huds created after the
        /// limit is lifted size themselves; this catches the ones that already existed, so it also runs after each
        /// scene load and now and then (cheap when there's nothing to do).
        /// </summary>
        public static void GrowHud()
        {
            Type hudType = typeof(P2PSession).Assembly.GetType("HUDCoopPlayers");   // internal class
            if (hudType == null) throw new TypeLoadException("HUDCoopPlayers");
            var elements = hudType.GetField("m_Elements", BindingFlags.NonPublic | BindingFlags.Instance);
            var prefab = hudType.GetField("m_HudPrefab", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (elements == null || prefab == null) throw new MissingFieldException("HUDCoopPlayers.m_Elements / m_HudPrefab");
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(hudType))
            {
                var hud = o as MonoBehaviour;
                if (hud == null || !hud.gameObject.scene.IsValid()) continue;   // prefab assets aren't in a scene
                var old = (Array)elements.GetValue(hud);
                if (old == null || old.Length >= Capacity - 1) continue;
                Type tag = old.GetType().GetElementType();
                Array big = Array.CreateInstance(tag, Capacity - 1);
                Array.Copy(old, big, old.Length);
                var template = prefab.GetValue(hud) as GameObject;
                if (template == null) throw new NullReferenceException("HUDCoopPlayers.m_HudPrefab is empty");
                for (int i = old.Length; i < big.Length; i++)
                {
                    GameObject made = UnityEngine.Object.Instantiate(template, hud.transform);
                    made.SetActive(false);
                    big.SetValue(made.GetComponent(tag), i);
                }
                elements.SetValue(hud, big);
                Debug.Log("[tribe] co-op hud grown to " + big.Length + " tags");
            }
        }

        /// <summary>
        /// Once a second. Tags us in the Steam lobby as patched, and when we own the lobby keeps its member limit at
        /// the chosen size. While anyone in the lobby isn't tagged the limit stays at the game's own four, so an
        /// unpatched player is never pushed past what their game can hold.
        /// </summary>
        public static void Maintain()
        {
            Lobby = 0; Members = 0; Limit = 0;
            Unpatched.Clear();
            Rows.Clear();
            LobbyId = OwnerId = CSteamID.Nil;
            if (!Applied || !SteamManager.Initialized) return;
            var steam = P2PTransportLayer.Instance as TransportLayerSteam;
            if (steam == null) return;
            if (lobbyField == null) lobbyField = typeof(TransportLayerSteam).GetField("m_LobbyId", BindingFlags.NonPublic | BindingFlags.Instance);
            if (lobbyField == null) return;
            var lobby = (CSteamID)lobbyField.GetValue(steam);
            if (lobby == CSteamID.Nil || !lobby.IsValid()) { taggedLobby = CSteamID.Nil; untaggedSince.Clear(); return; }

            CSteamID me = SteamUser.GetSteamID();
            if (taggedLobby != lobby) { SteamMatchmaking.SetLobbyMemberData(lobby, Tag, "1"); taggedLobby = lobby; }

            CSteamID host = SteamMatchmaking.GetLobbyOwner(lobby);
            LobbyId = lobby; OwnerId = host;
            Members = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < Members; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                var row = new Row { Id = member.m_SteamID, Self = member == me, Host = member == host, Patch = 1, Ping = -1 };
                row.Name = row.Self ? SteamFriends.GetPersonaName() : SteamFriends.GetFriendPersonaName(member);
                if (!row.Host) row.Ping = Players.PingOf(lobby, member, row.Self);
                if (!row.Self)
                {
                    if (SteamMatchmaking.GetLobbyMemberData(lobby, member, Tag) == "1") untaggedSince.Remove(member.m_SteamID);
                    else
                    {
                        float since;
                        if (!untaggedSince.TryGetValue(member.m_SteamID, out since)) untaggedSince[member.m_SteamID] = since = Time.unscaledTime;
                        bool late = Time.unscaledTime - since >= TagGrace;
                        row.Patch = late ? 0 : 2;
                        if (late) Unpatched.Add(row.Name);
                    }
                }
                Rows.Add(row);
            }

            bool owner = host == me;
            Lobby = owner ? 2 : 1;
            if (owner)
            {
                Police();
                // Never below the people already in: a limit under the head count would read as "someone's about to go".
                int wanted = Mathf.Clamp(Mathf.Max(Wanted, Members), Vanilla, Capacity);
                // Someone without tribe in a lobby of four or fewer is fine as long as nobody else can come in. Above
                // four they're being removed (auto-kick), so the lobby keeps its size.
                bool hold = Unpatched.Count > 0 && !(AutoKick && Members > Vanilla);
                int target = hold ? Vanilla : wanted;
                if (SteamMatchmaking.GetLobbyMemberLimit(lobby) != target)
                {
                    SteamMatchmaking.SetLobbyMemberLimit(lobby, target);
                    Debug.Log("[tribe] lobby limit set to " + target + (hold ? " (held: unpatched player present)" : ""));
                }
            }
            Limit = SteamMatchmaking.GetLobbyMemberLimit(lobby);
        }

        // ---- host: bans and auto-kick ------------------------------------------------------------------------------

        public static bool AutoKick = true;
        public static readonly HashSet<ulong> Bans = new HashSet<ulong>();
        /// <summary>Things the host should hear about (shown in the game and sent to tribe.exe); drained by the runner.</summary>
        public static readonly List<string> Events = new List<string>();
        static readonly Dictionary<ulong, float> kickedAt = new Dictionary<ulong, float>(), warnedAt = new Dictionary<ulong, float>();

        // Banned players go the moment they appear. Players without tribe go only when the lobby holds more than
        // four (the one situation where their game breaks), and only after the grace that made them "unpatched".
        static void Police()
        {
            foreach (var row in Rows)
            {
                if (row.Self) continue;
                bool banned = Bans.Contains(row.Id);
                bool unpatched = AutoKick && row.Patch == 0 && Members > Vanilla;
                if (!banned && !unpatched) continue;
                float last;
                if (kickedAt.TryGetValue(row.Id, out last) && Time.unscaledTime - last < 5f) continue;   // the kick takes a moment to land
                if (!banned)
                {
                    // Say why first, and leave three seconds to read it.
                    float warned;
                    if (!warnedAt.TryGetValue(row.Id, out warned)) { warnedAt[row.Id] = Time.unscaledTime; Say(row.Name + " needs tribe to join a lobby this size"); continue; }
                    if (Time.unscaledTime - warned < 3f) continue;
                    warnedAt.Remove(row.Id);
                }
                kickedAt[row.Id] = Time.unscaledTime;
                string error = Players.Kick(row.Id);
                Events.Add(error != null ? "could not kick " + row.Name + ": " + error : banned ? row.Name + " is banned · kicked" : row.Name + " has no tribe · kicked");
            }
        }

        // A line in the game's own text chat, so the player being removed (who has no tribe to tell them) and
        // everyone else can see why. The sender's own history needs the line added by hand, as the chat box does.
        static void Say(string text)
        {
            try
            {
                if (P2PSession.Instance != null) P2PSession.Instance.SendTextChatMessage(text);
                HUDTextChatHistory.AddMessageLocalized(text);
            }
            catch (Exception e) { Debug.LogWarning("[tribe] chat line failed: " + e.Message); }
        }
    }
}
