using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("DiscordReports", "LEVRO", "1.3.0")]
    [Description("Sends player reports to Discord with Combat Snapshot, AnyDesk Cheat Check, Karma Anti-Spam, and In-Game Moderation.")]
    public class DiscordReports : RustPlugin
    {
        #region Fields & Constants

        private PluginConfig _config;
        private readonly Dictionary<ulong, DateTime> _cooldowns = new Dictionary<ulong, DateTime>();
        private readonly List<ReportEntry> _recentReports = new List<ReportEntry>();
        private readonly Dictionary<ulong, ReportDraft> _playerDrafts = new Dictionary<ulong, ReportDraft>();
        private readonly Dictionary<ulong, string> _playerLanguage = new Dictionary<ulong, string>();
        private readonly HashSet<ulong> _frozenPlayers = new HashSet<ulong>();
        private readonly Dictionary<ulong, Vector3> _preSpectatePos = new Dictionary<ulong, Vector3>();
        private readonly HashSet<ulong> _spectatingAdmins = new HashSet<ulong>();

        // Cheat Check Sessions: Suspect SteamID -> Session
        private readonly Dictionary<ulong, CheckSession> _activeChecks = new Dictionary<ulong, CheckSession>();

        // In-memory combat hit ring buffers: Attacker SteamID -> List of hits
        private readonly Dictionary<ulong, List<CombatHitRecord>> _playerCombatLogs = new Dictionary<ulong, List<CombatHitRecord>>();

        // Persistent Karma Database: SteamID -> Karma Record
        private Dictionary<ulong, PlayerKarmaRecord> _karmaDatabase = new Dictionary<ulong, PlayerKarmaRecord>();

        // Last attacker memory for quick reports: VictimId -> AttackerId
        private readonly Dictionary<ulong, ulong> _lastAttackers = new Dictionary<ulong, ulong>();

        private int _reportCounter = 1;

        private const string PermAdmin = "discordreports.admin";
        private const string PermUse = "discordreports.use";

        // CUI Panel Identifiers
        private const string PanelMain = "DR_MainOverlay";
        private const string PanelAdmin = "DR_AdminPanel";
        private const string PanelCheckScreen = "DR_CheckOverlay";
        private const string PanelCheckAdmin = "DR_CheckAdminWidget";

        private readonly string[] QuickCategoriesRU = new[]
        {
            "Аимбот / WallHack",
            "Макросы / Скрипты",
            "Оскорбления / Чат",
            "Гриферство / Блокировка",
            "Подозрительная игра"
        };

        private readonly string[] QuickCategoriesEN = new[]
        {
            "Aimbot / WallHack",
            "No-Recoil / Macros",
            "Abuse / Toxicity",
            "Griefing / Blocking",
            "Suspicious Behavior"
        };

        private string GetLang(ulong userId)
        {
            return _playerLanguage.TryGetValue(userId, out string lang) ? lang : "RU";
        }

        private string T(ulong userId, string ru, string en)
        {
            return GetLang(userId) == "EN" ? en : ru;
        }

        #endregion

        #region Configuration

        public class PluginConfig
        {
            [JsonProperty(PropertyName = "Discord Webhook URL")]
            public string WebhookUrl { get; set; } = "https://discord.com/api/webhooks/yourWebHook";

            [JsonProperty(PropertyName = "Discord Mention Role (e.g. @here or <@&ROLE_ID>, or empty)")]
            public string MentionRole { get; set; } = "";

            [JsonProperty(PropertyName = "Discord Embed Color (Decimal code, 16724787 = Red)")]
            public int EmbedColor { get; set; } = 16724787;

            [JsonProperty(PropertyName = "Report Cooldown for Regular Players (Seconds)")]
            public int CooldownSeconds { get; set; } = 60;

            [JsonProperty(PropertyName = "Notify Online Admins In-Game (true/false)")]
            public bool NotifyOnlineAdmins { get; set; } = true;

            [JsonProperty(PropertyName = "Server Name for Discord")]
            public string ServerName { get; set; } = "RUSTALGIA";

            [JsonProperty(PropertyName = "Admin Alert Sound Prefab")]
            public string AdminAlertSound { get; set; } = "assets/prefabs/locks/keypad/effects/lock.code.updated.prefab";

            [JsonProperty(PropertyName = "Discord Invite URL for Checks")]
            public string DiscordInviteUrl { get; set; } = "https://discord.gg/rustalgia";

            [JsonProperty(PropertyName = "Discord Voice Channel Name for Checks")]
            public string DiscordCheckChannel { get; set; } = "#проверка";

            [JsonProperty(PropertyName = "Check Duration in Seconds (Default: 300 = 5 min)")]
            public int CheckDurationSeconds { get; set; } = 300;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception();

                if (string.IsNullOrEmpty(_config.WebhookUrl) || _config.WebhookUrl.Contains("YOUR_WEBHOOK_URL_HERE"))
                {
                    var dict = Config.ReadObject<Dictionary<string, object>>();
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            if (kvp.Key.Contains("Webhook URL") && kvp.Value != null)
                            {
                                string val = kvp.Value.ToString();
                                if (!string.IsNullOrEmpty(val) && !val.Contains("YOUR_WEBHOOK_URL_HERE"))
                                {
                                    _config.WebhookUrl = val;
                                }
                            }
                            if (kvp.Key.Contains("Назва сервера") && kvp.Value != null)
                            {
                                _config.ServerName = kvp.Value.ToString();
                            }
                        }
                    }
                }
            }
            catch
            {
                PrintWarning("Error reading config. Loading default configuration.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Models

        public class CheckSession
        {
            public ulong SuspectId { get; set; }
            public string SuspectName { get; set; }
            public ulong AdminId { get; set; }
            public string AdminName { get; set; }
            public DateTime StartTime { get; set; }
            public int RemainingSeconds { get; set; } = 300;
            public Vector3 FreezePosition { get; set; }
            [JsonIgnore]
            public Timer SessionTimer { get; set; }
        }

        public class CombatHitRecord
        {
            public DateTime Timestamp { get; set; }
            public ulong AttackerId { get; set; }
            public string AttackerName { get; set; }
            public ulong VictimId { get; set; }
            public string VictimName { get; set; }
            public string Weapon { get; set; }
            public float Distance { get; set; }
            public string BoneName { get; set; }
            public bool IsHeadshot { get; set; }
            public float Damage { get; set; }
        }

        public class PlayerKarmaRecord
        {
            public ulong SteamId { get; set; }
            public string PlayerName { get; set; }
            public int Karma { get; set; } = 100;
            public int TotalReports { get; set; }
            public int ConfirmedBans { get; set; }
            public int FalseReports { get; set; }
            public DateTime LastReportTime { get; set; }
            public int CooldownMultiplier { get; set; } = 1;
        }

        private class ReportDraft
        {
            public ulong TargetId { get; set; }
            public string TargetName { get; set; } = "None selected";
            public int CategoryIndex { get; set; } = 0;
            public string Comment { get; set; } = "";
            public int Page { get; set; } = 0;
        }

        public class ReportEntry
        {
            public int Id { get; set; }
            public string ReporterName { get; set; }
            public ulong ReporterId { get; set; }
            public string SuspectName { get; set; }
            public ulong SuspectId { get; set; }
            public string Reason { get; set; }
            public string Grid { get; set; }
            public Vector3 Position { get; set; }
            public DateTime Timestamp { get; set; }
            public bool IsResolved { get; set; }
            public string Status { get; set; } = "pending"; // pending, in_progress, resolved, banned, false_report
            public string ModeratorName { get; set; } = "";
            public int ReporterKarma { get; set; } = 100;
            public string CombatSnapshot { get; set; } = "";
        }

        private class SuspectCandidate
        {
            public ulong Id { get; set; }
            public string Name { get; set; }
            public string Status { get; set; }
            public bool IsSelfTest { get; set; }
        }

        private class DiscordMessage
        {
            [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)]
            public string Content { get; set; }

            [JsonProperty("embeds")]
            public List<DiscordEmbed> Embeds { get; set; } = new List<DiscordEmbed>();

            [JsonProperty("components", NullValueHandling = NullValueHandling.Ignore)]
            public List<DiscordActionRow> Components { get; set; } = null;
        }

        private class DiscordEmbed
        {
            [JsonProperty("title")]
            public string Title { get; set; }

            [JsonProperty("color")]
            public int Color { get; set; }

            [JsonProperty("fields")]
            public List<DiscordField> Fields { get; set; } = new List<DiscordField>();

            [JsonProperty("footer")]
            public DiscordFooter Footer { get; set; }

            [JsonProperty("timestamp")]
            public string Timestamp { get; set; }
        }

        private class DiscordField
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("value")]
            public string Value { get; set; }

            [JsonProperty("inline")]
            public bool Inline { get; set; }

            public DiscordField(string name, string value, bool inline = true)
            {
                Name = name;
                Value = value;
                Inline = inline;
            }
        }

        private class DiscordFooter
        {
            [JsonProperty("text")]
            public string Text { get; set; }
        }

        private class DiscordActionRow
        {
            [JsonProperty("type")]
            public int Type { get; set; } = 1;

            [JsonProperty("components")]
            public List<DiscordComponent> Components { get; set; } = new List<DiscordComponent>();
        }

        private class DiscordComponent
        {
            [JsonProperty("type")]
            public int Type { get; set; } = 2; // Button

            [JsonProperty("style")]
            public int Style { get; set; } = 5; // 5 = Link Button

            [JsonProperty("label")]
            public string Label { get; set; }

            [JsonProperty("url", NullValueHandling = NullValueHandling.Ignore)]
            public string Url { get; set; }

            [JsonProperty("emoji", NullValueHandling = NullValueHandling.Ignore)]
            public DiscordEmoji Emoji { get; set; }
        }

        private class DiscordEmoji
        {
            [JsonProperty("name")]
            public string Name { get; set; }
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermUse, this);

            LoadKarmaData();

            Puts("DiscordReports v1.3.0 initialized! Commands: /report, /check, /reports");
        }

        private void Unload()
        {
            SaveKarmaData();

            foreach (var kvp in _activeChecks)
            {
                kvp.Value.SessionTimer?.Destroy();
                BasePlayer suspect = BasePlayer.FindByID(kvp.Key);
                if (suspect != null)
                {
                    CuiHelper.DestroyUi(suspect, PanelCheckScreen);
                }
            }
            _activeChecks.Clear();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                CloseAllUi(player);
                if (_spectatingAdmins.Contains(player.userID))
                {
                    StopSpectating(player);
                }
            }
            _frozenPlayers.Clear();
            _playerDrafts.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;

            // Auto-ban if player disconnects during active cheat check
            if (_activeChecks.TryGetValue(player.userID, out CheckSession checkSession))
            {
                checkSession.SessionTimer?.Destroy();
                _activeChecks.Remove(player.userID);

                if (!IsAdminOrStaff(player.userID))
                {
                    ServerUsers.Set(player.userID, ServerUsers.UserGroup.Banned, player.displayName, "Лив с проверки на читы / Disconnected during cheat check");
                    ServerUsers.Save();

                    PrintToChat($"<color=#FF2222><b>[АНТИЧИТ]</b></color> Игрок <color=#FFFF00><b>{player.displayName}</b></color> покинул сервер во время активной проверки на читы и был автоматически <color=#FF2222><b>НАВСЕГДА ЗАБАНЕН</b></color>!");

                    SendDisconnectBanToDiscord(checkSession);
                }
            }

            _playerDrafts.Remove(player.userID);
            _frozenPlayers.Remove(player.userID);
            _spectatingAdmins.Remove(player.userID);
            _preSpectatePos.Remove(player.userID);
        }

        private object OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player != null && (_frozenPlayers.Contains(player.userID) || _activeChecks.ContainsKey(player.userID)))
            {
                input.Clear();
                if (input.current != null) input.current.buttons = 0;
                if (input.previous != null) input.previous.buttons = 0;

                if (_activeChecks.TryGetValue(player.userID, out CheckSession session))
                {
                    if (session.FreezePosition != Vector3.zero)
                    {
                        player.Teleport(session.FreezePosition);
                    }
                }
                return true;
            }
            return null;
        }

        private object OnPlayerTick(BasePlayer player, PlayerTick tick, bool wasPlayerTick)
        {
            if (player == null) return null;
            if (_activeChecks.TryGetValue(player.userID, out CheckSession session))
            {
                if (session.FreezePosition != Vector3.zero)
                {
                    tick.position = session.FreezePosition;
                }
                return true;
            }
            return null;
        }

        private object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker != null && (_frozenPlayers.Contains(attacker.userID) || _activeChecks.ContainsKey(attacker.userID)))
            {
                info.damageTypes.Clear();
                info.HitEntity = null;
                info.DidHit = false;
                return true;
            }
            return null;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // Protect player under check from being killed
            if (entity is BasePlayer suspect && _activeChecks.ContainsKey(suspect.userID))
            {
                info.damageTypes.Clear();
                info.HitMaterial = 0;
                return true;
            }

            // Record combat hits for Discord snapshot and analytics
            if (entity is BasePlayer victim && info?.InitiatorPlayer is BasePlayer attacker && attacker != victim)
            {
                RecordCombatHit(attacker, victim, info);
            }

            return null;
        }

        private void RecordCombatHit(BasePlayer attacker, BasePlayer victim, HitInfo info)
        {
            try
            {
                float dist = Vector3.Distance(info.PointStart, info.PointEnd);
                string weaponName = info.Weapon?.GetItem()?.info?.displayName?.english ?? info.WeaponPrefab?.name ?? "Unknown";
                string boneName = StringPool.Get(info.HitBone);
                bool isHead = info.isHeadshot || (boneName != null && boneName.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0);
                float dmg = info.damageTypes.Total();

                CombatHitRecord record = new CombatHitRecord
                {
                    Timestamp = DateTime.UtcNow,
                    AttackerId = attacker.userID,
                    AttackerName = attacker.displayName,
                    VictimId = victim.userID,
                    VictimName = victim.displayName,
                    Weapon = weaponName,
                    Distance = dist,
                    BoneName = boneName ?? "Body",
                    IsHeadshot = isHead,
                    Damage = dmg
                };

                if (!_playerCombatLogs.TryGetValue(attacker.userID, out var list))
                {
                    list = new List<CombatHitRecord>();
                    _playerCombatLogs[attacker.userID] = list;
                }
                list.Insert(0, record);
                if (list.Count > 30) list.RemoveAt(list.Count - 1);

                _lastAttackers[victim.userID] = attacker.userID;
            }
            catch (Exception ex)
            {
                PrintError($"RecordCombatHit error: {ex.Message}");
            }
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player != null && info?.InitiatorPlayer is BasePlayer attacker && attacker != player)
            {
                _lastAttackers[player.userID] = attacker.userID;
            }
        }

        #endregion

        #region Commands

        [ChatCommand("check")]
        private void CmdCheck(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsAdmin(player))
            {
                SendReply(player, "<color=#FF4444>[Error]</color> Permission denied.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                SendReply(player, "<color=#FFAA00>[CHECK]</color> Использование: <color=#00FF88>/check <номер репорта / ник / SteamID></color> или <color=#00FF88>/uncheck <ник/SteamID></color>");
                return;
            }

            BasePlayer target = null;

            // 1. Check if argument is a numeric report ID (e.g. /check 1)
            if (int.TryParse(args[0], out int repId))
            {
                ReportEntry rep = _recentReports.FirstOrDefault(r => r.Id == repId);
                if (rep != null)
                {
                    target = BasePlayer.FindByID(rep.SuspectId) ?? BasePlayer.FindSleeping(rep.SuspectId);
                    if (target == null)
                    {
                        SendReply(player, $"<color=#FF4444>[CHECK]</color> Подозреваемый по жалобе #{repId} (<color=#FFFF00>{rep.SuspectName}</color>, ID: {rep.SuspectId}) не найден на сервере.");
                        return;
                    }
                }
            }

            // 2. Fallback to lookup by player name or SteamID
            if (target == null)
            {
                target = FindPlayer(args[0]);
            }

            if (target == null)
            {
                SendReply(player, $"<color=#FF4444>[CHECK]</color> Игрок или жалоба не найдены: {args[0]}");
                return;
            }

            if (!target.IsConnected)
            {
                SendReply(player, $"<color=#FFAA00>[CHECK]</color> Игрок <color=#FFFF00>{target.displayName}</color> ({target.userID}) оффлайн / спит.");
                return;
            }

            if (target.IsAdmin && target.userID != player.userID)
            {
                SendReply(player, "<color=#FF4444>[CHECK]</color> Нельзя вызывать других администраторов на проверку.");
                return;
            }

            if (_activeChecks.ContainsKey(target.userID))
            {
                SendReply(player, $"<color=#FFAA00>[CHECK]</color> Игрок {target.displayName} уже находится на проверке!");
                return;
            }

            StartCheck(player, target);
        }

        [ChatCommand("uncheck")]
        private void CmdUncheck(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsAdmin(player)) return;
            if (args == null || args.Length == 0)
            {
                SendReply(player, "<color=#FFAA00>[CHECK]</color> Usage: /uncheck <player/steamid>");
                return;
            }

            BasePlayer target = FindPlayer(args[0]);
            ulong targetId = target != null ? target.userID : (ulong.TryParse(args[0], out ulong sid) ? sid : 0);
            if (targetId == 0 || !_activeChecks.ContainsKey(targetId))
            {
                SendReply(player, $"<color=#FF4444>[CHECK]</color> No active check found for: {args[0]}");
                return;
            }

            PassCheck(targetId, player);
        }

        [ChatCommand("report")]
        private void CmdReport(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args != null && args.Length > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase) && IsAdmin(player))
            {
                Plugin adminMenu = plugins.Find("AdminMenu");
                if (adminMenu != null)
                {
                    CloseAllUi(player);
                    adminMenu.Call("OpenAdminMenuForPlayer", player, "reports");
                    return;
                }
                OpenAdminPanel(player);
                return;
            }

            if (args != null && args.Length >= 2)
            {
                ProcessDirectReport(player, args[0], string.Join(" ", args.Skip(1)));
                return;
            }

            OpenPlayerReportUI(player);
        }

        [ChatCommand("reports")]
        private void CmdReports(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsAdmin(player))
            {
                SendReply(player, "<color=#FF4444>[Error]</color> You do not have permission to access the admin panel.");
                return;
            }

            Plugin adminMenu = plugins.Find("AdminMenu");
            if (adminMenu != null)
            {
                CloseAllUi(player);
                adminMenu.Call("OpenAdminMenuForPlayer", player, "reports");
                return;
            }

            OpenAdminPanel(player);
        }

        [ChatCommand("unspec")]
        private void CmdUnspec(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsAdmin(player)) return;

            if (_spectatingAdmins.Contains(player.userID))
            {
                StopSpectating(player);
            }
            else
            {
                SendReply(player, "<color=#FFAA00>[Spectate]</color> You are not currently spectating any player.");
            }
        }

        #endregion

        #region Cheat Check System (AnyDesk / Discord)

        public bool StartCheck(BasePlayer admin, BasePlayer suspect)
        {
            if (suspect == null || !suspect.IsConnected) return false;
            if (suspect.IsAdmin && (admin == null || admin.userID != suspect.userID)) return false;

            if (_activeChecks.ContainsKey(suspect.userID)) return false;

            CheckSession session = new CheckSession
            {
                SuspectId = suspect.userID,
                SuspectName = suspect.displayName,
                AdminId = admin?.userID ?? 0,
                AdminName = admin?.displayName ?? "Console",
                StartTime = DateTime.UtcNow,
                RemainingSeconds = _config.CheckDurationSeconds,
                FreezePosition = suspect.transform.position
            };

            _activeChecks[suspect.userID] = session;
            _frozenPlayers.Add(suspect.userID);

            // Call AdminMenu if present to freeze & godmode suspect
            Plugin adminMenu = plugins.Find("AdminMenu");
            if (adminMenu != null)
            {
                adminMenu.Call("FreezePlayer", suspect);
            }

            Effect.server.Run(_config.AdminAlertSound, suspect.transform.position);

            RenderCheckScreen(suspect, session);

            if (admin != null && admin.IsConnected)
            {
                RenderAdminCheckWidget(admin, session);
                SendReply(admin, $"<color=#00FF88>[CHECK]</color> Вызов на проверку игрока <color=#FFFF00>{suspect.displayName}</color> запущен! Время: {_config.CheckDurationSeconds / 60} мин.");
            }

            PrintToChat($"<color=#FFAA00>[ПРОВЕРКА]</color> Игрок <color=#FFFF00>{suspect.displayName}</color> вызван на проверку на читы администратором <color=#00FFAA>{session.AdminName}</color>.");

            session.SessionTimer = timer.Every(1.0f, () => UpdateCheckTick(session));

            SendCheckAlertToDiscord(session, "START");
            return true;
        }

        public void PassCheck(ulong suspectId, BasePlayer admin = null)
        {
            if (!_activeChecks.TryGetValue(suspectId, out CheckSession session)) return;

            session.SessionTimer?.Destroy();
            _activeChecks.Remove(suspectId);
            _frozenPlayers.Remove(suspectId);

            BasePlayer suspect = BasePlayer.FindByID(suspectId);
            if (suspect != null)
            {
                CuiHelper.DestroyUi(suspect, PanelCheckScreen);

                Plugin adminMenu = plugins.Find("AdminMenu");
                if (adminMenu != null)
                {
                    adminMenu.Call("UnfreezePlayer", suspect);
                    adminMenu.Call("AddStaffNote", suspect.userID, admin?.displayName ?? "Staff", "Проверен — чист (Passed check)", "CLEAN");
                }

                SendReply(suspect, "<color=#00FF88>[ПРОВЕРКА]</color> Проверка успешно пройдена! Спасибо за сотрудничество, приятной игры.");
            }

            if (admin != null)
            {
                CuiHelper.DestroyUi(admin, PanelCheckAdmin);
                SendReply(admin, $"<color=#00FF88>[CHECK]</color> Игрок <color=#FFFF00>{session.SuspectName}</color> успешно прошел проверку и оправдан.");
            }

            PrintToChat($"<color=#00FF88>[ПРОВЕРКА]</color> Игрок <color=#FFFF00>{session.SuspectName}</color> успешно <color=#00FF88>ПРОШЕЛ ПРОВЕРКУ</color> на читы!");
            SendCheckAlertToDiscord(session, "PASS");
        }

        public void FailCheck(ulong suspectId, string reason, BasePlayer admin = null)
        {
            if (!_activeChecks.TryGetValue(suspectId, out CheckSession session)) return;

            session.SessionTimer?.Destroy();
            _activeChecks.Remove(suspectId);
            _frozenPlayers.Remove(suspectId);

            BasePlayer suspect = BasePlayer.FindByID(suspectId);
            string name = suspect != null ? suspect.displayName : session.SuspectName;

            if (IsAdminOrStaff(suspectId))
            {
                if (suspect != null && suspect.IsConnected)
                {
                    CuiHelper.DestroyUi(suspect, PanelCheckScreen);
                    SendReply(suspect, "<color=#00FF88>[CHECK]</color> Время проверки истекло. <color=#FFFF00>Иммунитет администратора активен</color> (автобан и кик отменены).");
                }

                if (admin != null && admin.IsConnected)
                {
                    CuiHelper.DestroyUi(admin, PanelCheckAdmin);
                    SendReply(admin, $"<color=#00FF88>[CHECK]</color> Проверка игрока <color=#FFFF00>{name}</color> завершена без бана (иммунитет администратора).");
                }
                return;
            }

            ServerUsers.Set(suspectId, ServerUsers.UserGroup.Banned, name, $"Обнаружены читы: {reason}");
            ServerUsers.Save();

            if (suspect != null && suspect.IsConnected)
            {
                CuiHelper.DestroyUi(suspect, PanelCheckScreen);
                suspect.Kick($"Забанен за читы: {reason}");
            }

            if (admin != null)
            {
                CuiHelper.DestroyUi(admin, PanelCheckAdmin);
                SendReply(admin, $"<color=#FF4444>[CHECK]</color> Игрок <color=#FFFF00>{name}</color> забанен с причиной: {reason}.");
            }

            PrintToChat($"<color=#FF2222>[АНТИЧИТ]</color> Игрок <color=#FFFF00>{name}</color> был <color=#FF2222>ЗАБАНЕН НАВСЕГДА</color> по результатам проверки ({reason})!");
            SendCheckAlertToDiscord(session, "BAN", reason);
        }

        public void ExtendCheck(ulong suspectId, int extraSeconds = 120)
        {
            if (_activeChecks.TryGetValue(suspectId, out CheckSession session))
            {
                session.RemainingSeconds += extraSeconds;
                BasePlayer suspect = BasePlayer.FindByID(suspectId);
                if (suspect != null) RenderCheckScreen(suspect, session);

                BasePlayer admin = BasePlayer.FindByID(session.AdminId);
                if (admin != null)
                {
                    RenderAdminCheckWidget(admin, session);
                    SendReply(admin, $"<color=#00FF88>[CHECK]</color> Таймер продлен на +{extraSeconds} секунд.");
                }
            }
        }

        private void UpdateCheckTick(CheckSession session)
        {
            session.RemainingSeconds--;
            BasePlayer suspect = BasePlayer.FindByID(session.SuspectId);
            BasePlayer admin = BasePlayer.FindByID(session.AdminId);

            if (suspect != null && suspect.IsConnected)
            {
                RenderCheckScreen(suspect, session);
            }

            if (admin != null && admin.IsConnected)
            {
                RenderAdminCheckWidget(admin, session);
            }

            if (session.RemainingSeconds <= 0)
            {
                session.SessionTimer?.Destroy();
                session.SessionTimer = null;
                FailCheck(session.SuspectId, "Время на прохождение проверки истекло", admin);
            }
        }

        private void RenderCheckScreen(BasePlayer suspect, CheckSession session)
        {
            if (suspect == null) return;
            CuiHelper.DestroyUi(suspect, PanelCheckScreen);

            CuiElementContainer container = new CuiElementContainer();

            // 1. Dark vignette background with blur
            container.Add(new CuiPanel
            {
                Image = { Color = "0.03 0.04 0.07 0.96", Material = "assets/content/ui/uibackgroundblur.mat" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = false
            }, "Overlay", PanelCheckScreen);

            // 2. Center warning card
            string card = container.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.09 0.13 0.98" },
                RectTransform = { AnchorMin = "0.18 0.15", AnchorMax = "0.82 0.85" }
            }, PanelCheckScreen, "DR_CheckCard");

            // Red top border
            container.Add(new CuiPanel
            {
                Image = { Color = "0.95 0.15 0.18 1" },
                RectTransform = { AnchorMin = "0 0.985", AnchorMax = "1 1" }
            }, card);

            // Flashing Alert Header
            container.Add(new CuiLabel
            {
                Text = { Text = "🚨 ВНИМАНИЕ! ВЫ ВЫЗВАНЫ НА ПРОВЕРКУ ЧИТОВ 🚨", FontSize = 20, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 0.25 0.25 1" },
                RectTransform = { AnchorMin = "0.05 0.87", AnchorMax = "0.95 0.96" }
            }, card);

            container.Add(new CuiLabel
            {
                Text = { Text = "YOU HAVE BEEN SUMMONED FOR CHEAT INSPECTION", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "0.85 0.7 0.7 0.9" },
                RectTransform = { AnchorMin = "0.05 0.81", AnchorMax = "0.95 0.87" }
            }, card);

            // Timer Box
            string timerBox = container.Add(new CuiPanel
            {
                Image = { Color = "0.14 0.12 0.18 0.95" },
                RectTransform = { AnchorMin = "0.30 0.64", AnchorMax = "0.70 0.78" }
            }, card);

            int mins = Mathf.Max(0, session.RemainingSeconds / 60);
            int secs = Mathf.Max(0, session.RemainingSeconds % 60);
            string timerStr = $"{mins:D2}:{secs:D2}";
            string timerColor = session.RemainingSeconds < 60 ? "#FF2222" : "#FFCC00";

            container.Add(new CuiLabel
            {
                Text = { Text = $"⏱️ ОСТАЛОСЬ: <color={timerColor}><b>{timerStr}</b></color>", FontSize = 18, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, timerBox);

            // Instructions Box
            string instrBox = container.Add(new CuiPanel
            {
                Image = { Color = "0.06 0.07 0.11 0.9" },
                RectTransform = { AnchorMin = "0.06 0.18", AnchorMax = "0.94 0.60" }
            }, card);

            string instructions =
                "<b>ИНСТРУКЦИЯ ДЛЯ ПРОХОЖДЕНИЯ ПРОВЕРКИ:</b>\n\n" +
                $"1. Перейдите в наш Discord: <color=#00FFAA><b>{_config.DiscordInviteUrl}</b></color>\n" +
                $"2. Зайдите в голосовой канал <color=#00E5FF><b>{_config.DiscordCheckChannel}</b></color>\n" +
                "3. Подготовьте программу <b>AnyDesk</b> либо включите демонстрацию экрана.\n" +
                $"4. Модератор, вызвавший вас: <color=#FFAA00>{session.AdminName}</color>\n\n" +
                "<color=#FF4444><b>⚠️ ВНИМАНИЕ:</b> Любая попытка выхода с сервера (Disconnect / F1 kill) во время проверки</color>\n" +
                "<color=#FF2222><b>ПРИВЕДЕТ К АВТОМАТИЧЕСКОМУ ПЕРМАНЕНТНОМУ БАНУ БЕЗ ПРАВА НА РАЗБАН!</b></color>";

            container.Add(new CuiLabel
            {
                Text = { Text = instructions, FontSize = 12, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.92 0.94 0.98 1" },
                RectTransform = { AnchorMin = "0.04 0.05", AnchorMax = "0.96 0.95" }
            }, instrBox);

            // Bottom warning
            container.Add(new CuiLabel
            {
                Text = { Text = "Do NOT disconnect. Leaving the game will trigger an INSTANT PERMANENT BAN.", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 0.35 0.35 0.9" },
                RectTransform = { AnchorMin = "0.05 0.04", AnchorMax = "0.95 0.14" }
            }, card);

            CuiHelper.AddUi(suspect, container);
        }

        private void RenderAdminCheckWidget(BasePlayer admin, CheckSession session)
        {
            if (admin == null) return;
            CuiHelper.DestroyUi(admin, PanelCheckAdmin);

            CuiElementContainer container = new CuiElementContainer();

            string panel = container.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.09 0.13 0.96" },
                RectTransform = { AnchorMin = "0.74 0.76", AnchorMax = "0.99 0.98" },
                CursorEnabled = false
            }, "Hud", PanelCheckAdmin);

            // Top purple accent
            container.Add(new CuiPanel
            {
                Image = { Color = "0.66 0.33 0.97 1" },
                RectTransform = { AnchorMin = "0 0.96", AnchorMax = "1 1" }
            }, panel);

            int mins = Mathf.Max(0, session.RemainingSeconds / 60);
            int secs = Mathf.Max(0, session.RemainingSeconds % 60);
            string timerStr = $"{mins:D2}:{secs:D2}";

            container.Add(new CuiLabel
            {
                Text = { Text = $"🛡️ <b>ПРОВЕРКА ИГРОКА</b>\n<color=#00FFAA>{session.SuspectName}</color> ({session.SuspectId})\nТаймер: <color=#FFDD44><b>{timerStr}</b></color>", FontSize = 11, Align = TextAnchor.UpperLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.45", AnchorMax = "0.95 0.92" }
            }, panel);

            // Pass button (Green)
            container.Add(new CuiButton
            {
                Button = { Color = "0.15 0.65 0.38 0.95", Command = $"dr.ui check_pass {session.SuspectId}" },
                RectTransform = { AnchorMin = "0.05 0.08", AnchorMax = "0.33 0.40" },
                Text = { Text = "✓ Чист", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, panel);

            // +2 min button (Blue)
            container.Add(new CuiButton
            {
                Button = { Color = "0.18 0.50 0.85 0.95", Command = $"dr.ui check_extend {session.SuspectId}" },
                RectTransform = { AnchorMin = "0.36 0.08", AnchorMax = "0.65 0.40" },
                Text = { Text = "+2 Мин", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, panel);

            // Ban button (Red)
            container.Add(new CuiButton
            {
                Button = { Color = "0.85 0.20 0.20 0.95", Command = $"dr.ui check_ban {session.SuspectId}" },
                RectTransform = { AnchorMin = "0.68 0.08", AnchorMax = "0.95 0.40" },
                Text = { Text = "🔨 Бан", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, panel);

            CuiHelper.AddUi(admin, container);
        }

        #endregion

        #region Discord Webhook Dispatcher

        private void SendReportToDiscord(ReportEntry report)
        {
            if (string.IsNullOrEmpty(_config.WebhookUrl) || _config.WebhookUrl.Contains("YOUR_WEBHOOK_URL_HERE"))
            {
                PrintWarning("Discord Webhook is not configured in oxide/config/DiscordReports.json");
                return;
            }

            DiscordMessage message = new DiscordMessage();
            if (!string.IsNullOrEmpty(_config.MentionRole))
            {
                message.Content = _config.MentionRole;
            }

            string karmaBadge = report.ReporterKarma >= 150 ? "⭐ **[ПРИОРИТЕТ]** Доверенный информатор" : (report.ReporterKarma < 50 ? "⚠️ **[НИЗКАЯ КАРМА]** Возможен спам" : $"Карма доверия: {report.ReporterKarma}");

            DiscordEmbed embed = new DiscordEmbed
            {
                Title = $"🚨 Репорт #{report.Id} • {_config.ServerName}",
                Color = report.ReporterKarma >= 150 ? 16763904 : _config.EmbedColor, // Gold or Red
                Timestamp = DateTime.UtcNow.ToString("o"),
                Footer = new DiscordFooter { Text = $"{_config.ServerName} Anti-Cheat Toolkit • /report" }
            };

            embed.Fields.Add(new DiscordField("👤 Репортер", $"{report.ReporterName}\n[`{report.ReporterId}`](https://steamcommunity.com/profiles/{report.ReporterId})\n{karmaBadge}", true));

            string suspectField = report.SuspectId != 0
                ? $"{report.SuspectName}\n[`{report.SuspectId}`](https://steamcommunity.com/profiles/{report.SuspectId})"
                : report.SuspectName;
            embed.Fields.Add(new DiscordField("🎯 Подозреваемый", suspectField, true));

            embed.Fields.Add(new DiscordField("📍 Квадрат (Grid)", $"`{report.Grid}` (X: {report.Position.x:F0}, Z: {report.Position.z:F0})", true));
            embed.Fields.Add(new DiscordField("📝 Причина", $"```{report.Reason}```", false));

            if (!string.IsNullOrEmpty(report.CombatSnapshot))
            {
                embed.Fields.Add(new DiscordField("⚔️ Combat Snapshot (Попадания)", $"```{report.CombatSnapshot}```", false));
            }

            embed.Fields.Add(new DiscordField("👥 Онлайн", $"{BasePlayer.activePlayerList.Count}/{ConVar.Server.maxplayers}", true));
            embed.Fields.Add(new DiscordField("⚡ Действия админа", $"`/check {report.SuspectId}` — Вызов на проверку\n`/am` — Открыть панель управления", true));

            message.Embeds.Add(embed);

            if (report.SuspectId != 0)
            {
                embed.Fields.Add(new DiscordField("🔗 Профили игрока",
                    $"[🌐 Steam Профиль](https://steamcommunity.com/profiles/{report.SuspectId})  •  [📊 BattleMetrics](https://www.battlemetrics.com/rcon/players?filter[search]={report.SuspectId})", false));
            }

            string payload = JsonConvert.SerializeObject(message);
            webrequest.Enqueue(_config.WebhookUrl, payload, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    PrintWarning($"Discord Webhook delivery failed! HTTP Code: {code}");
                }
            }, this, RequestMethod.POST, new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        private void SendDisconnectBanToDiscord(CheckSession session)
        {
            if (string.IsNullOrEmpty(_config.WebhookUrl) || _config.WebhookUrl.Contains("YOUR_WEBHOOK_URL_HERE")) return;

            DiscordMessage message = new DiscordMessage();
            DiscordEmbed embed = new DiscordEmbed
            {
                Title = "🚨 АВТОБАН: ЛИВ С ПРОВЕРКИ НА ЧИТЫ!",
                Color = 16711680, // Bright Red
                Timestamp = DateTime.UtcNow.ToString("o"),
                Footer = new DiscordFooter { Text = $"{_config.ServerName} Anti-Cheat Shield" }
            };

            embed.Fields.Add(new DiscordField("Подозреваемый", $"{session.SuspectName} (`{session.SuspectId}`)", true));
            embed.Fields.Add(new DiscordField("Модератор проверки", session.AdminName, true));
            embed.Fields.Add(new DiscordField("Статус", "**ЗАБАНЕН НАВСЕГДА (Permanent Ban)**\nИгрок отключился от сервера во время активного таймера проверки.", false));

            if (session.SuspectId != 0)
            {
                embed.Fields.Add(new DiscordField("🔗 Профиль Steam", $"[🌐 Перейти в Steam](https://steamcommunity.com/profiles/{session.SuspectId})", false));
            }

            message.Embeds.Add(embed);

            string payload = JsonConvert.SerializeObject(message);
            webrequest.Enqueue(_config.WebhookUrl, payload, null, this, RequestMethod.POST, new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        private void SendCheckAlertToDiscord(CheckSession session, string state, string reason = "")
        {
            if (string.IsNullOrEmpty(_config.WebhookUrl) || _config.WebhookUrl.Contains("YOUR_WEBHOOK_URL_HERE")) return;

            DiscordMessage message = new DiscordMessage();
            int color = state == "START" ? 16753920 : (state == "PASS" ? 65280 : 16711680); // Orange, Green, Red
            string title = state == "START"
                ? $"🛡️ Вызов на проверку: {session.SuspectName}"
                : (state == "PASS" ? $"✅ Проверка пройдена: {session.SuspectName}" : $"🔨 Забанен по результатам проверки: {session.SuspectName}");

            DiscordEmbed embed = new DiscordEmbed
            {
                Title = title,
                Color = color,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Footer = new DiscordFooter { Text = $"{_config.ServerName} Moderation Log" }
            };

            embed.Fields.Add(new DiscordField("Игрок", $"{session.SuspectName} (`{session.SuspectId}`)", true));
            embed.Fields.Add(new DiscordField("Модератор", session.AdminName, true));
            if (!string.IsNullOrEmpty(reason))
            {
                embed.Fields.Add(new DiscordField("Причина / Вердикт", reason, false));
            }

            message.Embeds.Add(embed);

            string payload = JsonConvert.SerializeObject(message);
            webrequest.Enqueue(_config.WebhookUrl, payload, null, this, RequestMethod.POST, new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        private void SendTestReport(BasePlayer player)
        {
            if (string.IsNullOrEmpty(_config.WebhookUrl) || _config.WebhookUrl.Contains("YOUR_WEBHOOK_URL_HERE"))
            {
                SendReply(player, "<color=#FF4444>[DISCORD]</color> Webhook URL is not configured in oxide/config/DiscordReports.json");
                return;
            }

            DiscordMessage message = new DiscordMessage
            {
                Embeds = new List<DiscordEmbed>
                {
                    new DiscordEmbed
                    {
                        Title = "✅ DiscordReports & Anti-Cheat Integration Operational!",
                        Color = 65280,
                        Timestamp = DateTime.UtcNow.ToString("o"),
                        Fields = new List<DiscordField>
                        {
                            new DiscordField("Initiator", player.displayName, true),
                            new DiscordField("Server", _config.ServerName, true),
                            new DiscordField("Features Active", "• AnyDesk Check System\n• Combat Snapshot Dispatch\n• Reporter Karma Rating\n• Interactive Components", false)
                        },
                        Footer = new DiscordFooter { Text = $"{_config.ServerName} • In-game Report System" }
                    }
                }
            };

            string payload = JsonConvert.SerializeObject(message);
            webrequest.Enqueue(_config.WebhookUrl, payload, (code, response) =>
            {
                if (code == 200 || code == 204)
                {
                    SendReply(player, "<color=#00FF88>[DISCORD]</color> Test report successfully delivered to Discord channel!");
                }
                else
                {
                    SendReply(player, $"<color=#FF4444>[DISCORD ERROR]</color> HTTP Status Code: {code}");
                }
            }, this, RequestMethod.POST, new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        #endregion

        #region Helpers

        private bool IsAdminOrStaff(ulong userId)
        {
            var user = ServerUsers.Get(userId);
            if (user != null && (user.group == ServerUsers.UserGroup.Owner || user.group == ServerUsers.UserGroup.Moderator))
                return true;

            string idStr = userId.ToString();
            if (permission.UserHasPermission(idStr, PermAdmin) || permission.UserHasGroup(idStr, "admin"))
                return true;

            BasePlayer player = BasePlayer.FindByID(userId);
            if (player != null && (player.IsAdmin || (player.net?.connection != null && player.net.connection.authLevel > 0)))
                return true;

            Plugin adminMenu = plugins.Find("AdminMenu");
            if (adminMenu != null && player != null)
            {
                object isAdm = adminMenu.Call("IsAdmin", player);
                if (isAdm is bool b && b) return true;
            }

            return false;
        }

        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            return IsAdminOrStaff(player.userID);
        }

        private BasePlayer FindPlayer(string nameOrId)
        {
            if (string.IsNullOrEmpty(nameOrId)) return null;

            if (ulong.TryParse(nameOrId, out ulong id))
            {
                return BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
            }

            return BasePlayer.activePlayerList.FirstOrDefault(p => p.displayName.IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? BasePlayer.sleepingPlayerList.FirstOrDefault(p => p.displayName.IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void NotifyAdmins(ReportEntry report)
        {
            string audio = _config.AdminAlertSound;
            string karmaTag = report.ReporterKarma >= 150 ? "<color=#FFDD44>⭐[ПРИОРИТЕТ]</color> " : "";

            foreach (BasePlayer p in BasePlayer.activePlayerList)
            {
                if (IsAdmin(p))
                {
                    SendReply(p, $"<color=#FF4444><b>[РЕПОРТ #{report.Id}]</b></color> {karmaTag}Подозреваемый: <color=#FFFF00><b>{report.SuspectName}</b></color> (<color=#AAAAAA>{report.SuspectId}</color>)\n" +
                                 $"От: <color=#00FFAA>{report.ReporterName}</color> | Квадрат: <color=#FFFF00>{report.Grid}</color>\n" +
                                 $"Причина: <color=#FFFFFF>{report.Reason}</color>\n" +
                                 $"<color=#00E5FF>Команды: <b>/check {report.Id}</b> или <b>/check {report.SuspectId}</b> для вызова на проверку | <b>/am</b> для панели.</color>");

                    if (!string.IsNullOrEmpty(audio))
                    {
                        Effect.server.Run(audio, p.transform.position);
                    }
                }
            }
        }

        private string GetGrid(Vector3 pos)
        {
            try
            {
                float mapSize = TerrainMeta.Size.x;
                float halfSize = mapSize / 2f;
                float x = pos.x + halfSize;
                float z = halfSize - pos.z;

                const float cellSize = 146.2857f;
                int col = Mathf.FloorToInt(x / cellSize);
                int row = Mathf.FloorToInt(z / cellSize);

                string colStr = "";
                int c = col;
                while (c >= 0)
                {
                    colStr = (char)('A' + (c % 26)) + colStr;
                    c = (c / 26) - 1;
                }

                return $"{colStr}{row}";
            }
            catch
            {
                return "Unknown";
            }
        }

        private void CloseAllUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, PanelMain);
            CuiHelper.DestroyUi(player, PanelAdmin);
            CuiHelper.DestroyUi(player, PanelCheckAdmin);
        }

        private void StartSpectating(BasePlayer admin, BasePlayer target)
        {
            if (admin == null || target == null) return;
            _preSpectatePos[admin.userID] = admin.transform.position;
            _spectatingAdmins.Add(admin.userID);
            admin.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, true);
            admin.gameObject.SetLayerRecursive(10);
            admin.CancelInvoke("MetabolismUpdate");
            admin.Teleport(target.transform.position + Vector3.up * 1.5f);
            SendReply(admin, $"<color=#00FFAA>[Spectate]</color> Вы наблюдаете за: <b>{target.displayName}</b>. Введите <color=#FFFF00>/unspec</color> для выхода.");
        }

        private void StopSpectating(BasePlayer admin)
        {
            if (admin == null) return;
            _spectatingAdmins.Remove(admin.userID);
            admin.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false);
            admin.gameObject.SetLayerRecursive(17);
            if (_preSpectatePos.TryGetValue(admin.userID, out Vector3 returnPos))
            {
                admin.Teleport(returnPos);
                _preSpectatePos.Remove(admin.userID);
            }
            SendReply(admin, "<color=#00FFAA>[Spectate]</color> Режим наблюдения завершен.");
        }

        private void OpenAdminPanel(BasePlayer player)
        {
            if (player == null || !IsAdmin(player)) return;
            Plugin adminMenu = plugins.Find("AdminMenu");
            if (adminMenu != null)
            {
                CloseAllUi(player);
                adminMenu.Call("OpenAdminMenuForPlayer", player, "reports");
                return;
            }
            SendReply(player, "<color=#FFAA00>[Reports]</color> Список репортов доступен в админ-панели /am -> Жалобы.");
        }

        private void AddInputField(CuiElementContainer container, string parent, string text, int fontSize, TextAnchor align, string color, int charsLimit, string command, string anchorMin, string anchorMax)
        {
            container.Add(new CuiElement
            {
                Parent = parent,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = text ?? "",
                        FontSize = fontSize,
                        Align = align,
                        Color = color,
                        CharsLimit = charsLimit,
                        Command = command
                    },
                    new CuiRectTransformComponent { AnchorMin = anchorMin, AnchorMax = anchorMax }
                }
            });
        }

        private ReportDraft GetOrCreateDraft(ulong userId)
        {
            if (!_playerDrafts.TryGetValue(userId, out var draft))
            {
                draft = new ReportDraft();
                _playerDrafts[userId] = draft;
            }
            return draft;
        }

        private void OpenPlayerReportUI(BasePlayer player)
        {
            if (player == null) return;
            CloseAllUi(player);

            var draft = GetOrCreateDraft(player.userID);
            var karma = GetKarma(player.userID);

            // If no target selected yet, try to auto-suggest last attacker
            if (draft.TargetId == 0 && _lastAttackers.TryGetValue(player.userID, out ulong lastAttackerId))
            {
                BasePlayer lastAttacker = BasePlayer.FindByID(lastAttackerId) ?? BasePlayer.FindSleeping(lastAttackerId);
                if (lastAttacker != null && lastAttacker.userID != player.userID)
                {
                    draft.TargetId = lastAttacker.userID;
                    draft.TargetName = lastAttacker.displayName;
                }
            }

            CuiElementContainer container = new CuiElementContainer();

            // 1. Dark Blur Overlay
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.85", Material = "assets/content/ui/uibackgroundblur.mat" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", PanelMain);

            // 2. Main Dialog Frame
            string frameId = container.Add(new CuiPanel
            {
                Image = { Color = "0.06 0.07 0.10 0.98" },
                RectTransform = { AnchorMin = "0.12 0.08", AnchorMax = "0.88 0.92" }
            }, PanelMain, "DR_Frame");

            // Top Brand Line (Neon Purple)
            container.Add(new CuiPanel
            {
                Image = { Color = "0.66 0.33 0.97 1.0" },
                RectTransform = { AnchorMin = "0 0.992", AnchorMax = "1 1" }
            }, frameId);

            // Header Container
            string headerId = container.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.09 0.14 0.98" },
                RectTransform = { AnchorMin = "0 0.88", AnchorMax = "1 0.992" }
            }, frameId);

            container.Add(new CuiLabel
            {
                Text = { Text = $"🛡️ {_config.ServerName} • СИСТЕМА ЖАЛОБ И МОДЕРАЦИИ", FontSize = 14, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.02 0.45", AnchorMax = "0.85 0.92" }
            }, headerId);

            string karmaTag = karma.Karma >= 150 ? "<color=#00FFAA>⭐ Высокое (Доверенный)</color>" : (karma.Karma < 50 ? "<color=#FF6666>⚠️ Низкое</color>" : "<color=#FFDD44>Обычное</color>");
            container.Add(new CuiLabel
            {
                Text = { Text = $"Выберите игрока и причину жалобы. Ваш рейтинг доверия: <b>{karma.Karma}</b> ({karmaTag})", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.7 0.75 0.85 0.9" },
                RectTransform = { AnchorMin = "0.02 0.10", AnchorMax = "0.85 0.45" }
            }, headerId);

            // Close Button
            container.Add(new CuiButton
            {
                Button = { Color = "0.85 0.20 0.25 0.95", Command = "dr.ui close" },
                RectTransform = { AnchorMin = "0.955 0.20", AnchorMax = "0.988 0.80" },
                Text = { Text = "✕", FontSize = 13, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, headerId);

            // Body Split: Left Column (Players) & Right Column (Categories + Input)
            // LEFT COLUMN (0.02 to 0.49)
            string leftCol = container.Add(new CuiPanel
            {
                Image = { Color = "0.07 0.08 0.12 0.95" },
                RectTransform = { AnchorMin = "0.02 0.03", AnchorMax = "0.49 0.86" }
            }, frameId);

            container.Add(new CuiLabel
            {
                Text = { Text = "1. ВЫБЕРИТЕ НАРУШИТЕЛЯ", FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.66 0.33 0.97 1" },
                RectTransform = { AnchorMin = "0.04 0.93", AnchorMax = "0.96 0.98" }
            }, leftCol);

            // Last Attacker Button
            bool hasAttacker = _lastAttackers.TryGetValue(player.userID, out ulong lastAtkId);
            BasePlayer lastAtkPlayer = hasAttacker ? (BasePlayer.FindByID(lastAtkId) ?? BasePlayer.FindSleeping(lastAtkId)) : null;
            string lastAtkName = lastAtkPlayer != null ? lastAtkPlayer.displayName : (hasAttacker ? lastAtkId.ToString() : "Нет данных");
            bool isLastSelected = hasAttacker && draft.TargetId == lastAtkId;

            container.Add(new CuiButton
            {
                Button = { Color = isLastSelected ? "0.45 0.18 0.72 0.95" : (hasAttacker ? "0.22 0.16 0.30 0.95" : "0.12 0.13 0.18 0.60"), Command = hasAttacker ? $"dr.ui target {lastAtkId}" : "" },
                RectTransform = { AnchorMin = "0.04 0.84", AnchorMax = "0.96 0.91" },
                Text = { Text = $"⚔️ Последний обидчик: {lastAtkName}", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = hasAttacker ? "1 0.85 0.2 1" : "0.5 0.5 0.6 1" }
            }, leftCol);

            // Active Online Players List
            var activePlayers = BasePlayer.activePlayerList.Where(p => p != null && p.userID != player.userID && !p.IsAdmin).ToList();
            int pageSize = 6;
            int maxPages = Mathf.Max(1, (activePlayers.Count + pageSize - 1) / pageSize);
            if (draft.Page >= maxPages) draft.Page = maxPages - 1;

            var pagePlayers = activePlayers.Skip(draft.Page * pageSize).Take(pageSize).ToList();

            float rowH = 0.09f;
            float startY = 0.74f;

            if (pagePlayers.Count == 0)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "Других игроков онлайн не найдено.", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.6 0.6 0.7 0.8" },
                    RectTransform = { AnchorMin = "0.04 0.4", AnchorMax = "0.96 0.6" }
                }, leftCol);
            }
            else
            {
                for (int i = 0; i < pagePlayers.Count; i++)
                {
                    var p = pagePlayers[i];
                    bool isSelected = draft.TargetId == p.userID;
                    float yMin = startY - (i * (rowH + 0.015f));
                    float yMax = yMin + rowH;

                    string cardBg = isSelected ? "0.45 0.18 0.72 0.95" : "0.10 0.11 0.16 0.95";
                    string btnId = container.Add(new CuiButton
                    {
                        Button = { Color = cardBg, Command = $"dr.ui target {p.userID}" },
                        RectTransform = { AnchorMin = $"0.04 {yMin}", AnchorMax = $"0.96 {yMax}" },
                        Text = { Text = "" }
                    }, leftCol);

                    string statusTag = isSelected ? "<color=#00FF66>✓ ВЫБРАН</color>" : "<color=#8899AA>Нажмите для выбора</color>";
                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"👤 <b>{p.displayName}</b>\n<color=#AAAAAA>{p.userID}</color> • {statusTag}", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                        RectTransform = { AnchorMin = "0.04 0", AnchorMax = "0.96 1" }
                    }, btnId);
                }
            }

            // Pagination Controls at Bottom of Left Column
            int prevPage = Mathf.Max(0, draft.Page - 1);
            int nextPage = Mathf.Min(maxPages - 1, draft.Page + 1);

            container.Add(new CuiButton
            {
                Button = { Color = draft.Page > 0 ? "0.20 0.16 0.28 0.95" : "0.10 0.11 0.16 0.5", Command = draft.Page > 0 ? $"dr.ui page {prevPage}" : "" },
                RectTransform = { AnchorMin = "0.04 0.03", AnchorMax = "0.34 0.10" },
                Text = { Text = "◀ Назад", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, leftCol);

            container.Add(new CuiLabel
            {
                Text = { Text = $"Стр. {draft.Page + 1} / {maxPages}", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.7 0.75 0.85 1" },
                RectTransform = { AnchorMin = "0.36 0.03", AnchorMax = "0.64 0.10" }
            }, leftCol);

            container.Add(new CuiButton
            {
                Button = { Color = draft.Page < maxPages - 1 ? "0.20 0.16 0.28 0.95" : "0.10 0.11 0.16 0.5", Command = draft.Page < maxPages - 1 ? $"dr.ui page {nextPage}" : "" },
                RectTransform = { AnchorMin = "0.66 0.03", AnchorMax = "0.96 0.10" },
                Text = { Text = "Вперед ▶", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, leftCol);

            // RIGHT COLUMN (0.51 to 0.98)
            string rightCol = container.Add(new CuiPanel
            {
                Image = { Color = "0.07 0.08 0.12 0.95" },
                RectTransform = { AnchorMin = "0.51 0.03", AnchorMax = "0.98 0.86" }
            }, frameId);

            // Selected Suspect Status Box
            string targetBox = container.Add(new CuiPanel
            {
                Image = { Color = draft.TargetId != 0 ? "0.12 0.15 0.22 0.95" : "0.14 0.08 0.10 0.95" },
                RectTransform = { AnchorMin = "0.04 0.88", AnchorMax = "0.96 0.98" }
            }, rightCol);

            string targetSummary = draft.TargetId != 0
                ? $"🎯 ПОДОЗРЕВАЕМЫЙ: <color=#00FFAA><b>{draft.TargetName}</b></color> (<color=#AAAAAA>{draft.TargetId}</color>)"
                : "<color=#FF5555>⚠️ НАРУШИТЕЛЬ НЕ ВЫБРАН (Выберите игрока в списке слева)</color>";

            container.Add(new CuiLabel
            {
                Text = { Text = targetSummary, FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.98 1" }
            }, targetBox);

            // Category Selection Header
            container.Add(new CuiLabel
            {
                Text = { Text = "2. КАТЕГОРИЯ НАРУШЕНИЯ", FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.66 0.33 0.97 1" },
                RectTransform = { AnchorMin = "0.04 0.82", AnchorMax = "0.96 0.87" }
            }, rightCol);

            string[] cats = new[]
            {
                "🎯 Аимбот / Auto-Aim",
                "⚡ Макросы / Анти-отдача",
                "👁️ ВХ / WallHack / ESP",
                "🏃 Спидхак / FlyHack",
                "🤬 Оскорбления / Чат",
                "❓ Другое нарушение"
            };

            // 2 cols x 3 rows grid
            for (int c = 0; c < cats.Length; c++)
            {
                int row = c / 2;
                int col = c % 2;
                float xMin = col == 0 ? 0.04f : 0.52f;
                float xMax = col == 0 ? 0.48f : 0.96f;
                float yMin = 0.70f - (row * 0.09f);
                float yMax = yMin + 0.075f;

                bool isCatActive = draft.CategoryIndex == c;
                string catBg = isCatActive ? "0.45 0.18 0.72 0.98" : "0.10 0.11 0.16 0.95";
                string catTextColor = isCatActive ? "1 1 1 1" : "0.80 0.84 0.92 1";

                container.Add(new CuiButton
                {
                    Button = { Color = catBg, Command = $"dr.ui category {c}" },
                    RectTransform = { AnchorMin = $"{xMin} {yMin}", AnchorMax = $"{xMax} {yMax}" },
                    Text = { Text = cats[c], FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = catTextColor }
                }, rightCol);
            }

            // Comment Box Header
            container.Add(new CuiLabel
            {
                Text = { Text = "3. КОММЕНТАРИЙ (Укажите детали / время)", FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.66 0.33 0.97 1" },
                RectTransform = { AnchorMin = "0.04 0.38", AnchorMax = "0.96 0.43" }
            }, rightCol);

            string inputWell = container.Add(new CuiPanel
            {
                Image = { Color = "0.04 0.05 0.08 0.98" },
                RectTransform = { AnchorMin = "0.04 0.24", AnchorMax = "0.96 0.37" }
            }, rightCol);

            AddInputField(container, inputWell, draft.Comment, 11, TextAnchor.UpperLeft, "1 1 1 1", 120, "dr.ui comment ", "0.03 0.1", "0.97 0.9");

            // Telemetry & Snapshot Badge
            string grid = GetGrid(player.transform.position);
            container.Add(new CuiLabel
            {
                Text = { Text = $"📍 Сектор: <color=#FFFF00>{grid}</color> | ⚔️ CombatLog: <color=#00FF88>Прикреплен автоматически</color>", FontSize = 9, Align = TextAnchor.MiddleLeft, Color = "0.65 0.7 0.8 0.9" },
                RectTransform = { AnchorMin = "0.04 0.16", AnchorMax = "0.96 0.22" }
            }, rightCol);

            // Big Submit Button
            container.Add(new CuiButton
            {
                Button = { Color = "0.15 0.85 0.45 0.95", Command = "dr.ui send" },
                RectTransform = { AnchorMin = "0.04 0.03", AnchorMax = "0.96 0.13" },
                Text = { Text = "🚀 ОТПРАВИТЬ РЕПОРТ АДМИНИСТРАЦИИ И В DISCORD", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, rightCol);

            CuiHelper.AddUi(player, container);
        }

        private void SubmitReportDraft(BasePlayer player)
        {
            if (player == null) return;
            var draft = GetOrCreateDraft(player.userID);

            if (draft.TargetId == 0)
            {
                SendReply(player, "<color=#FF4444>[Репорт]</color> Сначала выберите подозреваемого игрока из списка слева!");
                return;
            }

            if (draft.TargetId == player.userID)
            {
                SendReply(player, "<color=#FF4444>[Репорт]</color> Вы не можете отправить репорт на самого себя.");
                return;
            }

            var karma = GetKarma(player.userID);
            int baseCooldown = _config.CooldownSeconds;
            int currentCooldown = baseCooldown * Mathf.Max(1, karma.CooldownMultiplier);

            if (_cooldowns.TryGetValue(player.userID, out DateTime cdUntil) && DateTime.UtcNow < cdUntil)
            {
                int remaining = (int)(cdUntil - DateTime.UtcNow).TotalSeconds;
                SendReply(player, $"<color=#FF4444>[Репорт]</color> Подождите еще <color=#FFFF00>{remaining} сек.</color> перед отправкой следующего репорта.");
                return;
            }

            string[] categories = new[]
            {
                "Аимбот / WallHack",
                "Макросы / Скрипты",
                "Оскорбления / Чат",
                "Гриферство / Блокировка",
                "Подозрительная игра",
                "Другое нарушение"
            };

            string categoryStr = (draft.CategoryIndex >= 0 && draft.CategoryIndex < categories.Length)
                ? categories[draft.CategoryIndex]
                : "Подозрительная игра";

            string fullReason = !string.IsNullOrWhiteSpace(draft.Comment)
                ? $"[{categoryStr}] {draft.Comment}"
                : $"[{categoryStr}]";

            _cooldowns[player.userID] = DateTime.UtcNow.AddSeconds(currentCooldown);
            karma.TotalReports++;
            SaveKarmaData();

            string combatSnapshot = GetCombatSnapshot(draft.TargetId, player.userID);

            ReportEntry report = new ReportEntry
            {
                Id = _reportCounter++,
                ReporterId = player.userID,
                ReporterName = player.displayName,
                SuspectId = draft.TargetId,
                SuspectName = draft.TargetName,
                Reason = fullReason,
                Grid = GetGrid(player.transform.position),
                Position = player.transform.position,
                Timestamp = DateTime.UtcNow,
                IsResolved = false,
                Status = "pending",
                ReporterKarma = karma.Karma,
                CombatSnapshot = combatSnapshot
            };

            _recentReports.Insert(0, report);
            if (_recentReports.Count > 100) _recentReports.RemoveAt(_recentReports.Count - 1);

            SendReportToDiscord(report);
            NotifyAdmins(report);

            _playerDrafts.Remove(player.userID);
            CloseAllUi(player);

            SendReply(player, $"<color=#00FF66>[Репорт #{report.Id}]</color> Ваша жалоба на игрока <color=#FFFF00>{draft.TargetName}</color> успешно отправлена модераторам.");
        }

        private void ProcessDirectReport(BasePlayer reporter, string targetNameOrId, string reason)
        {
            if (reporter == null) return;

            var karma = GetKarma(reporter.userID);
            int baseCooldown = _config.CooldownSeconds;
            int currentCooldown = baseCooldown * Mathf.Max(1, karma.CooldownMultiplier);

            if (_cooldowns.TryGetValue(reporter.userID, out DateTime cdUntil) && DateTime.UtcNow < cdUntil)
            {
                int remaining = (int)(cdUntil - DateTime.UtcNow).TotalSeconds;
                SendReply(reporter, $"<color=#FF4444>[Репорт]</color> Подождите еще <color=#FFFF00>{remaining} сек.</color> перед отправкой следующего репорта.");
                return;
            }

            BasePlayer target = BasePlayer.Find(targetNameOrId);
            ulong targetId = 0;
            string targetName = targetNameOrId;

            if (target != null)
            {
                targetId = target.userID;
                targetName = target.displayName;
            }
            else if (ulong.TryParse(targetNameOrId, out ulong parsedId))
            {
                targetId = parsedId;
            }

            if (targetId == reporter.userID)
            {
                SendReply(reporter, "<color=#FF4444>[Репорт]</color> Вы не можете отправить репорт на самого себя.");
                return;
            }

            _cooldowns[reporter.userID] = DateTime.UtcNow.AddSeconds(currentCooldown);
            karma.TotalReports++;
            SaveKarmaData();

            string combatSnapshot = targetId != 0 ? GetCombatSnapshot(targetId, reporter.userID) : "Нет данных боя.";

            ReportEntry report = new ReportEntry
            {
                Id = _reportCounter++,
                ReporterId = reporter.userID,
                ReporterName = reporter.displayName,
                SuspectId = targetId,
                SuspectName = targetName,
                Reason = string.IsNullOrWhiteSpace(reason) ? "Подозрение на нечестную игру" : reason,
                Grid = GetGrid(reporter.transform.position),
                Position = reporter.transform.position,
                Timestamp = DateTime.UtcNow,
                IsResolved = false,
                Status = "pending",
                ReporterKarma = karma.Karma,
                CombatSnapshot = combatSnapshot
            };

            _recentReports.Insert(0, report);
            if (_recentReports.Count > 100) _recentReports.RemoveAt(_recentReports.Count - 1);

            SendReportToDiscord(report);
            NotifyAdmins(report);

            SendReply(reporter, $"<color=#00FF66>[Репорт #{report.Id}]</color> Ваша жалоба на игрока <color=#FFFF00>{targetName}</color> успешно отправлена модераторам.");
        }

        [ConsoleCommand("dr.ui")]
        private void CmdDrUi(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null) return;

            string action = arg.GetString(0);
            switch (action.ToLower())
            {
                case "close":
                    CloseAllUi(player);
                    break;

                case "target":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong targetId))
                    {
                        var draft = GetOrCreateDraft(player.userID);
                        draft.TargetId = targetId;
                        var tgt = BasePlayer.FindByID(targetId) ?? BasePlayer.FindSleeping(targetId);
                        draft.TargetName = tgt != null ? tgt.displayName : targetId.ToString();
                        OpenPlayerReportUI(player);
                    }
                    break;

                case "category":
                    if (arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int catIdx))
                    {
                        var draft = GetOrCreateDraft(player.userID);
                        draft.CategoryIndex = Mathf.Clamp(catIdx, 0, 5);
                        OpenPlayerReportUI(player);
                    }
                    break;

                case "page":
                    if (arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int pageNum))
                    {
                        var draft = GetOrCreateDraft(player.userID);
                        draft.Page = Mathf.Max(0, pageNum);
                        OpenPlayerReportUI(player);
                    }
                    break;

                case "comment":
                    {
                        var draft = GetOrCreateDraft(player.userID);
                        string commentText = arg.Args != null && arg.Args.Length > 1 ? string.Join(" ", arg.Args.Skip(1)) : "";
                        draft.Comment = commentText;
                        OpenPlayerReportUI(player);
                    }
                    break;

                case "send":
                    SubmitReportDraft(player);
                    break;

                case "check_pass":
                    if (IsAdmin(player))
                    {
                        ulong passId = arg.GetULong(1);
                        PassCheck(passId, player);
                    }
                    break;

                case "check_extend":
                    if (IsAdmin(player))
                    {
                        ulong extendId = arg.GetULong(1);
                        ExtendCheck(extendId, 120);
                        SendReply(player, "<color=#00FFAA>[CHECK]</color> Время проверки продлено на 2 минуты.");
                    }
                    break;

                case "check_ban":
                    if (IsAdmin(player))
                    {
                        ulong banId = arg.GetULong(1);
                        FailCheck(banId, "Отказ от проверки / Чит подтвержден", player);
                    }
                    break;

                case "unspec":
                    if (IsAdmin(player))
                    {
                        StopSpectating(player);
                    }
                    break;
            }
        }

        #region Karma Management

        private PlayerKarmaRecord GetKarma(ulong steamId)
        {
            if (!_karmaDatabase.TryGetValue(steamId, out var record))
            {
                record = new PlayerKarmaRecord { PlayerName = steamId.ToString() };
                _karmaDatabase[steamId] = record;
            }
            return record;
        }

        private void RewardReporter(ulong reporterId, int amount)
        {
            var record = GetKarma(reporterId);
            record.Karma = Mathf.Clamp(record.Karma + amount, 0, 500);
            record.ConfirmedBans++;
            if (record.CooldownMultiplier > 1) record.CooldownMultiplier--;
            SaveKarmaData();

            BasePlayer player = BasePlayer.FindByID(reporterId);
            if (player != null && player.IsConnected)
            {
                SendReply(player, $"<color=#00FF66>[РЕПОРТЫ]</color> Ваш репорт подтвердился! Вы получили <color=#FFFF00>+{amount} кармы</color>. Рейтинг доверия: <b>{record.Karma}</b>.");
            }
        }

        private void PenalizeReporter(ulong reporterId, int amount)
        {
            var record = GetKarma(reporterId);
            record.Karma = Mathf.Clamp(record.Karma - amount, 0, 500);
            record.FalseReports++;
            record.CooldownMultiplier = Mathf.Clamp(record.CooldownMultiplier + 1, 1, 5);
            SaveKarmaData();

            BasePlayer player = BasePlayer.FindByID(reporterId);
            if (player != null && player.IsConnected)
            {
                SendReply(player, $"<color=#FF4444>[РЕПОРТЫ]</color> Ваш репорт признан ложным. <color=#FF8888>-{amount} кармы</color>. Рейтинг доверия: <b>{record.Karma}</b> (Кулдаун увеличен в {record.CooldownMultiplier}x).");
            }
        }

        private void LoadKarmaData()
        {
            try
            {
                _karmaDatabase = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerKarmaRecord>>("DiscordReports_Karma") ?? new Dictionary<ulong, PlayerKarmaRecord>();
            }
            catch (Exception ex)
            {
                PrintError($"Failed to load Karma data: {ex.Message}");
                _karmaDatabase = new Dictionary<ulong, PlayerKarmaRecord>();
            }
        }

        private void SaveKarmaData()
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject("DiscordReports_Karma", _karmaDatabase);
            }
            catch (Exception ex)
            {
                PrintError($"Failed to save Karma data: {ex.Message}");
            }
        }

        #endregion

        #endregion

        #region External API for AdminMenu & Other Plugins

        [HookMethod("GetReportsData")]
        public string GetReportsData()
        {
            return JsonConvert.SerializeObject(_recentReports);
        }

        [HookMethod("ResolveReport")]
        public bool ResolveReport(int reportId)
        {
            return ResolveReportWithVerdict(reportId, "resolved", "Admin");
        }

        [HookMethod("ResolveReportWithVerdict")]
        public bool ResolveReportWithVerdict(int reportId, string verdict, string moderatorName)
        {
            ReportEntry report = _recentReports.FirstOrDefault(r => r.Id == reportId);
            if (report != null)
            {
                report.IsResolved = true;
                report.Status = verdict;
                report.ModeratorName = moderatorName;

                if (verdict.Equals("ban", StringComparison.OrdinalIgnoreCase) || verdict.Equals("banned", StringComparison.OrdinalIgnoreCase))
                {
                    RewardReporter(report.ReporterId, 25);
                }
                else if (verdict.Equals("false_report", StringComparison.OrdinalIgnoreCase) || verdict.Equals("spam", StringComparison.OrdinalIgnoreCase))
                {
                    PenalizeReporter(report.ReporterId, 35);
                }
                return true;
            }
            return false;
        }

        [HookMethod("DeleteReport")]
        public bool DeleteReport(int reportId)
        {
            int removed = _recentReports.RemoveAll(r => r.Id == reportId);
            return removed > 0;
        }

        [HookMethod("StartPlayerCheck")]
        public bool StartPlayerCheck(BasePlayer admin, ulong suspectId)
        {
            BasePlayer target = BasePlayer.FindByID(suspectId) ?? BasePlayer.FindSleeping(suspectId);
            if (target == null)
            {
                if (admin != null) SendReply(admin, $"<color=#FF4444>[CHECK]</color> Игрок с ID {suspectId} не найден на сервере.");
                return false;
            }
            if (!target.IsConnected)
            {
                if (admin != null) SendReply(admin, $"<color=#FFAA00>[CHECK]</color> Игрок <color=#FFFF00>{target.displayName}</color> ({suspectId}) оффлайн / спит.");
                return false;
            }
            return StartCheck(admin, target);
        }

        [HookMethod("PassPlayerCheck")]
        public bool PassPlayerCheck(ulong suspectId, BasePlayer admin)
        {
            PassCheck(suspectId, admin);
            return true;
        }

        [HookMethod("FailPlayerCheck")]
        public bool FailPlayerCheck(ulong suspectId, string reason, BasePlayer admin)
        {
            FailCheck(suspectId, reason, admin);
            return true;
        }

        [HookMethod("IsPlayerUnderCheck")]
        public bool IsPlayerUnderCheck(ulong suspectId)
        {
            return _activeChecks.ContainsKey(suspectId);
        }

        [HookMethod("GetPlayerKarma")]
        public int GetPlayerKarma(ulong steamId)
        {
            return GetKarma(steamId).Karma;
        }

        [HookMethod("GetCombatSnapshot")]
        public string GetCombatSnapshot(ulong attackerId, ulong victimId)
        {
            if (!_playerCombatLogs.TryGetValue(attackerId, out var hits) || hits.Count == 0)
            {
                return "Нет недавних попаданий в журнале.";
            }

            var matchingHits = hits.Where(h => victimId == 0 || h.VictimId == victimId).Take(5).ToList();
            if (matchingHits.Count == 0)
            {
                matchingHits = hits.Take(5).ToList();
            }

            List<string> lines = new List<string>();
            int hsCount = 0;
            int total100m = 0;
            int hs100m = 0;

            for (int i = 0; i < matchingHits.Count; i++)
            {
                var h = matchingHits[i];
                int secondsAgo = Mathf.Max(0, (int)(DateTime.UtcNow - h.Timestamp).TotalSeconds);
                string boneTag = h.IsHeadshot ? "🎯 HEAD" : (h.BoneName.ToLower().Contains("chest") ? "🫁 CHEST" : "🦵 BODY");
                lines.Add($"{i + 1}. {h.Weapon} • {h.Distance:F0}m • {boneTag} ({h.Damage:F1} dmg) • {secondsAgo}s назад");

                if (h.IsHeadshot) hsCount++;
                if (h.Distance >= 100f)
                {
                    total100m++;
                    if (h.IsHeadshot) hs100m++;
                }
            }

            if (total100m >= 2 && ((float)hs100m / total100m) >= 0.60f)
            {
                lines.Add($"⚠️ ПОДОЗРЕНИЕ НА AIM ({hs100m}/{total100m} HS на дистанции 100м+)");
            }

            return string.Join("\n", lines);
        }

        #endregion
    }
}
