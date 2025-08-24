using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMod.API.Commands;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Players;
using OpenMod.Unturned.Plugins;
using OpenMod.Unturned.Users;
using SDG.Unturned;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenModParty
{
    public class PartyPlugin : OpenModUnturnedPlugin
    {
        public PartyPlugin(IServiceProvider sp) : base(sp) { }

        protected override UniTask OnLoadAsync()
        {
            Logger.LogInformation("OpenModParty loaded. Commands: /party <player>, /party accept, /party deny, /party leave");
            return UniTask.CompletedTask;
        }
    }

    public static class PartyService
    {
        // invitee Steam64 -> list of pending invites (last invite wins)
        private static readonly Dictionary<ulong, List<PartyInvite>> _pending = new();

        public static void SendInvite(UnturnedPlayer inviter, UnturnedPlayer invitee, TimeSpan ttl, string inviterName, string inviteeName)
        {
            var inv = new PartyInvite
            {
                Inviter = inviter.SteamId,
                Invitee = invitee.SteamId,
                ExpiresAt = DateTime.UtcNow + ttl
            };

            if (!_pending.TryGetValue(invitee.SteamId.m_SteamID, out var list))
            {
                list = new();
                _pending[invitee.SteamId.m_SteamID] = list;
            }

            list.RemoveAll(i => i.Expired);
            list.Add(inv);

            _ = inviter.PrintMessageAsync($"You invited {inviteeName}.");
            _ = invitee.PrintMessageAsync($"{inviterName} invited you to their party. Use /party accept or /party deny.");
        }

        public static bool Accept(UnturnedPlayer invitee, UnturnedPlayer inviter, string inviteeName, string inviterName)
        {
            if (!_pending.TryGetValue(invitee.SteamId.m_SteamID, out var list)) return false;
            list.RemoveAll(i => i.Expired);
            if (list.Count == 0) return false;

            // most recent invite, optionally filtered by inviter
            var chosen = inviter != null
                ? list.LastOrDefault(i => i.Inviter == inviter.SteamId)
                : list.Last();

            if (chosen.Inviter.m_SteamID == 0) return false;
            if (inviter == null) return false;

            // Ensure inviter has a group; create if needed
            var gid = inviter.Player.quests.groupID;
            if (gid == CSteamID.Nil || gid.m_SteamID == 0)
            {
                if (!TryCreateGroupAndAssignOwner(inviter, out gid)) return false;
            }

            // Join invitee to inviter's group (respect member limit = false)
            invitee.Player.quests.ServerAssignToGroup(gid, EPlayerGroupRank.MEMBER, false);

            _ = invitee.PrintMessageAsync($"You joined {inviterName}'s party.");
            _ = inviter.PrintMessageAsync($"{inviteeName} joined your party.");

            list.Remove(chosen);
            return true;
        }

        public static bool Deny(UnturnedPlayer invitee, UnturnedPlayer inviter, string inviteeName, string inviterName)
        {
            if (!_pending.TryGetValue(invitee.SteamId.m_SteamID, out var list)) return false;
            list.RemoveAll(i => i.Expired);
            if (list.Count == 0) return false;

            var chosen = inviter != null
                ? list.LastOrDefault(i => i.Inviter == inviter.SteamId)
                : list.Last();

            if (chosen.Inviter.m_SteamID == 0) return false;

            if (inviter != null)
                _ = inviter.PrintMessageAsync($"{inviteeName} denied your party invite.");

            _ = invitee.PrintMessageAsync("Invite denied.");
            list.Remove(chosen);
            return true;
        }

        public static bool Leave(UnturnedPlayer player)
        {
            var gid = player.Player.quests.groupID;
            if (gid == CSteamID.Nil || gid.m_SteamID == 0) return false;

            // Clear group by assigning NIL (rank ignored when NIL; still pass MEMBER + bool)
            player.Player.quests.ServerAssignToGroup(CSteamID.Nil, EPlayerGroupRank.MEMBER, false);
            _ = player.PrintMessageAsync("You left the party.");
            return true;
        }

        /// <summary>
        /// Create a new group and set owner as OWNER.
        /// Works across versions (serverCreateGroup / ServerCreateGroup).
        /// </summary>
        private static bool TryCreateGroupAndAssignOwner(UnturnedPlayer owner, out CSteamID groupId)
        {
            groupId = CSteamID.Nil;
            try
            {
                var gmType = typeof(GroupManager);
                var mi = gmType.GetMethod("serverCreateGroup") ?? gmType.GetMethod("ServerCreateGroup");
                if (mi == null) return false;

                object[] args = new object[] { default(CSteamID) };
                var ok = (bool)mi.Invoke(null, args);
                var newGroup = (CSteamID)args[0];

                if (!ok || newGroup == CSteamID.Nil || newGroup.m_SteamID == 0) return false;

                owner.Player.quests.ServerAssignToGroup(newGroup, EPlayerGroupRank.OWNER, false);
                groupId = newGroup;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private struct PartyInvite
        {
            public CSteamID Inviter;
            public CSteamID Invitee;
            public DateTime ExpiresAt;
            public bool Expired => DateTime.UtcNow > ExpiresAt;
        }
    }

    [Command("party")]
    [CommandDescription("Invite players to your party, accept/deny, or leave.")]
    public class CmdParty : UnturnedCommand
    {
        private readonly IUnturnedUserDirectory _users;

        public CmdParty(IServiceProvider sp, IUnturnedUserDirectory users) : base(sp)
        {
            _users = users;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Actor is not UnturnedUser meUser)
                throw new UserFriendlyException("Players only.");

            var me = meUser.Player;

            if (Context.Parameters.Length == 0)
                throw new CommandWrongUsageException("party");

            var sub = Context.Parameters[0].ToLowerInvariant();

            if (sub == "accept")
            {
                // optional second arg to disambiguate inviter
                UnturnedUser inviterUser = null;
                if (Context.Parameters.Length >= 2)
                {
                    var key = Context.Parameters[1];
                    inviterUser = _users.GetOnlineUsers()
                        .FirstOrDefault(x => x.Id.Equals(key, StringComparison.OrdinalIgnoreCase)
                                          || x.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase));
                }

                var inviterPlayer = inviterUser?.Player;
                if (!PartyService.Accept(me, inviterPlayer, meUser.DisplayName, inviterUser?.DisplayName ?? ""))
                    throw new UserFriendlyException("No matching invite.");
                return;
            }

            if (sub == "deny")
            {
                UnturnedUser inviterUser = null;
                if (Context.Parameters.Length >= 2)
                {
                    var key = Context.Parameters[1];
                    inviterUser = _users.GetOnlineUsers()
                        .FirstOrDefault(x => x.Id.Equals(key, StringComparison.OrdinalIgnoreCase)
                                          || x.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase));
                }

                var inviterPlayer = inviterUser?.Player;
                if (!PartyService.Deny(me, inviterPlayer, meUser.DisplayName, inviterUser?.DisplayName ?? ""))
                    throw new UserFriendlyException("No matching invite.");
                return;
            }

            if (sub == "leave")
            {
                if (!PartyService.Leave(me))
                    throw new UserFriendlyException("You’re not in a party.");
                return;
            }

            // otherwise treat the whole input as target player query
            string query = string.Join(" ", Context.Parameters);

            var targetUser = _users.GetOnlineUsers()
                .FirstOrDefault(x =>
                    x.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    x.Id.Equals(query, StringComparison.OrdinalIgnoreCase));

            if (targetUser == null || targetUser.SteamId.m_SteamID == me.SteamId.m_SteamID)
                throw new UserFriendlyException("Player not found.");

            PartyService.SendInvite(me, targetUser.Player, TimeSpan.FromSeconds(60),
                                    meUser.DisplayName, targetUser.DisplayName);

            await UniTask.CompletedTask;
        }
    }
}
