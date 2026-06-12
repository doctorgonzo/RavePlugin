using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("DocPlugin", "Doc", "0.1.0")]
    [Description("Rave warehouse controller — manages lights, lasers, and boomboxes in patterns")]
    public class DocPlugin : RustPlugin
    {
        #region Configuration

        private Configuration _config;

        private class Configuration
        {
            [JsonProperty("Permission")]
            public string Permission { get; set; } = "docplugin.dj";

            [JsonProperty("Light scan radius")]
            public float ScanRadius { get; set; } = 50f;

            [JsonProperty("Stations")]
            public Dictionary<string, string> Stations { get; set; } = new Dictionary<string, string>
            {
                ["dnb"] = "http://chi.bassdrive.co/;stream/1",
                ["techno"] = "http://ice1.somafm.com/thetrip-128-mp3",
                ["house"] = "http://ice1.somafm.com/beatblender-128-mp3",
                ["ambient"] = "http://ice1.somafm.com/dronezone-128-mp3",
                ["chillout"] = "http://ice2.somafm.com/spacestation-128-mp3",
                ["groovy"] = "http://ice1.somafm.com/groovesalad-128-mp3"
            };

            [JsonProperty("Countdown sound effect")]
            public string CountdownSound { get; set; } = "assets/prefabs/tools/flare/effects/ignite.prefab";

            [JsonProperty("Rave started sound effect")]
            public string StartedSound { get; set; } = "assets/prefabs/missions/effects/mission_objective_complete.prefab";

            [JsonProperty("Rave ended sound effect")]
            public string EndedSound { get; set; } = "";

            [JsonProperty("Countdown steps (seconds)")]
            public List<int> CountdownSteps { get; set; } = new List<int> { 300, 120, 60, 30, 10 };

            [JsonProperty("Announcement message")]
            public string AnnounceMessage { get; set; } = "<size=18><color=#ff0044>★ RAVE INCOMING ★</color></size>\n<color=#00ffcc>{time}</color> until the drop!\nLocation: <color=#ffcc00>{coords}</color>\nGet there or get left behind.";

            [JsonProperty("Rave started message")]
            public string StartedMessage { get; set; } = "<size=22><color=#ff0044>★ THE RAVE IS LIVE ★</color></size>\n<color=#00ffcc>Location: {coords}</color>\nLights up. Music on. Let's go.";

            [JsonProperty("Rave ended message")]
            public string EndedMessage { get; set; } = "<size=18><color=#ff0044>★ RAVE OVER ★</color></size>\nThanks for coming. See you next time.";

            [JsonProperty("Patterns")]
            public Dictionary<string, PatternConfig> Patterns { get; set; } = new Dictionary<string, PatternConfig>
            {
                ["strobe"] = new PatternConfig { IntervalMs = 200, Mode = "toggle_all" },
                ["slow_strobe"] = new PatternConfig { IntervalMs = 500, Mode = "toggle_all" },
                ["chase"] = new PatternConfig { IntervalMs = 150, Mode = "sequential" },
                ["wave"] = new PatternConfig { IntervalMs = 300, Mode = "sequential" },
                ["random"] = new PatternConfig { IntervalMs = 250, Mode = "random" },
                ["pulse"] = new PatternConfig { IntervalMs = 400, Mode = "pulse" }
            };
        }

        private class PatternConfig
        {
            [JsonProperty("Interval (ms)")]
            public int IntervalMs { get; set; } = 200;

            [JsonProperty("Mode")]
            public string Mode { get; set; } = "toggle_all";
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region State

        private Dictionary<string, List<ulong>> _zones = new Dictionary<string, List<ulong>>();
        private Dictionary<string, List<ulong>> _boomboxZones = new Dictionary<string, List<ulong>>();
        private Timer _patternTimer;
        private string _activePattern;
        private string _activeZone;
        private int _chaseIndex;
        private bool _toggleState;
        private string _dataFile = "DocPlugin_Zones";
        private List<Timer> _countdownTimers = new List<Timer>();
        private bool _raveActive;
        private Vector3 _raveLocation;

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(_config.Permission, this);
            LoadZoneData();
        }

        private void Unload()
        {
            StopPattern();
            CancelCountdown();
            SaveZoneData();
        }

        private void OnServerInitialized()
        {
            SaveZoneData();
            ApplyServerUrlList();
        }

        // Clients only stream URLs present in boombox.serverurllist; anything
        // else falls back to default audio. Register our stations there so
        // every client accepts them (they show up in the boombox UI too).
        private void ApplyServerUrlList()
        {
            if (_config.Stations.Count == 0) return;

            var parts = new List<string>();
            foreach (var kvp in _config.Stations)
                parts.Add($"{kvp.Key},{kvp.Value}");

            string list = string.Join(",", parts);
            ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "boombox.serverurllist", list);
            Puts($"Registered {_config.Stations.Count} stations in boombox.serverurllist");
        }

        #endregion

        #region Data Persistence

        private void LoadZoneData()
        {
            var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, Dictionary<string, List<ulong>>>>(_dataFile);
            if (data != null)
            {
                if (data.ContainsKey("lights"))
                    _zones = data["lights"];
                if (data.ContainsKey("boomboxes"))
                    _boomboxZones = data["boomboxes"];
            }
        }

        private void SaveZoneData()
        {
            var data = new Dictionary<string, Dictionary<string, List<ulong>>>
            {
                ["lights"] = _zones,
                ["boomboxes"] = _boomboxZones
            };
            Interface.Oxide.DataFileSystem.WriteObject(_dataFile, data);
        }

        #endregion

        #region Commands

        [ChatCommand("rave")]
        private void RaveCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, _config.Permission))
            {
                player.ChatMessage("<color=#ff0044>[RAVE]</color> No permission.");
                return;
            }

            if (args.Length == 0)
            {
                ShowHelp(player);
                return;
            }

            switch (args[0].ToLower())
            {
                case "scan":
                    CmdScan(player, args);
                    break;
                case "zones":
                    CmdZones(player);
                    break;
                case "clear":
                    CmdClear(player, args);
                    break;
                case "on":
                    CmdAllLights(player, args, true);
                    break;
                case "off":
                    CmdAllLights(player, args, false);
                    break;
                case "pattern":
                    CmdPattern(player, args);
                    break;
                case "stop":
                    CmdStop(player);
                    break;
                case "bpm":
                    CmdBpm(player, args);
                    break;
                case "station":
                    CmdStation(player, args);
                    break;
                case "stations":
                    CmdStations(player);
                    break;
                case "mute":
                    CmdMute(player, args);
                    break;
                case "patterns":
                    CmdPatterns(player);
                    break;
                case "start":
                    CmdStart(player, args);
                    break;
                case "end":
                    CmdEnd(player);
                    break;
                case "cancel":
                    CmdCancel(player);
                    break;
                default:
                    ShowHelp(player);
                    break;
            }
        }

        #endregion

        #region Subcommands

        private void CmdScan(BasePlayer player, string[] args)
        {
            string zone = args.Length > 1 ? args[1].ToLower() : "main";
            float radius = _config.ScanRadius;

            if (!_zones.ContainsKey(zone))
                _zones[zone] = new List<ulong>();
            if (!_boomboxZones.ContainsKey(zone))
                _boomboxZones[zone] = new List<ulong>();

            int lightCount = 0;
            int boomboxCount = 0;
            var entities = new List<BaseEntity>();
            Vis.Entities(player.transform.position, radius, entities);

            foreach (var entity in entities)
            {
                if (IsControllableLight(entity))
                {
                    if (!_zones[zone].Contains(entity.net.ID.Value))
                    {
                        _zones[zone].Add(entity.net.ID.Value);
                        lightCount++;
                    }
                }
                else if (entity is DeployableBoomBox)
                {
                    if (!_boomboxZones[zone].Contains(entity.net.ID.Value))
                    {
                        _boomboxZones[zone].Add(entity.net.ID.Value);
                        boomboxCount++;
                    }
                }
            }

            SaveZoneData();
            player.ChatMessage($"<color=#ff0044>[RAVE]</color> Scanned zone '<color=#00ffcc>{zone}</color>': found <color=#ffcc00>{lightCount}</color> new lights, <color=#ffcc00>{boomboxCount}</color> new boomboxes (radius {radius}m)");
            player.ChatMessage($"<color=#ff0044>[RAVE]</color> Zone totals — lights: {_zones[zone].Count}, boomboxes: {_boomboxZones[zone].Count}");
        }

        private void CmdZones(BasePlayer player)
        {
            if (_zones.Count == 0 && _boomboxZones.Count == 0)
            {
                player.ChatMessage("<color=#ff0044>[RAVE]</color> No zones registered. Use <color=#00ffcc>/rave scan [zone]</color>");
                return;
            }

            var allZones = _zones.Keys.Union(_boomboxZones.Keys).Distinct();
            foreach (var zone in allZones)
            {
                int lights = _zones.ContainsKey(zone) ? _zones[zone].Count : 0;
                int boxes = _boomboxZones.ContainsKey(zone) ? _boomboxZones[zone].Count : 0;
                player.ChatMessage($"<color=#ff0044>[RAVE]</color> <color=#00ffcc>{zone}</color>: {lights} lights, {boxes} boomboxes");
            }
        }

        private void CmdClear(BasePlayer player, string[] args)
        {
            if (args.Length > 1)
            {
                string zone = args[1].ToLower();
                _zones.Remove(zone);
                _boomboxZones.Remove(zone);
                SaveZoneData();
                player.ChatMessage($"<color=#ff0044>[RAVE]</color> Cleared zone '<color=#00ffcc>{zone}</color>'");
            }
            else
            {
                _zones.Clear();
                _boomboxZones.Clear();
                SaveZoneData();
                player.ChatMessage("<color=#ff0044>[RAVE]</color> All zones cleared.");
            }
        }

        private void CmdAllLights(BasePlayer player, string[] args, bool on)
        {
            string zone = args.Length > 1 ? args[1].ToLower() : null;
            int count = 0;

            foreach (var kvp in _zones)
            {
                if (zone != null && kvp.Key != zone) continue;
                foreach (ulong id in kvp.Value)
                {
                    var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                    if (entity != null && IsControllableLight(entity))
                    {
                        SetLightState(entity, on);
                        count++;
                    }
                }
            }

            string state = on ? "ON" : "OFF";
            string scope = zone ?? "all zones";
            player.ChatMessage($"<color=#ff0044>[RAVE]</color> Lights <color=#ffcc00>{state}</color> — {count} lights in {scope}");
        }

        private void CmdPattern(BasePlayer player, string[] args)
        {
            if (args.Length < 2)
            {
                player.ChatMessage("<color=#ff0044>[RAVE]</color> Usage: <color=#00ffcc>/rave pattern <name> [zone]</color>");
                return;
            }

            string patternName = args[1].ToLower();
            string zone = args.Length > 2 ? args[2].ToLower() : null;

            if (!_config.Patterns.ContainsKey(patternName))
            {
                player.ChatMessage($"<color=#ff0044>[RAVE]</color> Unknown pattern. Available: {string.Join(", ", _config.Patterns.Keys)}");
                return;
            }

            StopPattern();

            var pattern = _config.Patterns[patternName];
            _activePattern = patternName;
            _activeZone = zone;
            _chaseIndex = 0;
            _toggleState = false;

            float interval = pattern.IntervalMs / 1000f;

            _patternTimer = timer.Every(interval, () => RunPattern(pattern));

            string scope = zone ?? "all zones";
            player.ChatMessage($"<color=#ff0044>[RAVE]</color> Pattern '<color=#00ffcc>{patternName}</color>' running ({pattern.IntervalMs}ms) on {scope}");
        }

        private void CmdStop(BasePlayer player)
        {
            StopPattern();
            SetAllLights(true);
            player.ChatMessage("<color=#ff0044>[RAVE]</color> Stopped. Lights on.");
        }

        private void CmdBpm(BasePlayer player, string[] args)
        {
            if (args.Length < 2 || _activePattern == null)
            {
                player.ChatMessage("<color=#ff0044>[RAVE]</color> Usage: <color=#00ffcc>/rave bpm <value></color> (pattern must be running)");
                return;
            }

            int bpm;
            if (!int.TryParse(args[1], out bpm) || bpm < 30 || bpm > 600)
            {
                player.ChatMessage("<color=#ff0044>[RAVE]</color> BPM must be between 30 and 600.");
                return;
            }

            // Convert BPM to interval: one toggle per beat
            float interval = 60f / bpm;

            StopTimer();
            var pattern = _config.Patterns[_activePattern];
            _patternTimer = timer.Every(interval, () => RunPattern(pattern));

            player.ChatMessage($"<color=#ff0044>[RAVE]</color> Tempo set to <color=#ffcc00>{bpm} BPM</color> ({(int)(interval * 1000)}ms)");
        }

        private void CmdStation(BasePlayer player, string[] args)
        {
            if (args.Length < 2)
            {
                player.ChatMessage("<color=#ff0044>[RAVE]</color> Usage: <color=#00ffcc>/rave station <name> [zone]</color>");
                return;
            }

            string stationName = args[1].ToLower();
            string zone = args.Length > 2 ? args[2].ToLower() : null;

            if (!_config.Stations.ContainsKey(stationName))
            {
                player.ChatMessage($"<color=#ff0044>[RAVE]</color> Unknown station. Available: {string.Join(", ", _config.Stations.Keys)}");
                return;
            }

            string url = _config.Stations[stationName];
            int count = SetBoomboxes(url, zone);

            player.ChatMessage($"<color=#ff0044>[RAVE]</color> Now playing '<color=#00ffcc>{stationName}</color>' on {count} boombox(es)");
        }

        private void CmdStations(BasePlayer player)
        {
            player.ChatMessage("<color=#ff0044>[RAVE]</color> Stations:");
            foreach (var kvp in _config.Stations)
                player.ChatMessage($"  <color=#00ffcc>{kvp.Key}</color> — {kvp.Value}");
        }

        private void CmdMute(BasePlayer player, string[] args)
        {
            string zone = args.Length > 1 ? args[1].ToLower() : null;
            int count = SetBoomboxes(null, zone);
            player.ChatMessage($"<color=#ff0044>[RAVE]</color> Muted {count} boombox(es)");
        }

        private void CmdPatterns(BasePlayer player)
        {
            player.ChatMessage("<color=#ff0044>[RAVE]</color> Patterns:");
            foreach (var kvp in _config.Patterns)
                player.ChatMessage($"  <color=#00ffcc>{kvp.Key}</color> — {kvp.Value.Mode}, {kvp.Value.IntervalMs}ms");

            if (_activePattern != null)
                player.ChatMessage($"  Active: <color=#ffcc00>{_activePattern}</color>");
        }

        #endregion

        #region Announcements

        private void CmdStart(BasePlayer player, string[] args)
        {
            if (_raveActive)
            {
                player.ChatMessage("<color=#ff0044>[RAVE]</color> A rave is already active. Use <color=#00ffcc>/rave end</color> first.");
                return;
            }

            int countdown = 300;
            if (args.Length > 1)
            {
                if (!int.TryParse(args[1], out countdown) || countdown < 0)
                {
                    player.ChatMessage("<color=#ff0044>[RAVE]</color> Usage: <color=#00ffcc>/rave start [seconds]</color> (default 300)");
                    return;
                }
            }

            _raveLocation = player.transform.position;
            string coords = FormatCoords(_raveLocation);

            if (countdown == 0)
            {
                _raveActive = true;
                string msg = _config.StartedMessage
                    .Replace("{coords}", coords);
                BroadcastChat(msg, _config.StartedSound);
                player.ChatMessage("<color=#ff0044>[RAVE]</color> Rave is live!");
                return;
            }

            CancelCountdown();

            foreach (int step in _config.CountdownSteps)
            {
                if (step > countdown) continue;
                int delay = countdown - step;
                var t = timer.Once(delay, () =>
                {
                    string timeStr = FormatTime(step);
                    string msg = _config.AnnounceMessage
                        .Replace("{time}", timeStr)
                        .Replace("{coords}", coords);
                    BroadcastChat(msg, _config.CountdownSound);
                });
                _countdownTimers.Add(t);
            }

            var startTimer = timer.Once(countdown, () =>
            {
                _raveActive = true;
                string msg = _config.StartedMessage
                    .Replace("{coords}", coords);
                BroadcastChat(msg, _config.StartedSound);
                _countdownTimers.Clear();
            });
            _countdownTimers.Add(startTimer);

            string firstMsg = _config.AnnounceMessage
                .Replace("{time}", FormatTime(countdown))
                .Replace("{coords}", coords);
            BroadcastChat(firstMsg, _config.CountdownSound);
            player.ChatMessage($"<color=#ff0044>[RAVE]</color> Countdown started — {FormatTime(countdown)} until showtime.");
        }

        private void CmdEnd(BasePlayer player)
        {
            CancelCountdown();
            _raveActive = false;
            BroadcastChat(_config.EndedMessage, _config.EndedSound);
            player.ChatMessage("<color=#ff0044>[RAVE]</color> Rave ended.");
        }

        private void CmdCancel(BasePlayer player)
        {
            CancelCountdown();
            _raveActive = false;
            player.ChatMessage("<color=#ff0044>[RAVE]</color> Countdown cancelled.");
        }

        private void CancelCountdown()
        {
            foreach (var t in _countdownTimers)
                t?.Destroy();
            _countdownTimers.Clear();
        }

        private void BroadcastChat(string message, string sound = null)
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                p.ChatMessage(message);
                if (!string.IsNullOrEmpty(sound))
                    PlaySoundAtPlayer(p, sound);
            }
        }

        private void PlaySoundAtPlayer(BasePlayer player, string prefab)
        {
            var effect = new Effect(prefab, player.transform.position, Vector3.zero);
            EffectNetwork.Send(effect, player.net.connection);
        }

        private string FormatCoords(Vector3 pos)
        {
            return $"({pos.x:F0}, {pos.y:F0}, {pos.z:F0})";
        }

        private string FormatTime(int seconds)
        {
            if (seconds >= 60)
            {
                int mins = seconds / 60;
                int secs = seconds % 60;
                return secs > 0 ? $"{mins}m {secs}s" : $"{mins}m";
            }
            return $"{seconds}s";
        }

        #endregion

        #region Pattern Engine

        private void RunPattern(PatternConfig pattern)
        {
            var lights = GetActiveLights();
            if (lights.Count == 0) return;

            switch (pattern.Mode)
            {
                case "toggle_all":
                    _toggleState = !_toggleState;
                    foreach (var light in lights)
                        SetLightState(light, _toggleState);
                    break;

                case "sequential":
                    for (int i = 0; i < lights.Count; i++)
                        SetLightState(lights[i], i == _chaseIndex);
                    _chaseIndex = (_chaseIndex + 1) % lights.Count;
                    break;

                case "random":
                    foreach (var light in lights)
                        SetLightState(light, UnityEngine.Random.value > 0.5f);
                    break;

                case "pulse":
                    _toggleState = !_toggleState;
                    int half = lights.Count / 2;
                    for (int i = 0; i < lights.Count; i++)
                    {
                        bool on = i < half ? _toggleState : !_toggleState;
                        SetLightState(lights[i], on);
                    }
                    break;
            }
        }

        private List<BaseEntity> GetActiveLights()
        {
            var result = new List<BaseEntity>();

            foreach (var kvp in _zones)
            {
                if (_activeZone != null && kvp.Key != _activeZone) continue;
                foreach (ulong id in kvp.Value)
                {
                    var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                    if (entity != null && IsControllableLight(entity))
                        result.Add(entity);
                }
            }

            return result;
        }

        private void StopPattern()
        {
            StopTimer();
            _activePattern = null;
            _activeZone = null;
            _chaseIndex = 0;
            _toggleState = false;
        }

        private void StopTimer()
        {
            if (_patternTimer != null)
            {
                _patternTimer.Destroy();
                _patternTimer = null;
            }
        }

        #endregion

        #region Light Control

        private bool IsControllableLight(BaseEntity entity)
        {
            return entity is CeilingLight
                || entity is SearchLight
                || entity is SimpleLight
                || entity is FlasherLight
                || entity is SirenLight;
        }

        private void SetLightState(BaseEntity entity, bool on)
        {
            entity.SetFlag(BaseEntity.Flags.On, on);
            entity.SendNetworkUpdateImmediate();
        }

        private void SetAllLights(bool on)
        {
            foreach (var kvp in _zones)
            {
                foreach (ulong id in kvp.Value)
                {
                    var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                    if (entity != null && IsControllableLight(entity))
                        SetLightState(entity, on);
                }
            }
        }

        #endregion

        #region Boombox Control

        private int SetBoomboxes(string url, string zone)
        {
            var boomboxes = new List<DeployableBoomBox>();

            foreach (var kvp in _boomboxZones)
            {
                if (zone != null && kvp.Key != zone) continue;
                foreach (ulong id in kvp.Value)
                {
                    var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                    var boombox = entity as DeployableBoomBox;
                    if (boombox == null || boombox.IsDestroyed) continue;
                    if (boombox.BoxController == null) continue;
                    boomboxes.Add(boombox);
                }
            }

            foreach (var boombox in boomboxes)
            {
                boombox.BoxController.ServerTogglePlay(false);
                if (url != null)
                {
                    EjectCassette(boombox);
                    boombox.BoxController.CurrentRadioIp = url;
                }
                boombox.SendNetworkUpdateImmediate();
            }

            if (url != null && boomboxes.Count > 0)
            {
                timer.Once(2.0f, () =>
                {
                    foreach (var boombox in boomboxes)
                    {
                        if (boombox == null || boombox.IsDestroyed) continue;
                        if (boombox.BoxController == null) continue;
                        boombox.BoxController.ServerTogglePlay(true);
                        boombox.SendNetworkUpdateImmediate();
                    }
                });
            }

            return boomboxes.Count;
        }

        // A loaded cassette overrides the radio URL, so pop it out before playing
        private void EjectCassette(DeployableBoomBox boombox)
        {
            var inv = boombox.inventory;
            if (inv == null) return;

            for (int i = inv.itemList.Count - 1; i >= 0; i--)
            {
                var item = inv.itemList[i];
                item.Drop(boombox.transform.position + Vector3.up * 0.5f, Vector3.up);
            }
        }

        #endregion

        #region Help

        private void ShowHelp(BasePlayer player)
        {
            player.ChatMessage("<color=#ff0044>═══ RAVE CONTROLLER ═══</color>");
            player.ChatMessage("<color=#00ffcc>/rave scan [zone]</color> — Register nearby lights & boomboxes");
            player.ChatMessage("<color=#00ffcc>/rave zones</color> — List registered zones");
            player.ChatMessage("<color=#00ffcc>/rave clear [zone]</color> — Clear zone(s)");
            player.ChatMessage("<color=#00ffcc>/rave on [zone]</color> — All lights on");
            player.ChatMessage("<color=#00ffcc>/rave off [zone]</color> — All lights off");
            player.ChatMessage("<color=#00ffcc>/rave pattern <name> [zone]</color> — Start a pattern");
            player.ChatMessage("<color=#00ffcc>/rave patterns</color> — List available patterns");
            player.ChatMessage("<color=#00ffcc>/rave bpm <value></color> — Set pattern tempo");
            player.ChatMessage("<color=#00ffcc>/rave stop</color> — Stop pattern, lights on");
            player.ChatMessage("<color=#00ffcc>/rave station <name> [zone]</color> — Play a station");
            player.ChatMessage("<color=#00ffcc>/rave stations</color> — List stations");
            player.ChatMessage("<color=#00ffcc>/rave mute [zone]</color> — Stop music");
            player.ChatMessage("<color=#ff0044>── Announcements ──</color>");
            player.ChatMessage("<color=#00ffcc>/rave start [seconds]</color> — Countdown + announce (default 5m)");
            player.ChatMessage("<color=#00ffcc>/rave end</color> — Announce rave is over");
            player.ChatMessage("<color=#00ffcc>/rave cancel</color> — Cancel countdown silently");
        }

        #endregion
    }
}
