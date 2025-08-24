using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMod.API.Commands;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Players;
using OpenMod.Unturned.Plugins;
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
        // invitee -> list of invites (kept simple, last invite wins)
        private static readonly Dictionary<ulong, List<PartyInvite>> _pending = new();

        public static void SendInvite(UnturnedPlayer inviter, UnturnedPlayer invitee, TimeSpan ttl)
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

            ChatManager.serverSendMessage($"You invited {invitee.DisplayName}.",
                UnityEngine.Color.cyan, toPlayer: inviter.SteamPlayer, iconURL: string.Empty, useRichTextFormatting: false);
            ChatManager.serverSendMessage($"{inviter.DisplayName} invited you to their party. Use /party accept or /party deny.",
                UnityEngine.Color.cyan, toPlayer: invitee.SteamPlayer, iconURL: string.Empty, useRichTextFormatting: false);
        }

        public static bool Accept(UnturnedPlayer invitee, string inviterNameOrId = null)
        {
            if (!_pending.TryGetValue(invitee.SteamId.m_SteamID, out var list)) return false;
            list.RemoveAll(i => i.Expired);
            if (list.Count == 0) return false;

            PartyInvite chosen;
            if (!string.IsNullOrWhiteSpace(inviterNameOrId))
            {
                var match = Provider.clients.Select(UnturnedPlayer.FromSteamPlayer)
                    .FirstOrDefault(p => p.SteamId.m_SteamID.ToString() == inviterNameOrId ||
                                         p.DisplayName.IndexOf(inviterNameOrId, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match == null) return false;
                chosen = list.LastOrDefault(i => i.Inviter == match.SteamId);
                if (chosen.Inviter.m_SteamID == 0) return false;
            }
            else chosen = list.Last();

            var inviter = UnturnedPlayer.FromCSteamID(chosen.Inviter);
            if (inviter == null) { list.Remove(chosen); return false; }

            // Ensure inviter has a group; create if needed
            var gid = inviter.Player.quests.groupID;
            if (gid == CSteamID.Nil || gid.m_SteamID == 0)
            {
                if (!TryCreateGroupAndAssignOwner(inviter, out gid)) return false;
            }

            // Join invitee to inviter's group
            invitee.Player.quests.ServerAssignToGroup(gid, EPlayerGroupRank.MEMBER);

            ChatManager.serverSendMessage($"You joined {inviter.DisplayName}'s party.",
                UnityEngine.Color.cyan, toPlayer: invitee.SteamPlayer, iconURL: string.Empty, useRichTextFormatting: false);
            ChatManager.serverSendMessage($"{invitee.DisplayName} joined your party.",
                UnityEngine.Color.cyan, toPlayer: inviter.SteamPlayer, iconURL: string.Empty, useRichTextFormatting: false);

            list.Remove(chosen);
            return true;
        }

        public static bool Deny(UnturnedPlayer invitee, string inviterNameOrId = null)
        {
            if (!_pending.TryGetValue(invitee.SteamId.m_SteamID, out var list)) return false;
            list.RemoveAll(i => i.Expired);
            if (list.Count == 0) return false;

            PartyInvite chosen;
            if (!string.IsNullOrWhiteSpace(inviterNameOrId))
            {
                var match = Provider.clients.Select(UnturnedPlayer.FromSteamPlayer)
                    .FirstOrDefault(p => p.SteamId.m_SteamID.ToString() == inviterNameOrId ||
                                         p.DisplayName.IndexOf(inviterNameOrId, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match == null) return false;
                chosen = list.LastOrDefault(i => i.Inviter == match.SteamId);
                if (chosen.Inviter.m_SteamID == 0) return false;
            }
            else chosen = list.Last();

            var inviter = UnturnedPlayer.FromCSteamID(chosen.Inviter);
            if (inviter != null)
                ChatManager.serverSendMessage($"{invitee.DisplayName} denied your party invite.",
                    UnityEngine.Color.cyan, toPlayer: inviter.SteamPlayer, iconURL: string.Empty, useRichTextFormatting: false);

            ChatManager.serverSendMessage($"Invite denied.",
                UnityEngine.Color.cyan, toPlayer: invitee.SteamPlayer, iconURL: string.Empty, useRichTextFormatting: false);

            list.Remove(chosen);
            return true;
        }

        public static bool Leave(UnturnedPlayer player)
        {
            var gid = player.Player.quests.groupID;
            if (gid == CSteamID.Nil || gid.m_SteamID == 0) return false;

            player.Player.quests.ServerAssignToGroup(CSteamID.Nil, EPlayerGroupRank.NONE);
            ChatManager.serverSendMessage($"You left the party.",
                UnityEngine.Color.cyan, toPlayer: player.SteamPlayer, iconURL: string.Empty, useRichTextFormatting: false);
            return true;
        }

        /// <summary>
        /// Creates a new group and makes <paramref name="owner"/> the OWNER.
        /// Uses reflection to support both serverCreateGroup(out CSteamID) and ServerCreateGroup(out CSteamID).
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

                owner.Player.quests.ServerAssignToGroup(newGroup, EPlayerGroupRank.OWNER);
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
        public CmdParty(IServiceProvider sp) : base(sp) { }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Actor is not UnturnedUser uUser)
                throw new UserFriendlyException("Players only.");

            var p = uUser.Player;

            if (Context.Parameters.Length == 0)
                throw new CommandWrongUsageException(this);

            var sub = Context.Parameters[0].ToLowerInvariant();

            if (sub == "accept")
            {
                string who = Context.Parameters.Length >= 2 ? Context.Parameters[1] : null;
                if (!PartyService.Accept(p, who)) throw new UserFriendlyException("No matching invite.");
                return;
            }

            if (sub == "deny")
            {
                string who = Context.Parameters.Length >= 2 ? Context.Parameters[1] : null;
                if (!PartyService.Deny(p, who)) throw new UserFriendlyException("No matching invite.");
                return;
            }

            if (sub == "leave")
            {
                if (!PartyService.Leave(p)) throw new UserFriendlyException("You’re not in a party.");
                return;
            }

            // otherwise treat remaining args as the invite target
            string query = string.Join(" ", Context.Parameters);
            var target = Provider.clients.Select(UnturnedPlayer.FromSteamPlayer)
                           .FirstOrDefault(x => x.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                                             || x.SteamId.m_SteamID.ToString() == query);
            if (target == null || target.SteamId == p.SteamId)
                throw new UserFriendlyException("Player not found.");

            PartyService.SendInvite(p, target, TimeSpan.FromSeconds(60));

            await UniTask.CompletedTask;
        }
    }
}
