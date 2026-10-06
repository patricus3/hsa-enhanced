using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using PegasusShared;
using Blizzard.GameService.SDK.Client.Integration;
using Hearthstone;
using Hearthstone.Core.Streaming;
using Hearthstone.Streaming;
using UnityEngine;

namespace HSAEnhanced
{
    // The friends list (F4), ours on our core, built from the game's friend managers rather than
    // from the rows the list happens to draw (docs/friends-research.md):
    // - you (your BattleTag and best rank; Enter copies the BattleTag);
    // - friend requests (accept, decline);
    // - friends, sorted as the game sorts them, each with its status, rank and Battlegrounds rating;
    //   what the game's menu offers for that player (challenge in each mode, spectate, party,
    //   add, remove, report), with the game's reason when something is not possible;
    //   send a message, and the last messages with them;
    // - recent and nearby players; add a friend (typed BattleTag or email).
    // Open while the game shows its list; the list stays current while open.
    static class Friends
    {
        static FriendsUI s_ui;

        internal static bool Showing
        {
            get
            {
                try { var c = ChatMgr.Get(); return c != null && c.IsFriendListShowing(); } catch { return false; }
            }
        }

        // every frame
        internal static void Tick()
        {
#if !WITHOUT_HSA
            if (!Engine.Enabled) return;
#endif
            if (s_ui == null && Showing)
            {
                s_ui = new FriendsUI();
                Log.Info("friends: ours");
                Focus.Push(s_ui);
                s_ui.Start();
            }
            if (s_ui != null) s_ui.Tick();
        }

        internal static void Closed(FriendsUI ui) { if (s_ui == ui) s_ui = null; }

        internal static void Close()
        {
            try { var bar = BnetBar.Get(); if (bar != null) bar.HideFriendList(); } catch (Exception e) { Log.Error(e); }
        }

        // start of AccessibilityMgr.ShowUI: Hearthstone Access's friends list is not used
#if !WITHOUT_HSA
        internal static bool IsHsaList(object ui) { return Engine.Enabled && ui is FriendListFrame; }
#endif

        internal static string Checked(bool on) { return Speech.S(on ? "ACCESSIBILITY_OPTIONS_MENU_CHECKBOX_CHECKED" : "ACCESSIBILITY_OPTIONS_MENU_CHECKBOX_NOT_CHECKED"); }

        internal static string W(string key, params object[] args) { return Str.Clean(Speech.S(key, args)); }

        internal static string Name(BnetPlayer p)
        {
            try { return Str.Clean(FriendUtils.GetFriendListName(p, false)); } catch { return ""; }
        }

        // as the game's row shows it (FriendListFriendFrame.UpdatePresence)
        internal static string Status(BnetPlayer p, bool recent)
        {
            try
            {
                if (recent && BnetRecentPlayerMgr.Get().IsRecentStranger(p)) return Str.Clean(BnetRecentPlayerMgr.Get().GetRecentReason(p));
                if (!p.IsOnline()) return Str.Clean(FriendUtils.GetLastOnlineElapsedTimeString(p.GetBestLastOnlineMicrosec()));
                var hs = p.GetHearthstoneGameAccount();
                if (hs == null || !hs.IsOnline())
                {
                    var program = p.GetBestProgramId();
                    return program != null ? Str.Clean(BnetUtils.GetNameForProgramId(program)) : W("GLOBAL_PROGRAMNAME_PHOENIX");
                }
                if (p.IsAway()) return Str.Clean(FriendUtils.GetAwayTimeString(p.GetBestAwayTimeMicrosec()));
                if (p.IsBusy()) return W("GLOBAL_FRIENDLIST_BUSYSTATUS");
                return Str.Clean(PresenceMgr.Get().GetStatusText(p));
            }
            catch (Exception e) { Log.Error(e); return ""; }
        }

        // the best ranked medal the player shows, and the Battlegrounds rating
        internal static string Rank(MedalInfoTranslator medals)
        {
            try
            {
                if (medals == null || !medals.IsDisplayable()) return null;
                var format = medals.GetBestCurrentRankFormatType();
                var medal = medals.GetCurrentMedal(format);
                if (medal == null || !medal.IsValid()) return null;
                var name = Str.Clean(medal.GetRankName());
                if (medal.IsLegendRank() && medal.legendIndex > 0) name = Str.Join(name, medal.legendIndex.ToString());
                return Str.Join(Str.Word("GLOBAL_" + format.ToString().Replace("FT_", "")), name);
            }
            catch (Exception e) { Log.Error(e); return null; }
        }

        internal static string Ranks(BnetPlayer p)
        {
            string ranked = null, bg = null;
            try
            {
                var account = p.GetBestGameAccount();
                if (account != null) ranked = Rank(RankMgr.Get().GetRankedMedalFromRankPresenceField(account));
                var hs = p.GetHearthstoneGameAccount();
                int rating; PegasusShared.GameType type;
                if (hs != null && RankMgr.Get().GetBattlegroundsMedalFromRankPresenceField(hs, out rating, out type) && rating > 0)
                    bg = Str.Join(W("GLOBAL_BATTLEGROUNDS"), rating.ToString());
            }
            catch (Exception e) { Log.Error(e); }
            return Str.Join(ranked, bg);
        }

        internal static string Line(BnetPlayer p, bool recent)
        {
            return Str.Join(Name(p), Status(p, recent), p.IsOnline() ? Ranks(p) : null);
        }
    }

    class FriendsUI : Core.Screen
    {
        Menu m_menu;
        Func<Menu> m_view;
        float m_nextRefresh;

        internal override bool Alive
        {
            get
            {
                bool alive = Friends.Showing;
                if (!alive) Friends.Closed(this);
                return alive;
            }
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }

        internal void Start() { Show(Main, 0, true); }

        static string W(string key, params object[] args) { return Friends.W(key, args); }

        void Show(Func<Menu> view, int at, bool read)
        {
            m_view = view;
            Menu menu;
            try { menu = view(); }
            catch (Exception e) { Log.Error(e); menu = Main(); m_view = Main; }
            menu.Index = at;
            m_menu = menu;
            if (read) m_menu.StartReading();
        }

        // the lists change while open (friends come online, requests arrive): built again in place,
        // on the same player, without reading anything
        internal void Tick()
        {
            if (Time.unscaledTime < m_nextRefresh || m_view == null || m_menu == null) return;
            m_nextRefresh = Time.unscaledTime + 2f;
            try
            {
                var key = m_menu.KeyAt(m_menu.Index);
                var at = m_menu.Index;
                var menu = m_view();
                var k = menu.IndexOfKey(key);
                menu.Index = k >= 0 ? k : at;
                if (k >= 0) menu.Line = m_menu.Line;
                m_menu = menu;
            }
            catch (Exception e) { Log.Error(e); }
        }

        void Info(Menu menu, string text)
        {
            text = Str.Clean(text);
            if (text.Length > 0) menu.AddOption(text, () => Say(text));
        }

        // an option the game shows but does not allow now: Enter says why
        void Option(Menu menu, string label, bool enabled, Func<string> why, Action click, object key = null)
        {
            if (enabled) { menu.AddOption(label, click, key ?? label); return; }
            menu.AddOption(Str.Join(label, Str.Unavailable), () =>
            {
                string reason = null;
                try { reason = why == null ? null : why(); } catch (Exception e) { Log.Error(e); }
                Say(string.IsNullOrEmpty(reason) ? Str.Unavailable : reason);
            }, key ?? label);
        }

        #region Main
        Menu Main()
        {
            var menu = new Menu(this, Speech.S("ACCESSIBILITY_UI_SOCIAL_MENU_NAME"), Friends.Close);
            var challenges = FriendChallengeMgr.Get();
            if (challenges != null && challenges.DidSendChallenge())
                menu.AddOption(Str.Join(W("GLOBAL_FRIEND_CHALLENGE_HEADER"), W("GLOBAL_CANCEL")), () => { Log.Info("friends: challenge cancelled"); challenges.CancelChallenge(); }, "challenge");

            menu.AddOption(MeLine(), CopyBattleTag, "me");

            var invites = BnetFriendMgr.Get().GetReceivedInvites();
            if (invites != null && invites.Count > 0)
                menu.AddOption(W("GLOBAL_FRIENDLIST_REQUESTS_HEADER", invites.Count), () => Show(Requests, 0, true), "requests");

            var friends = BnetFriendMgr.Get().GetFriends();
            int online = 0;
            foreach (var f in friends) if (f.IsOnline()) online++;
            var header = online == friends.Count ? W("GLOBAL_FRIENDLIST_FRIENDS_HEADER_ALL_ONLINE", online) : W("GLOBAL_FRIENDLIST_FRIENDS_HEADER", online, friends.Count);
            menu.AddOption(header, () => Show(FriendList, 0, true), "friends");

            var recent = Recent();
            if (recent.Count > 0) menu.AddOption(W("GLOBAL_FRIENDLIST_RECENT_PLAYERS_HEADER", recent.Count), () => Show(RecentList, 0, true), "recent");
            var nearby = Nearby();
            if (nearby.Count > 0) menu.AddOption(W("GLOBAL_FRIENDLIST_NEARBY_PLAYERS_HEADER", nearby.Count), () => Show(NearbyList, 0, true), "nearby");

            menu.AddOption(W("GLUE_FRIEND_LIST_ADD_FRIENDS"), AddFriend, "add");
            return menu;
        }

        string MeLine()
        {
            var me = BnetPresenceMgr.Get().GetMyPlayer();
            if (me == null) return W("GLOBAL_FRIENDLIST_MYSTATUS");
            string rank = null;
            try { rank = Friends.Rank(RankMgr.Get().GetLocalPlayerMedalInfo()); } catch { }
            var tag = me.GetBattleTag();
            return Str.Join(tag == null ? Friends.Name(me) : Str.Clean(tag.GetString()), rank);
        }

        void CopyBattleTag()
        {
            var me = BnetPresenceMgr.Get().GetMyPlayer();
            var tag = me == null ? null : me.GetBattleTag();
            if (tag == null) return;
            GUIUtility.systemCopyBuffer = tag.GetString();
            Say(Speech.S("ACCESSIBILITY_UI_SOCIAL_BATTLETAG_COPIED_TO_CLIPBOARD"));
        }

        static List<BnetPlayer> Recent()
        {
            var list = new List<BnetPlayer>();
            try { var r = BnetRecentPlayerMgr.Get().GetRecentPlayers(); if (r != null) list.AddRange(r); } catch { }
            list.Sort(FriendUtils.RecentFriendSortCompare);
            return list;
        }

        static List<BnetPlayer> Nearby()
        {
            var list = new List<BnetPlayer>();
            try
            {
                var mgr = BnetNearbyPlayerMgr.Get();
                if (mgr != null && mgr.IsEnabled()) { var n = mgr.GetNearbyPlayers(); if (n != null) list.AddRange(n); }
            }
            catch { }
            list.Sort(FriendUtils.FriendSortCompare);
            return list;
        }

        void AddFriend()
        {
            var instruction = Speech.S(Localization.GetLocale() == Locale.zhCN ? "GLOBAL_ADDFRIEND_INSTRUCTION_CN" : "GLOBAL_ADDFRIEND_INSTRUCTION");
            TextInput.Ask(Str.Clean(instruction), text =>
            {
                text = text.Trim();
                if (text.Length == 0) { Read(); return; }
                bool sent = false;
                try { sent = BnetFriendMgr.Get().SendInvite(text); } catch (Exception e) { Log.Error(e); }
                Log.Info("friends: request sent " + sent);
                Say(sent ? W("GLOBAL_ADDFRIEND_SENT_CONFIRMATION") : W(Localization.GetLocale() == Locale.zhCN ? "GLOBAL_ADDFRIEND_ERROR_MALFORMED_CN" : "GLOBAL_ADDFRIEND_ERROR_MALFORMED"));
            }, Read);
        }
        #endregion

        #region Lists
        Menu PlayerList(string name, List<BnetPlayer> players, bool recent, Func<Menu> self)
        {
            // Left/Right between the players (their names), Down: status, then rank
            var menu = new Menu(this, name, () => Show(Main, 0, true)) { Horizontal = true };
            foreach (var p in players)
            {
                var player = p;
                menu.AddOption(Friends.Name(player), () => Show(() => Player(player, recent, self), 0, true), player,
                    () => new List<string> { Friends.Status(player, recent), player.IsOnline() ? Friends.Ranks(player) : null });
            }
            return menu;
        }

        Menu FriendList()
        {
            var friends = new List<BnetPlayer>(BnetFriendMgr.Get().GetFriends());
            friends.Sort(FriendUtils.FriendSortCompare);
            return PlayerList(FriendsHeader(friends), friends, false, FriendList);
        }

        static string FriendsHeader(List<BnetPlayer> friends)
        {
            int online = 0;
            foreach (var f in friends) if (f.IsOnline()) online++;
            return online == friends.Count ? W("GLOBAL_FRIENDLIST_FRIENDS_HEADER_ALL_ONLINE", online) : W("GLOBAL_FRIENDLIST_FRIENDS_HEADER", online, friends.Count);
        }

        Menu RecentList()
        {
            var list = Recent();
            return PlayerList(W("GLOBAL_FRIENDLIST_RECENT_PLAYERS_HEADER", list.Count), list, true, RecentList);
        }

        Menu NearbyList()
        {
            var list = Nearby();
            return PlayerList(W("GLOBAL_FRIENDLIST_NEARBY_PLAYERS_HEADER", list.Count), list, false, NearbyList);
        }

        Menu Requests()
        {
            var invites = BnetFriendMgr.Get().GetReceivedInvites() ?? new List<BnetInvitation>();
            var menu = new Menu(this, W("GLOBAL_FRIENDLIST_REQUESTS_HEADER", invites.Count), () => Show(Main, 0, true));
            foreach (var i in invites)
            {
                var invite = i;
                var when = W("GLOBAL_FRIENDLIST_REQUEST_SENT_TIME", FriendUtils.GetRequestElapsedTimeString((long)invite.GetCreationTimeS()));
                menu.AddOption(Str.Join(Str.Clean(invite.GetTargetName()), when), () => Show(() => Request(invite), 0, true), invite.GetId());
            }
            return menu;
        }

        Menu Request(BnetInvitation invite)
        {
            var menu = new Menu(this, Str.Clean(invite.GetTargetName()), () => Show(Requests, 0, true));
            menu.AddOption(W("GLOBAL_FRIEND_CHALLENGE_ACCEPT"), () =>
            {
                Log.Info("friends: request accepted");
                BnetFriendMgr.Get().AcceptInvite(invite);
                Show(Main, 0, true);
            });
            menu.AddOption(W("GLOBAL_FRIEND_CHALLENGE_DECLINE"), () =>
            {
                Log.Info("friends: request declined");
                BnetFriendMgr.Get().IgnoreInvite(invite.GetId());
                Show(Main, 0, true);
            });
            return menu;
        }
        #endregion

        #region A player
        // what the game's menu for the player offers (FriendListFlyoutMenu), in its order
        Menu Player(BnetPlayer p, bool recent, Func<Menu> list)
        {
            Action back = () => Show(list, 0, true);
            var menu = new Menu(this, Friends.Line(p, recent), back);
            bool friend = BnetFriendMgr.Get().IsFriend(p);
            var hsId = p.GetHearthstoneGameAccountId();
            var spectators = SpectatorManager.Get();
            var party = PartyManager.Get();
            var challenges = FriendChallengeMgr.Get();
            bool canChallenge = challenges.CanShowFriendlyChallenge(p);

            if (canChallenge)
            {
                bool extended = ShouldSeeHearthstoneChallengePopup();
                var label = W(extended ? "GLOBAL_FRIENDLIST_CHALLENGE_MENU_HEARTHSTONE_EXTENDED_MENU_BUTTON" : "GLOBAL_FRIENDLIST_CHALLENGE_MENU_HEARTHSTONE_BUTTON");
                bool enabled = challenges.IsHearthstoneFriendlyChallengeAvailable(p) && (extended || CollectionManager.Get().AccountHasValidDeck(FormatType.FT_STANDARD));
                Option(menu, label, enabled, () => HearthstoneWhyNot(p, extended), () =>
                {
                    if (extended) Show(() => HearthstoneFormats(p, recent, list), 0, true);
                    else ChallengeFormat(p, FormatType.FT_STANDARD);
                });
                Option(menu, W("GLOBAL_BATTLEGROUNDS"), challenges.IsBattlegroundsFriendlyChallengeAvailable(p, false), () => BattlegroundsWhyNot(p), () => InviteToBattlegrounds(p));
                Option(menu, W("GLOBAL_MERCENARIES"), challenges.IsMercenariesFriendlyChallengeAvailable(p), () => MercenariesWhyNot(p), () =>
                {
                    Log.Info("friends: mercenaries challenge");
                    party.StartMercenariesFriendlyChallengeEntry(p);
                    Friends.Close();
                });
            }
            if (spectators.CanSpectate(p))
                menu.AddOption(W("GLOBAL_SPECTATE"), () => { Log.Info("friends: spectate"); spectators.SpectatePlayer(p); });
            if (spectators.CanInviteToSpectateMyGame(hsId) || spectators.IsInvitedToSpectateMyGame(hsId))
                menu.AddOption(W("GLOBAL_FRIENDLIST_SPECTATE_TOOLTIP_INVITE_HEADER"), () => { Log.Info("friends: invite to spectate"); spectators.InviteToSpectateMe(p); });
            if (spectators.IsSpectatingMe(hsId))
                menu.AddOption(W("GLOBAL_FRIENDLIST_SPECTATE_MENU_KICK"), () => KickSpectator(p));
            if (spectators.IsSpectatingPlayer(hsId))
                menu.AddOption(W("GLOBAL_FRIENDLIST_SPECTATE_MENU_STOP"), StopSpectating);

            bool inParty = party.IsInBattlegroundsParty() && !SceneMgr.Get().IsInGame() && !GameMgr.Get().IsFindingGame();
            if (inParty && !party.IsPlayerInCurrentPartyOrPending(hsId))
                Option(menu, Speech.S("ACCESSIBILITY_UI_SOCIAL_INVITE_FRIEND_TO_BG"),
                    challenges.IsBattlegroundsFriendlyChallengeAvailable(p) && party.CanInvite(p.GetBestGameAccountId()),
                    () => InviteToPartyWhyNot(p), () => InviteToParty(p));
            if (inParty && party.CanKick(p.GetBestGameAccountId()))
                menu.AddOption(W("GLOBAL_FRIENDLIST_BATTLEGROUNDS_TOOLTIP_KICK_HEADER"), () => { Log.Info("friends: kicked from party"); party.KickPlayerFromParty(p.GetBestGameAccountId()); });

            if (!friend)
                Option(menu, W("GLOBAL_ADDFRIEND_BUTTON"), !BnetRecentPlayerMgr.Get().IsCurrentOpponent(p), null, () =>
                {
                    var tag = p.GetBattleTag();
                    bool sent = tag != null && BnetFriendMgr.Get().SendInvite(tag.GetString());
                    Log.Info("friends: request sent " + sent);
                    Say(sent ? W("GLOBAL_ADDFRIEND_SENT_CONFIRMATION") : W("GLOBAL_ADDFRIEND_ERROR_MALFORMED"));
                });

            if (friend)
            {
                menu.AddOption(Speech.S("ACCESSIBILITY_UI_SOCIAL_CHAT_SEND_MESSAGE"), () => SendMessage(p));
                foreach (var line in Messages(p)) Info(menu, line);
                menu.AddOption(W("GLOBAL_FRIENDLIST_CHALLENGE_MENU_REMOVE_BUTTON"), () =>
                {
                    var frame = ChatMgr.Get().FriendListFrame;
                    if (frame != null) frame.ShowRemoveFriendPopup(p);
                });
            }
            Option(menu, W("GLOBAL_FRIENDLIST_CHALLENGE_MENU_REPORT_BUTTON"), ReportEnabled(), () => W("GLOBAL_FRIENDLIST_REPORT_TOOLTIP_CURRENTLY_UNAVAILABLE_TEXT"),
                () => Show(() => ReportReasons(p, recent, list), 0, true));
            menu.AddOption(Str.Back, back);
            return menu;
        }

        static bool ReportEnabled()
        {
            var features = NetCache.Get().GetNetObject<NetCache.NetCacheFeatures>();
            return features != null && features.ReportPlayerEnabled;
        }

        static bool ShouldSeeHearthstoneChallengePopup()
        {
            return CollectionManager.Get().ShouldAccountSeeStandardWild() || TavernBrawlManager.Get().HasUnlockedTavernBrawl(BrawlType.BRAWL_TYPE_TAVERN_BRAWL);
        }

        // the game's own wording of why (its button tooltips), with the player's name
        static string Reason(string key, BnetPlayer p) { return W(key, p.GetBestName()); }

        static string Availability(BnetPlayer p)
        {
            string reason;
            return FriendListFlyoutMenu.GetAvailability(p, out reason) ? null : reason;
        }

        static string HearthstoneGeneric(BnetPlayer p)
        {
            var features = NetCache.Get().GetNetObject<NetCache.NetCacheFeatures>();
            if (features != null && !features.Games.Friendly) return "GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_MODE_UNAVAILABLE";
            if (!GameUtils.IsTraditionalTutorialComplete()) return "GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_TRADITIONAL_LOCKED";
            var hs = p.GetHearthstoneGameAccount();
            if (hs != null && hs.GetTutorialBeaten() < 1) return "GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGEE_NO_HEARTHSTONE_TUTORIAL_COMPLETE";
            return Availability(p);
        }

        static string HearthstoneWhyNot(BnetPlayer p, bool extended)
        {
            var key = HearthstoneGeneric(p);
            if (key == null && !extended && !CollectionManager.Get().AccountHasValidDeck(FormatType.FT_STANDARD)) key = "GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGER_NO_STANDARD_DECK";
            return key == null ? null : Reason(key, p);
        }

        static string BattlegroundsWhyNot(BnetPlayer p)
        {
            var features = NetCache.Get().GetNetObject<NetCache.NetCacheFeatures>();
            string key;
            if (features != null && !features.Games.BattlegroundsFriendlyChallenge) key = "GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_MODE_UNAVAILABLE";
            else if (!GameUtils.IsBattleGroundsTutorialComplete()) key = "GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_BATTLEGROUNDS_LOCKED";
            else if (!FriendChallengeMgr.Get().AllowBGInviteWhileInNPPGEnabled() && p.GetHearthstoneGameAccount() != null && !p.GetHearthstoneGameAccount().GetBattlegroundsTutorialComplete())
                key = "GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGEE_NO_BATTLEGROUNDS_TUTORIAL_COMPLETE";
            else key = Availability(p);
            return key == null ? null : Reason(key, p);
        }

        static string MercenariesWhyNot(BnetPlayer p)
        {
            var features = NetCache.Get().GetNetObject<NetCache.NetCacheFeatures>();
            string key;
            if (features != null && !features.Games.BattlegroundsFriendlyChallenge) key = "GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_MODE_UNAVAILABLE";
            else if (!GameUtils.IsMercenariesVillageTutorialComplete()) key = "GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_MERCS_LOCKED";
            else if (p.GetHearthstoneGameAccount() != null && !p.GetHearthstoneGameAccount().GetMercenariesTutorialComplete()) key = "GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGEE_NO_MERCS_TUTORIAL_COMPLETE";
            else key = Availability(p);
            return key == null ? null : Reason(key, p);
        }

        static string InviteToPartyWhyNot(BnetPlayer p)
        {
            var party = PartyManager.Get();
            var id = p.GetBestGameAccountId();
            string key;
            if (party.IsPlayerPendingInCurrentParty(id)) key = "GLOBAL_FRIENDLIST_BATTLEGROUNDS_TOOLTIP_INVITE_ALREADY_SENT_BODY";
            else if (party.GetCurrentPartySize() >= party.GetMaxPartySizeByPartyType(PartyType.BATTLEGROUNDS_PARTY)) key = "GLOBAL_FRIENDLIST_BATTLEGROUNDS_TOOLTIP_INVITE_FULL_PARTY_BODY";
            else if (!FriendChallengeMgr.Get().AllowBGInviteWhileInNPPGEnabled() && p.GetHearthstoneGameAccount() != null && !p.GetHearthstoneGameAccount().GetBattlegroundsTutorialComplete())
                key = "GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGEE_NO_BATTLEGROUNDS_TUTORIAL_COMPLETE";
            else if (!FriendChallengeMgr.Get().IsOpponentAvailable(p)) key = "GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGEE_USER_IS_BUSY";
            else return null;
            return Reason(key, p);
        }
        #endregion

        #region Challenges
        // Standard, Wild, Tavern Brawl (the game's Hearthstone... popup)
        Menu HearthstoneFormats(BnetPlayer p, bool recent, Func<Menu> list)
        {
            var menu = new Menu(this, W("GLOBAL_FRIENDLIST_CHALLENGE_MENU_HEADER_NONFRIEND", Friends.Name(p)), () => Show(() => Player(p, recent, list), 0, true));
            var challenges = FriendChallengeMgr.Get();
            var collection = CollectionManager.Get();
            var brawls = TavernBrawlManager.Get();
            bool available = challenges.IsHearthstoneFriendlyChallengeAvailable(p);
            Func<string> generic = () => { var k = HearthstoneGeneric(p); return k == null ? null : Reason(k, p); };

            Option(menu, W("GLOBAL_FRIENDLIST_CHALLENGE_MENU_STANDARD_DUEL_BUTTON"), available && collection.AccountHasValidDeck(FormatType.FT_STANDARD),
                () => generic() ?? Reason("GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGER_NO_STANDARD_DECK", p), () => ChallengeFormat(p, FormatType.FT_STANDARD));
            if (collection.ShouldAccountSeeStandardWild())
                Option(menu, W("GLOBAL_FRIENDLIST_CHALLENGE_MENU_WILD_DUEL_BUTTON"), available && collection.AccountHasValidDeck(FormatType.FT_WILD),
                    () => generic() ?? Reason("GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGER_NO_DECK", p), () => ChallengeFormat(p, FormatType.FT_WILD));

            bool brawl = available && brawls.IsTavernBrawlActive(BrawlType.BRAWL_TYPE_TAVERN_BRAWL) && brawls.CanChallengeToTavernBrawl(BrawlType.BRAWL_TYPE_TAVERN_BRAWL);
            if (brawl && brawls.GetMission(BrawlType.BRAWL_TYPE_TAVERN_BRAWL).canCreateDeck) brawl = brawls.HasValidDeck(BrawlType.BRAWL_TYPE_TAVERN_BRAWL);
            Option(menu, W("GLOBAL_TAVERN_BRAWL"), brawl, () => generic() ?? BrawlWhyNot(p), () => ChallengeBrawl(p));
            menu.AddOption(Str.Back, () => Show(() => Player(p, recent, list), 0, true));
            return menu;
        }

        static string BrawlWhyNot(BnetPlayer p)
        {
            var brawls = TavernBrawlManager.Get();
            string key = null;
            if (!brawls.HasUnlockedTavernBrawl(BrawlType.BRAWL_TYPE_TAVERN_BRAWL)) key = "GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGER_TAVERN_BRAWL_LOCKED";
            else if (!brawls.IsTavernBrawlActive(BrawlType.BRAWL_TYPE_TAVERN_BRAWL)) key = "GLOBAL_FRIENDLIST_CHALLENGE_TOOLTIP_NO_TAVERN_BRAWL";
            else if (!brawls.CanChallengeToTavernBrawl(BrawlType.BRAWL_TYPE_TAVERN_BRAWL)) key = "GLOBAL_FRIENDLIST_CHALLENGE_TOOLTIP_TAVERN_BRAWL_NOT_CHALLENGEABLE";
            else if (brawls.GetMission(BrawlType.BRAWL_TYPE_TAVERN_BRAWL).canCreateDeck && !brawls.HasValidDeck(BrawlType.BRAWL_TYPE_TAVERN_BRAWL)) key = "GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGER_NO_TAVERN_BRAWL_DECK";
            return key == null ? null : Reason(key, p);
        }

        void ChallengeFormat(BnetPlayer p, FormatType format)
        {
            if (!FriendChallengeMgr.Get().IsOpponentAvailable(p)) { Say(Reason("GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_THEYRE_UNAVAILABLE", p)); return; }
            Log.Info("friends: challenge " + format);
            FriendChallengeMgr.Get().SetChallengeMethod(FriendChallengeMgr.ChallengeMethod.FROM_FRIEND_LIST);
            FriendChallengeMgr.Get().SendChallenge(p, format, true);
            Friends.Close();
        }

        void ChallengeBrawl(BnetPlayer p)
        {
            if (!FriendChallengeMgr.Get().IsOpponentAvailable(p)) { Say(Reason("GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_THEYRE_UNAVAILABLE", p)); return; }
            var brawls = TavernBrawlManager.Get();
            Log.Info("friends: tavern brawl challenge");
            brawls.CurrentBrawlType = BrawlType.BRAWL_TYPE_TAVERN_BRAWL;
            var mission = brawls.CurrentMission();
            FriendChallengeMgr.Get().SendTavernBrawlChallenge(p, BrawlType.BRAWL_TYPE_TAVERN_BRAWL, mission.seasonId, mission.SelectedBrawlLibraryItemId);
            Friends.Close();
        }

        void InviteToBattlegrounds(BnetPlayer p)
        {
            var downloads = GameDownloadManagerProvider.Get();
            if (downloads == null || !downloads.IsModuleReadyToPlay(DownloadTags.Content.Bgs))
            {
                Log.Info("friends: battlegrounds needs downloading");
                Box.Get().HandleBattleGroundDownloadRequired("GLUE_BACON_INVITE_NEW_PLAYER_DOWNLOAD");
                return;
            }
            Log.Info("friends: battlegrounds invite");
            GameMgr.Get().SetPendingAutoConcede(true);
            if (CollectionManager.Get().IsInEditMode())
            {
                var deck = CollectionManager.Get().GetEditedDeck();
                if (deck != null) deck.SendChanges(CollectionDeck.ChangeSource.NavigateToSceneForPartyChallenge);
            }
            SceneMgr.Get().SetNextMode(SceneMgr.Mode.BACON);
            PartyManager.Get().SendInvite(PartyType.BATTLEGROUNDS_PARTY, p.GetBestGameAccountId());
        }

        void InviteToParty(BnetPlayer p)
        {
            var party = PartyManager.Get();
            Log.Info("friends: party invite");
            if (!party.IsPartyLeader()) { party.SendInviteSuggestion(PartyType.BATTLEGROUNDS_PARTY, p.GetBestGameAccountId()); return; }
            if (party.GetCurrentPartySize() >= party.GetBattlegroundsMaxRankedPartySize() && !party.IsInPrivateBattlegroundsParty())
            {
                var info = new AlertPopup.PopupInfo
                {
                    m_headerText = GameStrings.Get("GLUE_BACON_PRIVATE_PARTY_TITLE"),
                    m_text = GameStrings.Format("GLUE_BACON_PRIVATE_PARTY_WARNING", party.GetBattlegroundsMaxRankedPartySize()),
                    m_iconSet = AlertPopup.PopupInfo.IconSet.Default,
                    m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                    m_confirmText = GameStrings.Get("GLUE_COLLECTION_DECK_COMPLETE_POPUP_CONFIRM"),
                    m_cancelText = GameStrings.Get("GLUE_COLLECTION_DECK_COMPLETE_POPUP_CANCEL"),
                    m_responseCallback = (response, data) =>
                    {
                        if (response != AlertPopup.Response.CONFIRM) return;
                        PartyManager.Get().SetBattlegroundsPrivateParty(true);
                        PartyManager.Get().SendInvite(PartyType.BATTLEGROUNDS_PARTY, p.GetBestGameAccountId());
                    },
                };
                DialogManager.Get().ShowPopup(info);
                return;
            }
            party.SendInvite(PartyType.BATTLEGROUNDS_PARTY, p.GetBestGameAccountId());
        }

        void KickSpectator(BnetPlayer p)
        {
            var id = p.GetHearthstoneGameAccountId();
            if (!SpectatorManager.Get().IsSpectatingMe(id)) return;
            var info = new AlertPopup.PopupInfo
            {
                m_headerText = GameStrings.Get("GLOBAL_SPECTATOR_KICK_PROMPT_HEADER"),
                m_text = GameStrings.Format("GLOBAL_SPECTATOR_KICK_PROMPT_TEXT", FriendUtils.GetUniqueName(p)),
                m_showAlertIcon = true,
                m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_responseCallback = (response, data) =>
                {
                    if (response == AlertPopup.Response.CONFIRM) { Log.Info("friends: spectator kicked"); SpectatorManager.Get().KickSpectator(p, true); }
                },
            };
            DialogManager.Get().ShowPopup(info);
        }

        void StopSpectating()
        {
            if (GameMgr.Get().IsFindingGame() || SceneMgr.Get().IsTransitioning() || GameMgr.Get().IsTransitionPopupShown()) return;
            var info = new AlertPopup.PopupInfo
            {
                m_headerText = GameStrings.Get("GLOBAL_SPECTATOR_LEAVE_PROMPT_HEADER"),
                m_text = GameStrings.Get("GLOBAL_SPECTATOR_LEAVE_PROMPT_TEXT"),
                m_showAlertIcon = true,
                m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_responseCallback = (response, data) =>
                {
                    if (response != AlertPopup.Response.CONFIRM) return;
                    Log.Info("friends: stopped spectating");
                    SpectatorManager.Get().LeaveSpectatorMode();
                    Friends.Close();
                },
            };
            DialogManager.Get().ShowPopup(info);
        }
        #endregion

        #region Messages
        void SendMessage(BnetPlayer p)
        {
            TextInput.Ask(Speech.S("ACCESSIBILITY_UI_SOCIAL_CHAT_TYPE_MESSAGE_PROMPT"), text =>
            {
                text = text.Trim();
                if (text.Length == 0) { Read(); return; }
                bool sent = false;
                try { sent = BnetWhisperMgr.Get().SendWhisper(p, text); } catch (Exception e) { Log.Error(e); }
                Log.Info("friends: message sent " + sent);
                Say(sent ? Speech.S("ACCESSIBILITY_UI_SOCIAL_CHAT_MESSAGE_SENT", Friends.Name(p), text) : W("GLOBAL_CHAT_RECEIVER_OFFLINE", Friends.Name(p)));
            }, Read);
        }

        // the last messages with the player, oldest first
        static List<string> Messages(BnetPlayer p)
        {
            var lines = new List<string>();
            try
            {
                var whispers = BnetWhisperMgr.Get().GetWhispersWithPlayer(p);
                if (whispers == null) return lines;
                for (int i = Math.Max(0, whispers.Count - 10); i < whispers.Count; i++)
                {
                    var w = whispers[i];
                    var text = Str.Clean(ChatUtils.GetMessage(w));
                    if (text.Length == 0) continue;
                    lines.Add(WhisperUtil.IsSpeaker(p, w)
                        ? Speech.S("ACCESSIBILITY_UI_SOCIAL_CHAT_MESSAGE_RECEIVED", Friends.Name(p), text)
                        : Speech.S("ACCESSIBILITY_UI_SOCIAL_CHAT_MESSAGE_SENT", Friends.Name(p), text));
                }
            }
            catch (Exception e) { Log.Error(e); }
            return lines;
        }
        #endregion

        #region Report
        static readonly KeyValuePair<ReportType.ComplaintType, string>[] Reasons =
        {
            new KeyValuePair<ReportType.ComplaintType, string>(ReportType.ComplaintType.INAPPROPRIATE_NAME, "GLOBAL_REPORT_REASON_INAPPROPRIATE_NAME"),
            new KeyValuePair<ReportType.ComplaintType, string>(ReportType.ComplaintType.INAPPROPRIATE_COMMUNICATION, "GLOBAL_REPORT_REASON_INAPPROPRIATE_CHAT"),
            new KeyValuePair<ReportType.ComplaintType, string>(ReportType.ComplaintType.CHEATING, "GLOBAL_REPORT_REASON_CHEATING"),
        };

        static List<KeyValuePair<ReportType.SubcomplaintType, string>> Details(ReportType.ComplaintType reason)
        {
            var list = new List<KeyValuePair<ReportType.SubcomplaintType, string>>();
            switch (reason)
            {
                case ReportType.ComplaintType.INAPPROPRIATE_NAME:
                    list.Add(new KeyValuePair<ReportType.SubcomplaintType, string>(ReportType.SubcomplaintType.BATTLETAG, "GLOBAL_REPORT_DETAIL_BATTLETAG"));
                    break;
                case ReportType.ComplaintType.INAPPROPRIATE_COMMUNICATION:
                    list.Add(new KeyValuePair<ReportType.SubcomplaintType, string>(ReportType.SubcomplaintType.TEXT_CHAT, "GLOBAL_REPORT_DETAIL_HARASSMENT"));
                    list.Add(new KeyValuePair<ReportType.SubcomplaintType, string>(ReportType.SubcomplaintType.SPAM, "GLOBAL_REPORT_DETAIL_SPAM"));
                    list.Add(new KeyValuePair<ReportType.SubcomplaintType, string>(ReportType.SubcomplaintType.CHAT_ADVERTISEMENT, "GLOBAL_REPORT_DETAIL_ADVERTISEMENT"));
                    break;
                case ReportType.ComplaintType.CHEATING:
                    list.Add(new KeyValuePair<ReportType.SubcomplaintType, string>(ReportType.SubcomplaintType.HACKING, "GLOBAL_REPORT_DETAIL_HACKING"));
                    list.Add(new KeyValuePair<ReportType.SubcomplaintType, string>(ReportType.SubcomplaintType.BOTTING, "GLOBAL_REPORT_DETAIL_BOTTING"));
                    list.Add(new KeyValuePair<ReportType.SubcomplaintType, string>(ReportType.SubcomplaintType.BOOSTING_DERANKING, "GLOBAL_REPORT_DETAIL_INTENTIONALLY_LOSING_DERANKING"));
                    break;
            }
            return list;
        }

        static string ReportTitle(BnetPlayer p)
        {
            var tag = p.GetBattleTag();
            var name = tag == null ? Friends.Name(p) : tag.GetString();
            return W(BnetFriendMgr.Get().IsFriend(p) ? "GLOBAL_FRIENDLIST_REPORT_SELECT_HEADER_FRIEND" : "GLOBAL_FRIENDLIST_REPORT_SELECT_HEADER_NONFRIEND", name);
        }

        Menu ReportReasons(BnetPlayer p, bool recent, Func<Menu> list)
        {
            Action back = () => Show(() => Player(p, recent, list), 0, true);
            var menu = new Menu(this, Str.Join(ReportTitle(p), W("GLOBAL_FRIENDLIST_REPORT_SELECT_REASON")), back);
            foreach (var r in Reasons)
            {
                var reason = r.Key;
                var chosen = new HashSet<ReportType.SubcomplaintType>();
                menu.AddOption(W(r.Value), () => Show(() => ReportDetails(p, reason, chosen, back), 0, true));
            }
            menu.AddOption(Str.Back, back);
            return menu;
        }

        Menu ReportDetails(BnetPlayer p, ReportType.ComplaintType reason, HashSet<ReportType.SubcomplaintType> chosen, Action back)
        {
            var menu = new Menu(this, W("GLOBAL_FRIENDLIST_REPORT_SELECT_OPTIONS"), back);
            foreach (var d in Details(reason))
            {
                var detail = d.Key;
                var label = W(d.Value);
                menu.AddOption(() => Str.Join(label, Friends.Checked(chosen.Contains(detail))), () =>
                {
                    if (!chosen.Remove(detail)) chosen.Add(detail);
                    Say(Friends.Checked(chosen.Contains(detail)));
                });
            }
            menu.AddOption(W("GLOBAL_BUTTON_SUBMIT"), () =>
            {
                if (chosen.Count == 0) { Say(Str.Unavailable); return; }
                Log.Info("friends: report " + reason);
                BattleNet.Get().SubmitReport(p.GetAccountId(), reason, new List<ReportType.SubcomplaintType>(chosen));
                Say(W("GLOBAL_FRIENDLIST_REPORT_SELECT_COMPLETE"));
                back();
            });
            menu.AddOption(Str.Back, back);
            return menu;
        }
        #endregion
    }
}
