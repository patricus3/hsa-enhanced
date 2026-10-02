# Friends list (social menu): research for a rebuild

Source: the current vanilla `Assembly-CSharp.dll`, decompiled. The HSA behaviour comes from `hsa.diff.patch`. HSA has no license, so this file describes what it does and copies none of its code. Decompiled files are in `scratchpad/dec/social/`.

## 1. Opening, closing and detection

- **Button:** `BnetBar.m_friendButton` (a `BnetBarFriendButton`, singleton `BnetBarFriendButton.Get()`). On release it runs `BnetBar.ToggleFriendListShowing()` (private), which calls either `ShowFriendList()` (private) or `HideFriendList()` (public).
  - `ShowFriendList()` runs `ChatMgr.Get().ShowFriendsList()` and clears the pending-invites icon.
  - `HideFriendList()` runs `ChatMgr.Get().CloseChatUI()`, which hides the chat log and then calls `CloseFriendsList()`. That **destroys** the `FriendListFrame`.
- **`ChatMgr.ShowFriendsList()`** creates the frame on first use (`CreateFriendsListUI`), activates it and the close-catcher, calls `UpdateFriendItems()` and then fires `ChatMgr.OnFriendListToggled(true)`. It does nothing while a set-rotation or player-migration check is pending.
- **Other ways it closes:** `FriendListFriendFrame.CloseFriendsListMenu()`, which is called after sending a challenge. Also `FriendListFrame.OnScenePreUnload`, a click on the close-catcher, and `RAFManager`.
- **Detection:**
  - `ChatMgr.Get().IsFriendListShowing()` (frame is non-null and `activeSelf`).
  - `ChatMgr.Get().FriendListFrame`, which is null after closing.
  - The `ChatMgr.OnFriendListToggled(bool)` event.
  - `FriendListFrame.IsStarted` and `OnStarted`, because items are built in `Start()`.
  - `ChatMgr.IsChatLogUIShowing()` for the chat window.
- **Opening it ourselves:** prefer `BnetBarFriendButton.Get().TriggerRelease()`. It keeps the BnetBar layout and pending-icon logic. Only do this while the button is enabled: `BnetBar.UpdateButtonEnableState` disables it during dialogs. To close, call `BnetBar.Get().HideFriendList()`.
- **HSA:** F4 (`AccessibleKey.OPEN_SOCIAL_MENU`, a global key) calls `BnetBarFriendButton.TriggerRelease()`, but not while an options menu is shown. Escape (the game-menu key) calls `bnetBar.HideFriendList()` when the list is open. The hub menu's "Social" item only says "press F4".

## 2. Game data

### BnetFriendMgr (`BnetFriendMgr.Get()`)

- **Friends:**
  - `GetFriends()` returns `List<BnetPlayer>`.
  - Counts: `GetFriendCount()`, `GetOnlineFriendCount()`, `HasOnlineFriends()`.
  - Lookups: `IsFriend(player | BnetAccountId | BnetGameAccountId)`, `FindFriend(BnetAccountId)`.
  - Pending friends (added, but presence not yet displayable): `IsPendingFriend`, `FindNonPendingFriend`.
- **Incoming requests:** `GetReceivedInvites()` returns `List<BnetInvitation>`, or **null** when the privacy feature is off. `BnetInvitation` provides `GetId()`, `GetTargetName()`, `GetTargetId()` and `GetCreationTimeS()`.
- **Outgoing requests and limits:** `m_sentInvites`, `m_maxFriends`, `m_maxReceivedInvites` and `m_maxSentInvites` are all **private, with no getters** in the current build. HSA's `GetMaxFriends`, `GetSentInvites` and `GetMaxSentInvites` were methods it added to the game itself. Our options are reflection, or `BattleNet.GetFriendsInfo(ref FriendsInfo)` (fields `maxFriends`, `maxRecvInvites`, `maxSentInvites`). The game UI does not show sent requests at all.
- **Feature flag:** `IsFriendInviteFeatureEnabled` (privacy).
- **Change event:** `AddChangeListener(ChangeCallback(BnetFriendChangelist, object))`. The changelist has `GetAddedFriends`, `GetRemovedFriends`, `GetAddedReceivedInvites`, `GetRemovedReceivedInvites` and `AddAddedSentInvite`.
- **Actions:**
  - `AcceptInvite(BnetInvitation)`.
  - `IgnoreInvite(BnetInvitationId)`. This is "Decline"; there is no separate block call.
  - `SendInvite(string)` routes to `SendInviteByEmail` when the text contains "@" and to `SendInviteByBattleTag` when it contains "#". It returns false if the text is malformed. BattleTag regex: `^[^\W\d_][^\W_]{1,11}#\d+$`.
  - `RemoveFriend(BnetPlayer)`.

### BnetPlayer

- **Names:**
  - `GetBestName()`.
  - `GetBattleTag()` (`BnetBattleTag`: `GetName()`, `GetNumber()`, `GetString()`).
  - `GetFullName()` is the Real ID name; `GetAccount().GetFullName()` is the same.
  - `FriendUtils.GetFriendListName(p, addColorTags)` is the name the list shows: full name, else BattleTag.
  - `FriendUtils.GetUniqueName(p)` gives the BattleTag only when names collide or the player is a nearby stranger. Dialogs use it.
- **Presence:**
  - `IsOnline()`, `IsAway()`, `IsBusy()`, `IsAppearingOffline()`.
  - `GetBestAwayTimeMicrosec()`, `GetBestLastOnlineMicrosec()`, `GetBestProgramId()`.
  - Accounts: `GetHearthstoneGameAccount()` / `GetHearthstoneGameAccountId()`, `GetBestGameAccount()` / `GetBestGameAccountId()`.
- **BnetGameAccount:** `GetRichPresence()`, `GetTutorialBeaten()`, `GetBattlegroundsTutorialComplete()`, `GetMercenariesTutorialComplete()`, `GetSessionRecord()`.
- **Not in the API:** notes, nicknames and favourites. No such data exists in the client.
- **Rank:**
  - `RankMgr.Get().GetRankedMedalFromRankPresenceField(gameAccount)` returns a `MedalInfoTranslator`. Check `IsDisplayable()`.
  - `RankMgr.Get().GetBattlegroundsMedalFromRankPresenceField(gameAccount, out rating, out gameType)`.
- **Self:** `BnetPresenceMgr.Get().GetMyPlayer()`. For your own rank, `RankMgr.Get().GetLocalPlayerMedalInfo()`.
- **Presence changes:** `BnetPresenceMgr.Get().AddPlayersChangedListener((BnetPlayerChangelist, object))`, then `changelist.HasChange(player)`.

### Status line

`FriendListFriendFrame.UpdatePresence()` builds the status line as follows. It is pure logic, so we can copy the order:

1. Recent stranger in the recent-players frame: no status line. The recent frame instead shows `BnetRecentPlayerMgr.GetRecentReason(p)`.
2. Offline: `FriendUtils.GetLastOnlineElapsedTimeString(p.GetBestLastOnlineMicrosec())`. This gives "Last online 3 hours ago", or "Offline" when the value is 0.
3. Online but not in Hearthstone: `BnetUtils.GetNameForProgramId(p.GetBestProgramId())`, else `GLOBAL_PROGRAMNAME_PHOENIX` ("Online").
4. Away: `FriendUtils.GetAwayTimeString(p.GetBestAwayTimeMicrosec())` ("Away for 5 minutes").
5. Busy: `GLOBAL_FRIENDLIST_BUSYSTATUS` ("Busy").
6. Otherwise: **`PresenceMgr.Get().GetStatusText(BnetPlayer)`**. This formats a `PRESENCE_STATUS_*` key (PRESENCE.txt has 633 lines, e.g. "Hanging out in main menu" or "Doing battle with a friend"). It falls back to `GetRichPresence()` when the status is unknown or the player is unsubscribed.

`PresenceMgr.GetStatus(p)` returns the `Global.PresenceStatus` enum, and `PresenceMgr.IsStatusPlayingGame(status)` tests it.

**Pitfall:** `FriendDataModel.PlayerName` contains `<color=#…>` tags for friends, so strip rich text before speaking it.

### Sorting

The game sorts with `FriendUtils.FriendSortCompare`: online first, then in-Hearthstone, then in another game, then in the app, then friend before non-friend, then by name. `RecentFriendSortCompare` sorts by `TimeLastAddedToRecentPlayers`, newest first.

### Nearby and recent players

- **`BnetNearbyPlayerMgr.Get()`** (LAN discovery):
  - `IsEnabled()`, `GetNearbyPlayers()`, `HasNearbyStrangers()`.
  - `IsNearbyPlayer(p)`, `IsNearbyStranger(p)`.
  - `AddChangeListener((BnetRecentOrNearbyPlayerChangelist, object))`.
  - Nearby players can also be friends.
- **`BnetRecentPlayerMgr.Get()`:**
  - `GetRecentPlayers()`, `GetRecentReason(p)`, `IsCurrentOpponent(p)`, `IsRecentStranger(p)`, `GetCurrentOpponent()`, `AddChangeListener`.
  - Reason strings: Recent opponent, Former friend, New friend!, Recently challenged, Recently chatted, Last opponent, Current opponent!, spectating, recently spectated, current or recent teammate.
- **`FriendListFrame` sections:** requests, friends, recent, nearby. The frame keeps every row in a private `m_allItems` (`List<FriendListItem>`), sorted with `ItemsSortCompare`.
  - `FriendListItem` provides `ItemMainType`, `GetFriend()`, `GetRecentPlayer()`, `GetNearbyPlayer()` and `GetInvite()`.
  - Only **rendered** rows exist as `FriendListFriendFrame` objects, because `TouchList` is virtualized (`items.RenderedItems`).
  - Users can collapse sections (`FriendListItemHeader.IsShowingContents`, stored in Options).
- **Header strings:** `GLOBAL_FRIENDLIST_REQUESTS_HEADER`, `GLOBAL_FRIENDLIST_FRIENDS_HEADER(online, total)` or `GLOBAL_FRIENDLIST_FRIENDS_HEADER_ALL_ONLINE`, `GLOBAL_FRIENDLIST_RECENT_PLAYERS_HEADER`, `GLOBAL_FRIENDLIST_NEARBY_PLAYERS_HEADER`.
- **Recommendation:** read the managers directly, not the rendered frames.

## 3. Actions and the calls the UI makes

Pressing a friend's challenge button opens the flyout `FriendListFlyoutMenu`. Its `ButtonOption` values:

| Option | Shown when (`ShouldShowOption`) | Enabled when (`ShouldEnableOption`) | Call on release |
|---|---|---|---|
| Hearthstone | `FriendChallengeMgr.CanShowFriendlyChallenge(p)` | `IsHearthstoneFriendlyChallengeAvailable(p)`. If no sub-popup is used, a valid Standard deck is also required. | Opens the sub-popup `HearthstoneChallengePopup` when `ShouldAccountSeeStandardWild()` or Tavern Brawl is unlocked; otherwise `SendHearthstoneFriendlyChallenge()` (Standard) |
| Battlegrounds | same as Hearthstone | `IsBattlegroundsFriendlyChallengeAvailable(p, false)` | Requires the BG module to be downloaded. Then `GameMgr.SetPendingAutoConcede(true)`, `SceneMgr.SetNextMode(BACON)` and `PartyManager.Get().SendInvite(PartyType.BATTLEGROUNDS_PARTY, p.GetBestGameAccountId())` |
| Mercenaries | same as Hearthstone | `IsMercenariesFriendlyChallengeAvailable(p)` | `PartyManager.Get().StartMercenariesFriendlyChallengeEntry(p)` |
| Spectate | `SpectatorManager.CanSpectate(p)` | always | `SpectatorManager.Get().SpectatePlayer(p)` |
| InviteToSpectate | `CanInviteToSpectateMyGame(id)` or `IsInvitedToSpectateMyGame(id)` | always | `SpectatorManager.Get().InviteToSpectateMe(p)` |
| KickSpectator | `IsSpectatingMe(id)` | always | Confirm popup, then `KickSpectator(p, true)` |
| StopSpectating | `IsSpectatingPlayer(id)` | always | Confirm popup, then `LeaveSpectatorMode()` |
| InviteToParty | in a BG party (not in game or queue) and the player is not already in it | `IsBattlegroundsFriendlyChallengeAvailable(p)` and `PartyManager.CanInvite(id)` | Leader: `SendInvite(BATTLEGROUNDS_PARTY, id)`. A full ranked party first shows the private-party dialog. Non-leader: `SendInviteSuggestion(...)` |
| KickFromParty | in a BG party and `CanKick` | always | `KickPlayerFromParty(id)` |
| AddFriend | not a friend | not the current opponent | `BnetFriendMgr.SendInvite(p.GetBattleTag().GetString())` |
| Report | not a friend | `NetCacheFeatures.ReportPlayerEnabled` | `ShowReportingPopup()` |
| Options | is a friend | always | `FriendsListOptionsPopup`, which has "Remove friend" and "Report" |

- **The Hearthstone sub-popup** has exactly three buttons: Standard, Wild (only when `ShouldAccountSeeStandardWild()`) and Tavern Brawl. **There is no Twist or Classic button in the current game.** The enum values `TwistHearthstone` and `ClassicHearthstone` are unused.
- **Shared call for any format:**
  ```
  FriendChallengeMgr.Get().SetChallengeMethod(ChallengeMethod.FROM_FRIEND_LIST);
  FriendChallengeMgr.Get().SendChallenge(p, FormatType.FT_STANDARD | FT_WILD, enableDeckShare: true);
  ```
  - Pre-checks: `CollectionManager.Get().AccountHasValidDeck(format)` and `FriendChallengeMgr.IsOpponentAvailable(p)`.
  - Error popups: `GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGER_NO_STANDARD_DECK` / `_NO_DECK`, and `GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_THEYRE_UNAVAILABLE`.
  - **Twist is unverified:** `SendChallenge` accepts any `FormatType`, but whether the server supports FT_TWIST is unknown. HSA offers it.
- **Tavern Brawl:**
  - Checks, in order: `HasUnlockedTavernBrawl`, `CanChallengeToTavernBrawl`, and `canCreateDeck && !HasValidDeck`, which leads to `FriendChallengeMgr.ShowChallengerNeedsToCreateTavernBrawlDeckAlert()`.
  - Then `TavernBrawlManager.Get().CurrentBrawlType = BRAWL_TYPE_TAVERN_BRAWL; FriendChallengeMgr.Get().SendTavernBrawlChallenge(p, BRAWL_TYPE_TAVERN_BRAWL, CurrentMission().seasonId, CurrentMission().SelectedBrawlLibraryItemId)`.
  - Disabled reasons come from `HearthstoneChallengePopup.ShouldShowGenericHearthstoneTooltip` and the `On*ButtonOver` methods: mode unavailable, traditional tutorial locked, challengee has no tutorial, `FriendListFlyoutMenu.GetAvailability(p, out reasonKey)`. All are formatted with `player.GetBestName()`.
- **Duos:** there is no separate invite. A BG party becomes duos through the lobby (`BaconLobbyMgr.IsInDuosMode()`; scenario 5173 vs 3459).
- **While a challenge is pending:**
  - The challenger sees `ShowISentChallengeDialog`: "You are waiting for {0} to respond…" (`ResponseDisplay.NONE`, no visible button). Its response callback calls `RescindChallenge()`. To let the user cancel, call **`FriendChallengeMgr.Get().CancelChallenge()`**, which rescinds if we sent the challenge and declines if we received it.
  - **Receiving:** `HandleJoinedParty` fires `I_RECEIVED_CHALLENGE`, then `DialogManager.ShowFriendlyChallenge(...)` shows a `FriendlyChallengeDialog` (`m_challengeText`, `m_challengerName`, `m_acceptButton`, `m_denyButton`, `m_nearbyPlayerNote`, an optional quest).
    - Response: `AcceptChallenge()` or `DeclineChallenge()`.
    - BG party invites use the same dialog (`PartyManager` line ~1410), body `GLOBAL_FRIEND_CHALLENGE_BODY_BACON`.
    - BG suggestions use `DialogManager.ShowBattlegroundsSuggestion`.
  - **Events:** `FriendChallengeMgr.AddChangedListener(ChangedCallback(FriendChallengeEvent, BnetPlayer, FriendlyChallengeData, object))`. Values include I_SENT/I_RECEIVED/I_ACCEPTED/I_DECLINED/I_RESCINDED_CHALLENGE, OPPONENT_ACCEPTED/DECLINED/CANCELED/RESCINDED_CHALLENGE, OPPONENT_REMOVED_FROM_FRIENDS, QUEUE_CANCELED and the deck-share events.
- **Remove friend:** `ChatMgr.Get().FriendListFrame.ShowRemoveFriendPopup(BnetPlayer)` is **public** and does not need a rendered frame. It shows a confirm/cancel popup ("Are you sure you want to remove {0}?"); confirming calls `BnetFriendMgr.RemoveFriend`. `FriendListFrame.RemoveFriendPopupOpened`/`Closed` are events. Remove mode (`ToggleRemoveFriendsMode`) is purely visual.
- **Add friend:** `FriendListFrame.ShowAddFriendFrame(BnetPlayer prefill = null)` is public. The game's add button first checks the privacy flag and may show a `PrivacyFeaturesPopup(CHAT)`. `AddFriendFrame.m_InputTextField` is a real text field. On submit it calls `BnetFriendMgr.SendInvite(text)`, and on failure `UIStatus.AddError(GLOBAL_ADDFRIEND_ERROR_MALFORMED)`. Simplest option: our own text input plus `SendInvite`.
- **Requests:** Accept is `BnetFriendMgr.AcceptInvite(invite)`. Decline is `IgnoreInvite(invite.GetId())`. Time label: `GLOBAL_FRIENDLIST_REQUEST_SENT_TIME` with `FriendUtils.GetRequestElapsedTimeString((long)invite.GetCreationTimeS())`.
- **Report:**
  - `ReportingPopup` uses private static dictionaries `ReportReasons`, `ComplaintTypeLabels` and `SubcomplaintTypeLabels`. Read them by reflection.
  - Reasons: Inappropriate Name → BattleTag; Inappropriate Chat → Harassment, Spam, Advertisement; Cheating → Hacking, Botting, Intentionally Losing / Deranking.
  - Submit: `BattleNet.Get().SubmitReport(p.GetAccountId(), ComplaintType, List<SubcomplaintType>)`.
  - Block: no block action exists in the client.
- **Profile:** there is no friend-profile view; the list only shows rank medals. For "me", use the `FriendListFrame.me` name text plus `RankMgr.GetLocalPlayerMedalInfo()` and the BattleTag.
- **Chat:**
  - **Opening:** `ChatMgr.Get().OnFriendListFriendSelected(p)`, which is what clicking a friend row does. It runs `ShowChatForPlayer` (a privacy popup if chat is disabled), registers `PlayerChatInfo`, closes the flyout and calls `m_chatLogUI.ShowForPlayer(...)`.
  - **Sending:** on PC, `QuickChatFrame` calls `UniversalInputManager.Get().UseTextInput(TextInputParams{ m_maxCharacters = 512, m_completedCallback = OnInputComplete, ... })`. `OnInputComplete` calls `BnetWhisperMgr.Get().SendWhisper(receiver, text)`, which returns false if you or they are offline (`GLOBAL_CHAT_RECEIVER_OFFLINE`). Up/Down/Tab cycles through recent receivers. Pending drafts: `ChatMgr.Set/GetPendingMessage(accountId)`.
  - **Reading:** `BnetWhisperMgr.Get().GetWhispersWithPlayer(p)` returns `List<BnetWhisper>` history. Text: `ChatUtils.GetMessage(w)`. Speaker: `WhisperUtil.GetSpeaker` / `IsSpeaker(p, w)`. Deck codes: `ChatUtils.TryGetFormattedDeckcodeMessage`.
  - **Live messages:** `BnetWhisperMgr.Get().AddWhisperListener((BnetWhisper, object))`. Recent conversations: `ChatMgr.GetRecentWhisperPlayers()`.
  - **Vanilla:** Enter opens chat with the most recent whisperer (`ChatMgr.HandleGUIInputForQuickChat`).

## 4. Social notifications and where to hook

- **`SocialToastMgr.AddToast(UserAttentionBlocker, string, TOAST_TYPE, float, bool)`** is the 5-argument overload; all the others funnel into it. A postfix there, or a prefix on `SocialToast.SetText` (called only from `FadeInToast`), catches everything. Text contains `<color>` tags. `UserAttentionManager.CanShowAttentionGrabber` filters toasts and drops them during games. If we want them announced anyway, hook the event sources instead. Toast sources:
  - **Friend online and offline:** `CheckForOnlineStatusChanged`. The offline toast is silent.
  - **Friend request:** `OnFriendsChanged`, with the recent-opponent variant.
  - **Spectator invites and joins:** from `SpectatorManager`.
  - **Friend progress:** rank, legend, legendary opened, arena or brawl runs, class levels, achievements.
- **Whispers:** `BnetWhisperMgr.AddWhisperListener`. Alternatively `ChatMgr.OnWhisper` → `PopupNewChatBubble` → `ChatBubbleFrame.SetWhisper`; this is the point where HSA speaks.
- **Challenges:** `FriendChallengeMgr.AddChangedListener` for state. The dialog is `FriendlyChallengeDialog.Show()`. The sent, declined and canceled messages are `AlertPopup`s, which existing popup handling may already cover.
- **Presence and roster:** `BnetPresenceMgr.AddPlayersChangedListener`, `BnetFriendMgr.AddChangeListener`, and the recent and nearby listeners.

## 5. HSA's current behaviour and its weaknesses

**Structure:** `FriendListFrame` itself implements `AccessibleUI`, as a state machine with 13 states. The main menu is titled "Social menu" and Back closes the list. Its items:

1. "FRIEND REQUESTS - n", if the header exists.
2. "NEARBY - n".
3. "RECENT - n".
4. "FRIENDS - n Online".
5. Profile.
6. Add Friend.
7. Remove Friends.

**Lists:** each list is read as an `AccessibleListOfItems`; Confirm opens a sub-menu.

- **Friend line:** name, best rank, BG rating, then status from `FriendDataModel`. Offline friends get "Offline, name".
- **Friend menu:**
  - The challenge entry is decided by HSA's own copy of Blizzard's *old* challenge-button state logic, added to `FriendListChallengeButton`. It yields one of Challenge, Can't challenge, Spectate, Kick, Reveal cards or Invite to spectate.
  - Then "Invite to Tavern Brawl", "Invite to Battlegrounds party" and "Send message".
  - Challenge leads to a format menu: Standard, plus Wild and Twist (both only when Standard/Wild is unlocked).
- **Requests:** Accept or Decline, which press the frame's buttons.
- **Recent:** name and reason. Its menu has Send message (friends) or Add as friend (strangers), plus Report, which opens HSA's own report menu.
- **Profile:** your name and rank. Confirm copies your BattleTag.
- **Add friend:** opens the game's frame and speaks the instruction; typing goes to the game's text field.
- **Remove:** Confirm calls the row's delete handler, which opens the game popup.

**Weaknesses and bugs:**

1. **Built on an old game version.** HSA depends on members it added to the game (`GetMaxFriends`, `GetSentInvites`, `GetMaxSentInvites`) and on a resurrected `FriendListChallengeMenu` with static helpers. It also re-implements the challenge-button logic, which no longer matches the flyout-based UI. Battlegrounds invites set the next scene and send the invite *without* the module-download check or auto-concede, and *without* the private-party dialog. The Mercenaries challenge, invite-to-spectate as a flyout item, kick-from-party, stop spectating and leave party are missing.
2. **Only rendered rows.** The friends, nearby, requests and remove lists come from `GetRenderedItems` (`items.RenderedItems`), but the TouchList is virtualized. HSA tries to scroll the list as you move (`ScrollToItem` in `OnReadFriend`) and rebuilds after scrolling. Rows beyond the viewport are missing until a scroll happens, and lists can shrink or jump. A collapsed section yields an empty list.
3. **Index math.** "x of y" uses `m_allFriends`, built from `m_allItems` filtered by `GetFriend()!=null`, with a count override. The visible items are a subset, so numbers and boundaries disagree.
4. **Nearby and recent are broken on purpose.** HSA's `AddItem` drops RecentPlayer and NearbyPlayer rows ("23.6.0 refactor broke nearby/recents"), so those headers never exist. `GetNearbyOrRecentPlayersCount` also has an inverted condition: it never counts NearbyPlayer, so Nearby is never offered. Recent is read straight from `BnetRecentPlayerMgr` with no status, no challenge and no spectate.
5. **"Friends" can disappear.** The item is hidden when `AreAllFriendsNearby()` is true. Combined with bug 4, all friends become unreachable apart from Remove.
6. **Empty-list crashes.** Remove calls `StartReadingFromIndex(0)` and then `LogFatalError` when the list is empty, which is possible whenever rows aren't rendered. The nearby, requests and remove input handlers dereference `GetItemBeingRead()` before checking the count, risking a NullReferenceException. `ReadFriendsList` on an empty list leaves you in an empty state.
7. **Requests** depend on the header and on rendered frames. Their Accept and Decline labels reuse the *challenge* strings.
8. **Chat.** HSA disables `ChatMgr.HandleGUIInput` entirely (Enter does nothing). Incoming bubbles are spoken once as notifications. There is no conversation history, no list of recent conversations and no unread count. Sending only works through "Send message", which opens the game chat with no spoken context. Spoken bubbles depend on the bubble pop-up path.
9. **Misc.**
   - The help text in `CHALLENGING_FRIEND` returns the friend-menu help.
   - Speech uses `FriendDataModel` text, which can carry colour tags and is refreshed only every 30 s or on presence change.
   - Twist is offered without verification and Classic is missing.
   - Friend lines omit "away/busy" ordering context and online/total counts.
   - The friend's BattleTag is never read; only the full name is read.
   - Changes to the friend list re-read the whole main menu (`OnFriendsChanged` → `ReadFriendsMenu`), which throws the user out of whatever they were doing.

## 6. Strings (GLOBAL.txt and GLUE.txt, enUS)

| Key | English |
|---|---|
| GLOBAL_FRIENDLIST_REQUESTS_HEADER | FRIEND REQUESTS - {0} |
| GLOBAL_FRIENDLIST_FRIENDS_HEADER | FRIENDS - {0}/{1} Online (online, total) |
| GLOBAL_FRIENDLIST_FRIENDS_HEADER_ALL_ONLINE | FRIENDS - {0} Online |
| GLOBAL_FRIENDLIST_RECENT_PLAYERS_HEADER / _NEARBY_ | RECENT - {0} / NEARBY - {0} |
| GLOBAL_FRIENDLIST_REQUEST_SENT_TIME | Sent: {0} |
| GLOBAL_OFFLINE / GLOBAL_PROGRAMNAME_PHOENIX | Offline / Online |
| GLOBAL_FRIENDLIST_BUSYSTATUS | Busy |
| GLOBAL_DATETIME_LASTONLINE_* / _AFK_* / _FRIENDREQUEST_* | Last online {0} hours ago / Away for {0} minutes / … |
| GLOBAL_FRIENDLIST_RECENT_OPPONENT_STATUS, _NEW_FRIEND_, _CURRENT_OPPONENT_, _LAST_OPPONENT_, _RECENT_CHATTED_ | Recent opponent, New friend!, Current opponent!, Last opponent, Recently chatted |
| GLUE_FRIEND_LIST_ADD_FRIENDS / _REMOVE_FRIENDS | Add Friend / Remove Friends |
| GLOBAL_ADDFRIEND_HEADER / _INSTRUCTION / _BUTTON | Add a Battle.net Friend / Enter a BattleTag or email address. / Add as friend |
| GLOBAL_ADDFRIEND_SENT_CONFIRMATION / _ERROR_MALFORMED | Friend request sent. / You must enter an email address or Battle Tag. |
| GLOBAL_FRIENDLIST_REMOVE_FRIEND_ALERT_MESSAGE | Are you sure you want to remove {0}? |
| GLOBAL_FRIENDLIST_CHALLENGE_MENU_HEADER_FRIEND / _NONFRIEND | Challenge {0} to... |
| GLOBAL_STANDARD, GLOBAL_WILD, GLOBAL_TWIST, GLOBAL_CLASSIC, GLOBAL_TAVERN_BRAWL, GLOBAL_BATTLEGROUNDS, GLOBAL_MERCENARIES | Standard, Wild, Twist, Classic, Tavern Brawl, Battlegrounds, Mercenaries |
| GLOBAL_SPECTATE | Spectate |
| GLOBAL_FRIENDLIST_SPECTATE_MENU_KICK / _STOP | Kick Spectator / Stop Spectating |
| GLOBAL_FRIENDLIST_SPECTATE_TOOLTIP_INVITE_HEADER / _TEXT | Invite to Spectate / Invite {0} to spectate you. |
| GLOBAL_FRIENDLIST_SPECTATE_TOOLTIP_RECEIVED_INVITE_TEXT | {0} has invited you to be a spectator… |
| GLOBAL_FRIENDLIST_BATTLEGROUNDS_TOOLTIP_INVITE_HEADER / _BODY | Invite / Invite {0} to play Battlegrounds with you. |
| GLOBAL_FRIENDLIST_BATTLEGROUNDS_TOOLTIP_KICK_HEADER | Kick |
| GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_HEADER / _AVAILABLE | Friendly Challenge / Challenge {0} to a game of Hearthstone. |
| GLOBAL_FRIENDLIST_CHALLENGE_BUTTON_IM_UNAVAILABLE / _THEYRE_UNAVAILABLE / _IM_APPEARING_OFFLINE / _BATTLEGROUNDS_PARTY_MEMBER | You can't challenge {0} right now. Return to the main menu. / …Wait for them to return… / …You are set to appear offline. / You can't send a challenge while in a Battlegrounds party. |
| GLOBAL_FRIENDLIST_CHALLENGE_CHALLENGER_NO_STANDARD_DECK / _NO_DECK / _TAVERN_BRAWL_LOCKED | You don't have any Standard decks. / You need to build a deck first. / Unlock Tavern Brawl by reaching level 20 with any class. |
| GLOBAL_FRIENDLIST_CHALLENGE_TOOLTIP_NO_TAVERN_BRAWL | Tavern Brawl is not available right now. Check back soon! |
| GLOBAL_FRIEND_CHALLENGE_HEADER | Challenge |
| GLOBAL_FRIEND_CHALLENGE_BODY1 / _STANDARD / _WILD / _TAVERN_BRAWL_BODY1 / _BODY_BACON | You've been challenged by: / …to a Standard game by: / …Wild… / …Tavern Brawl… / You've been invited to join a Battlegrounds party by: |
| GLOBAL_FRIEND_CHALLENGE_ACCEPT / _DECLINE | Accept / Decline |
| GLOBAL_FRIEND_CHALLENGE_OPPONENT_WAITING_RESPONSE / _DECLINED / _CANCELED / _QUEUE_CANCELED | You are waiting for {0} to respond to your challenge. / {0} declined your challenge. / {0} canceled the challenge. / The challenge was canceled. |
| GLOBAL_FRIENDLIST_CHALLENGE_MENU_REPORT_BUTTON | Report... |
| GLOBAL_FRIENDLIST_REPORT_SELECT_HEADER_FRIEND / _REASON / _OPTIONS / _COMPLETE | Report {0} / Select a Reason... / Select all that apply: / Thank you for your report! |
| GLOBAL_REPORT_REASON_* / GLOBAL_REPORT_DETAIL_* | see section 3 |
| GLOBAL_CHAT_RECEIVER_OFFLINE / GLOBAL_CHAT_BUBBLE_RECEIVER_NAME | {0} is offline. / To: {0} |
| GLOBAL_SOCIAL_TOAST_FRIEND_ONLINE / _OFFLINE / _REQUEST | {1} has come online. / has gone offline. / has sent you a friend invite. ({0}=colour) |
| GLOBAL_SOCIAL_TOAST_SPECTATOR_INVITE_RECEIVED | {1} invites you to spectate the game! |
| GLUE_PROGRESSION_PROFILE_TITLE | Profile |
| PRESENCE_STATUS_* (PRESENCE.txt) | e.g. Hanging out in main menu |

## Summary

- **Open and close:** use `BnetBarFriendButton.Get().TriggerRelease()` and `BnetBar.Get().HideFriendList()`. Detect the list with `ChatMgr.IsFriendListShowing()` and the `OnFriendListToggled` event. Closing destroys `FriendListFrame`.
- **Build the menu from the managers, not from rendered rows:**
  - `BnetFriendMgr` for friends and received invites.
  - `BnetRecentPlayerMgr` and `BnetNearbyPlayerMgr` for recent and nearby players.
  - The status line should follow `FriendListFriendFrame.UpdatePresence`, which ends in `PresenceMgr.GetStatusText`.
- **Call the game's own actions:**
  - `FriendChallengeMgr.SendChallenge` / `SendTavernBrawlChallenge` / `CancelChallenge`.
  - `PartyManager.SendInvite(BATTLEGROUNDS_PARTY, …)` after the download check and auto-concede.
  - `SpectatorManager.SpectatePlayer` / `InviteToSpectateMe` / `KickSpectator` / `LeaveSpectatorMode`.
  - `FriendListFrame.ShowRemoveFriendPopup`, `BnetFriendMgr.SendInvite` / `AcceptInvite` / `IgnoreInvite`, `BattleNet.SubmitReport`.
  - Chat: `BnetWhisperMgr.SendWhisper` and `GetWhispersWithPlayer`.
- **Gaps in the game:** the current UI has no Twist or Classic challenge, no block, no notes and no favourites. Limits and sent invites are private.
- **Notifications:** hook `SocialToastMgr.AddToast` (5 arguments), `BnetWhisperMgr.AddWhisperListener` and `FriendChallengeMgr.AddChangedListener`.
- **HSA:** built on an older game version, reads only on-screen (virtualized) rows, has nearby/recent deliberately disabled plus a count bug that hides Nearby, can hide Friends, crashes on empty lists, mismatches indices, and has almost no chat support.
