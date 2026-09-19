using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using Network;

namespace Oxide.Plugins
{
    [Info("AdminMenu", "LEVRO", "1.3.0")]
    [Description("Rustalgia Server Control Center, Anti-Cheat Toolkit, Vanish 2.0 & In-Game Configurator")]
    public class AdminMenu : RustPlugin
    {
        #region Fields & Constants

        private PluginConfig _config;

        private const string PermMaster = "adminmenu.master";
        private const string PermUse = "adminmenu.use";

        // CUI Identifiers
        private const string PanelOverlay = "AM_Overlay";
        private const string PanelWindow = "AM_Window";
        private const string PanelSidebar = "AM_Sidebar";
        private const string PanelContent = "AM_Content";
        private const string PanelModal = "AM_Modal";
        private const string PanelVanishHUD = "AM_VanishHUD";

        // Theme Palette
        private const string ColBg = "0.06 0.07 0.10 0.98";
        private const string ColHeader = "0.08 0.09 0.13 0.98";
        private const string ColSidebar = "0.07 0.08 0.11 0.98";
        private const string ColDivider = "0.20 0.15 0.30 0.70";
        private const string ColCardBg = "0.10 0.11 0.16 0.94";
        private const string ColCardInner = "0.07 0.08 0.12 0.95";
        private const string ColNeonPurple = "0.66 0.33 0.97 1.0";
        private const string ColDarkPurple = "0.45 0.18 0.72 0.95";
        private const string ColMutedPurple = "0.25 0.16 0.38 0.85";
        private const string ColSuccess = "0.15 0.85 0.45 0.95";
        private const string ColDanger = "0.85 0.22 0.25 0.95";
        private const string ColInfo = "0.18 0.58 0.95 0.95";
        private const string ColWarning = "0.95 0.65 0.15 0.95";

        // Branding Storage
        private uint _logoFileId = 0;
        private uint _bannerFileId = 0;

        // Runtime states
        private readonly Dictionary<ulong, AdminSession> _sessions = new Dictionary<ulong, AdminSession>();
        private readonly HashSet<ulong> _godmodePlayers = new HashSet<ulong>();
        private readonly HashSet<ulong> _frozenPlayers = new HashSet<ulong>();
        private readonly Dictionary<ulong, Vector3> _frozenPositions = new Dictionary<ulong, Vector3>();
        private readonly HashSet<ulong> _mutedPlayers = new HashSet<ulong>();
        private readonly HashSet<ulong> _spectatingAdmins = new HashSet<ulong>();
        private readonly Dictionary<ulong, Vector3> _spectatePositions = new Dictionary<ulong, Vector3>();
        private Timer _freezeTimer = null;

        // Vanish
        private readonly HashSet<ulong> _vanishedPlayers = new HashSet<ulong>();

        // CombatLog Ring Buffers: Attacker SteamID -> List of Hits
        private readonly Dictionary<ulong, List<CombatHitRecord>> _combatHits = new Dictionary<ulong, List<CombatHitRecord>>();

        // Staff Notes Database: Target SteamID -> List of Notes
        private Dictionary<ulong, List<StaffNote>> _staffNotes = new Dictionary<ulong, List<StaffNote>>();

        // Dynamically Scanned Monument Bookmarks
        private readonly List<MonumentEntry> _monumentsList = new List<MonumentEntry>();

        #endregion

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty(PropertyName = "Server Display Name")]
            public string ServerName { get; set; } = "RUSTALGIA";

            [JsonProperty(PropertyName = "Server Subtitle / Slogan")]
            public string Subtitle { get; set; } = "SERVER CONTROL CENTER & ANTI-CHEAT SHIELD";

            [JsonProperty(PropertyName = "Admin Action Alert Sound")]
            public string AdminAlertSound { get; set; } = "assets/prefabs/locks/keypad/effects/lock.code.updated.prefab";

            [JsonProperty(PropertyName = "Broadcast Prefix")]
            public string BroadcastPrefix { get; set; } = "<color=#A855F7>[RUSTALGIA]</color>";

            [JsonProperty(PropertyName = "Custom Banner URL (Optional override)")]
            public string BannerUrl { get; set; } = "";

            [JsonProperty(PropertyName = "Custom Logo URL (Optional override)")]
            public string LogoUrl { get; set; } = "";

            [JsonProperty(PropertyName = "Master Admin SteamIDs (Full access to all tabs & config editor)")]
            public List<string> MasterAdminSteamIds { get; set; } = new List<string> { "76561198398099945" };

            [JsonProperty(PropertyName = "Moderator SteamIDs (Access to players, reports, self-tools)")]
            public List<string> ModeratorSteamIds { get; set; } = new List<string>();
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
            }
            catch
            {
                PrintWarning("Could not read config. Loading defaults.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Models & Session

        private enum FieldType
        {
            Boolean,
            Integer,
            Float,
            String,
            Other
        }

        private class ConfigFieldEntry
        {
            public string Key { get; set; }
            public string Value { get; set; }
            public FieldType Type { get; set; }
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

        public class StaffNote
        {
            public string Id { get; set; }
            public ulong AuthorId { get; set; }
            public string AuthorName { get; set; }
            public string Text { get; set; }
            public string Tag { get; set; } // CLEAN, MACRO, AIM, SUSPECT, TOXIC, NOTE
            public DateTime Timestamp { get; set; }
        }

        public class MonumentEntry
        {
            public string Key { get; set; }
            public string NameRU { get; set; }
            public string NameEN { get; set; }
            public string Icon { get; set; }
            public Vector3 Position { get; set; }
            public string Grid { get; set; }
            public string Tier { get; set; }
            public string RadLevel { get; set; }
        }

        private class ReportViewModel
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
            public string Status { get; set; } = "pending";
            public string ModeratorName { get; set; } = "";
            public int ReporterKarma { get; set; } = 100;
            public string CombatSnapshot { get; set; } = "";
        }

        private class AdminSession
        {
            public string ActiveTab { get; set; } = "dashboard";
            public string Language { get; set; } = "RU"; // "RU" or "EN"
            public string PlayerFilter { get; set; } = "online"; // "online" or "sleepers"
            public int Page { get; set; } = 0;
            public ulong SelectedPlayerId { get; set; } = 0;
            public string PlayerDetailSubView { get; set; } = "inventory"; // "inventory", "combatlog", "staffnotes"
            public string BroadcastDraft { get; set; } = "";
            public string SearchQuery { get; set; } = "";

            // Reports Tab State
            public int ReportsPage { get; set; } = 0;

            // Monuments Tab State
            public int MonumentsPage { get; set; } = 0;

            // Plugin Config Editor State
            public int PluginPage { get; set; } = 0;
            public string PluginSearchQuery { get; set; } = "";
            public string SelectedPlugin { get; set; } = "";
            public List<ConfigFieldEntry> ConfigFields { get; set; } = new List<ConfigFieldEntry>();
            public int EditingFieldIndex { get; set; } = -1;
            public bool IsConfigDirty { get; set; } = false;

            // Staff Note Input Draft
            public string NoteDraft { get; set; } = "";
        }

        private string T(AdminSession session, string ru, string en)
        {
            return session != null && session.Language == "EN" ? en : ru;
        }

        #endregion

        #region Oxide Hooks & Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermMaster, this);
            permission.RegisterPermission(PermUse, this);

            LoadStaffNotes();

            Puts("Rustalgia AdminMenu v1.3.0 initialized! Access with /admin or /am");
        }

        private void OnServerInitialized()
        {
            InitializeServer();
        }

        private void Loaded()
        {
            if (CommunityEntity.ServerInstance != null)
            {
                InitializeServer();
            }
        }

        private void InitializeServer()
        {
            LoadBrandingAssets();
            CleanupStuckCargoShips();
            ScanMonuments();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player.HasPlayerFlag(BasePlayer.PlayerFlags.Spectating) && !_spectatingAdmins.Contains(player.userID))
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false);
                    if (player.IsSleeping())
                    {
                        player.EndSleeping();
                    }
                    player.SendNetworkUpdateImmediate();
                    player.SendConsoleCommand("respawn");
                }
                CheckAdminStatus(player);
                player.SendConsoleCommand("gametip.hidegametip");
            }

            if (_freezeTimer == null)
            {
                _freezeTimer = timer.Every(0.1f, CheckFrozenPlayers);
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            player.SendConsoleCommand("gametip.hidegametip");

            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.Spectating) && !_spectatingAdmins.Contains(player.userID))
            {
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false);
                if (player.IsSleeping())
                {
                    player.EndSleeping();
                }
                player.SendNetworkUpdateImmediate();
                player.SendConsoleCommand("respawn");
            }

            CheckAdminStatus(player);
        }

        private void CheckAdminStatus(BasePlayer player)
        {
            if (player == null) return;

            if (IsMasterAdmin(player))
            {
                if (player.net?.connection != null && player.net.connection.authLevel < 2)
                {
                    player.net.connection.authLevel = 2;
                }
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                player.SendNetworkUpdateImmediate();

                if (!ServerUsers.Is(player.userID, ServerUsers.UserGroup.Owner))
                {
                    ServerUsers.Set(player.userID, ServerUsers.UserGroup.Owner, player.displayName, "Owner");
                    ServerUsers.Save();
                }
            }
            else if (IsAdmin(player))
            {
                if (player.net?.connection != null && player.net.connection.authLevel < 1)
                {
                    player.net.connection.authLevel = 1;
                }
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                player.SendNetworkUpdateImmediate();
            }
        }

        private void Unload()
        {
            _freezeTimer?.Destroy();
            _freezeTimer = null;

            SaveStaffNotes();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                CloseAdminMenu(player);
                CuiHelper.DestroyUi(player, PanelVanishHUD);

                if (_vanishedPlayers.Contains(player.userID))
                {
                    player.limitNetworking = false;
                    player.SendNetworkUpdateImmediate();
                }

                if (_spectatingAdmins.Contains(player.userID))
                {
                    StopSpectating(player);
                }
                if (player.IsConnected)
                {
                    player.SendConsoleCommand("gametip.hidegametip");
                }
            }

            _sessions.Clear();
            _godmodePlayers.Clear();
            _vanishedPlayers.Clear();
            _frozenPlayers.Clear();
            _frozenPositions.Clear();
            _mutedPlayers.Clear();
            _spectatingAdmins.Clear();
            _spectatePositions.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;
            player.SendConsoleCommand("gametip.hidegametip");
            _sessions.Remove(player.userID);
            _godmodePlayers.Remove(player.userID);
            _vanishedPlayers.Remove(player.userID);
            _frozenPlayers.Remove(player.userID);
            _frozenPositions.Remove(player.userID);
            _spectatingAdmins.Remove(player.userID);
            _spectatePositions.Remove(player.userID);
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null && arg.cmd != null)
            {
                string cmdName = arg.cmd.FullName;
                if (cmdName.StartsWith("inventory.give", StringComparison.OrdinalIgnoreCase) ||
                    cmdName.StartsWith("global.give", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsMasterAdmin(player))
                    {
                        if (arg.Args != null && arg.Args.Length > 0)
                        {
                            string itemArg = arg.GetString(0);
                            int amount = arg.GetInt(1, 1);
                            if (amount <= 0) amount = 1;

                            Item item = null;
                            if (int.TryParse(itemArg, out int itemId))
                            {
                                item = ItemManager.CreateByItemID(itemId, amount);
                            }
                            if (item == null)
                            {
                                item = ItemManager.CreateByName(itemArg, amount);
                            }

                            if (item != null)
                            {
                                player.GiveItem(item);
                                SendReply(player, $"<color=#00FF88>[ADMIN SILENT GIVE]</color> Выдано: <color=#FFFF00>{item.info.displayName.english} x{amount}</color>");
                                return true;
                            }
                        }
                    }
                    return null;
                }
            }
            return null;
        }

        private void CheckFrozenPlayers()
        {
            if (_frozenPlayers.Count == 0) return;

            foreach (ulong id in _frozenPlayers)
            {
                BasePlayer player = BasePlayer.FindByID(id);
                if (player != null && player.IsConnected && _frozenPositions.TryGetValue(id, out Vector3 freezePos))
                {
                    if (player.GetActiveItem() != null)
                    {
                        player.UpdateActiveItem(default(ItemId));
                    }
                    player.Teleport(freezePos);
                    player.SendNetworkUpdateImmediate();
                }
            }
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity is BasePlayer player && (_godmodePlayers.Contains(player.userID) || _vanishedPlayers.Contains(player.userID)))
            {
                info.damageTypes.Clear();
                info.HitMaterial = 0;
                info.PointStart = Vector3.zero;
                return true;
            }

            // Record combat hits for anti-cheat analytics
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

                if (!_combatHits.TryGetValue(attacker.userID, out var list))
                {
                    list = new List<CombatHitRecord>();
                    _combatHits[attacker.userID] = list;
                }

                list.Insert(0, record);
                if (list.Count > 40) list.RemoveAt(list.Count - 1);
            }
            catch (Exception ex)
            {
                PrintError($"CombatLog record error: {ex.Message}");
            }
        }

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player != null && _mutedPlayers.Contains(player.userID))
            {
                SendReply(player, "<color=#FF4444>[RESTRICTION]</color> You are currently muted by server administration.");
                return false;
            }
            return null;
        }

        private object OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null) return null;

            if (_frozenPlayers.Contains(player.userID))
            {
                input.Clear();
                if (input.current != null) input.current.buttons = 0;
                if (input.previous != null) input.previous.buttons = 0;

                if (_frozenPositions.TryGetValue(player.userID, out Vector3 freezePos))
                {
                    player.Teleport(freezePos);
                    player.SendNetworkUpdateImmediate();
                }
                return true;
            }

            if (_spectatingAdmins.Contains(player.userID))
            {
                if (input.WasJustPressed(BUTTON.FIRE_SECONDARY) || input.WasJustPressed(BUTTON.USE))
                {
                    StopSpectating(player);
                    OpenAdminMenu(player);
                }
            }
            return null;
        }

        private object OnPlayerTick(BasePlayer player, PlayerTick tick, bool wasPlayerTick)
        {
            if (player == null || _frozenPlayers.Count == 0 || !_frozenPlayers.Contains(player.userID))
                return null;

            if (_frozenPositions.TryGetValue(player.userID, out Vector3 freezePos))
            {
                tick.position = freezePos;
                float dist = Vector3.Distance(player.transform.position, freezePos);
                if (dist > 0.05f)
                {
                    player.Teleport(freezePos);
                    player.SendNetworkUpdateImmediate();
                }
            }
            return true;
        }

        private object OnActiveItemChange(BasePlayer player, Item oldItem, ItemId newItemId)
        {
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                if (newItemId != default(ItemId))
                {
                    player.UpdateActiveItem(default(ItemId));
                }
                return false;
            }
            return null;
        }

        private object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker != null && _frozenPlayers.Contains(attacker.userID))
            {
                info.damageTypes.Clear();
                info.HitEntity = null;
                info.DidHit = false;
                return true;
            }
            return null;
        }

        private object CanAttack(BasePlayer player)
        {
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                return false;
            }
            return null;
        }

        private object OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot shoot)
        {
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                return true;
            }
            return null;
        }

        private object CanEquipItem(PlayerInventory inventory, Item item, int targetSlot)
        {
            BasePlayer player = inventory?.gameObject?.GetComponent<BasePlayer>();
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                return false;
            }
            return null;
        }

        private object CanLootPlayer(BasePlayer target, BasePlayer player)
        {
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                return false;
            }
            return null;
        }

        private object CanOpenDoor(BasePlayer player, BaseLock doorLock)
        {
            if (player != null)
            {
                if (_frozenPlayers.Contains(player.userID)) return false;
                if (_vanishedPlayers.Contains(player.userID)) return true; // Silent bypass
            }
            return null;
        }

        private object CanUseLockedEntity(BasePlayer player, BaseLock baseLock)
        {
            if (player != null && _vanishedPlayers.Contains(player.userID))
            {
                return true; // Bypass key/code in Vanish
            }
            return null;
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player != null && _vanishedPlayers.Contains(player.userID))
            {
                // Vanished admin looting is silent
            }
        }

        private object CanBeTargeted(BasePlayer player, AutoTurret turret)
        {
            if (player != null && _vanishedPlayers.Contains(player.userID)) return false;
            return null;
        }

        private object CanBeTargeted(BasePlayer player, FlameTurret turret)
        {
            if (player != null && _vanishedPlayers.Contains(player.userID)) return false;
            return null;
        }

        private object CanBeTargeted(BasePlayer player, SamSite sam)
        {
            if (player != null && _vanishedPlayers.Contains(player.userID)) return false;
            return null;
        }

        private object CanBeTargeted(BasePlayer player, GunTrap trap)
        {
            if (player != null && _vanishedPlayers.Contains(player.userID)) return false;
            return null;
        }

        private object CanCraft(ItemCrafter crafter, ItemBlueprint bp, int amount)
        {
            BasePlayer player = crafter?.GetComponent<BasePlayer>();
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                return false;
            }
            return null;
        }

        private object CanMountEntity(BasePlayer player, BaseMountable entity)
        {
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                return false;
            }
            return null;
        }

        private object CanBuild(Planner planner, Construction construction, Construction.Target target)
        {
            BasePlayer player = planner?.GetOwnerPlayer();
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                return false;
            }
            return null;
        }

        private object CanMoveItem(Item item, PlayerInventory playerInventory, ItemContainerId targetContainerID, int targetSlot, int amount)
        {
            BasePlayer player = playerInventory?.gameObject?.GetComponent<BasePlayer>();
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                return false;
            }
            return null;
        }

        private object CanWearItem(PlayerInventory inventory, Item item, int targetSlot)
        {
            BasePlayer player = inventory?.gameObject?.GetComponent<BasePlayer>();
            if (player != null && _frozenPlayers.Contains(player.userID))
            {
                return false;
            }
            return null;
        }

        #endregion

        #region Public Plugin API (Hooks)

        [HookMethod("IsPlayerFrozen")]
        public bool IsPlayerFrozen(ulong playerId)
        {
            return _frozenPlayers.Contains(playerId);
        }

        [HookMethod("FreezePlayer")]
        public void FreezePlayer(BasePlayer player)
        {
            if (player == null) return;
            _frozenPlayers.Add(player.userID);
            _frozenPositions[player.userID] = player.transform.position;
            player.UpdateActiveItem(default(ItemId));
            player.SendNetworkUpdateImmediate();
        }

        [HookMethod("UnfreezePlayer")]
        public void UnfreezePlayer(BasePlayer player)
        {
            if (player == null) return;
            _frozenPlayers.Remove(player.userID);
            _frozenPositions.Remove(player.userID);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false);
            if (player.IsSleeping())
            {
                player.EndSleeping();
            }
            player.SendNetworkUpdateImmediate();
        }

        [HookMethod("AddStaffNote")]
        public void AddStaffNoteApi(ulong targetId, string authorName, string text, string tag)
        {
            AddStaffNote(targetId, authorName, text, tag);
        }

        private void CleanupStuckCargoShips()
        {
            try
            {
                var ships = UnityEngine.Object.FindObjectsOfType<CargoShip>();
                int killed = 0;
                foreach (var ship in ships)
                {
                    if (ship == null || ship.IsDestroyed) continue;
                    Vector3 pos = ship.transform.position;
                    if (pos.y < -10f || (Mathf.Abs(pos.x) < 5f && Mathf.Abs(pos.z) < 5f))
                    {
                        ship.Kill();
                        killed++;
                    }
                }
                if (killed > 0)
                {
                    Puts($"[Rustalgia] Cleaned up {killed} stuck/orphaned Cargo Ship(s).");
                }
            }
            catch (Exception ex)
            {
                PrintError($"CleanupStuckCargoShips error: {ex.Message}");
            }
        }

        private void SpawnCargoShipEvent()
        {
            CleanupStuckCargoShips();

            try
            {
                BaseEntity entity = GameManager.server.CreateEntity("assets/content/vehicles/boats/cargoship/cargoship.prefab", Vector3.zero, Quaternion.identity);
                if (entity != null)
                {
                    entity.Spawn();
                    CargoShip ship = entity as CargoShip;
                    if (ship != null)
                    {
                        ship.TriggeredEventSpawn();
                    }
                    Puts("[Rustalgia] Cargo Ship event triggered successfully.");
                }
            }
            catch (Exception ex)
            {
                PrintError($"Failed to spawn cargo ship event: {ex.Message}");
            }
        }

        private void ScanMonuments()
        {
            _monumentsList.Clear();
            if (TerrainMeta.Path?.Monuments == null) return;

            var monMap = new Dictionary<string, (string ru, string en, string icon, string tier, string rad)>
            {
                { "oilrig_2", ("Большая нефтянка", "Large Oil Rig", "🛢️", "Tier 3", "High") },
                { "oilrig_1", ("Малая нефтянка", "Small Oil Rig", "⛽", "Tier 2", "Med") },
                { "launch_site", ("Космодром", "Launch Site", "🚀", "Tier 3", "High") },
                { "airfield", ("Аэродром", "Airfield", "✈️", "Tier 2", "Med") },
                { "underwater_lab", ("Подводные лаборатории", "Underwater Labs", "🔬", "Tier 2", "Low") },
                { "sphere_tank", ("Сфера (The Dome)", "The Dome", "🔮", "Tier 1", "Low") },
                { "military_tunnel", ("Военные туннели", "Military Tunnels", "🚇", "Tier 3", "High") },
                { "compound", ("Город ученых (Outpost)", "Outpost", "🏪", "Safezone", "None") },
                { "bandit_town", ("Лагерь бандитов", "Bandit Camp", "🏕️", "Safezone", "None") },
                { "powerplant", ("Электростанция", "Power Plant", "⚡", "Tier 2", "Med") },
                { "water_treatment", ("Водоочистная станция", "Water Treatment", "💧", "Tier 2", "Med") },
                { "trainyard", ("Депо (Train Yard)", "Train Yard", "🚂", "Tier 2", "Med") },
                { "excavator", ("Гигантский экскаватор", "Giant Excavator", "🚜", "Tier 3", "High") },
                { "harbor", ("Порт (Harbor)", "Harbor", "🚢", "Tier 1", "Low") },
                { "arctic_research", ("Арктическая база", "Arctic Research", "❄️", "Tier 2", "Med") }
            };

            foreach (var mon in TerrainMeta.Path.Monuments)
            {
                if (mon == null) continue;
                string prefab = mon.name.ToLower();
                string matchedKey = null;

                foreach (var k in monMap.Keys)
                {
                    if (prefab.Contains(k))
                    {
                        matchedKey = k;
                        break;
                    }
                }

                if (matchedKey != null)
                {
                    var meta = monMap[matchedKey];
                    Vector3 pos = mon.transform.position;
                    float height = TerrainMeta.HeightMap.GetHeight(pos);
                    if (pos.y < height) pos.y = height + 1.5f;

                    _monumentsList.Add(new MonumentEntry
                    {
                        Key = matchedKey,
                        NameRU = meta.ru,
                        NameEN = meta.en,
                        Icon = meta.icon,
                        Position = pos,
                        Grid = GetGrid(pos),
                        Tier = meta.tier,
                        RadLevel = meta.rad
                    });
                }
            }

            Puts($"[AdminMenu] Scanned and indexed {_monumentsList.Count} monuments on current map.");
        }

        private void LoadStaffNotes()
        {
            try
            {
                _staffNotes = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, List<StaffNote>>>("AdminMenu_StaffNotes") ?? new Dictionary<ulong, List<StaffNote>>();
            }
            catch
            {
                _staffNotes = new Dictionary<ulong, List<StaffNote>>();
            }
        }

        private void SaveStaffNotes()
        {
            Interface.Oxide.DataFileSystem.WriteObject("AdminMenu_StaffNotes", _staffNotes);
        }

        public void AddStaffNote(ulong targetId, string authorName, string text, string tag = "NOTE")
        {
            if (!_staffNotes.TryGetValue(targetId, out var notes))
            {
                notes = new List<StaffNote>();
                _staffNotes[targetId] = notes;
            }

            notes.Insert(0, new StaffNote
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 6),
                AuthorName = authorName,
                Text = text,
                Tag = tag,
                Timestamp = DateTime.UtcNow
            });

            if (notes.Count > 30) notes.RemoveAt(notes.Count - 1);
            SaveStaffNotes();
        }

        #endregion

        #region Branding & FileStorage Loader

        private void LoadBrandingAssets()
        {
            try
            {
                string[] logoCandidates = new string[]
                {
                    Path.Combine(Interface.Oxide.DataDirectory, "Rustalgia", "logo.png"),
                    Path.Combine(Interface.Oxide.DataDirectory, "Rustalgia", "logo.jpg"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "data", "Rustalgia", "logo.png"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "data", "Rustalgia", "logo.jpg"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "..", "plugins_dev", "branding", "logo.png"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "..", "plugins_dev", "branding", "logo.jpg"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "..", "..", "plugins_dev", "branding", "logo.png"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "..", "..", "plugins_dev", "branding", "logo.jpg"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "branding", "logo.png"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "branding", "logo.jpg")
                };

                string logoPath = logoCandidates.FirstOrDefault(File.Exists);

                string[] bannerCandidates = new string[]
                {
                    Path.Combine(Interface.Oxide.DataDirectory, "Rustalgia", "banner.png"),
                    Path.Combine(Interface.Oxide.DataDirectory, "Rustalgia", "banner.jpg"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "data", "Rustalgia", "banner.png"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "data", "Rustalgia", "banner.jpg"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "..", "plugins_dev", "branding", "banner.png"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "..", "plugins_dev", "branding", "banner.jpg"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "..", "..", "plugins_dev", "branding", "banner.png"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "..", "..", "..", "plugins_dev", "branding", "banner.jpg"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "branding", "banner.png"),
                    Path.Combine(Interface.Oxide.PluginDirectory, "branding", "banner.jpg")
                };

                string bannerPath = bannerCandidates.FirstOrDefault(File.Exists);

                if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
                {
                    byte[] logoBytes = File.ReadAllBytes(logoPath);
                    if (CommunityEntity.ServerInstance?.net != null)
                    {
                        var storageType = logoPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || logoPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? FileStorage.Type.jpg : FileStorage.Type.png;
                        _logoFileId = FileStorage.server.Store(logoBytes, storageType, CommunityEntity.ServerInstance.net.ID);
                        Puts($"[Rustalgia] Logo asset cached into FileStorage (ID: {_logoFileId}) from {logoPath}");
                    }
                }

                if (!string.IsNullOrEmpty(bannerPath) && File.Exists(bannerPath))
                {
                    byte[] bannerBytes = File.ReadAllBytes(bannerPath);
                    if (CommunityEntity.ServerInstance?.net != null)
                    {
                        var storageType = bannerPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || bannerPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? FileStorage.Type.jpg : FileStorage.Type.png;
                        _bannerFileId = FileStorage.server.Store(bannerBytes, storageType, CommunityEntity.ServerInstance.net.ID);
                        Puts($"[Rustalgia] Banner asset cached into FileStorage (ID: {_bannerFileId}) from {bannerPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"[Rustalgia] Error caching branding assets: {ex.Message}");
            }
        }

        #endregion

        #region Permissions & Auth Check

        private bool IsMasterAdmin(ulong userId)
        {
            if (_config.MasterAdminSteamIds != null && _config.MasterAdminSteamIds.Contains(userId.ToString()))
                return true;
            if (permission.UserHasPermission(userId.ToString(), PermMaster))
                return true;
            if (ServerUsers.Is(userId, ServerUsers.UserGroup.Owner))
                return true;
            return false;
        }

        private bool IsMasterAdmin(BasePlayer player)
        {
            if (player == null) return false;
            if (IsMasterAdmin(player.userID))
                return true;
            if (player.net?.connection != null && player.net.connection.authLevel >= 2)
                return true;
            return false;
        }

        private bool IsAdmin(ulong userId)
        {
            if (IsMasterAdmin(userId)) return true;
            if (_config.ModeratorSteamIds != null && _config.ModeratorSteamIds.Contains(userId.ToString()))
                return true;
            if (permission.UserHasPermission(userId.ToString(), PermUse))
                return true;
            if (ServerUsers.Is(userId, ServerUsers.UserGroup.Moderator))
                return true;
            return false;
        }

        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            if (IsAdmin(player.userID)) return true;
            if (player.net?.connection != null && player.net.connection.authLevel >= 1)
                return true;
            return false;
        }

        #endregion

        #region Chat & Console Commands

        [ChatCommand("admin")]
        private void CmdAdmin(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player))
            {
                SendReply(player, "<color=#FF4444>[ДОСТУП ЗАПРЕЩЕН]</color> У вас нет прав для открытия панели администратора.");
                return;
            }

            CheckAdminStatus(player);
            OpenAdminMenu(player);
        }

        [ChatCommand("am")]
        private void CmdAm(BasePlayer player, string command, string[] args) => CmdAdmin(player, command, args);

        [ChatCommand("noclip")]
        private void CmdChatNoclip(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsAdmin(player)) return;
            ToggleNoclipDirect(player);
        }

        [ChatCommand("vanish")]
        private void CmdChatVanish(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsAdmin(player)) return;
            ToggleVanish(player);
        }

        [ChatCommand("v")]
        private void CmdChatV(BasePlayer player, string command, string[] args) => CmdChatVanish(player, command, args);

        [ChatCommand("tc")]
        private void CmdChatTC(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsAdmin(player)) return;
            InspectTargetStructure(player);
        }

        [ChatCommand("inspecttc")]
        private void CmdChatInspectTC(BasePlayer player, string command, string[] args) => CmdChatTC(player, command, args);

        [ChatCommand("say")]
        private void CmdSay(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsAdmin(player)) return;
            if (args == null || args.Length == 0) return;
            string msg = string.Join(" ", args);
            BroadcastServerMessage(msg, false);
        }

        [ChatCommand("broadcast")]
        private void CmdBroadcast(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsAdmin(player)) return;
            if (args == null || args.Length == 0) return;
            string msg = string.Join(" ", args);
            BroadcastServerMessage(msg, true);
        }

        [HookMethod("API_SendServerChat")]
        public void API_SendServerChat(string message)
        {
            SendLogoChat(message);
        }

        private void SendLogoChat(string message)
        {
            string displayName = _config.ServerName;
            string fullMsg = $"<color=#A855F7><b>[{displayName}]</b></color> {message}";
            ConsoleNetwork.BroadcastToAllClients("chat.add", 2, 0UL, fullMsg, displayName);
        }

        private void BroadcastServerMessage(string message, bool isToast)
        {
            SendLogoChat(message);

            if (isToast)
            {
                foreach (BasePlayer p in BasePlayer.activePlayerList)
                {
                    if (p != null && p.IsConnected)
                    {
                        p.SendConsoleCommand("gametip.showgametip", $"<color=#A855F7><b>[ВНИМАНИЕ СЕРВЕРА]</b></color>\n{message}");
                        timer.Once(6.0f, () =>
                        {
                            if (p != null && p.IsConnected)
                            {
                                p.SendConsoleCommand("gametip.hidegametip");
                            }
                        });
                    }
                }
            }
        }

        #endregion

        #region Core UI Construction & Shell

        private AdminSession GetSession(ulong userId)
        {
            if (!_sessions.TryGetValue(userId, out AdminSession session))
            {
                session = new AdminSession();
                _sessions[userId] = session;
            }
            return session;
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

        private void OpenAdminMenu(BasePlayer player)
        {
            if (player == null || !IsAdmin(player)) return;

            if (_logoFileId == 0)
            {
                LoadBrandingAssets();
            }

            CloseAdminMenu(player);

            AdminSession session = GetSession(player.userID);
            bool isMaster = IsMasterAdmin(player);

            CuiElementContainer container = new CuiElementContainer();

            // 1. Dark Glassmorphism Overlay (Full Screen)
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.85", Material = "assets/content/ui/uibackgroundblur.mat" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", PanelOverlay);

            // 2. Main Window Frame
            string windowId = container.Add(new CuiPanel
            {
                Image = { Color = ColBg },
                RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.95" }
            }, PanelOverlay, PanelWindow);

            // Top Neon Purple Brand Accent Line
            container.Add(new CuiPanel
            {
                Image = { Color = ColNeonPurple },
                RectTransform = { AnchorMin = "0 0.995", AnchorMax = "1 1" }
            }, windowId);

            // 3. Header Bar
            string headerId = container.Add(new CuiPanel
            {
                Image = { Color = ColHeader },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 0.995" }
            }, windowId);

            // Header Bottom Divider
            container.Add(new CuiPanel
            {
                Image = { Color = ColDivider },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.015" }
            }, headerId);

            // Header Logo
            if (_logoFileId != 0)
            {
                container.Add(new CuiElement
                {
                    Parent = headerId,
                    Components =
                    {
                        new CuiRawImageComponent { Png = _logoFileId.ToString(), Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = "0.015 0.12", AnchorMax = "0.055 0.88" }
                    }
                });
            }

            // Header Title and Subtitle
            float titleX = (_logoFileId != 0) ? 0.065f : 0.02f;
            string roleBadge = isMaster ? "<color=#A855F7>[MASTER ADMIN]</color>" : "<color=#38BDF8>[MODERATOR]</color>";

            container.Add(new CuiLabel
            {
                Text = { Text = $"{_config.ServerName}  {roleBadge}", FontSize = 15, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = $"{titleX} 0.48", AnchorMax = "0.45 0.92" }
            }, headerId);

            container.Add(new CuiLabel
            {
                Text = { Text = _config.Subtitle, FontSize = 9, Align = TextAnchor.MiddleLeft, Color = "0.65 0.68 0.78 0.85" },
                RectTransform = { AnchorMin = $"{titleX} 0.12", AnchorMax = "0.45 0.48" }
            }, headerId);

            // Telemetry Quick Stats in Header
            int online = BasePlayer.activePlayerList.Count;
            int ents = BaseNetworkable.serverEntities.Count;
            int fps = Performance.report.frameRate;
            int pluginsCount = plugins.GetAll().Length;

            string statsQuick = $"FPS: <color=#00FF88>{fps}</color> | Ents: <color=#00E5FF>{ents:N0}</color> | Online: <color=#FFAA00>{online}/{ConVar.Server.maxplayers}</color> | Plugins: <color=#C084FC>{pluginsCount}</color>";
            container.Add(new CuiLabel
            {
                Text = { Text = statsQuick, FontSize = 10, Align = TextAnchor.MiddleRight, Font = "robotocondensed-bold.ttf", Color = "0.85 0.88 0.95 1" },
                RectTransform = { AnchorMin = "0.45 0", AnchorMax = "0.85 1" }
            }, headerId);

            // Language Switcher Button [ 🌐 RU ] / [ 🌐 EN ]
            string langText = session.Language == "RU" ? "🌐 RU" : "🌐 EN";
            string langColor = session.Language == "RU" ? "0.35 0.20 0.60 0.95" : "0.20 0.45 0.70 0.95";
            container.Add(new CuiButton
            {
                Button = { Color = langColor, Command = "am.ui toggle_lang" },
                RectTransform = { AnchorMin = "0.865 0.20", AnchorMax = "0.935 0.80" },
                Text = { Text = langText, FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, headerId);

            // Close button [✕]
            container.Add(new CuiButton
            {
                Button = { Color = ColDanger, Command = "am.ui close" },
                RectTransform = { AnchorMin = "0.955 0.20", AnchorMax = "0.988 0.80" },
                Text = { Text = "✕", FontSize = 13, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, headerId);

            // 4. Left Navigation Sidebar
            RenderSidebar(container, windowId, session, isMaster);

            // 5. Render Main Content Area
            RenderContentArea(container, windowId, player, session, isMaster);

            // 6. Submodal for text input if editing a string config
            if (session.EditingFieldIndex >= 0 && session.EditingFieldIndex < session.ConfigFields.Count)
            {
                RenderTextInputModal(container, windowId, session);
            }

            CuiHelper.AddUi(player, container);
        }

        private void RenderSidebar(CuiElementContainer container, string parent, AdminSession session, bool isMaster)
        {
            string sidebarId = container.Add(new CuiPanel
            {
                Image = { Color = ColSidebar },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.185 0.91" }
            }, parent, PanelSidebar);

            // Sidebar Divider Line
            container.Add(new CuiPanel
            {
                Image = { Color = ColDivider },
                RectTransform = { AnchorMin = "0.99 0", AnchorMax = "1 1" }
            }, sidebarId);

            float tabY = 0.85f;
            float tabH = 0.065f;
            float tabGap = 0.010f;

            AddNavTab(container, sidebarId, T(session, "🏠 Главная", "🏠 Dashboard"), "dashboard", session.ActiveTab == "dashboard", tabY);
            tabY -= (tabH + tabGap);

            AddNavTab(container, sidebarId, T(session, "👥 Игроки", "👥 Players"), "players", session.ActiveTab == "players" || session.ActiveTab == "player_detail", tabY);
            tabY -= (tabH + tabGap);

            AddNavTab(container, sidebarId, T(session, "🚨 Репорты", "🚨 Reports"), "reports", session.ActiveTab == "reports", tabY);
            tabY -= (tabH + tabGap);

            AddNavTab(container, sidebarId, T(session, "📍 Монументы", "📍 Monuments"), "monuments", session.ActiveTab == "monuments", tabY);
            tabY -= (tabH + tabGap);

            AddNavTab(container, sidebarId, T(session, "⚡ Селф", "⚡ Self Tools"), "self", session.ActiveTab == "self", tabY);
            tabY -= (tabH + tabGap);

            if (isMaster)
            {
                AddNavTab(container, sidebarId, T(session, "🔌 Плагины", "🔌 Plugins & Configs"), "plugins", session.ActiveTab == "plugins" || session.ActiveTab == "plugin_config", tabY);
                tabY -= (tabH + tabGap);

                AddNavTab(container, sidebarId, T(session, "🌍 Сервер", "🌍 Server Control"), "server", session.ActiveTab == "server", tabY);
                tabY -= (tabH + tabGap);

                AddNavTab(container, sidebarId, T(session, "🚁 Ивенты", "🚁 World Events"), "events", session.ActiveTab == "events", tabY);
                tabY -= (tabH + tabGap);
            }

            AddNavTab(container, sidebarId, T(session, "📊 Диагностика", "📊 Diagnostics"), "stats", session.ActiveTab == "stats", tabY);

            // Sidebar Footer - Author Attribution & Version Badge
            string footerPanel = container.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.11 0.16 0.95" },
                RectTransform = { AnchorMin = "0.065 0.015", AnchorMax = "0.935 0.08" }
            }, sidebarId);

            container.Add(new CuiPanel
            {
                Image = { Color = ColNeonPurple },
                RectTransform = { AnchorMin = "0 0.94", AnchorMax = "1 1" }
            }, footerPanel);

            container.Add(new CuiLabel
            {
                Text = { Text = "CREATED BY LEVRO", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "0.75 0.55 1 1" },
                RectTransform = { AnchorMin = "0 0.45", AnchorMax = "1 0.95" }
            }, footerPanel);

            container.Add(new CuiLabel
            {
                Text = { Text = "v1.3.0 • ADMIN MENU", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "0.65 0.68 0.78 0.8" },
                RectTransform = { AnchorMin = "0 0.05", AnchorMax = "1 0.50" }
            }, footerPanel);
        }

        private void AddNavTab(CuiElementContainer container, string parent, string title, string tabKey, bool isActive, float yMax)
        {
            float yMin = yMax - 0.065f;
            string btnColor = isActive ? ColDarkPurple : "0.10 0.11 0.16 0.85";
            string txtColor = isActive ? "1 1 1 1" : "0.75 0.78 0.88 0.9";

            string tabBtnId = container.Add(new CuiButton
            {
                Button = { Color = btnColor, Command = $"am.ui tab {tabKey}" },
                RectTransform = { AnchorMin = $"0.065 {yMin}", AnchorMax = $"0.935 {yMax}" },
                Text = { Text = "" }
            }, parent);

            if (isActive)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = ColNeonPurple },
                    RectTransform = { AnchorMin = "0 0.12", AnchorMax = "0.025 0.88" }
                }, tabBtnId);
            }

            container.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = txtColor },
                RectTransform = { AnchorMin = "0.10 0", AnchorMax = "0.96 1" }
            }, tabBtnId);
        }

        private void CloseAdminMenu(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, PanelOverlay);
            CuiHelper.DestroyUi(player, PanelModal);
        }

        private void RefreshContent(BasePlayer player)
        {
            if (player == null || !IsAdmin(player)) return;
            AdminSession session = GetSession(player.userID);
            bool isMaster = IsMasterAdmin(player);

            CuiHelper.DestroyUi(player, PanelSidebar);
            CuiHelper.DestroyUi(player, PanelContent);

            CuiElementContainer container = new CuiElementContainer();
            RenderSidebar(container, PanelWindow, session, isMaster);
            RenderContentArea(container, PanelWindow, player, session, isMaster);
            CuiHelper.AddUi(player, container);
        }

        #endregion

        #region Content Area Router

        private void RenderContentArea(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session, bool isMaster)
        {
            string contentId = container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.195 0.02", AnchorMax = "0.985 0.895" }
            }, parent, PanelContent);

            switch (session.ActiveTab)
            {
                case "dashboard":
                    RenderDashboardTab(container, contentId, admin, session, isMaster);
                    break;
                case "players":
                    RenderPlayersTab(container, contentId, admin, session);
                    break;
                case "player_detail":
                    RenderPlayerDetailTab(container, contentId, admin, session, isMaster);
                    break;
                case "reports":
                    RenderReportsTab(container, contentId, admin, session);
                    break;
                case "monuments":
                    RenderMonumentsTab(container, contentId, admin, session);
                    break;
                case "self":
                    RenderSelfToolsTab(container, contentId, admin, session);
                    break;
                case "plugins":
                    if (isMaster) RenderPluginsTab(container, contentId, admin, session);
                    else RenderAccessDenied(container, contentId, session);
                    break;
                case "plugin_config":
                    if (isMaster) RenderPluginConfigEditorTab(container, contentId, admin, session);
                    else RenderAccessDenied(container, contentId, session);
                    break;
                case "server":
                    if (isMaster) RenderServerControlTab(container, contentId, admin, session);
                    else RenderAccessDenied(container, contentId, session);
                    break;
                case "events":
                    if (isMaster) RenderEventsTab(container, contentId, admin, session);
                    else RenderAccessDenied(container, contentId, session);
                    break;
                case "stats":
                    RenderDiagnosticsTab(container, contentId, admin, session);
                    break;
                default:
                    RenderDashboardTab(container, contentId, admin, session, isMaster);
                    break;
            }
        }

        private void RenderAccessDenied(CuiElementContainer container, string parent, AdminSession session)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "🔒 Доступ ограничен. Требуются права Главного Администратора.", "🔒 Access Denied. Head Administrator permission required."), FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.85 0.3 0.3 1" },
                RectTransform = { AnchorMin = "0.1 0.4", AnchorMax = "0.9 0.6" }
            }, parent);
        }

        #endregion

        #region Tab: Dashboard & Hero Banner

        private void RenderDashboardTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session, bool isMaster)
        {
            if (_bannerFileId == 0)
            {
                LoadBrandingAssets();
            }

            // Hero Banner Card
            string bannerId = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0.65", AnchorMax = "1 1" }
            }, parent);

            if (_bannerFileId != 0)
            {
                container.Add(new CuiElement
                {
                    Parent = bannerId,
                    Components =
                    {
                        new CuiRawImageComponent { Png = _bannerFileId.ToString(), Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }
                });
            }
            else
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.10 0.08 0.16 0.95" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, bannerId);

                container.Add(new CuiPanel
                {
                    Image = { Color = ColNeonPurple },
                    RectTransform = { AnchorMin = "0 0.98", AnchorMax = "1 1" }
                }, bannerId);

                container.Add(new CuiLabel
                {
                    Text = { Text = "⚡ <b>RUSTALGIA SERVER NETWORK</b>", FontSize = 22, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "0.95 0.85 1 1" },
                    RectTransform = { AnchorMin = "0 0.45", AnchorMax = "1 0.85" }
                }, bannerId);

                container.Add(new CuiLabel
                {
                    Text = { Text = "OFFICIAL SERVER CONTROL CENTER • ANTI-CHEAT 2.0 • ADVANCED TELEMETRY", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-regular.ttf", Color = "0.6 0.6 0.7 0.9" },
                    RectTransform = { AnchorMin = "0 0.15", AnchorMax = "1 0.45" }
                }, bannerId);
            }

            // Quick Stats Row
            string statsCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0.38", AnchorMax = "1 0.63" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "📊 СИСТЕМНАЯ СВОДКА И БЕЗОПАСНОСТЬ", "📊 SYSTEM OVERVIEW & SECURITY TELEMETRY"), FontSize = 12, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.95 0.75 0.25 1" },
                RectTransform = { AnchorMin = "0.03 0.82", AnchorMax = "0.97 0.96" }
            }, statsCard);

            int online = BasePlayer.activePlayerList.Count;
            int sleepers = BasePlayer.sleepingPlayerList.Count;
            int vanishedCount = _vanishedPlayers.Count;

            string infoA = $"{T(session, "Онлайн", "Online")}: <color=#00FFAA>{online} / {ConVar.Server.maxplayers}</color>\n" +
                           $"{T(session, "Спящие", "Sleepers")}: <color=#FFAA00>{sleepers}</color>\n" +
                           $"Vanish 2.0: <color=#C084FC>{vanishedCount} админов</color>";

            container.Add(new CuiLabel
            {
                Text = { Text = infoA, FontSize = 11, Align = TextAnchor.UpperLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.03 0.1", AnchorMax = "0.33 0.78" }
            }, statsCard);

            string infoB = $"Entities: <color=#00E5FF>{BaseNetworkable.serverEntities.Count:N0}</color>\n" +
                           $"FPS: <color=#00FF88>{Performance.report.frameRate}</color>\n" +
                           $"Memory: <color=#FFFF00>{GC.GetTotalMemory(false) / 1024 / 1024} MB</color>";

            container.Add(new CuiLabel
            {
                Text = { Text = infoB, FontSize = 11, Align = TextAnchor.UpperLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.36 0.1", AnchorMax = "0.66 0.78" }
            }, statsCard);

            string infoC = $"Uptime: <color=#FFAA00>{(int)(Time.realtimeSinceStartup / 3600)}h {(int)((Time.realtimeSinceStartup % 3600) / 60)}m</color>\n" +
                           $"Map Size: <color=#00FFAA>{TerrainMeta.Size.x}m</color>\n" +
                           $"Monuments: <color=#38BDF8>{_monumentsList.Count}</color>";

            container.Add(new CuiLabel
            {
                Text = { Text = infoC, FontSize = 11, Align = TextAnchor.UpperLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.69 0.1", AnchorMax = "0.97 0.78" }
            }, statsCard);

            // Broadcast Bar
            string broadCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.35" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "📢 БЫСТРОЕ ОБЪЯВЛЕНИЕ НА ВЕСЬ СЕРВЕР (BROADCAST)", "📢 GLOBAL SERVER BROADCAST ANNOUNCEMENT"), FontSize = 12, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.2 0.7 0.95 1" },
                RectTransform = { AnchorMin = "0.03 0.78", AnchorMax = "0.97 0.92" }
            }, broadCard);

            string broadInputWell = container.Add(new CuiPanel
            {
                Image = { Color = ColCardInner },
                RectTransform = { AnchorMin = "0.03 0.28", AnchorMax = "0.97 0.72" }
            }, broadCard);

            AddInputField(container, broadInputWell, session.BroadcastDraft, 11, TextAnchor.MiddleLeft, "1 1 1 1", 120, "am.ui input_broadcast ", "0.02 0.1", "0.98 0.9");

            // Send Chat Button
            container.Add(new CuiButton
            {
                Button = { Color = "0.15 0.60 0.35 0.95", Command = "am.ui send_broadcast chat" },
                RectTransform = { AnchorMin = "0.03 0.06", AnchorMax = "0.48 0.24" },
                Text = { Text = T(session, "💬 Отправить в чат", "💬 Send to Chat Only"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, broadCard);

            // Send Toast Button
            container.Add(new CuiButton
            {
                Button = { Color = ColDarkPurple, Command = "am.ui send_broadcast toast" },
                RectTransform = { AnchorMin = "0.52 0.06", AnchorMax = "0.97 0.24" },
                Text = { Text = T(session, "🔔 Отправить на экран (Toast + Звук)", "🔔 Screen Toast + Audio Alert"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, broadCard);
        }

        #endregion

        #region Tab: Reports Management (DiscordReports Integration)

        private List<ReportViewModel> GetReportsFromPlugin()
        {
            try
            {
                Plugin dr = plugins.Find("DiscordReports");
                if (dr != null)
                {
                    string json = dr.Call("GetReportsData") as string;
                    if (!string.IsNullOrEmpty(json))
                    {
                        return JsonConvert.DeserializeObject<List<ReportViewModel>>(json) ?? new List<ReportViewModel>();
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"Error fetching reports: {ex.Message}");
            }
            return new List<ReportViewModel>();
        }

        private void RenderReportsTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session)
        {
            // Top Bar
            string topBar = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 0.99" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "🚨 ЖУРНАЛ ЖАЛОБ ИГРОКОВ (DISCORD REPORTS & ANTI-CHEAT)", "🚨 PLAYER REPORTS & ANTI-CHEAT AUDIT LOG"), FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.95 0.35 0.35 1" },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.70 1" }
            }, topBar);

            container.Add(new CuiButton
            {
                Button = { Color = ColDarkPurple, Command = "am.ui refresh_reports" },
                RectTransform = { AnchorMin = "0.85 0.15", AnchorMax = "0.98 0.85" },
                Text = { Text = T(session, "🔄 Обновить", "🔄 Refresh"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, topBar);

            List<ReportViewModel> reports = GetReportsFromPlugin();

            if (reports == null || reports.Count == 0)
            {
                string emptyCard = container.Add(new CuiPanel
                {
                    Image = { Color = ColCardBg },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.89" }
                }, parent);

                container.Add(new CuiLabel
                {
                    Text = { Text = T(session, "✅ Все чисто! Активных жалоб на игроков нет.\nНовые жалобы, поданные через /report, отобразятся здесь.", "✅ All clear! No active player reports.\nNew player reports submitted via /report will show up here."), FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.65 0.70 0.85 1" },
                    RectTransform = { AnchorMin = "0.1 0.35", AnchorMax = "0.9 0.65" }
                }, emptyCard);
                return;
            }

            int perPage = 5;
            int totalPages = Mathf.Max(1, Mathf.CeilToInt((float)reports.Count / perPage));
            if (session.ReportsPage >= totalPages) session.ReportsPage = totalPages - 1;
            if (session.ReportsPage < 0) session.ReportsPage = 0;

            var pageReports = reports.Skip(session.ReportsPage * perPage).Take(perPage).ToList();

            float rowY = 0.88f;
            float rowH = 0.135f;
            float gap = 0.012f;

            foreach (var r in pageReports)
            {
                float yMin = rowY - rowH;
                string rowId = container.Add(new CuiPanel
                {
                    Image = { Color = ColCardBg },
                    RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {rowY}" }
                }, parent);

                string stripeCol = r.IsResolved ? ColSuccess : ColDanger;
                container.Add(new CuiPanel
                {
                    Image = { Color = stripeCol },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0.012 1" }
                }, rowId);

                string statusBadge = r.IsResolved
                    ? $"<color=#00FF88><b>[{T(session, "РЕШЕНО", "RESOLVED")}]</b></color>"
                    : $"<color=#FF4444><b>[{T(session, "АКТИВЕН", "PENDING")}]</b></color>";

                string karmaBadge = r.ReporterKarma >= 150
                    ? "<color=#FFDD44>⭐[ПРИОРИТЕТ]</color> "
                    : (r.ReporterKarma < 50 ? "<color=#FF4444>⚠️[СПАМЕР?]</color> " : "");

                int minsAgo = Mathf.Max(0, (int)(DateTime.UtcNow - r.Timestamp).TotalMinutes);
                string timeAgo = $"{minsAgo}m ago";

                string suspectTitle = string.IsNullOrEmpty(r.SuspectName) ? $"#{r.SuspectId}" : r.SuspectName;
                string reporterTitle = string.IsNullOrEmpty(r.ReporterName) ? "Unknown" : r.ReporterName;
                string titleTxt = $"#{r.Id} {statusBadge} {karmaBadge}Подозреваемый: <color=#00FFAA><b>{suspectTitle}</b></color> <color=#888899>({r.SuspectId})</color>  |  От: <color=#FFAA00>{reporterTitle}</color>  |  Квадрат: <color=#FFFF00>{r.Grid}</color>  |  <color=#777788>{timeAgo}</color>";

                container.Add(new CuiLabel
                {
                    Text = { Text = titleTxt, FontSize = 10, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.9 0.9 1 1" },
                    RectTransform = { AnchorMin = "0.025 0.60", AnchorMax = "0.58 0.95" }
                }, rowId);

                string reasonTxt = $"{T(session, "Причина", "Reason")}: <color=#FFDD44>{r.Reason}</color>";
                if (!string.IsNullOrEmpty(r.CombatSnapshot))
                {
                    reasonTxt += $"\n<color=#38BDF8>⚔️ Попадания:</color> {r.CombatSnapshot.Replace("\n", " | ")}";
                }

                container.Add(new CuiLabel
                {
                    Text = { Text = reasonTxt, FontSize = 9, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.9 1" },
                    RectTransform = { AnchorMin = "0.025 0.05", AnchorMax = "0.58 0.58" }
                }, rowId);

                // Action 1: TP
                container.Add(new CuiButton
                {
                    Button = { Color = "0.15 0.62 0.38 0.95", Command = $"am.ui quick_tp {r.SuspectId}" },
                    RectTransform = { AnchorMin = "0.59 0.52", AnchorMax = "0.66 0.90" },
                    Text = { Text = "⚡ ТП", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                // Action 2: Spec
                container.Add(new CuiButton
                {
                    Button = { Color = ColDarkPurple, Command = $"am.ui spectate {r.SuspectId}" },
                    RectTransform = { AnchorMin = "0.67 0.52", AnchorMax = "0.74 0.90" },
                    Text = { Text = "👁️ Спек", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                // Action 3: Check (AnyDesk Summon)
                container.Add(new CuiButton
                {
                    Button = { Color = "0.95 0.55 0.15 0.95", Command = $"am.ui report_check {r.SuspectId}" },
                    RectTransform = { AnchorMin = "0.75 0.52", AnchorMax = "0.85 0.90" },
                    Text = { Text = "🛡️ Проверка", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                // Action 4: Ban (+25 Karma)
                container.Add(new CuiButton
                {
                    Button = { Color = "0.85 0.20 0.25 0.95", Command = $"am.ui report_ban {r.Id} {r.SuspectId}" },
                    RectTransform = { AnchorMin = "0.86 0.52", AnchorMax = "0.985 0.90" },
                    Text = { Text = "🔨 Бан (+25K)", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                // Action 5: Resolve (Innocent)
                container.Add(new CuiButton
                {
                    Button = { Color = "0.18 0.52 0.85 0.95", Command = $"am.ui report_resolve {r.Id}" },
                    RectTransform = { AnchorMin = "0.59 0.10", AnchorMax = "0.71 0.45" },
                    Text = { Text = "✓ Оправдан", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                // Action 6: False Report (-35 Karma)
                container.Add(new CuiButton
                {
                    Button = { Color = "0.60 0.25 0.25 0.95", Command = $"am.ui report_false {r.Id}" },
                    RectTransform = { AnchorMin = "0.72 0.10", AnchorMax = "0.85 0.45" },
                    Text = { Text = "🚫 Ложный (-35K)", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                // Action 7: Delete
                container.Add(new CuiButton
                {
                    Button = { Color = "0.30 0.32 0.40 0.95", Command = $"am.ui report_del {r.Id}" },
                    RectTransform = { AnchorMin = "0.86 0.10", AnchorMax = "0.985 0.45" },
                    Text = { Text = "✕ Удалить", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                rowY -= (rowH + gap);
            }

            // Pagination footer
            if (totalPages > 1)
            {
                float pageBtnY = 0.01f;
                container.Add(new CuiButton
                {
                    Button = { Color = session.ReportsPage > 0 ? ColDarkPurple : "0.15 0.16 0.22 0.8", Command = $"am.ui report_page {session.ReportsPage - 1}" },
                    RectTransform = { AnchorMin = $"0.35 {pageBtnY}", AnchorMax = $"0.45 {pageBtnY + 0.05f}" },
                    Text = { Text = T(session, "◀ Назад", "◀ Prev"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, parent);

                container.Add(new CuiLabel
                {
                    Text = { Text = $"{session.ReportsPage + 1} / {totalPages}", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = $"0.46 {pageBtnY}", AnchorMax = $"0.54 {pageBtnY + 0.05f}" }
                }, parent);

                container.Add(new CuiButton
                {
                    Button = { Color = session.ReportsPage < totalPages - 1 ? ColDarkPurple : "0.15 0.16 0.22 0.8", Command = $"am.ui report_page {session.ReportsPage + 1}" },
                    RectTransform = { AnchorMin = $"0.55 {pageBtnY}", AnchorMax = $"0.65 {pageBtnY + 0.05f}" },
                    Text = { Text = T(session, "Вперед ▶", "Next ▶"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, parent);
            }
        }

        #endregion

        #region Tab: Monuments (Fast Bookmarks)

        private void RenderMonumentsTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session)
        {
            // Top Bar
            string topBar = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 0.99" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "📍 ЗАКЛАДКИ БЫСТРОГО ТЕЛЕПОРТА (МОНУМЕНТЫ КАРТЫ)", "📍 QUICK MONUMENT TELEPORT BOOKMARKS"), FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.38 0.75 0.95 1" },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.70 1" }
            }, topBar);

            container.Add(new CuiButton
            {
                Button = { Color = ColDarkPurple, Command = "am.ui rescan_monuments" },
                RectTransform = { AnchorMin = "0.85 0.15", AnchorMax = "0.98 0.85" },
                Text = { Text = T(session, "🔄 Пересканировать", "🔄 Rescan Map"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, topBar);

            if (_monumentsList.Count == 0)
            {
                string emptyCard = container.Add(new CuiPanel
                {
                    Image = { Color = ColCardBg },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.89" }
                }, parent);

                container.Add(new CuiLabel
                {
                    Text = { Text = T(session, "Монументы не обнаружены на данной карте или еще загружаются.", "No monuments found on current procedural map or still loading."), FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.65 0.70 0.85 1" },
                    RectTransform = { AnchorMin = "0.1 0.35", AnchorMax = "0.9 0.65" }
                }, emptyCard);
                return;
            }

            int perPage = 9;
            int totalPages = Mathf.Max(1, Mathf.CeilToInt((float)_monumentsList.Count / perPage));
            if (session.MonumentsPage >= totalPages) session.MonumentsPage = totalPages - 1;
            if (session.MonumentsPage < 0) session.MonumentsPage = 0;

            var pageMonuments = _monumentsList.Skip(session.MonumentsPage * perPage).Take(perPage).ToList();

            // 3 cols x 3 rows grid
            int cols = 3;
            int rows = 3;
            float cellW = 0.32f;
            float cellH = 0.27f;
            float startY = 0.88f;

            for (int i = 0; i < pageMonuments.Count; i++)
            {
                int r = i / cols;
                int c = i % cols;

                float xMin = c * (cellW + 0.02f);
                float xMax = xMin + cellW;
                float yMax = startY - (r * (cellH + 0.02f));
                float yMin = yMax - cellH;

                MonumentEntry m = pageMonuments[i];

                string card = container.Add(new CuiPanel
                {
                    Image = { Color = ColCardBg },
                    RectTransform = { AnchorMin = $"{xMin} {yMin}", AnchorMax = $"{xMax} {yMax}" }
                }, parent);

                // Top violet accent
                container.Add(new CuiPanel
                {
                    Image = { Color = ColNeonPurple },
                    RectTransform = { AnchorMin = "0 0.96", AnchorMax = "1 1" }
                }, card);

                string mTitle = session.Language == "RU" ? m.NameRU : m.NameEN;
                container.Add(new CuiLabel
                {
                    Text = { Text = $"{m.Icon} <b>{mTitle}</b>", FontSize = 12, Align = TextAnchor.UpperLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.06 0.65", AnchorMax = "0.94 0.92" }
                }, card);

                string radColor = m.RadLevel == "High" ? "#FF4444" : (m.RadLevel == "Med" ? "#FFAA00" : "#00FF88");
                string mInfo = $"Квадрат: <color=#FFFF00><b>{m.Grid}</b></color>  |  Зона: <color=#00FFAA>{m.Tier}</color>\nРадиация: <color={radColor}>{m.RadLevel}</color>";

                container.Add(new CuiLabel
                {
                    Text = { Text = mInfo, FontSize = 10, Align = TextAnchor.UpperLeft, Color = "0.8 0.82 0.9 1" },
                    RectTransform = { AnchorMin = "0.06 0.32", AnchorMax = "0.94 0.62" }
                }, card);

                // Teleport button
                container.Add(new CuiButton
                {
                    Button = { Color = "0.15 0.62 0.38 0.95", Command = $"am.ui tp_monument {m.Position.x.ToString(CultureInfo.InvariantCulture)} {m.Position.y.ToString(CultureInfo.InvariantCulture)} {m.Position.z.ToString(CultureInfo.InvariantCulture)}" },
                    RectTransform = { AnchorMin = "0.06 0.08", AnchorMax = "0.94 0.28" },
                    Text = { Text = T(session, "⚡ Телепортироваться", "⚡ Teleport Here"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, card);
            }

            // Pagination
            if (totalPages > 1)
            {
                float pageY = 0.01f;
                container.Add(new CuiButton
                {
                    Button = { Color = session.MonumentsPage > 0 ? ColDarkPurple : "0.15 0.16 0.22 0.8", Command = $"am.ui monument_page {session.MonumentsPage - 1}" },
                    RectTransform = { AnchorMin = $"0.35 {pageY}", AnchorMax = $"0.45 {pageY + 0.05f}" },
                    Text = { Text = T(session, "◀ Назад", "◀ Prev"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, parent);

                container.Add(new CuiLabel
                {
                    Text = { Text = $"{session.MonumentsPage + 1} / {totalPages}", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = $"0.46 {pageY}", AnchorMax = $"0.54 {pageY + 0.05f}" }
                }, parent);

                container.Add(new CuiButton
                {
                    Button = { Color = session.MonumentsPage < totalPages - 1 ? ColDarkPurple : "0.15 0.16 0.22 0.8", Command = $"am.ui monument_page {session.MonumentsPage + 1}" },
                    RectTransform = { AnchorMin = $"0.55 {pageY}", AnchorMax = $"0.65 {pageY + 0.05f}" },
                    Text = { Text = T(session, "Вперед ▶", "Next ▶"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, parent);
            }
        }

        #endregion

        #region Tab: Players & Moderation

        private void RenderPlayersTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session)
        {
            // Search and Filter Header Bar
            string filterBar = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 0.99" }
            }, parent);

            string btnOnlineCol = session.PlayerFilter == "online" ? ColDarkPurple : "0.13 0.14 0.20 0.9";
            container.Add(new CuiButton
            {
                Button = { Color = btnOnlineCol, Command = "am.ui filter online" },
                RectTransform = { AnchorMin = "0.015 0.15", AnchorMax = "0.18 0.85" },
                Text = { Text = $"{T(session, "🟢 Онлайн", "🟢 Online")} ({BasePlayer.activePlayerList.Count})", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, filterBar);

            string btnSleepCol = session.PlayerFilter == "sleepers" ? ColDarkPurple : "0.13 0.14 0.20 0.9";
            container.Add(new CuiButton
            {
                Button = { Color = btnSleepCol, Command = "am.ui filter sleepers" },
                RectTransform = { AnchorMin = "0.19 0.15", AnchorMax = "0.36 0.85" },
                Text = { Text = $"{T(session, "💤 Спящие", "💤 Sleepers")} ({BasePlayer.sleepingPlayerList.Count})", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, filterBar);

            // Fetch players matching filter & query
            List<BasePlayer> list = (session.PlayerFilter == "online")
                ? BasePlayer.activePlayerList.ToList()
                : BasePlayer.sleepingPlayerList.ToList();

            if (!string.IsNullOrEmpty(session.SearchQuery))
            {
                list = list.Where(p => p.displayName.IndexOf(session.SearchQuery, StringComparison.OrdinalIgnoreCase) >= 0 || p.UserIDString.Contains(session.SearchQuery)).ToList();
            }

            int perPage = 7;
            int totalPages = Mathf.Max(1, Mathf.CeilToInt((float)list.Count / perPage));
            if (session.Page >= totalPages) session.Page = totalPages - 1;
            if (session.Page < 0) session.Page = 0;

            var pageList = list.Skip(session.Page * perPage).Take(perPage).ToList();

            float rowY = 0.88f;
            float rowH = 0.105f;
            float gap = 0.012f;

            foreach (var target in pageList)
            {
                float yMin = rowY - rowH;
                string rowId = container.Add(new CuiPanel
                {
                    Image = { Color = ColCardBg },
                    RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {rowY}" }
                }, parent);

                string authBadge = target.IsAdmin ? "<color=#C084FC>[ADMIN]</color> " : "";
                string hpText = $"HP: <color={(target.health > 50 ? "#00FFAA" : "#FF5555")}>{(int)target.health}</color>/100";
                int ping = (target.net?.connection != null) ? Network.Net.sv.GetAveragePing(target.net.connection) : 0;
                string pingText = target.IsConnected ? $"Ping: <color=#00E5FF>{ping}</color>ms" : "Sleeping";
                string posGrid = GetGrid(target.transform.position);

                // Staff notes badge
                string notesBadge = "";
                if (_staffNotes.TryGetValue(target.userID, out var notes) && notes.Count > 0)
                {
                    notesBadge = $" <color=#FFDD44><b>[📝 {notes.Count}]</b></color>";
                }

                // Check Aim suspicion indicator
                string aimSuspicionTag = "";
                if (IsAimSuspicious(target.userID, out float hsPct, out int hits100, out int hs100))
                {
                    aimSuspicionTag = $" <color=#FF2222><b>[⚠️ AIM: {hsPct:F0}%]</b></color>";
                }

                string pInfo = $"{authBadge}<b>{target.displayName}</b>{notesBadge}{aimSuspicionTag}   <color=#AAAAAA>({target.userID})</color>\n{hpText}  |  {pingText}  |  Grid: <color=#FFFF00>{posGrid}</color>";
                container.Add(new CuiLabel
                {
                    Text = { Text = pInfo, FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.55 1" }
                }, rowId);

                // TP to player
                container.Add(new CuiButton
                {
                    Button = { Color = "0.15 0.60 0.38 0.95", Command = $"am.ui quick_tp {target.userID}" },
                    RectTransform = { AnchorMin = "0.58 0.20", AnchorMax = "0.68 0.80" },
                    Text = { Text = "⚡ TP", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                // Bring player
                container.Add(new CuiButton
                {
                    Button = { Color = "0.20 0.50 0.85 0.95", Command = $"am.ui bring {target.userID}" },
                    RectTransform = { AnchorMin = "0.69 0.20", AnchorMax = "0.79 0.80" },
                    Text = { Text = "🧲 Bring", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                // Inspect & Moderate
                container.Add(new CuiButton
                {
                    Button = { Color = ColDarkPurple, Command = $"am.ui inspect {target.userID}" },
                    RectTransform = { AnchorMin = "0.80 0.20", AnchorMax = "0.98 0.80" },
                    Text = { Text = T(session, "🔍 Управление ▶", "🔍 Inspect & Mod ▶"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                rowY -= (rowH + gap);
            }

            // Pagination
            if (totalPages > 1)
            {
                float pY = 0.01f;
                container.Add(new CuiButton
                {
                    Button = { Color = session.Page > 0 ? ColDarkPurple : "0.15 0.16 0.22 0.8", Command = $"am.ui page {session.Page - 1}" },
                    RectTransform = { AnchorMin = $"0.35 {pY}", AnchorMax = $"0.45 {pY + 0.05f}" },
                    Text = { Text = T(session, "◀ Назад", "◀ Prev"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, parent);

                container.Add(new CuiLabel
                {
                    Text = { Text = $"{session.Page + 1} / {totalPages}", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = $"0.46 {pY}", AnchorMax = $"0.54 {pY + 0.05f}" }
                }, parent);

                container.Add(new CuiButton
                {
                    Button = { Color = session.Page < totalPages - 1 ? ColDarkPurple : "0.15 0.16 0.22 0.8", Command = $"am.ui page {session.Page + 1}" },
                    RectTransform = { AnchorMin = $"0.55 {pY}", AnchorMax = $"0.65 {pY + 0.05f}" },
                    Text = { Text = T(session, "Вперед ▶", "Next ▶"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, parent);
            }
        }

        private void RenderPlayerDetailTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session, bool isMaster)
        {
            BasePlayer target = BasePlayer.FindByID(session.SelectedPlayerId) ?? BasePlayer.FindSleeping(session.SelectedPlayerId);
            if (target == null)
            {
                session.ActiveTab = "players";
                RenderPlayersTab(container, parent, admin, session);
                return;
            }

            // Top Bar: Back button and summary header
            string topBar = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 0.99" }
            }, parent);

            container.Add(new CuiButton
            {
                Button = { Color = ColDarkPurple, Command = "am.ui tab players" },
                RectTransform = { AnchorMin = "0.015 0.15", AnchorMax = "0.14 0.85" },
                Text = { Text = T(session, "◀ К списку", "◀ Back to List"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, topBar);

            string targetSummary = $"Игрок: <color=#00FFAA><b>{target.displayName}</b></color> ({target.userID})  |  Здоровье: <color=#00FF88>{(int)target.health}</color>/100  |  Квадрат: <color=#FFFF00>{GetGrid(target.transform.position)}</color>";
            container.Add(new CuiLabel
            {
                Text = { Text = targetSummary, FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.16 0", AnchorMax = "0.98 1" }
            }, topBar);

            // Left Section: Tabs Switcher (Inventory | CombatLog | Staff Notes)
            string leftWrapper = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.62 0.89" }
            }, parent);

            // Subview Selector Buttons
            string subBar = container.Add(new CuiPanel
            {
                Image = { Color = ColCardInner },
                RectTransform = { AnchorMin = "0.02 0.91", AnchorMax = "0.98 0.98" }
            }, leftWrapper);

            string subView = session.PlayerDetailSubView ?? "inventory";

            string invBtnCol = subView == "inventory" ? ColDarkPurple : "0.12 0.13 0.18 0.9";
            container.Add(new CuiButton
            {
                Button = { Color = invBtnCol, Command = "am.ui pview inventory" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.32 1" },
                Text = { Text = T(session, "🎒 Инвентарь", "🎒 Inventory"), FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, subBar);

            string combatBtnCol = subView == "combatlog" ? ColDarkPurple : "0.12 0.13 0.18 0.9";
            container.Add(new CuiButton
            {
                Button = { Color = combatBtnCol, Command = "am.ui pview combatlog" },
                RectTransform = { AnchorMin = "0.34 0", AnchorMax = "0.66 1" },
                Text = { Text = T(session, "⚔️ CombatLog", "⚔️ CombatLog"), FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, subBar);

            string notesBtnCol = subView == "staffnotes" ? ColDarkPurple : "0.12 0.13 0.18 0.9";
            int noteCount = _staffNotes.TryGetValue(target.userID, out var nList) ? nList.Count : 0;
            container.Add(new CuiButton
            {
                Button = { Color = notesBtnCol, Command = "am.ui pview staffnotes" },
                RectTransform = { AnchorMin = "0.68 0", AnchorMax = "1 1" },
                Text = { Text = $"{T(session, "📝 Заметки", "📝 Staff Notes")} ({noteCount})", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, subBar);

            // Left Section Body
            if (subView == "combatlog")
            {
                RenderPlayerCombatLog(container, leftWrapper, target, session);
            }
            else if (subView == "staffnotes")
            {
                RenderPlayerStaffNotes(container, leftWrapper, target, session);
            }
            else
            {
                // Default: Inventory
                // Wear
                container.Add(new CuiLabel
                {
                    Text = { Text = T(session, "Одежда и броня:", "Clothing & Armor:"), FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.7 0.75 0.85 1" },
                    RectTransform = { AnchorMin = "0.03 0.84", AnchorMax = "0.97 0.89" }
                }, leftWrapper);
                RenderInventoryRow(container, leftWrapper, target.inventory.containerWear, 0.72f, 0.11f, 8);

                // Belt
                container.Add(new CuiLabel
                {
                    Text = { Text = T(session, "Быстрый пояс (Слоты 1-6):", "Belt Quickslots:"), FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.7 0.75 0.85 1" },
                    RectTransform = { AnchorMin = "0.03 0.65", AnchorMax = "0.97 0.70" }
                }, leftWrapper);
                RenderInventoryRow(container, leftWrapper, target.inventory.containerBelt, 0.53f, 0.11f, 6);

                // Main
                container.Add(new CuiLabel
                {
                    Text = { Text = T(session, "Основной рюкзак:", "Main Backpack Inventory:"), FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.7 0.75 0.85 1" },
                    RectTransform = { AnchorMin = "0.03 0.46", AnchorMax = "0.97 0.51" }
                }, leftWrapper);
                RenderInventoryGrid(container, leftWrapper, target.inventory.containerMain, 0.03f, 0.45f, 6, 4);
            }

            // Right Section: Moderation Actions
            string actCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0.64 0", AnchorMax = "1 0.89" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "🛡️ ДЕЙСТВИЯ МОДЕРАЦИИ", "🛡️ MODERATION ACTIONS"), FontSize = 12, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.2 0.7 0.95 1" },
                RectTransform = { AnchorMin = "0.05 0.93", AnchorMax = "0.95 0.98" }
            }, actCard);

            float startY = 0.88f;
            float btnH = 0.088f;
            float gapY = 0.015f;

            // Row 1: TP to him | Bring to me
            AddGridButton(container, actCard, T(session, "⚡ Телепорт к нему", "⚡ Teleport to Him"), "0.15 0.62 0.38 0.95", $"am.ui quick_tp {target.userID}", 0.04f, 0.48f, startY, btnH);
            AddGridButton(container, actCard, T(session, "🧲 Притянуть к себе", "🧲 Bring to Me"), "0.20 0.50 0.85 0.95", $"am.ui bring {target.userID}", 0.52f, 0.96f, startY, btnH);
            startY -= (btnH + gapY);

            // Row 2: Spectate | Freeze
            bool isSpec = _spectatingAdmins.Contains(admin.userID);
            string specTxt = isSpec ? T(session, "👁️ Выйти из спека", "👁️ Stop Spectating") : T(session, "👁️ Следить (Спек)", "👁️ Spectate Player");
            AddGridButton(container, actCard, specTxt, isSpec ? "0.8 0.45 0.1 0.95" : ColDarkPurple, $"am.ui spectate {target.userID}", 0.04f, 0.48f, startY, btnH);

            bool isFrozen = _frozenPlayers.Contains(target.userID);
            string frzTxt = isFrozen ? T(session, "🔥 Разморозить", "🔥 Unfreeze") : T(session, "❄️ Заморозить", "❄️ Freeze");
            AddGridButton(container, actCard, frzTxt, isFrozen ? "0.8 0.45 0.1 0.95" : "0.15 0.65 0.85 0.95", $"am.ui toggle_freeze {target.userID}", 0.52f, 0.96f, startY, btnH);
            startY -= (btnH + gapY);

            // Row 3: AnyDesk Check | Heal
            AddGridButton(container, actCard, T(session, "🛡️ Вызов на проверку", "🛡️ Summon to Check"), "0.95 0.55 0.15 0.95", $"am.ui report_check {target.userID}", 0.04f, 0.48f, startY, btnH);
            AddGridButton(container, actCard, T(session, "❤️ Вылечить (Хил)", "❤️ Heal Vitals"), "0.18 0.65 0.45 0.95", $"am.ui heal_player {target.userID}", 0.52f, 0.96f, startY, btnH);
            startY -= (btnH + gapY);

            // Row 4: Mute | Clear Inv
            bool isMuted = _mutedPlayers.Contains(target.userID);
            string muteTxt = isMuted ? T(session, "🔊 Размутить", "🔊 Unmute Chat") : T(session, "🤐 Замутить в чате", "🤐 Mute Chat");
            AddGridButton(container, actCard, muteTxt, isMuted ? "0.8 0.45 0.1 0.95" : "0.55 0.40 0.85 0.95", $"am.ui toggle_mute {target.userID}", 0.04f, 0.48f, startY, btnH);
            AddGridButton(container, actCard, T(session, "🎒 Очистить инв.", "🎒 Clear Inventory"), "0.75 0.35 0.15 0.95", $"am.ui strip_inv {target.userID}", 0.52f, 0.96f, startY, btnH);
            startY -= (btnH + gapY);

            // Row 5: Kick | Ban
            AddGridButton(container, actCard, T(session, "🚫 Кикнуть", "🚫 Kick Player"), "0.85 0.25 0.20 0.95", $"am.ui kick {target.userID}", 0.04f, 0.48f, startY, btnH);
            if (isMaster)
            {
                AddGridButton(container, actCard, T(session, "🔨 Забанить", "🔨 Ban Player"), "0.90 0.15 0.18 0.98", $"am.ui ban {target.userID}", 0.52f, 0.96f, startY, btnH);
            }
            startY -= (btnH + gapY);

            // Row 6: Master / Mod Roles (Master Admins Only)
            if (isMaster && target.userID != admin.userID)
            {
                bool targetIsMaster = IsMasterAdmin(target);
                bool targetIsMod = IsAdmin(target);

                if (targetIsMaster || targetIsMod)
                {
                    AddGridButton(container, actCard, T(session, "❌ Снять права админа", "❌ Revoke Admin Role"), "0.65 0.20 0.20 0.98", $"am.ui revoke_admin {target.userID}", 0.04f, 0.96f, startY, btnH);
                }
                else
                {
                    AddGridButton(container, actCard, T(session, "👑 Сделать Главным", "👑 Grant Master"), ColDarkPurple, $"am.ui grant_master {target.userID}", 0.04f, 0.48f, startY, btnH);
                    AddGridButton(container, actCard, T(session, "🛡️ Сделать Модератором", "🛡️ Grant Moderator"), "0.20 0.50 0.85 0.95", $"am.ui grant_mod {target.userID}", 0.52f, 0.96f, startY, btnH);
                }
            }
        }

        private void RenderPlayerCombatLog(CuiElementContainer container, string parent, BasePlayer target, AdminSession session)
        {
            bool hasHits = _combatHits.TryGetValue(target.userID, out var hits) && hits.Count > 0;
            bool isAimAlert = IsAimSuspicious(target.userID, out float hsPct100, out int total100, out int hs100);

            // AIM Alert Banner if detected
            float topY = 0.88f;
            if (isAimAlert)
            {
                string alertBox = container.Add(new CuiPanel
                {
                    Image = { Color = "0.85 0.15 0.18 0.95" },
                    RectTransform = { AnchorMin = "0.03 0.77", AnchorMax = "0.97 0.88" }
                }, parent);

                container.Add(new CuiLabel
                {
                    Text = { Text = $"⚠️ <b>ПОДОЗРЕНИЕ НА AIMBOT!</b> ({hs100}/{total100} попаданий в голову на дистанции 100м+ = {hsPct100:F0}% HS)", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, alertBox);

                topY = 0.75f;
            }

            // Summary Stats Card
            string summaryCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardInner },
                RectTransform = { AnchorMin = $"0.03 {topY - 0.12f}", AnchorMax = $"0.97 {topY}" }
            }, parent);

            int totalHits = hasHits ? hits.Count : 0;
            int totalHs = hasHits ? hits.Count(h => h.IsHeadshot) : 0;
            float totalHsPct = totalHits > 0 ? ((float)totalHs / totalHits) * 100f : 0f;
            float avgDist = totalHits > 0 ? hits.Average(h => h.Distance) : 0f;

            string statsText = $"Всего попаданий: <color=#00FFAA><b>{totalHits}</b></color>  |  Общий HS: <color={(totalHsPct > 45 ? "#FFAA00" : "#00FF88")}><b>{totalHsPct:F0}%</b></color>  |  Дальний HS (100м+): <color={(hsPct100 > 60 ? "#FF3333" : "#00FFAA")}><b>{hsPct100:F0}%</b></color>  |  Ср. дистанция: <b>{avgDist:F0}м</b>";
            container.Add(new CuiLabel
            {
                Text = { Text = statsText, FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, summaryCard);

            // Table of Hits
            float rowY = topY - 0.14f;
            float rowH = 0.048f;
            float gap = 0.008f;

            if (!hasHits)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "Нет зафиксированных выстрелов/попаданий данного игрока за текущую сессию.", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.6 0.65 0.75 1" },
                    RectTransform = { AnchorMin = "0.05 0.2", AnchorMax = "0.95 0.5" }
                }, parent);
                return;
            }

            var recentHits = hits.Take(10).ToList();
            for (int i = 0; i < recentHits.Count; i++)
            {
                var h = recentHits[i];
                float yMax = rowY - (i * (rowH + gap));
                float yMin = yMax - rowH;

                string rowId = container.Add(new CuiPanel
                {
                    Image = { Color = i % 2 == 0 ? "0.09 0.10 0.14 0.9" : "0.07 0.08 0.11 0.9" },
                    RectTransform = { AnchorMin = $"0.03 {yMin}", AnchorMax = $"0.97 {yMax}" }
                }, parent);

                int secAgo = Mathf.Max(0, (int)(DateTime.UtcNow - h.Timestamp).TotalSeconds);
                string boneBadge = h.IsHeadshot ? "<color=#FF2222><b>[🎯 HEAD]</b></color>" : (h.BoneName.ToLower().Contains("chest") ? "<color=#00E5FF>[🫁 CHEST]</color>" : $"<color=#AAAAAA>[{h.BoneName}]</color>");
                string distStr = h.Distance >= 100f ? $"<color=#FFDD44><b>{h.Distance:F0}m</b></color>" : $"{h.Distance:F0}m";

                string hitLine = $"#{i + 1} {h.Weapon} ➔ {h.VictimName}  |  Дист: {distStr}  |  {boneBadge}  |  Урон: <color=#00FFAA>-{h.Damage:F1}HP</color>  |  {secAgo}с назад";

                container.Add(new CuiLabel
                {
                    Text = { Text = hitLine, FontSize = 9, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.97 1" }
                }, rowId);
            }
        }

        private void RenderPlayerStaffNotes(CuiElementContainer container, string parent, BasePlayer target, AdminSession session)
        {
            // Quick Add Note Presets
            string presetBar = container.Add(new CuiPanel
            {
                Image = { Color = ColCardInner },
                RectTransform = { AnchorMin = "0.03 0.77", AnchorMax = "0.97 0.88" }
            }, parent);

            container.Add(new CuiButton
            {
                Button = { Color = "0.15 0.65 0.38 0.95", Command = $"am.ui note_quick {target.userID} CLEAN Проверен_чист" },
                RectTransform = { AnchorMin = "0.02 0.15", AnchorMax = "0.24 0.85" },
                Text = { Text = "✓ Чист", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, presetBar);

            container.Add(new CuiButton
            {
                Button = { Color = "0.95 0.60 0.15 0.95", Command = $"am.ui note_quick {target.userID} MACRO Макрос_на_отдачу" },
                RectTransform = { AnchorMin = "0.26 0.15", AnchorMax = "0.48 0.85" },
                Text = { Text = "⚠️ Макрос", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, presetBar);

            container.Add(new CuiButton
            {
                Button = { Color = "0.85 0.20 0.25 0.95", Command = $"am.ui note_quick {target.userID} AIM Подозрение_на_AIM" },
                RectTransform = { AnchorMin = "0.50 0.15", AnchorMax = "0.72 0.85" },
                Text = { Text = "🎯 Подозрение AIM", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, presetBar);

            container.Add(new CuiButton
            {
                Button = { Color = ColDarkPurple, Command = $"am.ui note_quick {target.userID} TOXIC Нарушение_чат_токсик" },
                RectTransform = { AnchorMin = "0.74 0.15", AnchorMax = "0.98 0.85" },
                Text = { Text = "🤐 Токсик", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, presetBar);

            // Custom note input field
            string customBar = container.Add(new CuiPanel
            {
                Image = { Color = ColCardInner },
                RectTransform = { AnchorMin = "0.03 0.67", AnchorMax = "0.97 0.75" }
            }, parent);

            AddInputField(container, customBar, session.NoteDraft, 10, TextAnchor.MiddleLeft, "1 1 1 1", 100, "am.ui input_notedraft ", "0.02 0.1", "0.76 0.9");

            container.Add(new CuiButton
            {
                Button = { Color = "0.18 0.55 0.95 0.95", Command = $"am.ui note_save_draft {target.userID}" },
                RectTransform = { AnchorMin = "0.78 0.1", AnchorMax = "0.98 0.9" },
                Text = { Text = "+ Добавить", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, customBar);

            // Notes History
            bool hasNotes = _staffNotes.TryGetValue(target.userID, out var notes) && notes.Count > 0;
            if (!hasNotes)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "Заметок от администрации по этому игроку пока нет.", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.6 0.65 0.75 1" },
                    RectTransform = { AnchorMin = "0.05 0.2", AnchorMax = "0.95 0.5" }
                }, parent);
                return;
            }

            float rowY = 0.64f;
            float rowH = 0.088f;
            float gap = 0.010f;

            var topNotes = notes.Take(6).ToList();
            for (int i = 0; i < topNotes.Count; i++)
            {
                var n = topNotes[i];
                float yMax = rowY - (i * (rowH + gap));
                float yMin = yMax - rowH;

                string noteCard = container.Add(new CuiPanel
                {
                    Image = { Color = "0.08 0.09 0.13 0.95" },
                    RectTransform = { AnchorMin = $"0.03 {yMin}", AnchorMax = $"0.97 {yMax}" }
                }, parent);

                string tagColor = n.Tag == "CLEAN" ? "#00FFAA" : (n.Tag == "AIM" ? "#FF3333" : (n.Tag == "MACRO" ? "#FFAA00" : "#A855F7"));
                string tagBadge = $"<color={tagColor}><b>[{n.Tag}]</b></color>";
                string timeStr = $"{n.Timestamp:dd.MM.yyyy HH:mm}";

                string headerText = $"{tagBadge} <color=#FFAA00>{n.AuthorName}</color> <color=#888899>({timeStr})</color>";
                container.Add(new CuiLabel
                {
                    Text = { Text = headerText, FontSize = 10, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.03 0.52", AnchorMax = "0.85 0.95" }
                }, noteCard);

                container.Add(new CuiLabel
                {
                    Text = { Text = n.Text, FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.9 0.92 0.98 1" },
                    RectTransform = { AnchorMin = "0.03 0.05", AnchorMax = "0.85 0.48" }
                }, noteCard);

                // Delete button
                container.Add(new CuiButton
                {
                    Button = { Color = "0.6 0.2 0.2 0.8", Command = $"am.ui note_del {target.userID} {n.Id}" },
                    RectTransform = { AnchorMin = "0.88 0.20", AnchorMax = "0.97 0.80" },
                    Text = { Text = "✕", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, noteCard);
            }
        }

        private bool IsAimSuspicious(ulong attackerId, out float hsPercentage, out int total100m, out int hs100m)
        {
            hsPercentage = 0f;
            total100m = 0;
            hs100m = 0;

            if (!_combatHits.TryGetValue(attackerId, out var list) || list.Count == 0)
                return false;

            var hits100mList = list.Where(h => h.Distance >= 100f).ToList();
            total100m = hits100mList.Count;
            if (total100m < 3) return false;

            hs100m = hits100mList.Count(h => h.IsHeadshot);
            hsPercentage = ((float)hs100m / total100m) * 100f;

            return hsPercentage >= 60f;
        }

        private void AddGridButton(CuiElementContainer container, string parent, string text, string color, string command, float xMin, float xMax, float yMax, float h)
        {
            container.Add(new CuiButton
            {
                Button = { Color = color, Command = command },
                RectTransform = { AnchorMin = $"{xMin} {yMax - h}", AnchorMax = $"{xMax} {yMax}" },
                Text = { Text = text, FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, parent);
        }

        private void RenderInventoryRow(CuiElementContainer container, string parent, ItemContainer itemContainer, float yMin, float h, int maxCols)
        {
            if (itemContainer == null) return;
            float slotW = 0.94f / maxCols;
            float yMax = yMin + h;

            for (int i = 0; i < maxCols; i++)
            {
                float xMin = 0.03f + (i * slotW);
                float xMax = xMin + slotW - 0.01f;

                Item item = itemContainer.GetSlot(i);
                string slotColor = item != null ? "0.16 0.20 0.28 0.9" : "0.08 0.09 0.12 0.7";

                string slotId = container.Add(new CuiPanel
                {
                    Image = { Color = slotColor },
                    RectTransform = { AnchorMin = $"{xMin} {yMin}", AnchorMax = $"{xMax} {yMax}" }
                }, parent);

                if (item != null)
                {
                    string shortTitle = item.info.displayName.english;
                    if (shortTitle.Length > 9) shortTitle = shortTitle.Substring(0, 8) + "..";

                    string label = $"{shortTitle}\n<color=#00FFAA>x{item.amount}</color>";
                    container.Add(new CuiLabel
                    {
                        Text = { Text = label, FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }, slotId);
                }
            }
        }

        private void RenderInventoryGrid(CuiElementContainer container, string parent, ItemContainer itemContainer, float startX, float startY, int cols, int rows)
        {
            if (itemContainer == null) return;
            float slotW = 0.94f / cols;
            float slotH = 0.48f / rows;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int slotIdx = (r * cols) + c;
                    float xMin = startX + (c * slotW);
                    float xMax = xMin + slotW - 0.01f;
                    float yMax = startY - (r * slotH);
                    float yMin = yMax - slotH + 0.01f;

                    Item item = itemContainer.GetSlot(slotIdx);
                    string slotColor = item != null ? "0.16 0.20 0.28 0.9" : "0.08 0.09 0.12 0.7";

                    string slotId = container.Add(new CuiPanel
                    {
                        Image = { Color = slotColor },
                        RectTransform = { AnchorMin = $"{xMin} {yMin}", AnchorMax = $"{xMax} {yMax}" }
                    }, parent);

                    if (item != null)
                    {
                        string shortTitle = item.info.displayName.english;
                        if (shortTitle.Length > 8) shortTitle = shortTitle.Substring(0, 7) + "..";

                        string label = $"{shortTitle}\n<color=#00FFAA>x{item.amount}</color>";
                        container.Add(new CuiLabel
                        {
                            Text = { Text = label, FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                            RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                        }, slotId);
                    }
                }
            }
        }

        #endregion

        #region Tab: Self Tools & Inspector

        private void RenderSelfToolsTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session)
        {
            // Left Card: Personal Admin Powers
            string powerCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.48 0.99" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "⚡ ПЕРСОНАЛЬНЫЕ ПРАВА И РЕЖИМЫ", "⚡ PERSONAL ADMIN CONTROLS"), FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.9 0.75 0.2 1" },
                RectTransform = { AnchorMin = "0.05 0.91", AnchorMax = "0.95 0.97" }
            }, powerCard);

            // 1. Vanish 2.0 Mode Button
            bool isVanished = _vanishedPlayers.Contains(admin.userID);
            string vanishText = isVanished ? T(session, "👻 Режим Vanish 2.0 (Полная невидимость): [ВКЛЮЧЕН]", "👻 Vanish 2.0 (Full Invisibility & Silent): [ENABLED]") : T(session, "👻 Режим Vanish 2.0 (Полная невидимость): [ВЫКЛЮЧЕН]", "👻 Vanish 2.0 (Full Invisibility & Silent): [DISABLED]");
            string vanishColor = isVanished ? ColSuccess : "0.75 0.25 0.20 0.95";
            container.Add(new CuiButton
            {
                Button = { Color = vanishColor, Command = "am.ui toggle_vanish" },
                RectTransform = { AnchorMin = "0.05 0.76", AnchorMax = "0.95 0.88" },
                Text = { Text = vanishText, FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, powerCard);

            // 2. Godmode
            bool isGod = _godmodePlayers.Contains(admin.userID);
            string godText = isGod ? T(session, "🛡️ Режим бессмертия (Godmode): [ВКЛЮЧЕН]", "🛡️ Invulnerability (Godmode): [ENABLED]") : T(session, "🛡️ Режим бессмертия (Godmode): [ВЫКЛЮЧЕН]", "🛡️ Invulnerability (Godmode): [DISABLED]");
            string godColor = isGod ? ColSuccess : "0.75 0.25 0.20 0.95";
            container.Add(new CuiButton
            {
                Button = { Color = godColor, Command = "am.ui toggle_god" },
                RectTransform = { AnchorMin = "0.05 0.62", AnchorMax = "0.95 0.74" },
                Text = { Text = godText, FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, powerCard);

            // 3. Noclip
            container.Add(new CuiButton
            {
                Button = { Color = "0.15 0.60 0.40 0.95", Command = "am.ui toggle_noclip" },
                RectTransform = { AnchorMin = "0.05 0.48", AnchorMax = "0.95 0.60" },
                Text = { Text = T(session, "🚀 Переключить Noclip (Полет сквозь стены)", "🚀 Toggle Noclip (Free Flying Mode)"), FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, powerCard);

            // 4. TC & CodeLock Inspector
            container.Add(new CuiButton
            {
                Button = { Color = "0.18 0.50 0.85 0.95", Command = "am.ui inspect_tc" },
                RectTransform = { AnchorMin = "0.05 0.34", AnchorMax = "0.95 0.46" },
                Text = { Text = T(session, "🔐 Проверить шкаф / замок (Луч в прицеле)", "🔐 Inspect TC & CodeLock (Crosshair Raycast)"), FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, powerCard);

            // 5. Heal Self
            container.Add(new CuiButton
            {
                Button = { Color = "0.18 0.65 0.38 0.95", Command = "am.ui heal_self" },
                RectTransform = { AnchorMin = "0.05 0.20", AnchorMax = "0.95 0.32" },
                Text = { Text = T(session, "❤️ Восстановить здоровье и калории (100%)", "❤️ Heal & Feed Vitals (100%)"), FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, powerCard);

            // Right Card: Kits & Loadouts
            string kitCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0.52 0", AnchorMax = "1 0.99" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "🎒 БЫСТРАЯ ВЫДАЧА СНАРЯЖЕНИЯ", "🎒 QUICK EQUIPMENT LOADOUTS"), FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.2 0.7 0.95 1" },
                RectTransform = { AnchorMin = "0.05 0.91", AnchorMax = "0.95 0.97" }
            }, kitCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.75 0.35 0.15 0.95", Command = "am.ui kit_pvp" },
                RectTransform = { AnchorMin = "0.05 0.74", AnchorMax = "0.95 0.87" },
                Text = { Text = T(session, "⚔️ Выдать PvP Сет (AK-47 + Металл Сет)", "⚔️ Give Full Combat PvP Kit (AK + Full Metal)"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, kitCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.25 0.55 0.80 0.95", Command = "am.ui kit_builder" },
                RectTransform = { AnchorMin = "0.05 0.58", AnchorMax = "0.95 0.71" },
                Text = { Text = T(session, "🏗️ Выдать Строительный Сет (План + Ресурсы)", "🏗️ Give Builder Kit (Plan, Hammer, Resources)"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, kitCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.85 0.25 0.25 0.95", Command = "am.ui clear_self_inv" },
                RectTransform = { AnchorMin = "0.05 0.42", AnchorMax = "0.95 0.55" },
                Text = { Text = T(session, "🎒 Очистить мой инвентарь", "🎒 Clear My Inventory"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, kitCard);
        }

        private void ToggleNoclipDirect(BasePlayer player)
        {
            if (player == null) return;
            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
            player.SendNetworkUpdateImmediate();
            CloseAdminMenu(player);
            rust.RunClientCommand(player, "noclip");
            AdminSession session = GetSession(player.userID);
            SendReply(player, session.Language == "RU"
                ? "<color=#00FF88>[ADMIN]</color> Режим <b>Noclip</b> переключен! (Меню закрыто для полета. Пробел - лететь, Shift - ускорение)"
                : "<color=#00FF88>[ADMIN]</color> <b>Noclip</b> toggled! (Menu closed for flight. Space - fly, Shift - speed boost)");
        }

        private void ToggleVanish(BasePlayer player)
        {
            if (player == null) return;
            AdminSession session = GetSession(player.userID);

            if (_vanishedPlayers.Contains(player.userID))
            {
                _vanishedPlayers.Remove(player.userID);
                player.limitNetworking = false;
                player.SendNetworkUpdateImmediate();

                CuiHelper.DestroyUi(player, PanelVanishHUD);
                SendReply(player, session.Language == "RU"
                    ? "<color=#FFAA00>[ADMIN]</color> Режим <b>Vanish 2.0</b> <color=#FF4444>ОТКЛЮЧЕН</color>. Вы снова видимы и издаете звуки."
                    : "<color=#FFAA00>[ADMIN]</color> <b>Vanish 2.0</b> <color=#FF4444>DISABLED</color>. You are now visible.");
            }
            else
            {
                _vanishedPlayers.Add(player.userID);
                _godmodePlayers.Add(player.userID);
                player.limitNetworking = true;

                if (player.net?.group?.subscribers != null)
                {
                    List<Connection> subscribers = new List<Connection>();
                    foreach (Connection conn in player.net.group.subscribers)
                    {
                        if (conn != player.net.connection)
                        {
                            subscribers.Add(conn);
                        }
                    }

                    if (subscribers.Count > 0)
                    {
                        var write = Network.Net.sv.StartWrite();
                        write.PacketID(Network.Message.Type.EntityDestroy);
                        write.EntityID(player.net.ID);
                        write.UInt8((byte)BaseNetworkable.DestroyMode.None);
                        write.Send(new SendInfo(subscribers));
                    }
                }

                RenderVanishHud(player);
                SendReply(player, session.Language == "RU"
                    ? "<color=#00FF88>[ADMIN]</color> Режим <b>Vanish 2.0</b> <color=#00FF88>ВКЛЮЧЕН</color>! (Полная невидимость, глушение шагов, дверей и ящиков, игнор турелями)."
                    : "<color=#00FF88>[ADMIN]</color> <b>Vanish 2.0</b> <color=#00FF88>ENABLED</color>! (Fully invisible, silent interactions, turrets ignore).");
            }

            if (session.ActiveTab == "self")
            {
                OpenAdminMenu(player);
            }
        }

        private void RenderVanishHud(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, PanelVanishHUD);

            CuiElementContainer container = new CuiElementContainer();
            string hud = container.Add(new CuiPanel
            {
                Image = { Color = "0.07 0.08 0.12 0.92" },
                RectTransform = { AnchorMin = "0.36 0.912", AnchorMax = "0.64 0.942" },
                CursorEnabled = false
            }, "Hud", PanelVanishHUD);

            container.Add(new CuiPanel
            {
                Image = { Color = ColNeonPurple },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.08" }
            }, hud);

            container.Add(new CuiLabel
            {
                Text = { Text = "👻 <b>VANISH 2.0</b> <color=#00FF88>АКТИВЕН</color> • НЕВИДИМ • ТИХИЙ РЕЖИМ (/v)", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "0.92 0.82 1 1" },
                RectTransform = { AnchorMin = "0 0.08", AnchorMax = "1 1" }
            }, hud);

            CuiHelper.AddUi(player, container);
        }

        private void InspectTargetStructure(BasePlayer admin)
        {
            if (admin == null) return;
            RaycastHit hit;
            if (!Physics.Raycast(admin.eyes.HeadRay(), out hit, 60f, LayerMask.GetMask("Construction", "Deployed", "Default")))
            {
                SendReply(admin, "<color=#FFAA00>[INSPECTOR]</color> Ничего не обнаружено в прицеле. Подойдите ближе или наведитесь на постройку/шкаф/замок.");
                return;
            }

            BaseEntity ent = hit.GetEntity();
            if (ent == null)
            {
                SendReply(admin, "<color=#FFAA00>[INSPECTOR]</color> Объект не является игровой сущностью.");
                return;
            }

            BuildingPrivlidge priv = ent as BuildingPrivlidge;
            CodeLock codeLock = ent as CodeLock;

            if (priv == null && ent is BuildingBlock block)
            {
                priv = block.GetBuildingPrivilege();
            }
            if (priv == null && ent is DecayEntity decayEnt)
            {
                priv = decayEnt.GetBuildingPrivilege();
            }
            if (codeLock == null)
            {
                codeLock = ent.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
            }

            if (priv == null && codeLock == null)
            {
                SendReply(admin, $"<color=#FFAA00>[INSPECTOR]</color> Объект: <color=#FFFF00>{ent.ShortPrefabName}</color>, но к нему не привязан шкаф или кодовый замок.");
                return;
            }

            RenderTCInspectorModal(admin, ent, priv, codeLock);
        }

        private void RenderTCInspectorModal(BasePlayer admin, BaseEntity ent, BuildingPrivlidge priv, CodeLock codeLock)
        {
            CuiHelper.DestroyUi(admin, PanelModal);

            CuiElementContainer container = new CuiElementContainer();

            // Background Dim
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.85", Material = "assets/content/ui/uibackgroundblur.mat" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", PanelModal);

            // Modal Card
            string modal = container.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.09 0.13 0.98" },
                RectTransform = { AnchorMin = "0.20 0.15", AnchorMax = "0.80 0.85" }
            }, PanelModal, "AM_InspectorModal");

            // Top Cyan Accent
            container.Add(new CuiPanel
            {
                Image = { Color = "0.18 0.58 0.95 1" },
                RectTransform = { AnchorMin = "0 0.985", AnchorMax = "1 1" }
            }, modal);

            // Title
            container.Add(new CuiLabel
            {
                Text = { Text = "🔐 ИНСПЕКТОР СТРОЕНИЯ: ШКАФ И КОДОВЫЙ ЗАМОК", FontSize = 15, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.04 0.91", AnchorMax = "0.75 0.97" }
            }, modal);

            // Close button
            container.Add(new CuiButton
            {
                Button = { Color = ColDanger, Command = "am.ui close_modal" },
                RectTransform = { AnchorMin = "0.93 0.91", AnchorMax = "0.975 0.97" },
                Text = { Text = "✕", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, modal);

            // Header info box
            string headerBox = container.Add(new CuiPanel
            {
                Image = { Color = ColCardInner },
                RectTransform = { AnchorMin = "0.04 0.77", AnchorMax = "0.96 0.89" }
            }, modal);

            Vector3 pos = ent.transform.position;
            string grid = GetGrid(pos);
            string entInfo = $"Объект: <color=#00FFAA><b>{ent.ShortPrefabName}</b></color>  |  Квадрат: <color=#FFFF00><b>{grid}</b></color> (X: {pos.x:F0}, Z: {pos.z:F0})  |  ХП: <color=#00FF88>{(int)ent.Health()}/{(int)ent.MaxHealth()}</color>";

            if (codeLock != null)
            {
                entInfo += $"\nКодовый замок: Код <color=#00FFAA><b>{codeLock.code}</b></color>  |  Гостевой: <color=#FFAA00><b>{codeLock.guestCode}</b></color>  |  Авторизовано: <b>{codeLock.whitelistPlayers.Count}</b>";
            }

            container.Add(new CuiLabel
            {
                Text = { Text = entInfo, FontSize = 10, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.97 1" }
            }, headerBox);

            // TC Info & Authorized Players
            if (priv != null)
            {
                float protectedMinutes = priv.GetProtectedMinutes();
                string upkeepStr = protectedMinutes > 0 ? $"{(int)(protectedMinutes / 60)}ч {((int)protectedMinutes % 60)}м" : "Гниет (0 ч)";

                container.Add(new CuiLabel
                {
                    Text = { Text = $"Шкаф строения: Защита Upkeep: <color=#00FFAA><b>{upkeepStr}</b></color>  |  Авторизовано игроков: <color=#FFFF00><b>{priv.authorizedPlayers.Count}</b></color>", FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.9 0.75 0.25 1" },
                    RectTransform = { AnchorMin = "0.04 0.71", AnchorMax = "0.96 0.76" }
                }, modal);

                // Authorized Players Table
                string tableCard = container.Add(new CuiPanel
                {
                    Image = { Color = ColCardInner },
                    RectTransform = { AnchorMin = "0.04 0.14", AnchorMax = "0.96 0.70" }
                }, modal);

                if (priv.authorizedPlayers.Count == 0)
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = "В шкафу нет авторизованных игроков (Пустой шкаф).", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.6 0.65 0.75 1" },
                        RectTransform = { AnchorMin = "0.05 0.3", AnchorMax = "0.95 0.7" }
                    }, tableCard);
                }
                else
                {
                    float rowY = 0.95f;
                    float rowH = 0.14f;
                    float gap = 0.02f;

                    var authList = priv.authorizedPlayers.Take(6).ToList();
                    for (int i = 0; i < authList.Count; i++)
                    {
                        var ap = authList[i];
                        float yMax = rowY - (i * (rowH + gap));
                        float yMin = yMax - rowH;

                        string rowId = container.Add(new CuiPanel
                        {
                            Image = { Color = i % 2 == 0 ? "0.09 0.10 0.15 0.9" : "0.07 0.08 0.12 0.9" },
                            RectTransform = { AnchorMin = $"0.02 {yMin}", AnchorMax = $"0.98 {yMax}" }
                        }, tableCard);

                        ulong authSteamId = ap;
                        BasePlayer targetP = BasePlayer.FindByID(authSteamId);
                        string pName = targetP != null ? targetP.displayName : authSteamId.ToString();
                        bool isOnline = (targetP != null && targetP.IsConnected);
                        string statusBadge = isOnline ? "<color=#00FFAA>[ОНЛАЙН]</color>" : "<color=#888899>[ОФЛАЙН]</color>";

                        string teamInfo = "";
                        var team = RelationshipManager.ServerInstance.FindPlayersTeam(authSteamId);
                        if (team != null)
                        {
                            teamInfo = $" | Клан/Команда: {team.members.Count} чел.";
                        }

                        string pLine = $"{statusBadge} <b>{pName}</b>  |  SteamID: <color=#FFFF00>{authSteamId}</color>{teamInfo}";

                        container.Add(new CuiLabel
                        {
                            Text = { Text = pLine, FontSize = 10, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                            RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.75 1" }
                        }, rowId);

                        // TP to this player button
                        container.Add(new CuiButton
                        {
                            Button = { Color = "0.15 0.62 0.38 0.95", Command = $"am.ui quick_tp {authSteamId}" },
                            RectTransform = { AnchorMin = "0.78 0.15", AnchorMax = "0.97 0.85" },
                            Text = { Text = "⚡ ТП к игроку", FontSize = 9, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                        }, rowId);
                    }
                }

                // Action Buttons at bottom
                container.Add(new CuiButton
                {
                    Button = { Color = "0.15 0.62 0.38 0.95", Command = $"am.ui tp_monument {priv.transform.position.x.ToString(CultureInfo.InvariantCulture)} {(priv.transform.position.y + 1f).ToString(CultureInfo.InvariantCulture)} {priv.transform.position.z.ToString(CultureInfo.InvariantCulture)}" },
                    RectTransform = { AnchorMin = "0.04 0.03", AnchorMax = "0.48 0.11" },
                    Text = { Text = "⚡ Телепортироваться к шкафу", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, modal);

                container.Add(new CuiButton
                {
                    Button = { Color = "0.85 0.20 0.25 0.95", Command = $"am.ui tc_clear {priv.net.ID}" },
                    RectTransform = { AnchorMin = "0.52 0.03", AnchorMax = "0.96 0.11" },
                    Text = { Text = "🧹 Очистить авторизацию шкафа", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, modal);
            }

            CuiHelper.AddUi(admin, container);
        }

        #endregion

        #region Tab: Server Control (Time, Weather, Maintenance)

        private void RenderServerControlTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "🌍 УПРАВЛЕНИЕ СЕРВЕРОМ, ВРЕМЕНЕМ И ПОГОДОЙ", "🌍 SERVER CONTROL, TIME & WEATHER ENGINE"), FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.9 0.75 0.2 1" },
                RectTransform = { AnchorMin = "0.02 0.94", AnchorMax = "0.98 0.99" }
            }, parent);

            // Row 1: Time of Day Cards
            string timeCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0.52", AnchorMax = "0.48 0.92" }
            }, parent);

            float curTime = TOD_Sky.Instance != null ? TOD_Sky.Instance.Cycle.Hour : 12f;
            container.Add(new CuiLabel
            {
                Text = { Text = $"{T(session, "☀️ ВРЕМЯ СУТОК (ТЕКУЩЕЕ:", "☀️ TIME OF DAY (CURRENT:")} <color=#00FFAA>{(int)curTime:D2}:{(int)((curTime % 1) * 60):D2}</color>)", FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.82", AnchorMax = "0.95 0.95" }
            }, timeCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.85 0.60 0.15 0.95", Command = "am.ui set_time 12" },
                RectTransform = { AnchorMin = "0.05 0.55", AnchorMax = "0.48 0.78" },
                Text = { Text = "☀️ Полдень (12:00)", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, timeCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.85 0.40 0.15 0.95", Command = "am.ui set_time 8" },
                RectTransform = { AnchorMin = "0.52 0.55", AnchorMax = "0.95 0.78" },
                Text = { Text = "🌅 Утро (08:00)", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, timeCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.75 0.25 0.45 0.95", Command = "am.ui set_time 19" },
                RectTransform = { AnchorMin = "0.05 0.25", AnchorMax = "0.48 0.48" },
                Text = { Text = "🌇 Закат (19:00)", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, timeCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.15 0.18 0.35 0.95", Command = "am.ui set_time 0" },
                RectTransform = { AnchorMin = "0.52 0.25", AnchorMax = "0.95 0.48" },
                Text = { Text = "🌙 Полночь (00:00)", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, timeCard);

            // Row 2: Weather Cards
            string weatherCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0.52 0.52", AnchorMax = "1 0.92" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "⛅ ПРИНУДИТЕЛЬНАЯ ПОГОДА", "⛅ WEATHER OVERRIDE"), FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.82", AnchorMax = "0.95 0.95" }
            }, weatherCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.15 0.65 0.40 0.95", Command = "am.ui set_weather clear" },
                RectTransform = { AnchorMin = "0.05 0.55", AnchorMax = "0.48 0.78" },
                Text = { Text = "☀️ Ясно (Clear)", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, weatherCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.20 0.50 0.80 0.95", Command = "am.ui set_weather rain" },
                RectTransform = { AnchorMin = "0.52 0.55", AnchorMax = "0.95 0.78" },
                Text = { Text = "🌧️ Дождь (Rain)", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, weatherCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.35 0.40 0.55 0.95", Command = "am.ui set_weather fog" },
                RectTransform = { AnchorMin = "0.05 0.25", AnchorMax = "0.48 0.48" },
                Text = { Text = "🌫️ Туман (Fog)", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, weatherCard);

            container.Add(new CuiButton
            {
                Button = { Color = "0.45 0.55 0.75 0.95", Command = "am.ui set_weather storm" },
                RectTransform = { AnchorMin = "0.52 0.25", AnchorMax = "0.95 0.48" },
                Text = { Text = "⚡ Гроза (Storm)", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, weatherCard);

            // Bottom Maintenance Card
            string maintCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.48" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "🛠️ ОБСЛУЖИВАНИЕ И СЕРВЕРНЫЕ КОМАНДЫ", "🛠️ MAINTENANCE & EXECUTIVE ACTIONS"), FontSize = 12, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.85 0.35 0.35 1" },
                RectTransform = { AnchorMin = "0.03 0.80", AnchorMax = "0.97 0.92" }
            }, maintCard);

            // Server Save
            container.Add(new CuiButton
            {
                Button = { Color = "0.15 0.65 0.38 0.95", Command = "am.ui srv_save" },
                RectTransform = { AnchorMin = "0.03 0.45", AnchorMax = "0.31 0.72" },
                Text = { Text = T(session, "💾 Сохранить мир (server.save)", "💾 Save Map World"), FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, maintCard);

            // Garbage Collection
            container.Add(new CuiButton
            {
                Button = { Color = "0.20 0.50 0.85 0.95", Command = "am.ui srv_gc" },
                RectTransform = { AnchorMin = "0.34 0.45", AnchorMax = "0.62 0.72" },
                Text = { Text = T(session, "🧹 Очистить RAM (GC Collect)", "🧹 Garbage Collect RAM"), FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, maintCard);

            // Restart in 5 min
            container.Add(new CuiButton
            {
                Button = { Color = "0.85 0.20 0.25 0.95", Command = "am.ui srv_restart 300" },
                RectTransform = { AnchorMin = "0.65 0.45", AnchorMax = "0.97 0.72" },
                Text = { Text = T(session, "⏱️ Перезапуск через 5 мин", "⏱️ Restart in 5 Minutes"), FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, maintCard);
        }

        #endregion

        #region Tab: World Events

        private void RenderEventsTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "🚁 ПРИНУДИТЕЛЬНЫЙ ВЫЗОВ ИВЕНТОВ КАРТЫ", "🚁 MANUAL WORLD EVENTS TRIGGER"), FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.9 0.75 0.2 1" },
                RectTransform = { AnchorMin = "0.02 0.94", AnchorMax = "0.98 0.99" }
            }, parent);

            float startY = 0.88f;
            float cardH = 0.14f;
            float gap = 0.02f;

            AddEventCard(container, parent, "🚢 Корабль (Cargo Ship)", "Призывает грузовой океанский танкер ученых на карту.", "am.ui event cargoship", startY, cardH, session);
            startY -= (cardH + gap);

            AddEventCard(container, parent, "🚁 Боевой вертолет (Patrol Helicopter)", "Призывает патрульный боевой вертолет ВВС Rust.", "am.ui event heli", startY, cardH, session);
            startY -= (cardH + gap);

            AddEventCard(container, parent, "📦 Вертолет с грузом (Chinook CH47)", "Вызывает транспортный чинук со сбросом запертого ящика.", "am.ui event ch47", startY, cardH, session);
            startY -= (cardH + gap);

            AddEventCard(container, parent, "✈️ Самолет со сбросом (AirDrop Plane)", "Вызывает грузовой самолет со сбросом припасов на парашюте.", "am.ui event airdrop", startY, cardH, session);
            startY -= (cardH + gap);

            AddEventCard(container, parent, "🚜 Танк Космодрома (Bradley APC)", "Респавнит бронетранспортер Брэдли на Космодроме.", "am.ui event bradley", startY, cardH, session);
        }

        private void AddEventCard(CuiElementContainer container, string parent, string title, string description, string command, float yMax, float h, AdminSession session)
        {
            string card = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = $"0 {yMax - h}", AnchorMax = $"1 {yMax}" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 12, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.03 0.55", AnchorMax = "0.75 0.95" }
            }, card);

            container.Add(new CuiLabel
            {
                Text = { Text = description, FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.7 0.75 0.85 1" },
                RectTransform = { AnchorMin = "0.03 0.10", AnchorMax = "0.75 0.50" }
            }, card);

            container.Add(new CuiButton
            {
                Button = { Color = ColDarkPurple, Command = command },
                RectTransform = { AnchorMin = "0.80 0.20", AnchorMax = "0.97 0.80" },
                Text = { Text = T(session, "⚡ Запустить", "⚡ Trigger"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, card);
        }

        #endregion

        #region Tab: Plugins (Config Editor)

        private void RenderPluginsTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session)
        {
            string topBar = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 0.99" }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "🔌 ПЛАГИНЫ СЕРВЕРА И РЕДАКТОР КОНФИГУРАЦИЙ", "🔌 SERVER PLUGINS & LIVE IN-GAME CONFIG EDITOR"), FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.95 0.75 0.25 1" },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.70 1" }
            }, topBar);

            container.Add(new CuiButton
            {
                Button = { Color = ColDarkPurple, Command = "am.ui refresh_plugins" },
                RectTransform = { AnchorMin = "0.85 0.15", AnchorMax = "0.98 0.85" },
                Text = { Text = T(session, "🔄 Обновить", "🔄 Refresh"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, topBar);

            var pluginList = plugins.GetAll().ToList();
            int perPage = 7;
            int totalPages = Mathf.Max(1, Mathf.CeilToInt((float)pluginList.Count / perPage));
            if (session.PluginPage >= totalPages) session.PluginPage = totalPages - 1;
            if (session.PluginPage < 0) session.PluginPage = 0;

            var pagePlugins = pluginList.Skip(session.PluginPage * perPage).Take(perPage).ToList();

            float rowY = 0.88f;
            float rowH = 0.105f;
            float gap = 0.012f;

            foreach (var p in pagePlugins)
            {
                float yMin = rowY - rowH;
                string rowId = container.Add(new CuiPanel
                {
                    Image = { Color = ColCardBg },
                    RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {rowY}" }
                }, parent);

                string pTitle = $"<color=#00FFAA><b>{p.Name}</b></color> <color=#888899>v{p.Version}</color> by <color=#FFAA00>{p.Author}</color>\n<color=#CCCCCC>{p.Description}</color>";
                container.Add(new CuiLabel
                {
                    Text = { Text = pTitle, FontSize = 11, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.68 1" }
                }, rowId);

                // Reload button
                container.Add(new CuiButton
                {
                    Button = { Color = "0.15 0.60 0.38 0.95", Command = $"am.ui plugin_reload {p.Name}" },
                    RectTransform = { AnchorMin = "0.70 0.20", AnchorMax = "0.82 0.80" },
                    Text = { Text = "🔄 Reload", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                // Edit Config button
                container.Add(new CuiButton
                {
                    Button = { Color = ColDarkPurple, Command = $"am.ui plugin_config {p.Name}" },
                    RectTransform = { AnchorMin = "0.83 0.20", AnchorMax = "0.98 0.80" },
                    Text = { Text = "⚙️ Config ▶", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                }, rowId);

                rowY -= (rowH + gap);
            }

            // Pagination
            if (totalPages > 1)
            {
                float pY = 0.01f;
                container.Add(new CuiButton
                {
                    Button = { Color = session.PluginPage > 0 ? ColDarkPurple : "0.15 0.16 0.22 0.8", Command = $"am.ui plugin_page {session.PluginPage - 1}" },
                    RectTransform = { AnchorMin = $"0.35 {pY}", AnchorMax = $"0.45 {pY + 0.05f}" },
                    Text = { Text = T(session, "◀ Назад", "◀ Prev"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, parent);

                container.Add(new CuiLabel
                {
                    Text = { Text = $"{session.PluginPage + 1} / {totalPages}", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = $"0.46 {pY}", AnchorMax = $"0.54 {pY + 0.05f}" }
                }, parent);

                container.Add(new CuiButton
                {
                    Button = { Color = session.PluginPage < totalPages - 1 ? ColDarkPurple : "0.15 0.16 0.22 0.8", Command = $"am.ui plugin_page {session.PluginPage + 1}" },
                    RectTransform = { AnchorMin = $"0.55 {pY}", AnchorMax = $"0.65 {pY + 0.05f}" },
                    Text = { Text = T(session, "Вперед ▶", "Next ▶"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, parent);
            }
        }

        private void RenderPluginConfigEditorTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session)
        {
            string topBar = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 0.99" }
            }, parent);

            container.Add(new CuiButton
            {
                Button = { Color = ColDarkPurple, Command = "am.ui tab plugins" },
                RectTransform = { AnchorMin = "0.015 0.15", AnchorMax = "0.14 0.85" },
                Text = { Text = T(session, "◀ К плагинам", "◀ Back to Plugins"), FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, topBar);

            container.Add(new CuiLabel
            {
                Text = { Text = $"Конфиг плагина: <color=#00FFAA><b>{session.SelectedPlugin}.json</b></color>", FontSize = 12, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.16 0", AnchorMax = "0.75 1" }
            }, topBar);

            // Save Config
            container.Add(new CuiButton
            {
                Button = { Color = session.IsConfigDirty ? ColSuccess : "0.15 0.18 0.25 0.8", Command = "am.ui config_save" },
                RectTransform = { AnchorMin = "0.82 0.15", AnchorMax = "0.98 0.85" },
                Text = { Text = T(session, "💾 Сохранить и применить", "💾 Save & Reload"), FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, topBar);

            // Config fields rows
            float rowY = 0.88f;
            float rowH = 0.095f;
            float gap = 0.012f;

            if (session.ConfigFields == null || session.ConfigFields.Count == 0)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "Конфигурационный файл не найден или не содержит строковых/числовых параметров верхнего уровня.", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.6 0.65 0.75 1" },
                    RectTransform = { AnchorMin = "0.05 0.4", AnchorMax = "0.95 0.6" }
                }, parent);
                return;
            }

            for (int i = 0; i < session.ConfigFields.Count; i++)
            {
                if (i >= 8) break; // Display top 8 fields per view
                var field = session.ConfigFields[i];

                float yMin = rowY - rowH;
                string rowId = container.Add(new CuiPanel
                {
                    Image = { Color = ColCardBg },
                    RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {rowY}" }
                }, parent);

                string keyText = $"<color=#00FFAA><b>{field.Key}</b></color>\n<color=#888899>Тип: {field.Type}</color>";
                container.Add(new CuiLabel
                {
                    Text = { Text = keyText, FontSize = 10, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.60 1" }
                }, rowId);

                if (field.Type == FieldType.Boolean)
                {
                    bool bVal = field.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    string bColor = bVal ? ColSuccess : "0.75 0.25 0.20 0.95";
                    string bText = bVal ? "TRUE (ВКЛ)" : "FALSE (ВЫКЛ)";

                    container.Add(new CuiButton
                    {
                        Button = { Color = bColor, Command = $"am.ui config_toggle_bool {i}" },
                        RectTransform = { AnchorMin = "0.65 0.18", AnchorMax = "0.95 0.82" },
                        Text = { Text = bText, FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
                    }, rowId);
                }
                else
                {
                    container.Add(new CuiButton
                    {
                        Button = { Color = "0.15 0.18 0.25 0.9", Command = $"am.ui config_edit_string {i}" },
                        RectTransform = { AnchorMin = "0.65 0.18", AnchorMax = "0.95 0.82" },
                        Text = { Text = $"✏️ {field.Value}", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                    }, rowId);
                }

                rowY -= (rowH + gap);
            }
        }

        private void RenderTextInputModal(CuiElementContainer container, string parent, AdminSession session)
        {
            var field = session.ConfigFields[session.EditingFieldIndex];

            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.7" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, parent, PanelModal);

            string modal = container.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.11 0.16 0.98" },
                RectTransform = { AnchorMin = "0.25 0.35", AnchorMax = "0.75 0.65" }
            }, PanelModal);

            container.Add(new CuiLabel
            {
                Text = { Text = $"Редактирование: <color=#00FFAA>{field.Key}</color>", FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.7", AnchorMax = "0.95 0.9" }
            }, modal);

            string inputWell = container.Add(new CuiPanel
            {
                Image = { Color = ColCardInner },
                RectTransform = { AnchorMin = "0.05 0.35", AnchorMax = "0.95 0.65" }
            }, modal);

            AddInputField(container, inputWell, field.Value, 11, TextAnchor.MiddleLeft, "1 1 1 1", 120, "am.ui config_input_val ", "0.03 0.1", "0.97 0.9");

            container.Add(new CuiButton
            {
                Button = { Color = ColSuccess, Command = "am.ui config_confirm_edit" },
                RectTransform = { AnchorMin = "0.55 0.08", AnchorMax = "0.95 0.28" },
                Text = { Text = "✓ Применить", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, modal);

            container.Add(new CuiButton
            {
                Button = { Color = ColDanger, Command = "am.ui config_cancel_edit" },
                RectTransform = { AnchorMin = "0.05 0.08", AnchorMax = "0.45 0.28" },
                Text = { Text = "✕ Отмена", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, modal);
        }

        #endregion

        #region Tab: Diagnostics & Telemetry

        private void RenderDiagnosticsTab(CuiElementContainer container, string parent, BasePlayer admin, AdminSession session)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = T(session, "📊 ДИАГНОСТИКА СЕРВЕРА И МОНИТОРИНГ РЕСУРСОВ", "📊 SYSTEM DIAGNOSTICS & HARDWARE TELEMETRY"), FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.9 0.75 0.2 1" },
                RectTransform = { AnchorMin = "0.02 0.94", AnchorMax = "0.98 0.99" }
            }, parent);

            string diagCard = container.Add(new CuiPanel
            {
                Image = { Color = ColCardBg },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.92" }
            }, parent);

            int online = BasePlayer.activePlayerList.Count;
            int sleepers = BasePlayer.sleepingPlayerList.Count;
            int ents = BaseNetworkable.serverEntities.Count;
            int fps = Performance.report.frameRate;
            long memMb = GC.GetTotalMemory(false) / 1024 / 1024;
            int uptimeMins = (int)(Time.realtimeSinceStartup / 60);

            string diagA = $"<b>ПРОИЗВОДИТЕЛЬНОСТЬ СЕРВЕРА:</b>\n\n" +
                           $"• Серверный FPS: <color=#00FF88><b>{fps} FPS</b></color>\n" +
                           $"• Занято памяти (Mono RAM): <color=#00E5FF><b>{memMb} MB</b></color>\n" +
                           $"• Общее количество энтити: <color=#FFAA00><b>{ents:N0}</b></color>\n" +
                           $"• Время работы без рестарта: <color=#C084FC><b>{uptimeMins / 60}ч {uptimeMins % 60}м</b></color>\n" +
                           $"• Активных таймеров / сессий: <b>{_sessions.Count}</b>\n\n" +
                           $"<b>ОНЛАЙН И ИГРОКИ:</b>\n\n" +
                           $"• Игроков онлайн: <color=#00FFAA><b>{online} / {ConVar.Server.maxplayers}</b></color>\n" +
                           $"• Спящих на карте: <color=#FFFF00><b>{sleepers}</b></color>\n" +
                           $"• В режиме невидимости (Vanish 2.0): <color=#C084FC><b>{_vanishedPlayers.Count}</b></color>\n" +
                           $"• Замороженных на месте: <color=#38BDF8><b>{_frozenPlayers.Count}</b></color>";

            container.Add(new CuiLabel
            {
                Text = { Text = diagA, FontSize = 12, Align = TextAnchor.UpperLeft, Font = "robotocondensed-bold.ttf", Color = "0.9 0.92 0.98 1" },
                RectTransform = { AnchorMin = "0.04 0.1", AnchorMax = "0.55 0.95" }
            }, diagCard);

            string diagB = $"<b>МОДУЛИ МОДЕРАЦИИ И БЕЗОПАСНОСТИ:</b>\n\n" +
                           $"• Заметок персонала в базе: <color=#00FFAA><b>{_staffNotes.Values.Sum(v => v.Count)}</b></color>\n" +
                           $"• Отслеживаемых боев в CombatLog: <color=#00E5FF><b>{_combatHits.Count} игроков</b></color>\n" +
                           $"• Проиндексировано монументов: <color=#FFAA00><b>{_monumentsList.Count}</b></color>\n" +
                           $"• Плагин DiscordReports: <color={(plugins.Find("DiscordReports") != null ? "#00FF88" : "#FF5555")}><b>{(plugins.Find("DiscordReports") != null ? "ПОДКЛЮЧЕН" : "НЕ НАЙДЕН")}</b></color>\n" +
                           $"• Версия системы: <color=#C084FC><b>AdminMenu v1.3.0</b></color>";

            container.Add(new CuiLabel
            {
                Text = { Text = diagB, FontSize = 12, Align = TextAnchor.UpperLeft, Font = "robotocondensed-bold.ttf", Color = "0.9 0.92 0.98 1" },
                RectTransform = { AnchorMin = "0.58 0.1", AnchorMax = "0.96 0.95" }
            }, diagCard);
        }

        #endregion

        #region UI Console Command Dispatcher

        [ConsoleCommand("am.ui")]
        private void CmdConsoleUi(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !IsAdmin(player)) return;

            if (!arg.HasArgs(1)) return;

            AdminSession session = GetSession(player.userID);
            bool isMaster = IsMasterAdmin(player);
            string action = arg.GetString(0).ToLower();

            switch (action)
            {
                case "close":
                    CloseAdminMenu(player);
                    break;

                case "close_modal":
                    CuiHelper.DestroyUi(player, PanelModal);
                    break;

                case "toggle_lang":
                    session.Language = (session.Language == "RU") ? "EN" : "RU";
                    OpenAdminMenu(player);
                    break;

                case "tab":
                    if (arg.HasArgs(2))
                    {
                        session.ActiveTab = arg.GetString(1).ToLower();
                        RefreshContent(player);
                    }
                    break;

                case "filter":
                    if (arg.HasArgs(2))
                    {
                        session.PlayerFilter = arg.GetString(1).ToLower();
                        session.Page = 0;
                        RefreshContent(player);
                    }
                    break;

                case "page":
                    if (arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int p))
                    {
                        session.Page = p;
                        RefreshContent(player);
                    }
                    break;

                case "monument_page":
                    if (arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int mp))
                    {
                        session.MonumentsPage = mp;
                        RefreshContent(player);
                    }
                    break;

                case "rescan_monuments":
                    ScanMonuments();
                    OpenAdminMenu(player);
                    break;

                case "tp_monument":
                    if (arg.HasArgs(4) &&
                        float.TryParse(arg.GetString(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float mx) &&
                        float.TryParse(arg.GetString(2), NumberStyles.Float, CultureInfo.InvariantCulture, out float my) &&
                        float.TryParse(arg.GetString(3), NumberStyles.Float, CultureInfo.InvariantCulture, out float mz))
                    {
                        CloseAdminMenu(player);
                        player.Teleport(new Vector3(mx, my + 1.2f, mz));
                        SendReply(player, $"<color=#00FF88>[ADMIN]</color> Телепортирован на монумент ({mx:F0}, {my:F0}, {mz:F0}).");
                    }
                    break;

                case "inspect":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong targetId))
                    {
                        session.SelectedPlayerId = targetId;
                        session.PlayerDetailSubView = "inventory";
                        session.ActiveTab = "player_detail";
                        RefreshContent(player);
                    }
                    break;

                case "pview":
                    if (arg.HasArgs(2))
                    {
                        session.PlayerDetailSubView = arg.GetString(1).ToLower();
                        RefreshContent(player);
                    }
                    break;

                case "note_quick":
                    if (arg.HasArgs(3) && ulong.TryParse(arg.GetString(1), out ulong noteTargetId))
                    {
                        string tag = arg.GetString(2);
                        string text = arg.HasArgs(4) ? arg.GetString(3).Replace("_", " ") : "Заметка";
                        AddStaffNote(noteTargetId, player.displayName, text, tag);
                        RefreshContent(player);
                    }
                    break;

                case "input_notedraft":
                    if (arg.Args != null && arg.Args.Length > 1)
                    {
                        session.NoteDraft = string.Join(" ", arg.Args.Skip(1)).Trim();
                    }
                    break;

                case "note_save_draft":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong ndTargetId) && !string.IsNullOrEmpty(session.NoteDraft))
                    {
                        AddStaffNote(ndTargetId, player.displayName, session.NoteDraft, "NOTE");
                        session.NoteDraft = "";
                        OpenAdminMenu(player);
                    }
                    break;

                case "note_del":
                    if (arg.HasArgs(3) && ulong.TryParse(arg.GetString(1), out ulong delNoteTid))
                    {
                        string noteId = arg.GetString(2);
                        if (_staffNotes.TryGetValue(delNoteTid, out var nList))
                        {
                            nList.RemoveAll(n => n.Id == noteId);
                            SaveStaffNotes();
                        }
                        OpenAdminMenu(player);
                    }
                    break;

                case "toggle_vanish":
                    ToggleVanish(player);
                    break;

                case "inspect_tc":
                    InspectTargetStructure(player);
                    break;

                case "tc_clear":
                    if (arg.HasArgs(2) && uint.TryParse(arg.GetString(1), out uint tcNetId))
                    {
                        BaseNetworkable bn = BaseNetworkable.serverEntities.Find(new NetworkableId(tcNetId));
                        BuildingPrivlidge priv = bn as BuildingPrivlidge;
                        if (priv != null)
                        {
                            priv.authorizedPlayers.Clear();
                            priv.SendNetworkUpdateImmediate();
                            SendReply(player, "<color=#00FF88>[INSPECTOR]</color> Авторизация шкафа успешно очищена!");
                            InspectTargetStructure(player);
                        }
                    }
                    break;

                case "quick_tp":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong tpId))
                    {
                        BasePlayer target = BasePlayer.FindByID(tpId) ?? BasePlayer.FindSleeping(tpId);
                        if (target != null)
                        {
                            CloseAdminMenu(player);
                            player.Teleport(target.transform.position + Vector3.up * 1.0f);
                            SendReply(player, session.Language == "RU"
                                ? $"<color=#00FF88>[ADMIN]</color> Телепортирован к <color=#FFFF00>{target.displayName}</color>."
                                : $"<color=#00FF88>[ADMIN]</color> Teleported to <color=#FFFF00>{target.displayName}</color>.");
                        }
                    }
                    break;

                case "bring":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong brId))
                    {
                        BasePlayer target = BasePlayer.FindByID(brId) ?? BasePlayer.FindSleeping(brId);
                        if (target != null)
                        {
                            target.Teleport(player.transform.position + player.transform.forward * 2.0f);
                            SendReply(player, session.Language == "RU"
                                ? $"<color=#00FF88>[ADMIN]</color> Игрок <color=#FFFF00>{target.displayName}</color> перемещен к вам."
                                : $"<color=#00FF88>[ADMIN]</color> Brought <color=#FFFF00>{target.displayName}</color> to your location.");
                            OpenAdminMenu(player);
                        }
                    }
                    break;

                case "toggle_freeze":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong frzId))
                    {
                        BasePlayer target = BasePlayer.FindByID(frzId);
                        if (target != null)
                        {
                            bool nowFrozen = ToggleFreezePlayer(target);
                            if (!nowFrozen)
                            {
                                SendReply(target, session.Language == "RU" ? "<color=#00FF88>[ADMIN]</color> Вы были разморожены." : "<color=#00FF88>[ADMIN]</color> You have been unfrozen.");
                                SendReply(player, session.Language == "RU" ? $"<color=#00FF88>[ADMIN]</color> Игрок <color=#FFFF00>{target.displayName}</color> разморожен." : $"<color=#00FF88>[ADMIN]</color> Player <color=#FFFF00>{target.displayName}</color> unfrozen.");
                            }
                            else
                            {
                                SendReply(target, session.Language == "RU" ? "<color=#FF4444>[ВНИМАНИЕ]</color> Вы временно заморожены администратором сервера!" : "<color=#FF4444>[ATTENTION]</color> You have been temporarily frozen by administration.");
                                SendReply(player, session.Language == "RU" ? $"<color=#00E5FF>[ADMIN]</color> Игрок <color=#FFFF00>{target.displayName}</color> заморожен на месте." : $"<color=#00E5FF>[ADMIN]</color> Player <color=#FFFF00>{target.displayName}</color> frozen.");
                            }
                            OpenAdminMenu(player);
                        }
                    }
                    break;

                case "spectate":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong spId))
                    {
                        if (_spectatingAdmins.Contains(player.userID))
                        {
                            StopSpectating(player);
                            OpenAdminMenu(player);
                        }
                        else
                        {
                            BasePlayer target = BasePlayer.FindByID(spId);
                            if (target != null)
                            {
                                CloseAdminMenu(player);
                                StartSpectating(player, target);
                            }
                        }
                    }
                    break;

                case "toggle_mute":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong mutId))
                    {
                        if (_mutedPlayers.Contains(mutId))
                        {
                            _mutedPlayers.Remove(mutId);
                            SendReply(player, $"<color=#00FF88>[ADMIN]</color> Игрок {mutId} размучен.");
                        }
                        else
                        {
                            _mutedPlayers.Add(mutId);
                            SendReply(player, $"<color=#FF4444>[ADMIN]</color> Игрок {mutId} замучен в чате.");
                        }
                        OpenAdminMenu(player);
                    }
                    break;

                case "heal_player":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong hlId))
                    {
                        BasePlayer target = BasePlayer.FindByID(hlId);
                        if (target != null)
                        {
                            HealPlayerFully(target);
                            SendReply(player, $"<color=#00FF88>[ADMIN]</color> Здоровье и сытость игрока {target.displayName} полностью восстановлены.");
                        }
                        OpenAdminMenu(player);
                    }
                    break;

                case "strip_inv":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong strId))
                    {
                        BasePlayer target = BasePlayer.FindByID(strId) ?? BasePlayer.FindSleeping(strId);
                        if (target != null)
                        {
                            target.inventory.Strip();
                            SendReply(player, $"<color=#FFAA00>[ADMIN]</color> Инвентарь игрока {target.displayName} полностью очищен.");
                        }
                        OpenAdminMenu(player);
                    }
                    break;

                case "kick":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong kcId))
                    {
                        BasePlayer target = BasePlayer.FindByID(kcId);
                        if (IsMasterAdmin(kcId) || (target != null && IsMasterAdmin(target)))
                        {
                            SendReply(player, "<color=#FF4444>[ADMIN]</color> Нельзя кикнуть создателя сервера.");
                            return;
                        }
                        if (target != null)
                        {
                            target.Kick("Исключен администратором сервера / Kicked by server administration");
                            SendReply(player, $"<color=#FFAA00>[ADMIN]</color> Игрок {target.displayName} кикнут с сервера.");
                        }
                        OpenAdminMenu(player);
                    }
                    break;

                case "ban":
                    if (isMaster && arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong bnId))
                    {
                        BasePlayer target = BasePlayer.FindByID(bnId) ?? BasePlayer.FindSleeping(bnId);
                        if (IsMasterAdmin(bnId) || IsAdmin(bnId) || (target != null && (IsMasterAdmin(target) || IsAdmin(target))))
                        {
                            SendReply(player, "<color=#FF4444>[ADMIN]</color> Нельзя забанить администратора сервера.");
                            return;
                        }
                        string tName = target != null ? target.displayName : $"Player #{bnId}";
                        ServerUsers.Set(bnId, ServerUsers.UserGroup.Banned, tName, "Заблокирован администратором через AdminMenu");
                        ServerUsers.Save();

                        if (target != null && target.IsConnected)
                        {
                            target.Kick("Заблокирован администратором / Permanently Banned");
                        }
                        SendReply(player, $"<color=#FF4444>[ADMIN]</color> Игрок {tName} перманентно забанен.");
                        OpenAdminMenu(player);
                    }
                    break;

                case "report_check":
                    if (arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong rcTargetId))
                    {
                        Plugin dr = plugins.Find("DiscordReports");
                        if (dr != null)
                        {
                            dr.Call("StartPlayerCheck", player, rcTargetId);
                        }
                        else
                        {
                            SendReply(player, "<color=#FF4444>[ERROR]</color> DiscordReports plugin not loaded.");
                        }
                    }
                    break;

                case "report_ban":
                    if (isMaster && arg.HasArgs(3) && int.TryParse(arg.GetString(1), out int rbRepId) && ulong.TryParse(arg.GetString(2), out ulong rbSuspectId))
                    {
                        BasePlayer target = BasePlayer.FindByID(rbSuspectId) ?? BasePlayer.FindSleeping(rbSuspectId);
                        if (IsMasterAdmin(rbSuspectId) || IsAdmin(rbSuspectId) || (target != null && (IsMasterAdmin(target) || IsAdmin(target))))
                        {
                            SendReply(player, "<color=#FF4444>[ADMIN]</color> Нельзя забанить администратора сервера.");
                            return;
                        }
                        string tName = target != null ? target.displayName : $"Player #{rbSuspectId}";
                        ServerUsers.Set(rbSuspectId, ServerUsers.UserGroup.Banned, tName, "Использование читов / Ban via Report");
                        ServerUsers.Save();

                        if (target != null && target.IsConnected) target.Kick("Забанен за читы");

                        Plugin dr = plugins.Find("DiscordReports");
                        if (dr != null)
                        {
                            dr.Call("ResolveReportWithVerdict", rbRepId, "ban", player.displayName);
                        }

                        SendReply(player, $"<color=#FF4444>[BAN]</color> Игрок {tName} забанен. Репортеру начислена карма.");
                        OpenAdminMenu(player);
                    }
                    break;

                case "report_resolve":
                    if (arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int rResolveId))
                    {
                        Plugin dr = plugins.Find("DiscordReports");
                        if (dr != null)
                        {
                            dr.Call("ResolveReportWithVerdict", rResolveId, "resolved", player.displayName);
                        }
                        OpenAdminMenu(player);
                    }
                    break;

                case "report_false":
                    if (arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int rFalseId))
                    {
                        Plugin dr = plugins.Find("DiscordReports");
                        if (dr != null)
                        {
                            dr.Call("ResolveReportWithVerdict", rFalseId, "false_report", player.displayName);
                        }
                        SendReply(player, $"<color=#FFAA00>[REPORTS]</color> Репорт #{rFalseId} помечен как ложный. Репортер оштрафован.");
                        OpenAdminMenu(player);
                    }
                    break;

                case "report_del":
                    if (arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int rDelId))
                    {
                        Plugin dr = plugins.Find("DiscordReports");
                        if (dr != null)
                        {
                            dr.Call("DeleteReport", rDelId);
                        }
                        OpenAdminMenu(player);
                    }
                    break;

                case "report_page":
                    if (arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int rPage))
                    {
                        session.ReportsPage = rPage;
                        OpenAdminMenu(player);
                    }
                    break;

                case "refresh_reports":
                    OpenAdminMenu(player);
                    break;

                case "toggle_god":
                    ToggleGodmode(player);
                    OpenAdminMenu(player);
                    break;

                case "toggle_noclip":
                    ToggleNoclipDirect(player);
                    break;

                case "heal_self":
                    HealPlayerFully(player);
                    SendReply(player, "<color=#00FF88>[ADMIN]</color> Ваше здоровье, вода и сытость полностью восстановлены.");
                    OpenAdminMenu(player);
                    break;

                case "kit_pvp":
                    GiveItem(player, "rifle.ak", 1);
                    GiveItem(player, "ammo.rifle", 256);
                    GiveItem(player, "metal.facemask", 1);
                    GiveItem(player, "metal.plate.torso", 1);
                    GiveItem(player, "roadsign.kilt", 1);
                    GiveItem(player, "syringe.medical", 10);
                    SendReply(player, "<color=#00FF88>[ADMIN]</color> Выдан комплект снаряжения PvP (AK-47 + Металл Сет).");
                    break;

                case "kit_builder":
                    GiveItem(player, "building.planner", 1);
                    GiveItem(player, "hammer", 1);
                    GiveItem(player, "wood", 10000);
                    GiveItem(player, "stones", 10000);
                    GiveItem(player, "metal.refined", 2000);
                    SendReply(player, "<color=#00FF88>[ADMIN]</color> Выдан строительный комплект ресурсов.");
                    break;

                case "clear_self_inv":
                    player.inventory.Strip();
                    SendReply(player, "<color=#FFAA00>[ADMIN]</color> Ваш инвентарь полностью очищен.");
                    OpenAdminMenu(player);
                    break;

                case "set_time":
                    if (isMaster && arg.HasArgs(2) && float.TryParse(arg.GetString(1), out float h))
                    {
                        if (TOD_Sky.Instance != null) TOD_Sky.Instance.Cycle.Hour = h;
                        OpenAdminMenu(player);
                    }
                    break;

                case "set_weather":
                    if (isMaster && arg.HasArgs(2))
                    {
                        string wType = arg.GetString(1);
                        switch (wType)
                        {
                            case "clear":
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.clear");
                                break;
                            case "rain":
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.rain 1");
                                break;
                            case "fog":
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.fog 1");
                                break;
                            case "storm":
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.storm 1");
                                break;
                        }
                        OpenAdminMenu(player);
                    }
                    break;

                case "srv_save":
                    if (isMaster)
                    {
                        ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.save");
                        SendReply(player, "<color=#00FF88>[ADMIN]</color> Мир сохранен (server.save).");
                    }
                    break;

                case "srv_gc":
                    if (isMaster)
                    {
                        GC.Collect();
                        SendReply(player, "<color=#00FF88>[ADMIN]</color> Сборщик мусора GC выполнен.");
                        OpenAdminMenu(player);
                    }
                    break;

                case "srv_restart":
                    if (isMaster && arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int restSec))
                    {
                        ConsoleSystem.Run(ConsoleSystem.Option.Server, $"restart {restSec}");
                        BroadcastServerMessage($"Сервер перезапустится через {restSec / 60} минут для планового обслуживания!", true);
                    }
                    break;

                case "event":
                    if (isMaster && arg.HasArgs(2))
                    {
                        string ev = arg.GetString(1);
                        switch (ev)
                        {
                            case "cargoship":
                                SpawnCargoShipEvent();
                                break;
                            case "heli":
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "heli.call");
                                break;
                            case "ch47":
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "ch47.call");
                                break;
                            case "airdrop":
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "supply.call");
                                break;
                            case "bradley":
                                ConsoleSystem.Run(ConsoleSystem.Option.Server, "bradley.respawn");
                                break;
                        }
                        SendReply(player, $"<color=#00FF88>[ADMIN]</color> Ивент {ev} запущен.");
                    }
                    break;

                case "input_broadcast":
                    if (arg.Args != null && arg.Args.Length > 1)
                    {
                        session.BroadcastDraft = string.Join(" ", arg.Args.Skip(1)).Trim();
                    }
                    break;

                case "send_broadcast":
                    if (arg.HasArgs(2) && !string.IsNullOrEmpty(session.BroadcastDraft))
                    {
                        bool toast = arg.GetString(1) == "toast";
                        BroadcastServerMessage(session.BroadcastDraft, toast);
                        session.BroadcastDraft = "";
                        OpenAdminMenu(player);
                    }
                    break;

                case "plugin_reload":
                    if (isMaster && arg.HasArgs(2))
                    {
                        string plName = arg.GetString(1);
                        ConsoleSystem.Run(ConsoleSystem.Option.Server, $"oxide.reload {plName}");
                        SendReply(player, $"<color=#00FF88>[ADMIN]</color> Плагин {plName} перезагружается...");
                        timer.Once(1.0f, () => OpenAdminMenu(player));
                    }
                    break;

                case "plugin_config":
                    if (isMaster && arg.HasArgs(2))
                    {
                        string plName = arg.GetString(1);
                        LoadPluginConfigForEditing(session, plName);
                        session.ActiveTab = "plugin_config";
                        OpenAdminMenu(player);
                    }
                    break;

                case "plugin_page":
                    if (arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int plPage))
                    {
                        session.PluginPage = plPage;
                        OpenAdminMenu(player);
                    }
                    break;

                case "config_toggle_bool":
                    if (isMaster && arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int fIdx))
                    {
                        if (fIdx >= 0 && fIdx < session.ConfigFields.Count)
                        {
                            var f = session.ConfigFields[fIdx];
                            f.Value = f.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
                            session.IsConfigDirty = true;
                            OpenAdminMenu(player);
                        }
                    }
                    break;

                case "config_edit_string":
                    if (isMaster && arg.HasArgs(2) && int.TryParse(arg.GetString(1), out int strIdx))
                    {
                        session.EditingFieldIndex = strIdx;
                        OpenAdminMenu(player);
                    }
                    break;

                case "config_input_val":
                    if (isMaster && session.EditingFieldIndex >= 0 && session.EditingFieldIndex < session.ConfigFields.Count)
                    {
                        if (arg.Args != null && arg.Args.Length > 1)
                        {
                            session.ConfigFields[session.EditingFieldIndex].Value = string.Join(" ", arg.Args.Skip(1)).Trim();
                        }
                    }
                    break;

                case "config_confirm_edit":
                    session.EditingFieldIndex = -1;
                    session.IsConfigDirty = true;
                    OpenAdminMenu(player);
                    break;

                case "config_cancel_edit":
                    session.EditingFieldIndex = -1;
                    OpenAdminMenu(player);
                    break;

                case "config_save":
                    if (isMaster && session.IsConfigDirty)
                    {
                        SaveEditedPluginConfig(session);
                        session.IsConfigDirty = false;
                        SendReply(player, $"<color=#00FF88>[ADMIN]</color> Конфигурация плагина {session.SelectedPlugin} сохранена и перезагружена!");
                        OpenAdminMenu(player);
                    }
                    break;

                case "grant_master":
                    if (isMaster && arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong gmId))
                    {
                        BasePlayer target = BasePlayer.FindByID(gmId) ?? BasePlayer.FindSleeping(gmId);
                        GrantMasterAdmin(player, gmId, target != null ? target.displayName : $"Player #{gmId}");
                        OpenAdminMenu(player);
                    }
                    break;

                case "grant_mod":
                    if (isMaster && arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong gmodId))
                    {
                        BasePlayer target = BasePlayer.FindByID(gmodId) ?? BasePlayer.FindSleeping(gmodId);
                        GrantModerator(player, gmodId, target != null ? target.displayName : $"Player #{gmodId}");
                        OpenAdminMenu(player);
                    }
                    break;

                case "revoke_admin":
                    if (isMaster && arg.HasArgs(2) && ulong.TryParse(arg.GetString(1), out ulong rvkId))
                    {
                        BasePlayer target = BasePlayer.FindByID(rvkId) ?? BasePlayer.FindSleeping(rvkId);
                        RevokeAdmin(player, rvkId, target != null ? target.displayName : $"Player #{rvkId}");
                        OpenAdminMenu(player);
                    }
                    break;
            }
        }

        #endregion

        #region Config Serialization & Parser

        private void LoadPluginConfigForEditing(AdminSession session, string pluginName)
        {
            session.SelectedPlugin = pluginName;
            session.ConfigFields.Clear();
            session.IsConfigDirty = false;
            session.EditingFieldIndex = -1;

            string configPath = Path.Combine(Interface.Oxide.ConfigDirectory, $"{pluginName}.json");
            if (!File.Exists(configPath)) return;

            try
            {
                string json = File.ReadAllText(configPath);
                JObject obj = JObject.Parse(json);

                foreach (var prop in obj.Properties())
                {
                    FieldType fType = FieldType.Other;
                    if (prop.Value.Type == JTokenType.Boolean) fType = FieldType.Boolean;
                    else if (prop.Value.Type == JTokenType.Integer) fType = FieldType.Integer;
                    else if (prop.Value.Type == JTokenType.Float) fType = FieldType.Float;
                    else if (prop.Value.Type == JTokenType.String) fType = FieldType.String;

                    if (fType != FieldType.Other)
                    {
                        session.ConfigFields.Add(new ConfigFieldEntry
                        {
                            Key = prop.Name,
                            Value = prop.Value.ToString(),
                            Type = fType
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"Error loading config for {pluginName}: {ex.Message}");
            }
        }

        private void SaveEditedPluginConfig(AdminSession session)
        {
            string configPath = Path.Combine(Interface.Oxide.ConfigDirectory, $"{session.SelectedPlugin}.json");
            if (!File.Exists(configPath)) return;

            try
            {
                string json = File.ReadAllText(configPath);
                JObject obj = JObject.Parse(json);

                foreach (var field in session.ConfigFields)
                {
                    if (obj[field.Key] != null)
                    {
                        if (field.Type == FieldType.Boolean)
                        {
                            obj[field.Key] = field.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (field.Type == FieldType.Integer && int.TryParse(field.Value, out int iVal))
                        {
                            obj[field.Key] = iVal;
                        }
                        else if (field.Type == FieldType.Float && float.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fVal))
                        {
                            obj[field.Key] = fVal;
                        }
                        else
                        {
                            obj[field.Key] = field.Value;
                        }
                    }
                }

                File.WriteAllText(configPath, obj.ToString(Formatting.Indented));
                ConsoleSystem.Run(ConsoleSystem.Option.Server, $"oxide.reload {session.SelectedPlugin}");
            }
            catch (Exception ex)
            {
                PrintError($"Error saving config for {session.SelectedPlugin}: {ex.Message}");
            }
        }

        #endregion

        #region Helper Routines (Spectating, Godmode, Vitals, Grid)

        private void ToggleGodmode(BasePlayer player)
        {
            if (player == null) return;
            AdminSession session = GetSession(player.userID);
            if (_godmodePlayers.Contains(player.userID))
            {
                _godmodePlayers.Remove(player.userID);
                SendReply(player, session.Language == "RU"
                    ? "<color=#FFAA00>[ADMIN]</color> Режим бессмертия <color=#FF4444>ОТКЛЮЧЕН</color>."
                    : "<color=#FFAA00>[ADMIN]</color> Godmode <color=#FF4444>DISABLED</color>.");
            }
            else
            {
                _godmodePlayers.Add(player.userID);
                HealPlayerFully(player);
                SendReply(player, session.Language == "RU"
                    ? "<color=#00FF88>[ADMIN]</color> Режим бессмертия <color=#00FF88>ВКЛЮЧЕН</color>."
                    : "<color=#00FF88>[ADMIN]</color> Godmode <color=#00FF88>ENABLED</color>.");
            }
        }

        private bool ToggleFreezePlayer(BasePlayer target)
        {
            if (target == null) return false;

            if (_frozenPlayers.Contains(target.userID))
            {
                UnfreezePlayer(target);
                return false;
            }
            else
            {
                FreezePlayer(target);
                return true;
            }
        }

        private void StartSpectating(BasePlayer admin, BasePlayer target)
        {
            if (admin == null || target == null) return;
            if (!_spectatePositions.ContainsKey(admin.userID))
            {
                _spectatePositions[admin.userID] = admin.transform.position;
            }

            _spectatingAdmins.Add(admin.userID);
            admin.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, true);
            admin.Teleport(target.transform.position + Vector3.up * 2f);

            AdminSession session = GetSession(admin.userID);
            SendReply(admin, session.Language == "RU"
                ? $"<color=#00FF88>[ADMIN]</color> Начата слежка за <color=#FFFF00>{target.displayName}</color>. (Нажмите ПКМ или E для выхода)"
                : $"<color=#00FF88>[ADMIN]</color> Now spectating <color=#FFFF00>{target.displayName}</color>. (Press RMB or E to exit)");
        }

        private void StopSpectating(BasePlayer admin)
        {
            if (admin == null) return;
            _spectatingAdmins.Remove(admin.userID);
            admin.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false);

            if (_spectatePositions.TryGetValue(admin.userID, out Vector3 originalPos))
            {
                admin.Teleport(originalPos);
                _spectatePositions.Remove(admin.userID);
            }

            AdminSession session = GetSession(admin.userID);
            SendReply(admin, session.Language == "RU"
                ? "<color=#00FF88>[ADMIN]</color> Режим слежки завершен. Вы вернулись на исходную позицию."
                : "<color=#00FF88>[ADMIN]</color> Spectate mode terminated. Restored original position.");
        }

        private void HealPlayerFully(BasePlayer player)
        {
            if (player == null) return;
            player.health = player.MaxHealth();
            player.metabolism.calories.value = player.metabolism.calories.max;
            player.metabolism.hydration.value = player.metabolism.hydration.max;
            player.metabolism.bleeding.value = 0f;
            player.metabolism.radiation_level.value = 0f;
            player.metabolism.poison.value = 0f;
            player.metabolism.temperature.value = 24f;
        }

        private void GiveItem(BasePlayer player, string shortname, int amount)
        {
            if (player == null) return;
            Item item = ItemManager.CreateByName(shortname, amount);
            if (item != null)
            {
                player.GiveItem(item);
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

        private void GrantMasterAdmin(BasePlayer caller, ulong targetId, string targetName)
        {
            string sid = targetId.ToString();
            if (!_config.MasterAdminSteamIds.Contains(sid))
            {
                _config.MasterAdminSteamIds.Add(sid);
            }
            _config.ModeratorSteamIds.Remove(sid);
            SaveConfig();

            permission.GrantUserPermission(sid, PermMaster, this);
            ServerUsers.Set(targetId, ServerUsers.UserGroup.Owner, targetName, "Owner");
            ServerUsers.Save();

            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target != null && target.IsConnected)
            {
                CheckAdminStatus(target);
                SendReply(target, "<color=#A855F7>[ADMIN]</color> Вам выданы полномочия <b>Главного Администратора (Master Admin)</b>!");
            }
            SendReply(caller, $"<color=#00FF88>[ADMIN]</color> Игроку {targetName} ({targetId}) успешно выданы права Главного Администратора.");
        }

        private void GrantModerator(BasePlayer caller, ulong targetId, string targetName)
        {
            string sid = targetId.ToString();
            if (!_config.ModeratorSteamIds.Contains(sid))
            {
                _config.ModeratorSteamIds.Add(sid);
            }
            _config.MasterAdminSteamIds.Remove(sid);
            SaveConfig();

            permission.GrantUserPermission(sid, PermUse, this);
            ServerUsers.Set(targetId, ServerUsers.UserGroup.Moderator, targetName, "Moderator");
            ServerUsers.Save();

            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target != null && target.IsConnected)
            {
                CheckAdminStatus(target);
                SendReply(target, "<color=#38BDF8>[ADMIN]</color> Вам выданы полномочия <b>Модератора сервера (Moderator)</b>!");
            }
            SendReply(caller, $"<color=#00FF88>[ADMIN]</color> Игроку {targetName} ({targetId}) успешно выданы права Модератора.");
        }

        private void RevokeAdmin(BasePlayer caller, ulong targetId, string targetName)
        {
            string sid = targetId.ToString();
            _config.MasterAdminSteamIds.Remove(sid);
            _config.ModeratorSteamIds.Remove(sid);
            SaveConfig();

            permission.RevokeUserPermission(sid, PermMaster);
            permission.RevokeUserPermission(sid, PermUse);

            ServerUsers.Remove(targetId);
            ServerUsers.Save();

            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target != null && target.IsConnected)
            {
                if (target.net?.connection != null) target.net.connection.authLevel = 0;
                target.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                target.SendNetworkUpdateImmediate();
                SendReply(target, "<color=#FF4444>[ADMIN]</color> Ваши административные привилегии были отозваны.");
            }
            SendReply(caller, $"<color=#FF4444>[ADMIN]</color> Все права администратора у игрока {targetName} ({targetId}) сняты.");
        }

        #endregion

        #region External API

        [HookMethod("OpenAdminMenuForPlayer")]
        public void OpenAdminMenuForPlayer(BasePlayer player, string tab = "dashboard")
        {
            if (player == null || !IsAdmin(player)) return;
            AdminSession session = GetSession(player.userID);
            session.ActiveTab = tab.ToLower();
            OpenAdminMenu(player);
        }

        #endregion
    }
}
